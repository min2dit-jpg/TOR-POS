namespace TorPos.App;

public static class ProductBuild
{
#if TOR_KIOSK_PRODUCT
    public const string? FixedEdition = "KIOSK";
    public const string ProductName = "TOR KIOSK";
    public const string DataDirectoryName = "TOR-KIOSK";
    public const string RunningMutexName = "TOR-KIOSK-Running";
#elif TOR_DOENER_PRODUCT
    public const string? FixedEdition = "IMBISS";
    public const string ProductName = "TOR DÖNER";
    public const string DataDirectoryName = "TOR-DOENER";
    public const string RunningMutexName = "TOR-DOENER-Running";
#else
    // Legacy/shared build remains available while the split is being qualified.
    public const string? FixedEdition = null;
    public const string ProductName = "TOR POS Pro";
    public const string DataDirectoryName = "TOR-POS-Pro";
    public const string RunningMutexName = "TOR-POS-Pro-Running";
#endif

    public static bool IsSplitProduct => FixedEdition is not null;

    public static void ConfigureEnvironment()
    {
        if (FixedEdition is null)
        {
            // R182: the shared build must never inherit a product identity from the
            // environment it was started in. Without this an externally set
            // TOR_POS_PRODUCT_EDITION would redirect AppPaths - including the
            // machine-wide trial/licence identity - into a split product's roots.
            Environment.SetEnvironmentVariable(
                "TOR_POS_PRODUCT_EDITION", null, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(
                "TOR_POS_PRODUCT_NAME", null, EnvironmentVariableTarget.Process);
            return;
        }

        Environment.SetEnvironmentVariable(
            "TOR_POS_PRODUCT_EDITION",
            FixedEdition,
            EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable(
            "TOR_POS_PRODUCT_NAME",
            ProductName,
            EnvironmentVariableTarget.Process);
    }
}
