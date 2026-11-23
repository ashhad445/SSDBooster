using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NvmeBoost
{
    /// Vendor-neutral "Rapid Mode alternative" tweaks with backup + revert.
    public static partial class Tweaks
    {
        const string SUB_DISK = "0012ee47-9041-4b5d-9b77-535fba8b1442";
        const string SUB_PCIE = "501a4d13-42af-4429-9fd1-a8218c268e20";
        const string ASPM = "ee12f906-d277-404b-b6da-e5fa1a576df5";

        sealed class PowerSetting
        {
            public string Name, Sub, Guid, Note; public bool Aggressive; public bool Hidden;
            public PowerSetting(string name, string sub, string guid, bool aggressive, bool hidden, string note)
            { Name = name; Sub = sub; Guid = guid; Aggressive = aggressive; Hidden = hidden; Note = note; }
        }

        static readonly PowerSetting[] PowerSettings =
        {
            new PowerSetting("Primary NVMe Idle Timeout (AC)",   SUB_DISK, "d639518a-e56d-4345-8af2-b9f32fb26109", false, true, "0 = no APST idle drop; fixes PM981/Lexar micro-stutter"),
            new PowerSetting("Secondary NVMe Idle Timeout (AC)", SUB_DISK, "d3d55efd-c1ff-424e-9dc3-441be7833010", false, true, "0 = no deep idle transitions"),
            new PowerSetting("AHCI Link Power Mgmt HIPM/DIPM (AC)", SUB_DISK, "0b2d69d7-a2a1-449c-9680-f91c70521c60", false, true, "0 = Active (SATA SSDs)"),
            new PowerSetting("Turn off disk after (AC)",         SUB_DISK, "6738e2c4-e8a5-4a42-b16a-e040e769756e", false, false, "0 = never"),
            new PowerSetting("PCIe ASPM (AC)",                   SUB_PCIE, ASPM,                                   true,  false, "(aggressive) 0 = off, lower latency"),
        };

        static readonly string BackupDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NvmeBoost");
        static readonly string BackupFile = Path.Combine(BackupDir, "backup.txt");
        const string Absent = "<absent>";

        // ---------------------------------------------------------------- public API

        public static List<TweakState> Inspect(DiskInfo disk)
        {
            var rows = new List<TweakState>();

            Guard(() =>
            {
                int? v = ReadDiskDword(disk, "UserWriteCacheSetting");
                rows.Add(new TweakState { Name = "Write caching on device", Current = Fmt(v), Recommended = "1", IsOptimal = v == 1, Note = "Per-disk; reboot required" });
            }, "Inspect write cache", rows);

            Guard(() =>
            {
                int? v = ReadDiskDword(disk, "CacheIsPowerProtected");
                rows.Add(new TweakState { Name = "Turn off write-cache buffer flushing", Current = Fmt(v), Recommended = "1", IsOptimal = v == 1, Note = "(aggressive) Rapid-Mode-like; data-loss risk on power cut" });
            }, "Inspect buffer flushing", rows);

            Guard(() =>
            {
                int? v = FsutilQuery("DisableDeleteNotify");
                rows.Add(new TweakState { Name = "TRIM (NTFS DisableDeleteNotify)", Current = Fmt(v), Recommended = "0", IsOptimal = v == 0, Note = "0 = TRIM enabled" });
            }, "Inspect TRIM", rows);

            Guard(() =>
            {
                int? v = FsutilQuery("disablelastaccess");
                rows.Add(new TweakState { Name = "Disable last-access timestamps", Current = Fmt(v), Recommended = "1", IsOptimal = v == 1 || v == 3, Note = "Fewer metadata writes" });
            }, "Inspect last access", rows);

            Guard(() =>
            {
                int? v = FsutilQuery("memoryusage");
                rows.Add(new TweakState { Name = "NTFS memory usage", Current = v.HasValue ? v.ToString() : "1 (default)", Recommended = "2", IsOptimal = v == 2, Note = "Larger NTFS paged-pool cache" });
            }, "Inspect memoryusage", rows);

            foreach (var ps in PowerSettings)
            {
                var p = ps;
                Guard(() =>
                {
                    int? v = PowerQueryAc("SCHEME_CURRENT", p.Sub, p.Guid);
                    rows.Add(new TweakState { Name = p.Name, Current = v.HasValue ? v.ToString() : "n/a", Recommended = "0", IsOptimal = !v.HasValue || v == 0, Note = p.Note });
                }, "Inspect " + p.Name, rows);
            }

            Guard(() =>
            {
                string start = SysMainStartType(); string state = SysMainState();
                bool ok = start != null && start.StartsWith("AUTO") && state == "RUNNING";
                rows.Add(new TweakState { Name = "SysMain (RAM prefetch cache)", Current = (start ?? "?") + " / " + (state ?? "?"), Recommended = "AUTO / RUNNING", IsOptimal = ok, Note = "Windows' own RAM caching of hot files" });
            }, "Inspect SysMain", rows);

            return rows;
        }

        public static void Apply(DiskInfo disk, bool aggressive, bool dryRun)
        {
            Ui.Header((dryRun ? "[DRY RUN] " : "") + "Applying tweaks to disk " + disk.Number + " (" + disk.Model + ")" + (aggressive ? " [AGGRESSIVE]" : ""));
            var backup = LoadBackup();

            // 1. Write caching
            Step("Enable write caching on device", () => SetDiskDword(disk, "UserWriteCacheSetting", 1, backup, dryRun));

            // 2. Buffer flushing (aggressive)
            if (aggressive)
            {
                Step("Turn off write-cache buffer flushing", () =>
                {
                    Ui.Warn("This is what makes it behave like Samsung Rapid Mode: writes are acknowledged from cache");
                    Ui.Info("without forcing flushes. A sudden power loss / hard crash can lose or corrupt recent data.");
                    Ui.Info("Laptops with a working battery are lower risk; desktops without a UPS are higher risk.");
                    bool laptop = HasBattery();
                    if (disk.IsSystemDisk && !laptop)
                        Ui.Color(ConsoleColor.Red, "[RISK] No battery detected and this is the SYSTEM disk. Use a UPS or skip --aggressive.");
                    SetDiskDword(disk, "CacheIsPowerProtected", 1, backup, dryRun);
                });
            }
            Ui.Info("Per-disk cache changes take effect after a reboot.");

            // 3-5. fsutil
            Step("TRIM enabled", () => SetFsutil("DisableDeleteNotify", 0, backup, dryRun));
            Step("Disable last-access timestamps", () =>
            {
                int? cur = FsutilQuery("disablelastaccess");
                if (cur == 1 || cur == 3) { Ui.Ok("disablelastaccess already " + cur); return; }
                SetFsutil("disablelastaccess", 1, backup, dryRun);
            });
            Step("NTFS memory usage = 2", () => SetFsutil("memoryusage", 2, backup, dryRun));

            // 6. Power (AC only)
            string scheme = null;
            try { scheme = ActiveScheme(); } catch (Exception ex) { Ui.Err("Could not read active power scheme: " + ex.Message); }
            if (scheme != null)
            {
                bool changed = false;
                foreach (var ps in PowerSettings)
                {
                    if (ps.Aggressive && !aggressive) continue;
                    var p = ps;
                    Step(p.Name + " -> 0", () => { if (SetPowerAc(scheme, p, 0, backup, dryRun)) changed = true; });
                }
                if (changed && !dryRun)
                    Step("Re-activate power scheme", () => { Ui.Run("powercfg", "/setactive SCHEME_CURRENT", out _); Ui.Ok("Power scheme re-applied (AC values only; battery values untouched)"); });
            }

            // 7. SysMain
            Step("SysMain service automatic + running", () =>
            {
                string start = SysMainStartType(); string state = SysMainState();
                if (start == null) { Ui.Warn("SysMain service not found"); return; }
                AddBackup(backup, "svc|SysMain|start", start, dryRun);
                AddBackup(backup, "svc|SysMain|state", state ?? "STOPPED", dryRun);
                if (!start.StartsWith("AUTO"))
                {
                    if (dryRun) Ui.Info("Would set SysMain start=auto (was " + start + ")");
                    else { Check(Ui.Run("sc", "config SysMain start= auto", out string o), o); Ui.Ok("SysMain start type -> auto"); }
                }
                if (state != "RUNNING")
                {
                    if (dryRun) Ui.Info("Would start SysMain");
                    else { Ui.Run("sc", "start SysMain", out _); Ui.Ok("SysMain started"); }
                }
                if (start.StartsWith("AUTO") && state == "RUNNING") Ui.Ok("SysMain already automatic and running");
            });

            if (!dryRun) { SaveBackup(backup); Ui.Info("Original values saved to " + BackupFile); }
            Ui.Info("Reboot to activate all changes.");
        }

        public static void Revert(bool dryRun)
        {
            Ui.Header((dryRun ? "[DRY RUN] " : "") + "Reverting tweaks");
            var backup = LoadBackup();
            if (backup.Count == 0) { Ui.Warn("No backup found at " + BackupFile + " - nothing to revert."); return; }

            bool powerChanged = false;
            foreach (var kv in backup.ToList())
            {
                string key = kv.Key, val = kv.Value;
                Step("Restore " + key + " = " + val, () =>
                {
                    var parts = key.Split('|');
                    switch (parts[0])
                    {
                        case "reg":
                            {
                                string path = @"SYSTEM\CurrentControlSet\Enum\" + parts[1] + @"\Device Parameters\Disk";
                                if (dryRun) { Ui.Info("Would restore HKLM\\" + path + "\\" + parts[2] + " -> " + val); return; }
                                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                                using (var k = hklm.CreateSubKey(path, true))
                                {
                                    if (val == Absent) k.DeleteValue(parts[2], false);
                                    else k.SetValue(parts[2], int.Parse(val), RegistryValueKind.DWord);
                                }
                                Ui.Ok(parts[2] + " restored (" + val + ")");
                                break;
                            }
                        case "fsutil":
                            {
                                string target = val == Absent ? (parts[1] == "memoryusage" ? "1" : "0") : val;
                                if (dryRun) { Ui.Info("Would run fsutil behavior set " + parts[1] + " " + target); return; }
                                Check(Ui.Run("fsutil", "behavior set " + parts[1] + " " + target, out string o), o);
                                Ui.Ok("fsutil " + parts[1] + " restored (" + target + ")");
                                break;
                            }
                        case "pwr":
                            {
                                if (dryRun) { Ui.Info("Would set AC " + parts[3] + " on scheme " + parts[1] + " -> " + val); return; }
                                Check(Ui.Run("powercfg", "/setacvalueindex " + parts[1] + " " + parts[2] + " " + parts[3] + " " + val, out string o), o);
                                powerChanged = true;
                                Ui.Ok("Power setting " + parts[3] + " restored (" + val + ")");
                                break;
                            }
                        case "svc":
                            {
                                if (parts[2] == "start")
                                {
                                    string mode = val.StartsWith("AUTO") ? (val.Contains("DELAYED") ? "delayed-auto" : "auto")
                                                : val.StartsWith("DEMAND") ? "demand" : val.StartsWith("DISABLED") ? "disabled" : null;
                                    if (mode == null) { Ui.Warn("Unknown SysMain start type '" + val + "', left as is"); return; }
                                    if (dryRun) { Ui.Info("Would set SysMain start= " + mode); return; }
                                    Check(Ui.Run("sc", "config SysMain start= " + mode, out string o), o);
                                    Ui.Ok("SysMain start type restored (" + mode + ")");
                                }
                                else if (parts[2] == "state" && val != "RUNNING")
                                {
                                    if (dryRun) { Ui.Info("Would stop SysMain"); return; }
                                    Ui.Run("sc", "stop SysMain", out _);
                                    Ui.Ok("SysMain stopped (was not running before)");
                                }
                                break;
                            }
                        default: Ui.Warn("Unknown backup entry: " + key); break;
                    }
                });
            }
            if (powerChanged) Ui.Run("powercfg", "/setactive SCHEME_CURRENT", out _);

            if (!dryRun)
            {
                try { File.Delete(BackupFile); Ui.Ok("Backup file removed"); } catch (Exception ex) { Ui.Warn("Could not delete backup: " + ex.Message); }
                Ui.Info("Reboot to complete the revert.");
            }
        }

        // ---------------------------------------------------------------- registry (per disk)

        static string DiskKeyPath(DiskInfo disk)
        {
            if (string.IsNullOrEmpty(disk.PnpInstanceId)) throw new InvalidOperationException("Disk has no PnP instance ID");
            return @"SYSTEM\CurrentControlSet\Enum\" + disk.PnpInstanceId + @"\Device Parameters\Disk";
        }

        static int? ReadDiskDword(DiskInfo disk, string name)
        {
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k = hklm.OpenSubKey(DiskKeyPath(disk), false))
            {
                object o = k?.GetValue(name);
                if (o == null) return null;
                try { return Convert.ToInt32(o); } catch { return null; }
            }
        }

        static void SetDiskDword(DiskInfo disk, string name, int value, Dictionary<string, string> backup, bool dryRun)
        {
            int? cur = ReadDiskDword(disk, name);
            if (cur == value) { Ui.Ok(name + " already " + value); return; }
            AddBackup(backup, "reg|" + disk.PnpInstanceId + "|" + name, cur.HasValue ? cur.Value.ToString() : Absent, dryRun);
            if (dryRun) { Ui.Info("Would set " + name + " " + Fmt(cur) + " -> " + value); return; }
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k = hklm.CreateSubKey(DiskKeyPath(disk), true))
                k.SetValue(name, value, RegistryValueKind.DWord);
            Ui.Ok(name + " " + Fmt(cur) + " -> " + value);
        }

        // ---------------------------------------------------------------- fsutil

        static int? FsutilQuery(string setting)
        {
            Ui.Run("fsutil", "behavior query " + setting, out string o);
            int? found = null;
            foreach (var line in o.Split('\n'))
            {
                var m = Regex.Match(line, @"(\w+)\s*=\s*(\d+)");
                if (!m.Success) continue;
                // DisableDeleteNotify prints NTFS and ReFS lines; prefer NTFS.
                if (setting.Equals("DisableDeleteNotify", StringComparison.OrdinalIgnoreCase))
                {
                    if (line.IndexOf("NTFS", StringComparison.OrdinalIgnoreCase) >= 0) return int.Parse(m.Groups[2].Value);
                    if (line.IndexOf("ReFS", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                }
                if (found == null) found = int.Parse(m.Groups[2].Value);
            }
            return found;
        }

        static void SetFsutil(string setting, int value, Dictionary<string, string> backup, bool dryRun)
        {
            int? cur = FsutilQuery(setting);
            if (cur == value) { Ui.Ok(setting + " already " + value); return; }
            AddBackup(backup, "fsutil|" + setting, cur.HasValue ? cur.Value.ToString() : Absent, dryRun);
            if (dryRun) { Ui.Info("Would set fsutil " + setting + " " + Fmt(cur) + " -> " + value); return; }
            Check(Ui.Run("fsutil", "behavior set " + setting + " " + value, out string o), o);
            Ui.Ok(setting + " " + Fmt(cur) + " -> " + value);
        }

        // ---------------------------------------------------------------- powercfg

        static string ActiveScheme()
        {
            Ui.Run("powercfg", "/getactivescheme", out string o);
            var m = Regex.Match(o, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            if (!m.Success) throw new InvalidOperationException("powercfg /getactivescheme returned: " + o.Trim());
            return m.Value;
        }

        static int? PowerQueryAc(string scheme, string sub, string setting)
        {
            int rc = Ui.Run("powercfg", "/query " + scheme + " " + sub + " " + setting, out string o);
            if (rc != 0) return null;
            var m = Regex.Match(o, @"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)");
            if (m.Success) return Convert.ToInt32(m.Groups[1].Value, 16);
            // Non-English Windows: the last two 0x######## values are current AC then DC.
            var all = Regex.Matches(o, @":\s*0x([0-9a-fA-F]{8})\s*$", RegexOptions.Multiline);
            if (all.Count >= 2) return Convert.ToInt32(all[all.Count - 2].Groups[1].Value, 16);
            return null;
        }

        static bool SetPowerAc(string scheme, PowerSetting p, int value, Dictionary<string, string> backup, bool dryRun)
        {
            if (p.Hidden && !dryRun) Ui.Run("powercfg", "-attributes " + p.Sub + " " + p.Guid + " -ATTRIB_HIDE", out _);
            int? cur = PowerQueryAc(scheme, p.Sub, p.Guid);
            if (!cur.HasValue) { Ui.Info(p.Name + ": n/a on this system, skipped"); return false; }
            if (cur == value) { Ui.Ok(p.Name + " already " + value); return false; }
            AddBackup(backup, "pwr|" + scheme + "|" + p.Sub + "|" + p.Guid, cur.Value.ToString(), dryRun);
            if (dryRun) { Ui.Info("Would set " + p.Name + " " + cur + " -> " + value); return false; }
            Check(Ui.Run("powercfg", "/setacvalueindex " + scheme + " " + p.Sub + " " + p.Guid + " " + value, out string o), o);
            Ui.Ok(p.Name + " " + cur + " -> " + value);
            return true;
        }

        // ---------------------------------------------------------------- SysMain

        static string SysMainStartType()
        {
            if (Ui.Run("sc", "qc SysMain", out string o) != 0) return null;
            var m = Regex.Match(o, @"START_TYPE\s*:\s*\d+\s+(\w+)(\s+\(DELAYED\))?");
            if (!m.Success) return null;
            return m.Groups[1].Value + (m.Groups[2].Success ? "_DELAYED" : "");
        }

        static string SysMainState()
        {
            Ui.Run("sc", "query SysMain", out string o);
            var m = Regex.Match(o, @"STATE\s*:\s*\d+\s+(\w+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        // ---------------------------------------------------------------- backup

        static Dictionary<string, string> LoadBackup()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(BackupFile)) return d;
                foreach (var raw in File.ReadAllLines(BackupFile))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int i = line.IndexOf('=');
                    if (i > 0 && !d.ContainsKey(line.Substring(0, i))) d[line.Substring(0, i)] = line.Substring(i + 1);
                }
            }
            catch (Exception ex) { Ui.Warn("Could not read backup: " + ex.Message); }
            return d;
        }

        static void SaveBackup(Dictionary<string, string> d)
        {
            try
            {
                Directory.CreateDirectory(BackupDir);
                var lines = new List<string> { "# NvmeBoost original values - used by 'revert'. Do not edit." };
                lines.AddRange(d.Select(kv => kv.Key + "=" + kv.Value));
                File.WriteAllLines(BackupFile, lines);
            }
            catch (Exception ex) { Ui.Err("Could not save backup: " + ex.Message); }
        }

        /// Records the ORIGINAL value only once; re-applying never overwrites it.
        static void AddBackup(Dictionary<string, string> d, string key, string value, bool dryRun)
        {
            if (dryRun || d.ContainsKey(key)) return;
            d[key] = value;
        }

        // ---------------------------------------------------------------- helpers

        static bool HasBattery()
        {
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\cimv2", "SELECT * FROM Win32_Battery"))
                    return s.Get().Count > 0;
            }
            catch { return false; }
        }

        static void Step(string name, Action a)
        {
            try { a(); }
            catch (Exception ex) { Ui.Err(name + ": " + ex.Message); }
        }

        static void Guard(Action a, string what, List<TweakState> rows)
        {
            try { a(); }
            catch (Exception ex) { rows.Add(new TweakState { Name = what, Current = "error", Recommended = "-", IsOptimal = false, Note = ex.Message }); }
        }

        static void Check(int rc, string output)
        {
            if (rc != 0) throw new InvalidOperationException("exit code " + rc + ": " + output.Trim());
        }

        static string Fmt(int? v) => v.HasValue ? v.Value.ToString() : "not set";
    }
}
