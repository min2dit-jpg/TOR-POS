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
            return;

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
