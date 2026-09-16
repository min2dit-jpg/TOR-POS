using TorPos.Core;

// R116: closes finding İ3 (Medium) from this session's full project audit.
//
// MainWindow's sale gate was inverted. It read:
//
//     if (IsUnlicensedDevelopmentTestMode()) return true;   // no licence -> allowed
//     if (license.IsActive && FiscalRelease.Enabled && ProductionAllowed) return true;
//     ScannerStatus.Text = "KASSIEREN GESPERRT ...";
//
// Since FiscalRelease.Enabled is hard-coded false, the second line can never
// be true today - so a LICENSED till was blocked outright while an UNLICENSED
// one was let through as a "development test mode". A paying customer could
// not even try their own register.
//
// The rule now lives in TorPos.Core.SaleModePolicy: a real fiscal sale still
// requires the licence AND the fiscal release AND full readiness; everything
// else falls back to a clearly marked simulation instead of a refusal.
public static class R116ReviewTests
{
    public static Task Run(Action<bool, string> assert)
    {
        // The case that was backwards: licensed, but the fiscal release gate
        // is still closed (today's reality for every customer).
        assert(
            !SaleModePolicy.CanCommitProductionSale(isTraining: false, licenseActive: true, fiscalReleaseEnabled: false, productionAllowed: true),
            "R116 a licensed till with the fiscal release still closed cannot book a production sale");
        assert(
            SaleModePolicy.IsSimulation(isTraining: false, licenseActive: true, fiscalReleaseEnabled: false, productionAllowed: true),
            "R116 that licensed till sells in simulation mode instead of being refused - this is the inversion that was fixed");

        // An unlicensed till must not be MORE capable than a licensed one.
        assert(
            SaleModePolicy.IsSimulation(isTraining: false, licenseActive: false, fiscalReleaseEnabled: false, productionAllowed: true),
            "R116 an unlicensed till is also simulation-only");
        assert(
            !SaleModePolicy.CanCommitProductionSale(isTraining: false, licenseActive: false, fiscalReleaseEnabled: true, productionAllowed: true),
            "R116 the licence still gates real sales - an unlicensed till can never book one, even with everything else green");

        // Training is always a simulation, whatever else is true.
        assert(
            SaleModePolicy.IsSimulation(isTraining: true, licenseActive: true, fiscalReleaseEnabled: true, productionAllowed: true),
            "R116 TRAINING is always a simulation, even on a fully released and licensed till");

        // Fiscal readiness is not optional either.
        assert(
            !SaleModePolicy.CanCommitProductionSale(isTraining: false, licenseActive: true, fiscalReleaseEnabled: true, productionAllowed: false),
            "R116 an unready fiscal state blocks a production sale even with a licence and the release gate open");

        // The one combination that books for real.
        assert(
            SaleModePolicy.CanCommitProductionSale(isTraining: false, licenseActive: true, fiscalReleaseEnabled: true, productionAllowed: true) &&
            !SaleModePolicy.IsSimulation(isTraining: false, licenseActive: true, fiscalReleaseEnabled: true, productionAllowed: true),
            "R116 exactly one combination books a real fiscal sale: licensed, released, ready and not training");

        return Task.CompletedTask;
    }
}
