// Shared contracts. Each module is implemented in its own file by a separate author.
// Do NOT change signatures here without coordinating.
using System.Collections.Generic;

namespace NvmeBoost
{
    /// One physical disk as seen by Windows (any vendor: Samsung OEM, Lexar, WD, Crucial, SK hynix, ...).
    public sealed class DiskInfo
    {
        public int Number;              // PhysicalDrive number (MSFT_Disk.Number / Win32_DiskDrive.Index)
        public string Model;            // e.g. "SAMSUNG MZVLB512HAJQ-000H1", "Lexar SSD NM790 1TB"
        public string Vendor;           // best-effort brand guess from model string ("Samsung", "Lexar", ...)
        public string Serial;
        public string Firmware;
        public string BusType;          // "NVMe", "SATA", "USB", ...
        public string MediaType;        // "SSD", "HDD", "Unspecified"
        public ulong SizeBytes;
        public string PnpInstanceId;    // e.g. "SCSI\\DISK&VEN_NVME&PROD_SAMSUNG_MZVLB512\\5&...&0&000000"
        public string Health;           // "Healthy"/"Warning"/"Unhealthy"/"Unknown"
        public int? TemperatureC;
        public int? WearPercent;        // percentage used (0 = new), if reported
        public ulong? PowerOnHours;
        public List<string> DriveLetters = new List<string>();
        public bool IsSystemDisk;
        public bool IsSsd => MediaType == "SSD" || BusType == "NVMe";
    }

    /// Disk enumeration + health (DiskInfo.cs)
    public static partial class Disks
    {
        // public static List<DiskInfo> Enumerate();
    }

    /// One tweak row for status display.
    public sealed class TweakState
    {
        public string Name;
        public string Current;
        public string Recommended;
        public bool IsOptimal;
        public string Note;
    }

    /// System/disk tweaks with backup + revert (Tweaks.cs)
    public static partial class Tweaks
    {
        // public static List<TweakState> Inspect(DiskInfo disk);
        // public static void Apply(DiskInfo disk, bool aggressive, bool dryRun);   // backs up first
        // public static void Revert(bool dryRun);                                  // restores from backup
    }

    /// RAM cache warming + benchmark (Cache.cs)
    public static partial class Cache
    {
        // public static void Preload(IEnumerable<string> paths, int budgetMB, int maxFileMB);
        // public static void InstallPreloadTask(IEnumerable<string> paths, int budgetMB);  // scheduled task at logon
        // public static void RemovePreloadTask();
        // public static void Benchmark(string driveLetter);                       // wraps winsat disk
    }

    /// Console helpers shared by all modules (Ui.cs, provided)
}
