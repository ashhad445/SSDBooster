using System;
using System.Diagnostics;

namespace NvmeBoost
{
    public static class Ui
    {
        public static void Header(string s) { var c = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine("\n== " + s + " =="); Console.ForegroundColor = c; }
        public static void Ok(string s) => Color(ConsoleColor.Green, "[ OK ] " + s);
        public static void Warn(string s) => Color(ConsoleColor.Yellow, "[WARN] " + s);
        public static void Err(string s) => Color(ConsoleColor.Red, "[FAIL] " + s);
        public static void Info(string s) => Console.WriteLine("       " + s);
        public static void Color(ConsoleColor col, string s) { var c = Console.ForegroundColor; Console.ForegroundColor = col; Console.WriteLine(s); Console.ForegroundColor = c; }
        public static bool Confirm(string q) { Console.Write(q + " [y/N] "); var r = Console.ReadLine(); return r != null && r.Trim().ToLowerInvariant().StartsWith("y"); }

        /// Run a process, capture stdout+stderr. Returns exit code.
        public static int Run(string exe, string args, out string output)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd(); string e = p.StandardError.ReadToEnd(); p.WaitForExit();
                output = o + e; return p.ExitCode;
            }
        }
    }
}
