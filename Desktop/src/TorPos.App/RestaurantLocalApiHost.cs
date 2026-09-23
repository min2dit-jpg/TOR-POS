using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TorPos.Application;
using TorPos.Core;
using TorPos.Infrastructure;

#if TOR_RESTAURANT_PRODUCT
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.RateLimiting;
#endif

namespace TorPos.App;

public sealed record RestaurantLocalApiStatus(
    bool Running,
    string HttpsEndpoint,
    int Port,
    string CertificateSha256,
    string Message);

public sealed class RestaurantLocalApiHost : IAsyncDisposable
{
    private readonly ISettingsRepository _settings;
    private readonly RestaurantEntitlementService _entitlements;
    private readonly RestaurantHandheldPairingService _pairing;
    private readonly IRestaurantHandheldService _handheld;
    private readonly SemaphoreSlim _gate = new(1, 1);

#if TOR_RESTAURANT_PRODUCT
    private WebApplication? _app;
    private X509Certificate2? _certificate;
#endif

    public RestaurantLocalApiStatus Status { get; private set; } =
        new(false, "", 0, "", "Handheld-API nicht gestartet.");

    public RestaurantLocalApiHost(
        ISettingsRepository settings,
        RestaurantEntitlementService entitlements,
        RestaurantHandheldPairingService pairing,
        IRestaurantHandheldService handheld)
    {
        _settings = settings;
        _entitlements = entitlements;
        _pairing = pairing;
        _handheld = handheld;
    }

    public async Task StartOrRestartAsync(
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await StopCoreAsync();

            if (!_entitlements.IsEnabled(
                    RestaurantFeature.HandheldBestellung))
            {
                Status = new(
                    false,
                    "",
                    0,
                    "",
                    "TOR Restaurant Plus ist nicht aktiv.");
                return;
            }

            var enabledRaw = await _settings.GetAsync(
                "restaurant.handheld.api.enabled",
                "false",
                ct);

            if (!bool.TryParse(enabledRaw, out var enabled) ||
                !enabled)
            {
                Status = new(
                    false,
                    "",
                    0,
                    "",
                    "Handheld-API ist deaktiviert.");
                return;
            }

            var portRaw = await _settings.GetAsync(
                "restaurant.handheld.api.port",
                "17831",
                ct);

            if (!int.TryParse(portRaw, out var port) ||
                port is < 1024 or > 65535)
            {
                throw new InvalidOperationException(
                    "Handheld-API-Port muss zwischen 1024 und 65535 liegen.");
            }

#if TOR_RESTAURANT_PRODUCT
            var cert = EnsureServerCertificate();
            var builder = WebApplication.CreateSlimBuilder();

            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode =
                    StatusCodes.Status429TooManyRequests;

                options.AddPolicy(
                    "pairing",
                    httpContext =>
                        RateLimitPartition.GetFixedWindowLimiter(
                            httpContext.Connection.RemoteIpAddress?.ToString()
                                ?? "unknown",
                            _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = 10,
                                Window = TimeSpan.FromMinutes(1),
                                QueueLimit = 0,
                                AutoReplenishment = true
                            }));

                options.AddPolicy(
                    "device",
                    httpContext =>
                        RateLimitPartition.GetFixedWindowLimiter(
                            httpContext.Connection.RemoteIpAddress?.ToString()
                                ?? "unknown",
                            _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = 180,
                                Window = TimeSpan.FromMinutes(1),
                                QueueLimit = 0,
                                AutoReplenishment = true
                            }));
            });

            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(
                    port,
                    listen => listen.UseHttps(cert));
            });

            var app = builder.Build();
            app.UseRateLimiter();

            app.MapGet(
                "/api/v1/health",
                () => Results.Ok(new
                {
                    product = "TOR Restaurant Plus",
                    apiVersion = 1,
                    secure = true
                }));

            app.MapPost(
                    "/api/v1/pair",
                    async (PairRequest request, CancellationToken token) =>
                    {
                        try
                        {
                            var paired = await _pairing.PairAsync(
                                request.PairingCode,
                                request.DeviceId,
                                request.DisplayName,
                                token);

                            return Results.Ok(new
                            {
                                paired.DeviceId,
                                paired.DisplayName,
                                paired.DeviceToken,
                                paired.PairedAt,
                                certificateSha256 =
                                    CertificateSha256(cert),
                                apiVersion = 1
                            });
                        }
                        catch (InvalidOperationException ex)
                        {
                            return Results.BadRequest(new
                            {
                                error = ex.Message
                            });
                        }
                        catch (ArgumentException ex)
                        {
                            return Results.BadRequest(new
                            {
                                error = ex.Message
                            });
                        }
                    })
                .RequireRateLimiting("pairing");

            app.MapGet(
                    "/api/v1/tables",
                    async (HttpContext context, CancellationToken token) =>
                    {
                        if (!TryDeviceCredentials(
                                context,
                                out var deviceId,
                                out var deviceToken))
                        {
                            return Results.Unauthorized();
                        }

                        try
                        {
                            var tables =
                                await _handheld.GetTablesAsync(
                                    deviceId,
                                    deviceToken,
                                    token);

                            return Results.Ok(tables);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            return Results.Unauthorized();
                        }
                        catch (InvalidOperationException ex)
                        {
                            return Results.Conflict(new
                            {
                                error = ex.Message
                            });
                        }
                    })
                .RequireRateLimiting("device");

            app.MapPost(
                    "/api/v1/items",
                    async (
                        HttpContext context,
                        AddItemRequest request,
                        CancellationToken token) =>
                    {
                        if (!TryDeviceCredentials(
                                context,
                                out var deviceId,
                                out var deviceToken))
                        {
                            return Results.Unauthorized();
                        }

                        try
                        {
                            var result =
                                await _handheld.AddItemAsync(
                                    new RestaurantHandheldAddItemRequest(
                                        request.SessionId,
                                        request.ExpectedSessionVersion,
                                        request.ProductId,
                                        request.Quantity,
                                        request.OperatorName,
                                        deviceId,
                                        deviceToken),
                                    token);

                            return Results.Ok(result);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            return Results.Unauthorized();
                        }
                        catch (ArgumentOutOfRangeException ex)
                        {
                            return Results.BadRequest(new
                            {
                                error = ex.Message
                            });
                        }
                        catch (InvalidOperationException ex)
                        {
                            return Results.Conflict(new
                            {
                                error = ex.Message
                            });
                        }
                    })
                .RequireRateLimiting("device");

            await app.StartAsync(ct);

            _app = app;
            _certificate = cert;

            var host = Environment.MachineName;
            Status = new(
                true,
                $"https://{host}:{port}",
                port,
                CertificateSha256(cert),
                "Handheld-API läuft über HTTPS.");
#else
            Status = new(
                false,
                "",
                0,
                "",
                "Handheld-API ist nur im TOR Restaurant Build verfügbar.");
#endif
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
#if TOR_RESTAURANT_PRODUCT
        if (_app is not null)
        {
            try
            {
                using var stopTimeout =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(3));
                await _app.StopAsync(
                    stopTimeout.Token);
            }
            finally
            {
                await _app.DisposeAsync();
                _app = null;
            }
        }

        _certificate?.Dispose();
        _certificate = null;
#endif

        Status = new(
            false,
            "",
            0,
            "",
            "Handheld-API nicht gestartet.");
    }

#if TOR_RESTAURANT_PRODUCT
    private static X509Certificate2 EnsureServerCertificate()
    {
        const string subject =
            "CN=TOR Restaurant Local API";

        using var store = new X509Store(
            StoreName.My,
            StoreLocation.CurrentUser);

        store.Open(
            OpenFlags.ReadWrite);

        var existing = store.Certificates
            .Find(
                X509FindType.FindBySubjectDistinguishedName,
                subject,
                validOnly: false)
            .OfType<X509Certificate2>()
            .Where(x =>
                x.HasPrivateKey &&
                x.NotAfter.ToUniversalTime() >
                    DateTime.UtcNow.AddDays(30))
            .OrderByDescending(x => x.NotAfter)
            .FirstOrDefault();

        if (existing is not null)
            return existing;

        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            subject,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                false,
                false,
                0,
                false));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature |
                X509KeyUsageFlags.KeyEncipherment,
                false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);

        foreach (var address in GetLocalAddresses())
            san.AddIpAddress(address);

        request.CertificateExtensions.Add(
            san.Build());

        var created = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(5));

        created.FriendlyName =
            "TOR Restaurant Local API";

        store.Add(created);

        var persisted = store.Certificates
            .Find(
                X509FindType.FindByThumbprint,
                created.Thumbprint,
                validOnly: false)
            .OfType<X509Certificate2>()
            .FirstOrDefault(x => x.HasPrivateKey);

        if (persisted is null)
        {
            created.Dispose();
            throw new InvalidOperationException(
                "Lokales HTTPS-Zertifikat konnte nicht dauerhaft gespeichert werden.");
        }

        created.Dispose();
        return persisted;
    }

    private static IEnumerable<IPAddress> GetLocalAddresses()
    {
        IPAddress[] addresses;

        try
        {
            addresses = Dns.GetHostAddresses(
                Dns.GetHostName());
        }
        catch
        {
            yield break;
        }

        foreach (var address in addresses)
        {
            if (IPAddress.IsLoopback(address))
                continue;

            if (address.AddressFamily is
                AddressFamily.InterNetwork or
                AddressFamily.InterNetworkV6)
            {
                yield return address;
            }
        }
    }

    private static string CertificateSha256(
        X509Certificate2 certificate) =>
        certificate.GetCertHashString(
            HashAlgorithmName.SHA256);

    private static bool TryDeviceCredentials(
        HttpContext context,
        out string deviceId,
        out string deviceToken)
    {
        deviceId =
            context.Request.Headers["X-TOR-Device-Id"]
                .ToString()
                .Trim();

        var authorization =
            context.Request.Headers.Authorization
                .ToString()
                .Trim();

        const string prefix = "Bearer ";
        deviceToken =
            authorization.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase)
                ? authorization[prefix.Length..].Trim()
                : "";

        return deviceId.Length > 0 &&
               deviceToken.Length > 0;
    }
#endif

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }

#if TOR_RESTAURANT_PRODUCT
    private sealed record PairRequest(
        string PairingCode,
        string DeviceId,
        string DisplayName);

    private sealed record AddItemRequest(
        string SessionId,
        long ExpectedSessionVersion,
        long ProductId,
        decimal Quantity,
        string OperatorName);
#endif
}
