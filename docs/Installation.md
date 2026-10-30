# Installation

## Requirements

- Windows 11 x64 and administrator rights
- [.NET 9 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/9.0)
- NVIDIA driver with `nvapi64.dll` and `nvidia-smi.exe`; the dGPU must be active
- ASUS System Control Interface / ATK ACPI driver

The tested laptop model is GA403WW. The experimental EC hold additionally requires
BIOS `GA403WW.312`, PawnIO and the bundled `LpcACPIEC.bin` next to the executable.
PawnIO is optional for all other features.

## Download and verify

1. Download `NvTgpHold-v1.1.2-win-x64.zip` from the official
   [release page](https://github.com/pablo906/NvTgpHold/releases/latest).
2. In PowerShell, calculate the checksum and compare it with that release's notes:

   ```powershell
   Get-FileHash .\NvTgpHold-v1.1.2-win-x64.zip -Algorithm SHA256
   ```

3. Extract the entire ZIP to a folder you control, such as `C:\Tools\NvTgpHold`.
   Keep the DLLs, runtime configuration, EC module and license notices together.
4. Run `NvTgpHold.exe` and accept the UAC prompt. Right-click its tray icon to open
   the menu. Continue with [First run](Usage.md#first-run).

Only use releases from this repository or build from its source. A matching
checksum checks file integrity; it does not establish that a power setting is safe.

## Upgrade

Exit the running utility before extracting a new release into its folder. Settings
and the vBIOS offset cache are stored in `%ProgramData%\NvTgpHold`, so replacing
the application files preserves them. Re-run **Run firmware / driver test** after
upgrading the application or NVIDIA driver. A saved EC-hold opt-in resumes on launch
only if the model, BIOS and dependencies still pass detection.

The old **65 W** CPU profile is no longer offered. Its saved selection is treated
as **Off**. The **45 W** profile now uses 45 / 65 / 90 W limits; review the
[profile table](Usage.md#cpu-power-strix-point) when upgrading.

## Optional: start with Windows

Use Task Scheduler to run the executable **at log on**, with **Run with highest
privileges** enabled. The Startup folder does not handle the required elevation.
Remember that saved CPU profiles, auto re-assert and an enabled EC hold can resume;
the GPU CURRENT hold target itself is not saved.

## Optional: install PawnIO

On a supported machine without PawnIO, the CPU menu offers **Install PawnIO…**.
After confirmation, it downloads the official installer, verifies its Authenticode
signature and signer, then runs it from protected staging storage. Complete setup
and enable the experimental hold only after reading its separate warning. If
detection still fails, see [Troubleshooting](Troubleshooting.md).

## Uninstall

Follow [Reverting changes](Safety.md#reverting-changes), remove any scheduled task,
exit the application and delete its folder. Remove `%ProgramData%\NvTgpHold` only
if you also want to discard settings and the vBIOS offset cache. Removing this
folder alone does not remove the NVIDIA registry override.
