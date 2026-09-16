using TorPos.Core;

// R114: closes the update-path finding (G1) from this session's full audit.
//
// TorUpdateService skipped BOTH the pinned-certificate check and the
// Authenticode verification whenever the update server was
// 127.0.0.1/localhost/::1 - a developer convenience that shipped to
// customers. The update server URL is read from app_settings in the SQLite
// file under %APPDATA%, which the ordinary cashier account can write, and the
// staged installer is later launched elevated ("-Verb RunAs"). The only check
// left on that path was SHA-256 against the manifest served by the very same
// server, which verifies nothing.
//
// The decision now lives in TorPos.Core.UpdateTrustPolicy as a pure function,
// specifically so the rule can be asserted here regardless of whether the
// test suite itself is built in Debug or Release.
public static class R114ReviewTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var loopbackIp = new Uri("http://127.0.0.1:8099/");
        var loopbackName = new Uri("http://localhost:8099/");
        var remote = new Uri("https://updates.torpos.example/");

        // The fix: a customer (Release) build enforces signatures everywhere,
        // including loopback.
        assert(
            UpdateTrustPolicy.RequiresSignatureEnforcement(loopbackIp, developmentBuild: false),
            "R114 a production build requires signature verification even for a 127.0.0.1 update server");
        assert(
            UpdateTrustPolicy.RequiresSignatureEnforcement(loopbackName, developmentBuild: false),
            "R114 a production build requires signature verification even for a localhost update server");
        assert(
            UpdateTrustPolicy.RequiresSignatureEnforcement(remote, developmentBuild: false),
            "R114 a production build requires signature verification for a remote update server");

        // The developer convenience still exists, but only in a dev build.
        assert(
            !UpdateTrustPolicy.RequiresSignatureEnforcement(loopbackIp, developmentBuild: true),
            "R114 a development build may still stage an unsigned update from its own loopback server");
        assert(
            UpdateTrustPolicy.RequiresSignatureEnforcement(remote, developmentBuild: true),
            "R114 the development exception never extends to a remote server - only loopback is waived");

        // A host that merely starts with "localhost"/"127.0.0.1" is NOT
        // loopback, so it can never borrow the development exception.
        assert(
            !UpdateTrustPolicy.IsLoopback(new Uri("https://localhost.attacker.example/")) &&
            !UpdateTrustPolicy.IsLoopback(new Uri("https://127.0.0.1.attacker.example/")),
            "R114 a look-alike hostname is not treated as loopback");
        assert(
            UpdateTrustPolicy.RequiresSignatureEnforcement(new Uri("https://localhost.attacker.example/"), developmentBuild: true),
            "R114 even a development build enforces signatures against a look-alike loopback hostname");

        return Task.CompletedTask;
    }
}
