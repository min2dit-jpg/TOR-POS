namespace TorPos.Core;

public static class TorRelease
{
    public const string Product = "TOR POS Pro";
    // From R145 on a release carries a name chosen by the owner. The numeric
    // Version stays for what needs digits: installer, file version, update
    // comparison and the DSFinV-K software version.
    public const string ReleaseName = "Merd-M";
    public const string Version = "0.7.33.877";
    public const string Revision = "R177";
    public const string UserAgentVersion = "0.7.33-R177";

    public static string DisplayName => $"{Product} {ReleaseName}";

    // Set this to the TOR/Demirkaan GmbH production code-signing certificate thumbprint
    // before enabling remote automatic updates. Empty intentionally blocks remote installs.
    public const string UpdateSignerThumbprint = "";
}

/// <summary>
/// R114: decides whether a staged update must pass the pinned-certificate and
/// Authenticode checks.
///
/// Before this, TorUpdateService skipped BOTH checks whenever the update
/// server was 127.0.0.1/localhost/::1 - a developer convenience that also
/// shipped to customers. The update server URL lives in app_settings inside
/// the SQLite file under %APPDATA%, which the ordinary cashier account can
/// write, so anyone able to set that value and run a local listener could get
/// an arbitrary installer staged and then launched elevated ("-Verb RunAs").
/// The remaining SHA-256 check was no control at all: the hash came from the
/// same server as the file.
///
/// The exception now exists only in development builds. Kept as a pure
/// function in Core so the rule itself is testable regardless of the build
/// configuration the test suite happens to run under.
/// </summary>
public static class UpdateTrustPolicy
{
    public static bool IsLoopback(Uri server) =>
        server.Host is "127.0.0.1" or "localhost" or "::1";

    /// <summary>
    /// True when the pinned signer + Authenticode verification must run.
    /// A production build always requires it, including on loopback.
    /// </summary>
    public static bool RequiresSignatureEnforcement(Uri server, bool developmentBuild) =>
        !developmentBuild || !IsLoopback(server);
}
