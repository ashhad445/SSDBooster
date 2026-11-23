using System;
using System.Collections.Generic;
using System.Linq;

namespace NvmeBoost
{
    public static class Program
    {
        const string Usage = @"NvmeBoost - vendor-neutral 'Rapid Mode' alternative for NVMe/SATA SSDs
(Samsung OEM e.g. MZVLB512HAJQ-000H1 / PM981, Lexar, WD, Crucial, SK hynix, Kioxia, ...)

Usage: NvmeBoost.exe                         interactive menu
       NvmeBoost.exe list                    list disks with health/temp/wear
       NvmeBoost.exe status  [--disk N]      show current vs recommended tweaks
       NvmeBoost.exe apply   [--disk N] [--aggressive] [--dry-run] [--yes]
       NvmeBoost.exe revert  [--dry-run]     restore original settings from backup
       NvmeBoost.exe preload [--budget MB] [--max-file MB] [--quiet] [paths...]
       NvmeBoost.exe autopreload [--budget MB] [paths...]   run preload at every logon
       NvmeBoost.exe autopreload --remove
       NvmeBoost.exe bench   [--drive C]     run winsat disk benchmark

--aggressive also turns off Windows write-cache buffer flushing (the closest equivalent
to Samsung Rapid Mode's write-back RAM cache) and PCIe ASPM. Risk: data loss on sudden
power loss. Settings take effect after a reboot.";

        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0) return Menu();
                string cmd = args[0].ToLowerInvariant();
                var rest = args.Skip(1).ToList();
                switch (cmd)
                {
                    case "list": ListDisks(); return 0;
                    case "status": Status(PickDisk(rest, false)); return 0;
                    case "apply":
                    {
                        var d = PickDisk(rest, false); if (d == null) return 1;
                        bool aggr = Flag(rest, "--aggressive"), dry = Flag(rest, "--dry-run"), yes = Flag(rest, "--yes");
                        if (!dry && !yes && !Ui.Confirm($"Apply {(aggr ? "AGGRESSIVE" : "standard")} tweaks to disk {d.Number} ({d.Model})?")) return 1;
                        Tweaks.Apply(d, aggr, dry); if (!dry) Ui.Warn("Reboot to activate write-cache changes."); return 0;
                    }
                    case "revert": Tweaks.Revert(Flag(rest, "--dry-run")); return 0;
                    case "preload":
                        Cache.Quiet = Flag(rest, "--quiet");
                        Cache.Preload(Paths(rest), Opt(rest, "--budget", 2048), Opt(rest, "--max-file", 256)); return 0;
                    case "autopreload":
                        if (Flag(rest, "--remove")) Cache.RemovePreloadTask();
                        else Cache.InstallPreloadTask(Paths(rest), Opt(rest, "--budget", 2048));
                        return 0;
                    case "bench":
                    {
                        string drv = OptS(rest, "--drive") ?? Environment.GetEnvironmentVariable("SystemDrive") ?? "C";
                        Cache.Benchmark(drv.TrimEnd(':', '\\')); return 0;
                    }
                    case "-h": case "--help": case "help": case "/?": Console.WriteLine(Usage); return 0;
                    default: Console.WriteLine(Usage); return 2;
                }
            }
            catch (Exception ex) { Ui.Err(ex.Message); return 1; }
        }

        static int Menu()
        {
            Console.Title = "NvmeBoost";
            while (true)
            {
                Ui.Header("NvmeBoost - SSD Rapid Mode alternative");
                Console.WriteLine(" 1) List disks / health\n 2) Status of tweaks\n 3) Apply standard tweaks (safe)\n 4) Apply AGGRESSIVE tweaks (Rapid-Mode-like write-back)\n 5) Revert all changes\n 6) Preload apps into RAM cache now\n 7) Enable preload at every logon\n 8) Disable preload at logon\n 9) Benchmark (winsat)\n 0) Exit");
                Console.Write("> ");
                string k = (Console.ReadLine() ?? "0").Trim();
                try
                {
                    switch (k)
                    {
                        case "1": ListDisks(); break;
                        case "2": Status(PickDisk(new List<string>(), true)); break;
                        case "3": case "4":
                        {
                            var d = PickDisk(new List<string>(), true); if (d == null) break;
                            bool aggr = k == "4";
                            Tweaks.Apply(d, aggr, true);
                            if (Ui.Confirm("Apply these changes?")) { Tweaks.Apply(d, aggr, false); Ui.Warn("Reboot to activate write-cache changes."); }
                            break;
                        }
                        case "5": if (Ui.Confirm("Restore original settings?")) Tweaks.Revert(false); break;
                        case "6": Cache.Preload(AskPaths(), AskInt("RAM budget MB", 2048), 256); break;
                        case "7": Cache.InstallPreloadTask(AskPaths(), AskInt("RAM budget MB", 2048)); break;
                        case "8": Cache.RemovePreloadTask(); break;
                        case "9":
                        {
                            Console.Write("Drive letter [C]: ");
                            string s = (Console.ReadLine() ?? "").Trim().TrimEnd(':', '\\');
                            Cache.Benchmark(s.Length > 0 ? s : "C"); break;
                        }
                        case "0": case "q": return 0;
                    }
                }
                catch (Exception ex) { Ui.Err(ex.Message); }
                Console.Write("\nPress Enter..."); Console.ReadLine();
            }
        }

        static void ListDisks()
        {
            Ui.Header("Disks");
            foreach (var d in Disks.Enumerate())
            {
                Ui.Color(d.IsSsd ? ConsoleColor.White : ConsoleColor.Gray,
                    $" [{d.Number}] {d.Model}  ({d.Vendor}, {d.BusType}/{d.MediaType}, {d.SizeBytes / 1_000_000_000UL} GB){(d.IsSystemDisk ? "  [SYSTEM]" : "")}");
                Ui.Info($"FW {d.Firmware ?? "?"} | Health {d.Health ?? "?"} | Temp {Fmt(d.TemperatureC, "C")} | Wear {Fmt(d.WearPercent, "%")} | Power-on {(d.PowerOnHours?.ToString() ?? "?")} h | {string.Join(" ", d.DriveLetters.Select(x => x + ":"))}");
                if (d.Vendor == "Samsung" && d.Model != null && d.Model.Contains("-000"))
                    Ui.Info("OEM Samsung drive: Magician/Rapid Mode unsupported; firmware updates come from the laptop maker (HP/Lenovo/Dell).");
            }
        }

        static void Status(DiskInfo d)
        {
            if (d == null) return;
            Ui.Header($"Tweaks for disk {d.Number}: {d.Model}");
            foreach (var t in Tweaks.Inspect(d))
            {
                var line = $"{t.Name,-42} now: {t.Current,-12} want: {t.Recommended,-10} {t.Note}";
                if (t.IsOptimal) Ui.Ok(line); else Ui.Warn(line);
            }
        }

        static DiskInfo PickDisk(List<string> rest, bool interactive)
        {
            var disks = Disks.Enumerate();
            if (disks.Count == 0) { Ui.Err("No disks found."); return null; }
            int n = Opt(rest, "--disk", -1);
            if (n < 0 && interactive && disks.Count > 1)
            {
                foreach (var x in disks) Console.WriteLine($" [{x.Number}] {x.Model}{(x.IsSystemDisk ? " [SYSTEM]" : "")}");
                n = AskInt("Disk number", (disks.FirstOrDefault(x => x.IsSystemDisk) ?? disks[0]).Number);
            }
            var d = n >= 0 ? disks.FirstOrDefault(x => x.Number == n) : (disks.FirstOrDefault(x => x.IsSystemDisk && x.IsSsd) ?? disks.FirstOrDefault(x => x.IsSsd) ?? disks[0]);
            if (d == null) Ui.Err($"Disk {n} not found."); else if (!d.IsSsd) Ui.Warn($"Disk {d.Number} does not look like an SSD.");
            return d;
        }

        static string Fmt(int? v, string unit) => v.HasValue ? v + unit : "?";
        static bool Flag(List<string> a, string f) { bool r = a.Any(x => x.Equals(f, StringComparison.OrdinalIgnoreCase)); a.RemoveAll(x => x.Equals(f, StringComparison.OrdinalIgnoreCase)); return r; }
        static string OptS(List<string> a, string f)
        {
            int i = a.FindIndex(x => x.Equals(f, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= a.Count) return null;
            string v = a[i + 1]; a.RemoveRange(i, 2); return v;
        }
        static int Opt(List<string> a, string f, int def) => int.TryParse(OptS(a, f), out int v) ? v : def;
        static List<string> Paths(List<string> a) => a.Where(x => !x.StartsWith("--")).ToList();
        static int AskInt(string q, int def) { Console.Write($"{q} [{def}]: "); return int.TryParse(Console.ReadLine(), out int v) ? v : def; }
        static List<string> AskPaths()
        {
            Console.Write("Folders to preload (separate with ';', empty = defaults): ");
            return (Console.ReadLine() ?? "").Split(';').Select(s => s.Trim().Trim('"')).Where(s => s.Length > 0).ToList();
        }
    }
}
