# Installation

## Requirements

- Windows 11 x64 and an administrator account
- [.NET 9 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/9.0)
- Current NVIDIA driver (includes `nvidia-smi`, used for the status line)
- ASUS ATK ACPI driver (installed with Armoury Crate or ASUS System Control Interface)

## Download

1. Open the repository's **Releases** page and download `NvTgpHold-vX.Y.Z-win-x64.zip`.
2. Verify the SHA-256 listed in the release notes:
   ```powershell
   Get-FileHash .\NvTgpHold-v1.0.0-win-x64.zip
   ```
3. Extract the zip to a folder you control, e.g. `C:\Tools\NvTgpHold`. The tool saves a small settings file next to the exe, so the folder must be writable.
4. Run `NvTgpHold.exe` and accept the UAC prompt. A shield icon appears in the tray.

Only download releases from this repository.

## Start with Windows (optional)

Because the tool needs elevation, use Task Scheduler rather than the Startup folder:
create a task that runs `NvTgpHold.exe` **at log on** with **Run with highest privileges** checked.

## Uninstall

1. Tray menu → **Restore defaults**, then reboot.
2. Remove any scheduled task you created and delete the folder.
