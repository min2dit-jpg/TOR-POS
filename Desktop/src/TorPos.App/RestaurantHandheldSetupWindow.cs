using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public sealed class RestaurantHandheldSetupWindow : Window
{
    private readonly RestaurantHandheldPairingService _pairing;
    private readonly AuthenticatedUser _user;

    private readonly TextBlock _pairingCode = new()
    {
        FontSize = 32,
        FontWeight = FontWeight.Bold,
        Text = "------"
    };

    private readonly TextBlock _pairingInfo = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.75
    };

    private readonly ListBox _devices = new()
    {
        MinHeight = 260
    };

    private readonly Button _deactivate = new()
    {
        Content = "GERÄT DEAKTIVIEREN",
        MinHeight = 44,
        IsEnabled = false
    };

    public RestaurantHandheldSetupWindow(
        RestaurantHandheldPairingService pairing,
        AuthenticatedUser user)
    {
        _pairing = pairing;
        _user = user;

        Title = "TOR Restaurant Plus · Handheld";
        Width = 760;
        Height = 620;
        MinWidth = 680;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0D1420"));

        _devices.ItemTemplate =
            new Avalonia.Controls.Templates.FuncDataTemplate<RestaurantHandheldDeviceInfo>(
                (device, _) => new StackPanel
                {
                    Spacing = 3,
                    Margin = new Thickness(6),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = device is null
                                ? ""
                                : $"{device.DisplayName} · {device.DeviceId}",
                            FontSize = 16,
                            FontWeight = FontWeight.Bold
                        },
                        new TextBlock
                        {
                            Text = device is null
                                ? ""
                                : $"Status: {(device.IsActive ? "AKTIV" : "DEAKTIVIERT")} · " +
                                  $"Zuletzt gesehen: {device.LastSeenAt.ToLocalTime():dd.MM.yyyy HH:mm:ss}",
                            Opacity = 0.72
                        }
                    }
                });

        _devices.SelectionChanged += (_, _) =>
        {
            _deactivate.IsEnabled =
                _devices.SelectedItem is RestaurantHandheldDeviceInfo d &&
                d.IsActive;
        };

        var createCode = new Button
        {
            Content = "NEUEN PAIRING-CODE ERZEUGEN",
            MinHeight = 46,
            FontWeight = FontWeight.Bold
        };
        createCode.Click += async (_, _) =>
            await CreateCodeAsync();

        var refresh = new Button
        {
            Content = "GERÄTELISTE AKTUALISIEREN",
            MinHeight = 42
        };
        refresh.Click += async (_, _) =>
            await ReloadAsync();

        _deactivate.Click += async (_, _) =>
            await DeactivateSelectedAsync();

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "HANDHELD-VERBINDUNG",
                        FontSize = 26,
                        FontWeight = FontWeight.Bold
                    },
                    new TextBlock
                    {
                        Text =
                            "Nur TOR Restaurant Plus. Der Pairing-Code ist einmalig und läuft automatisch ab. " +
                            "Handhelds greifen niemals direkt auf die lokale SQLite-Datenbank zu.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.76
                    },
                    new Border
                    {
                        Padding = new Thickness(16),
                        Background = new SolidColorBrush(Color.Parse("#101925")),
                        CornerRadius = new CornerRadius(10),
                        Child = new StackPanel
                        {
                            Spacing = 10,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = "PAIRING-CODE",
                                    FontWeight = FontWeight.Bold
                                },
                                _pairingCode,
                                _pairingInfo,
                                createCode
                            }
                        }
                    },
                    new TextBlock
                    {
                        Text = "VERBUNDENE GERÄTE",
                        FontSize = 18,
                        FontWeight = FontWeight.Bold
                    },
                    _devices,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { refresh, _deactivate }
                    }
                }
            }
        };

        Opened += async (_, _) =>
            await ReloadAsync();
    }

    private async Task CreateCodeAsync()
    {
        try
        {
            var code = await _pairing.CreatePairingCodeAsync(
                _user.Username,
                TimeSpan.FromMinutes(10));

            _pairingCode.Text = code.Code;
            _pairingInfo.Text =
                $"Gültig bis {code.ExpiresAt.ToLocalTime():dd.MM.yyyy HH:mm:ss}. " +
                "Nach erfolgreicher Verbindung kann derselbe Code nicht erneut verwendet werden.";
        }
        catch (Exception ex)
        {
            _pairingCode.Text = "------";
            _pairingInfo.Text = ex.Message;
        }
    }

    private async Task ReloadAsync()
    {
        try
        {
            _devices.ItemsSource =
                await _pairing.ListDevicesAsync();

            _deactivate.IsEnabled = false;
        }
        catch (Exception ex)
        {
            _pairingInfo.Text = ex.Message;
        }
    }

    private async Task DeactivateSelectedAsync()
    {
        if (_devices.SelectedItem is not RestaurantHandheldDeviceInfo device ||
            !device.IsActive)
        {
            return;
        }

        await _pairing.DeactivateAsync(
            device.DeviceId);

        await ReloadAsync();
    }
}
