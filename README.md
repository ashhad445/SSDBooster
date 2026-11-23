# NvmeBoost: a Rapid Mode alternative for any SSD

Samsung Magician blocks Rapid Mode on OEM drives such as the **SAMSUNG MZVLB512HAJQ-000H1 (PM981)**. Rapid Mode also never supported NVMe drives. NvmeBoost works on any brand (Samsung OEM, Lexar, WD, Crucial, SK hynix, Kioxia, and others), because it only changes Windows settings. It never talks to the drive's firmware.

Requires Windows 10 or 11. No install is needed, since .NET Framework 4.8 is built in. Run it as Administrator (it asks for elevation itself).

Double-click `NvmeBoost.exe` to get a menu, or use the command line:

| Command | What it does |
|---|---|
| `list` | Shows model, brand, firmware, health, temperature, wear and power-on hours |
| `status` | Shows each setting's current value next to the recommended one |
| `apply` | Safe tweaks: device write caching, TRIM, no last-access writes, larger NTFS cache, NVMe power saving (APST) off when plugged in, SysMain on |
| `apply --aggressive` | Adds **"turn off write-cache buffer flushing"** (the closest match to Rapid Mode's write-back RAM cache) and PCIe ASPM off |
| `revert` | Restores every original value from `%ProgramData%\NvmeBoost\backup.txt` |
| `preload [paths]` | Reads your apps into the Windows RAM cache so they load from RAM |
| `autopreload [paths]` / `--remove` | Runs preload 60 seconds after every logon (scheduled task) |
| `bench` | Runs the `winsat disk` benchmark. Run it before and after applying (and rebooting) to compare. |

Add `--dry-run` to preview changes, `--disk N` to choose a disk, and `--yes` to skip the prompt.

**Warning:** `--aggressive` means writes that are still in RAM are lost on a sudden power cut, which is the same risk Rapid Mode had. It is fine on laptops with a battery, but use a UPS on desktops. Reboot after `apply` or `revert`.

Firmware updates for OEM drives come from the laptop maker (HP, Lenovo or Dell support pages), not from Samsung Magician.

Build: `cd src && dotnet build -c Release` (works on Linux too).
