using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace NvmeBoost
{
    public static partial class Cache
    {
        public static bool Quiet;
        public const string TaskName = "NvmeBoost Preload";

        static readonly string[] HotExt = { ".exe", ".dll", ".pak", ".asar", ".dat", ".bin", ".so", ".node", ".jar" };

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        static ulong AvailablePhysMB()
        {
            var m = new MEMORYSTATUSEX();
            return GlobalMemoryStatusEx(m) ? m.ullAvailPhys / (1024 * 1024) : 0;
        }

        public static List<string> DefaultPaths()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
            var cands = new[]
            {
                Path.Combine(local, "Programs"),
                Path.Combine(pf, @"Google\Chrome"),
                Path.Combine(pf, "Mozilla Firefox"),
                Path.Combine(pf, @"Microsoft Office\root"),
            };
            return cands.Where(Directory.Exists).ToList();
        }

        static void Log(string s) { if (!Quiet) Console.WriteLine(s); }

        static IEnumerable<FileInfo> Walk(string root)
        {
            var stack = new Stack<DirectoryInfo>();
            if (File.Exists(root)) { yield return new FileInfo(root); yield break; }
            if (!Directory.Exists(root)) yield break;
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                FileSystemInfo[] entries;
                try { entries = d.GetFileSystemInfos(); }
                catch (UnauthorizedAccessException) { continue; }
                catch (SecurityException) { continue; }
                catch (IOException) { continue; }
                foreach (var e in entries)
                {
                    FileAttributes a;
                    try { a = e.Attributes; } catch { continue; }
                    if ((a & FileAttributes.ReparsePoint) != 0) continue;
                    if ((a & FileAttributes.Directory) != 0) stack.Push((DirectoryInfo)e);
                    else yield return (FileInfo)e;
                }
            }
        }

        public static void Preload(IEnumerable<string> paths, int budgetMB, int maxFileMB)
        {
            var roots = (paths ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (roots.Count == 0)
            {
                roots = DefaultPaths();
                if (roots.Count == 0) { if (!Quiet) Ui.Warn("No paths given and no default app folders found."); return; }
                if (!Quiet) { Ui.Info("Using default folders:"); foreach (var r in roots) Ui.Info("  " + r); }
            }

            ulong avail = AvailablePhysMB();
            long budget = budgetMB <= 0 ? long.MaxValue : budgetMB;
            if (avail > 0) budget = Math.Min(budget, (long)(avail / 2));
            if (budget <= 0) { if (!Quiet) Ui.Warn("Not enough free RAM to preload."); return; }
            long budgetBytes = budget * 1024L * 1024L;
            long maxFile = (maxFileMB <= 0 ? 512 : maxFileMB) * 1024L * 1024L;

            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

            if (!Quiet) Ui.Header($"Preloading into RAM cache (budget {budget} MB, free RAM {avail} MB)");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<FileInfo>();
            foreach (var r in roots)
                foreach (var f in Walk(r))
                {
                    try { if (f.Length == 0 || f.Length > maxFile) continue; } catch { continue; }
                    if (seen.Add(f.FullName)) files.Add(f);
                }

            DateTime Recency(FileInfo f) { try { return f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc; } catch { return DateTime.MinValue; } }
            var ordered = files
                .OrderBy(f => HotExt.Contains(f.Extension.ToLowerInvariant()) ? 0 : 1)
                .ThenByDescending(Recency)
                .ToList();

            var buf = new byte[1 << 20];
            long done = 0; int count = 0, skipped = 0;
            var sw = Stopwatch.StartNew();
            long lastTick = 0;
            foreach (var f in ordered)
            {
                long len;
                try { len = f.Length; } catch { skipped++; continue; }
                if (done + len > budgetBytes) continue; // try smaller files that still fit
                try
                {
                    using (var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan))
                    {
                        int n; while ((n = fs.Read(buf, 0, buf.Length)) > 0) done += n;
                    }
                    count++;
                }
                catch { skipped++; }

                if (!Quiet && sw.ElapsedMilliseconds - lastTick > 250)
                {
                    lastTick = sw.ElapsedMilliseconds;
                    double mb = done / 1048576.0;
                    Console.Write($"\r       {count} files, {mb:F0} / {budget} MB, {mb / Math.Max(sw.Elapsed.TotalSeconds, 0.001):F0} MB/s   ");
                }
                if (done >= budgetBytes) break;
            }
            sw.Stop();
            if (!Quiet)
            {
                double mb = done / 1048576.0;
                Console.WriteLine();
                Ui.Ok($"Cached {count} files ({mb:F0} MB) in {sw.Elapsed.TotalSeconds:F1}s ({mb / Math.Max(sw.Elapsed.TotalSeconds, 0.001):F0} MB/s), {skipped} skipped.");
                Ui.Info("Files now sit in the Windows standby cache; Windows evicts them automatically when apps need RAM.");
            }
        }

        static string XmlEsc(string s) => System.Security.SecurityElement.Escape(s);

        public static void InstallPreloadTask(IEnumerable<string> paths, int budgetMB)
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            var sb = new StringBuilder($"preload --budget {budgetMB} --quiet");
            foreach (var p in paths ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(p)) sb.Append(" \"").Append(p.TrimEnd('\\')).Append('"');

            string xml =
$@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>NvmeBoost: warm the Windows file cache with frequently used apps after logon.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><Delay>PT60S</Delay></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><GroupId>S-1-5-32-545</GroupId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT30M</ExecutionTimeLimit>
    <Priority>7</Priority>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{XmlEsc(exe)}</Command><Arguments>{XmlEsc(sb.ToString())}</Arguments></Exec></Actions>
</Task>";
            string tmp = Path.Combine(Path.GetTempPath(), "NvmeBoostTask.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            int rc = Ui.Run("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F", out string o);
            try { File.Delete(tmp); } catch { }
            if (rc == 0) { Ui.Ok($"Scheduled task \"{TaskName}\" installed (runs 60s after logon)."); Ui.Info("Command: " + exe + " " + sb); }
            else { Ui.Err("schtasks failed: " + o.Trim()); }
        }

        public static void RemovePreloadTask()
        {
            int rc = Ui.Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F", out string o);
            if (rc == 0) Ui.Ok($"Scheduled task \"{TaskName}\" removed.");
            else Ui.Warn("Could not remove task (maybe not installed): " + o.Trim());
        }

        static readonly Regex WinsatLine = new Regex(@"^>\s*(Disk\s+.+?)\s{2,}([\d.,]+\s*(?:MB/s|ms))\s*([\d.,]+)?\s*$", RegexOptions.Compiled);

        public static void Benchmark(string driveLetter)
        {
            string d = (driveLetter ?? "C").Trim().TrimEnd('\\').TrimEnd(':');
            if (d.Length == 0) d = "C";
            Ui.Header($"Benchmark drive {d}: (winsat disk) - takes about a minute");
            int rc; string o;
            try { rc = Ui.Run("winsat.exe", $"disk -drive {d}", out o); }
            catch (Exception ex) { rc = -1; o = ex.Message; }

            var rows = new List<string[]>();
            foreach (var raw in o.Split('\n'))
            {
                var m = WinsatLine.Match(raw.Trim());
                if (m.Success) rows.Add(new[] { Regex.Replace(m.Groups[1].Value, @"\s+", " "), m.Groups[2].Value, m.Groups[3].Value });
            }

            if (rc != 0 || rows.Count == 0)
            {
                Ui.Err("winsat failed or produced no results (needs admin; not present on some Windows editions).");
                Console.WriteLine(o);
                Ui.Info("Alternative: Microsoft DiskSpd - https://github.com/microsoft/diskspd");
                return;
            }

            int w = Math.Max(30, rows.Max(r => r[0].Length) + 2);
            Console.WriteLine("       " + "Test".PadRight(w) + "Result".PadRight(16) + "Score");
            Console.WriteLine("       " + new string('-', w + 22));
            foreach (var r in rows) Console.WriteLine("       " + r[0].PadRight(w) + r[1].PadRight(16) + r[2]);
            Ui.Info("Tip: run this before and after 'apply' (and a reboot) to compare.");
        }
    }
}
