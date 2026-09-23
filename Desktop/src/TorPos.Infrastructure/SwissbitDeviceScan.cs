namespace TorPos.Infrastructure;

/// <summary>
/// Finds a Swissbit TSE by looking at the volumes Windows has mounted.
///
/// This deliberately does not need the WORM API. A hardware TSE is a USB mass
/// storage volume before it is anything else - it carries TSE_COMM.DAT and
/// TSE_INFO.DAT and is usually labelled SWISSBIT - so the stick can be
/// recognised before, and without, the licensed SDK.
///
/// That matters for the one moment it is most needed: an operator who has just
/// plugged in a brand new TSE used to be told only that the SDK was missing,
/// with nothing to say their device had arrived at all. It reads as "your
/// hardware is not there" when it plainly is.
/// </summary>
public static class SwissbitDeviceScan
{
    public static IReadOnlyList<string> FindMountPoints()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<string>();

        var result = new List<string>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                    continue;

                var root = drive.RootDirectory.FullName;

                var hasCommunicationFile =
                    File.Exists(Path.Combine(root, "TSE_COMM.DAT"));

                var hasInfoFile =
                    File.Exists(Path.Combine(root, "TSE_INFO.DAT"));

                var swissbitLabel =
                    string.Equals(
                        drive.VolumeLabel,
                        "SWISSBIT",
                        StringComparison.OrdinalIgnoreCase);

                if (hasCommunicationFile || hasInfoFile || swissbitLabel)
                {
                    // Swissbit's Windows examples address the mount point.
                    result.Add(root.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar));
                }
            }
            catch
            {
                // A stick pulled out mid-scan, or a volume Windows will not let
                // us read, is simply not a candidate. Never a crash: this runs
                // on the start-up path of a till.
            }
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
