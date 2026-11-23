using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace NvmeBoost
{
    public static partial class Disks
    {
        const string StorageNs = @"root\Microsoft\Windows\Storage";
        const string CimNs = @"root\cimv2";

        public static List<DiskInfo> Enumerate()
        {
            var list = new List<DiskInfo>();
            bool storageOk = false;
            try
            {
                var scope = new ManagementScope(StorageNs);
                scope.Connect();
                foreach (var pd in Query(scope, "SELECT * FROM MSFT_PhysicalDisk"))
                {
                    try
                    {
                        var d = new DiskInfo();
                        int num;
                        if (!int.TryParse(Str(pd, "DeviceId"), out num)) continue;
                        d.Number = num;
                        d.Model = (Str(pd, "FriendlyName") ?? Str(pd, "Model"))?.Trim();
                        d.Serial = Str(pd, "SerialNumber")?.Trim();
                        d.Firmware = Str(pd, "FirmwareVersion")?.Trim();
                        d.BusType = MapBus(Int(pd, "BusType"));
                        d.MediaType = MapMedia(Int(pd, "MediaType"));
                        d.SizeBytes = ULong(pd, "Size") ?? 0;
                        d.Health = MapHealth(Int(pd, "HealthStatus"));
                        ReadReliability(scope, pd, d);
                        list.Add(d);
                    }
                    catch { }
                }
                storageOk = list.Count > 0;
                if (storageOk)
                {
                    // System/boot flags
                    foreach (var dk in Query(scope, "SELECT Number, IsSystem, IsBoot FROM MSFT_Disk"))
                    {
                        try
                        {
                            var n = Int(dk, "Number");
                            var d = list.FirstOrDefault(x => x.Number == n);
                            if (d != null) d.IsSystemDisk = Bool(dk, "IsSystem") == true || Bool(dk, "IsBoot") == true;
                        }
                        catch { }
                    }
                    // Drive letters
                    foreach (var p in Query(scope, "SELECT DiskNumber, DriveLetter FROM MSFT_Partition"))
                    {
                        try
                        {
                            var n = Int(p, "DiskNumber");
                            var d = list.FirstOrDefault(x => x.Number == n);
                            object dl = p["DriveLetter"];
                            if (d == null || dl == null) continue;
                            char c = dl is char ch ? ch : (char)Convert.ToUInt16(dl);
                            if (char.IsLetter(c)) { var s = char.ToUpperInvariant(c) + ":"; if (!d.DriveLetters.Contains(s)) d.DriveLetters.Add(s); }
                        }
                        catch { }
                    }
                }
            }
            catch { storageOk = false; }

            // PNP instance ids (and fallback data) from Win32_DiskDrive
            List<ManagementBaseObject> w32 = new List<ManagementBaseObject>();
            try { w32 = Query(new ManagementScope(CimNs), "SELECT * FROM Win32_DiskDrive"); } catch { }

            if (!storageOk)
            {
                Ui.Warn("Storage WMI namespace unavailable; using Win32_DiskDrive fallback (limited health info).");
                list.Clear();
                foreach (var w in w32)
                {
                    try
                    {
                        var d = new DiskInfo();
                        var idx = Int(w, "Index"); if (idx == null) continue;
                        d.Number = idx.Value;
                        d.Model = Str(w, "Model")?.Trim();
                        d.Serial = Str(w, "SerialNumber")?.Trim();
                        d.Firmware = Str(w, "FirmwareRevision")?.Trim();
                        d.SizeBytes = ULong(w, "Size") ?? 0;
                        string iface = Str(w, "InterfaceType") ?? "";
                        string model = d.Model ?? "";
                        string pnp = Str(w, "PNPDeviceID") ?? "";
                        bool nvme = Has(model, "NVMe") || Has(pnp, "NVME");
                        d.BusType = nvme ? "NVMe" : iface.Equals("USB", StringComparison.OrdinalIgnoreCase) ? "USB" : iface.Equals("IDE", StringComparison.OrdinalIgnoreCase) ? "SATA" : (string.IsNullOrEmpty(iface) ? "Unknown" : iface);
                        d.MediaType = (nvme || Has(model, "SSD") || Has(iface, "SSD")) ? "SSD" : "Unspecified";
                        string st = Str(w, "Status");
                        d.Health = st == null ? "Unknown" : st.Equals("OK", StringComparison.OrdinalIgnoreCase) ? "Healthy" : "Warning";
                        list.Add(d);
                    }
                    catch { }
                }
                FillLettersFallback(list);
            }

            foreach (var w in w32)
            {
                try
                {
                    var idx = Int(w, "Index");
                    var d = list.FirstOrDefault(x => x.Number == idx);
                    if (d != null) d.PnpInstanceId = Str(w, "PNPDeviceID");
                }
                catch { }
            }

            foreach (var d in list) d.Vendor = GuessVendor(d.Model);
            list.Sort((a, b) => a.Number.CompareTo(b.Number));
            return list;
        }

        static void ReadReliability(ManagementScope scope, ManagementBaseObject pd, DiskInfo d)
        {
            try
            {
                string oid = Str(pd, "ObjectId");
                if (string.IsNullOrEmpty(oid)) return;
                string esc = oid.Replace("\\", "\\\\").Replace("\"", "\\\"");
                string q = "ASSOCIATORS OF {MSFT_PhysicalDisk.ObjectId=\"" + esc + "\"} WHERE AssocClass=MSFT_PhysicalDiskToStorageReliabilityCounter";
                foreach (var rc in Query(scope, q))
                {
                    try
                    {
                        var t = Int(rc, "Temperature"); if (t.HasValue && t.Value > 0 && t.Value < 200) d.TemperatureC = t;
                        var wv = Int(rc, "Wear"); if (wv.HasValue && wv.Value >= 0 && wv.Value <= 255) d.WearPercent = wv;
                        var h = ULong(rc, "PowerOnHours"); if (h.HasValue) d.PowerOnHours = h;
                    }
                    catch { }
                    break;
                }
            }
            catch { }
        }

        static void FillLettersFallback(List<DiskInfo> list)
        {
            try
            {
                var scope = new ManagementScope(CimNs);
                foreach (var d in list)
                {
                    try
                    {
                        foreach (var part in Query(scope, "ASSOCIATORS OF {Win32_DiskDrive.DeviceID='\\\\.\\PHYSICALDRIVE" + d.Number + "'} WHERE AssocClass=Win32_DiskDriveToDiskPartition"))
                            foreach (var ld in Query(scope, "ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + Str(part, "DeviceID") + "'} WHERE AssocClass=Win32_LogicalDiskToPartition"))
                            {
                                var s = Str(ld, "DeviceID");
                                if (!string.IsNullOrEmpty(s) && !d.DriveLetters.Contains(s)) d.DriveLetters.Add(s);
                            }
                        var sysDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").ToUpperInvariant();
                        if (d.DriveLetters.Contains(sysDrive)) d.IsSystemDisk = true;
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static string GuessVendor(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return "Unknown";
            string m = " " + model.ToUpperInvariant() + " ";
            bool Sw(params string[] p) => p.Any(x => model.TrimStart().ToUpperInvariant().StartsWith(x));
            bool C(params string[] p) => p.Any(x => m.Contains(x));
            if (C("SAMSUNG") || Sw("MZV", "MZ-", "MZ")) return "Samsung";
            if (C("LEXAR")) return "Lexar";
            if (C("WDC", "WESTERN DIGITAL") || Sw("WD ", "WDS", "WD_", "WD")) return "Western Digital";
            if (C("CRUCIAL") || Sw("CT")) return "Crucial";
            if (C("SK HYNIX", "SKHYNIX", "HYNIX") || Sw("HFM", "HFS")) return "SK hynix";
            if (C("KIOXIA", "TOSHIBA") || Sw("KXG", "KBG", "THNSN")) return "Kioxia";
            if (C("INTEL") || Sw("SSDPE", "SSDSC")) return "Intel";
            if (C("MICRON") || Sw("MTFD")) return "Micron";
            if (C("KINGSTON")) return "Kingston";
            if (C("SEAGATE") || Sw("ZP", "ST")) return "Seagate";
            if (C("ADATA", "XPG")) return "ADATA";
            if (C("SABRENT")) return "Sabrent";
            if (C("CORSAIR")) return "Corsair";
            if (C("TEAMGROUP", "TEAM ", "T-FORCE", "TFORCE")) return "Teamgroup";
            if (C("PHISON")) return "Phison";
            if (C("SILICON POWER") || Sw("SPCC")) return "Silicon Power";
            if (C("NETAC")) return "Netac";
            if (C("HIKVISION", "HS-SSD")) return "Hikvision";
            if (C("PATRIOT")) return "Patriot";
            if (C("PNY")) return "PNY";
            if (C("TRANSCEND")) return "Transcend";
            return "Unknown";
        }

        static string MapBus(int? b)
        {
            switch (b)
            {
                case 17: return "NVMe"; case 11: return "SATA"; case 7: return "USB";
                case 8: return "RAID"; case 10: return "SAS"; case 3: return "ATA";
                case 1: return "SCSI"; case 12: return "SD"; case 13: return "MMC";
                case 15: return "File Backed Virtual"; case 16: return "Storage Spaces";
                case null: return "Unknown"; default: return "Other(" + b + ")";
            }
        }
        static string MapMedia(int? m) => m == 3 ? "HDD" : m == 4 ? "SSD" : m == 5 ? "SCM" : "Unspecified";
        static string MapHealth(int? h) => h == 0 ? "Healthy" : h == 1 ? "Warning" : h == 2 ? "Unhealthy" : "Unknown";

        static List<ManagementBaseObject> Query(ManagementScope scope, string wql)
        {
            var r = new List<ManagementBaseObject>();
            try
            {
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery(wql)))
                using (var col = s.Get())
                    foreach (var o in col) r.Add(o);
            }
            catch { }
            return r;
        }
        static bool Has(string s, string sub) => s != null && s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;
        static object Get(ManagementBaseObject o, string p) { try { return o[p]; } catch { return null; } }
        static string Str(ManagementBaseObject o, string p) { var v = Get(o, p); return v?.ToString(); }
        static int? Int(ManagementBaseObject o, string p) { var v = Get(o, p); try { return v == null ? (int?)null : Convert.ToInt32(v); } catch { return null; } }
        static ulong? ULong(ManagementBaseObject o, string p) { var v = Get(o, p); try { return v == null ? (ulong?)null : Convert.ToUInt64(v); } catch { return null; } }
        static bool? Bool(ManagementBaseObject o, string p) { var v = Get(o, p); try { return v == null ? (bool?)null : Convert.ToBoolean(v); } catch { return null; } }
    }
}
