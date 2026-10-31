# NvTgpHold

A Windows tray utility for adjusting and holding NVIDIA GPU power limits on the
ASUS ROG Zephyrus G14 GA403WW. It can also apply AMD Strix Point CPU power profiles.

**Use at your own risk.** Raising power limits can cause overheating, instability,
charger or battery stress, and permanent hardware damage. This is an unofficial
tool, tested on one laptop model; compatibility and safe power levels are not
guaranteed. Read [Safety](docs/Safety.md) before using it.

## Download and start

1. Download the Windows x64 ZIP from [Releases](https://github.com/pablo906/NvTgpHold/releases/latest).
2. Compare its SHA-256 with the checksum in that release, then extract the entire ZIP.
3. Install the [.NET 9 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/9.0)
   if needed, and run `NvTgpHold.exe`. Accept the administrator prompt.
4. Right-click the tray icon and choose **Run firmware / driver test**.

Requires Windows 11 x64, an active NVIDIA GPU with its driver, and the ASUS System
Control Interface / ATK ACPI driver. Release ZIPs require the .NET runtime above.
See [Installation](docs/Installation.md) for upgrading and optional startup tasks.

## Features

| Feature | Purpose |
|---|---|
| Power profiles | Apply a full profile (ASUS mode, CPU PPT, GPU power / boost / temp target, CPU + GPU fan curves) through ASUS ATK, so NvTgpHold can replace G-Helper. Presets mirror the owner's Silent / Balanced / Turbo modes plus a Manual-like one; limits and fans are re-asserted in the background. |
| Hold CURRENT | Set the enforced GPU power limit and re-apply it after detected drops and power events. |
| Set MAX ceiling | Save a persistent ceiling override; apply it with an offered GPU restart or a reboot. A vBIOS dump is read once to locate the field; nothing is flashed. |
| NVPCF sync | Match the ASUS platform power setting to the hold target, with readback and prior-value restoration. |
| Auto re-assert | Optionally resend the GPU hold and CPU profile every 1–30 seconds; default interval is 2 seconds. |
| CPU power profiles | Apply 18 / 28 / 35 / 45 W sustained CPU profiles on AMD Strix Point, with optional Windows Efficient Enabled boost mode. |
| Experimental EC CPU ladder hold | On GA403WW BIOS 312 only, periodically reset EC throttle accumulators. Requires PawnIO and explicit opt-in. |

Menu presets are available targets, **not validated safe power ratings** for your
laptop. Re-applying limits cannot guarantee that firmware will sustain them.
Dynamic Boost is turned off during a GPU hold by default; an optional mode keeps
it on. G-Helper's automatic power-limit application can conflict with the hold.

### Experimental EC feature

The EC hold shares hardware ports with Windows and ASUS firmware. Its mutex,
readback, retries and automatic stop on repeated failures **cannot prevent every
collision or undo a misplaced write**. Fan-mode anomalies were observed during
development; their cause has not been established. The final retry/breaker path
has not been validated on hardware. The feature is off by default and separate
from ordinary CPU profiles. See [Usage](docs/Usage.md#experimental-ec-cpu-ladder-hold)
and the [technical reference](docs/EC-Ladder.md) before enabling it.

## Documentation

- [Installation](docs/Installation.md): requirements, download, upgrade and startup
- [Usage](docs/Usage.md): first run, menu options and settings persistence
- [Safety and reverting changes](docs/Safety.md)
- [Troubleshooting](docs/Troubleshooting.md)
- [Building and release checks](docs/Building-from-Source.md)
- [GitHub wiki](https://github.com/pablo906/NvTgpHold/wiki): published copy of these guides

The Markdown files in `docs/` are the documentation source. Firmware addresses
and research notes belong in the technical reference, rather than the setup guide.

## Revert or uninstall

Choose **Restore defaults**. If a MAX override is removed, accept the GPU restart
after closing GPU applications, or reboot. Exit the utility and remove any startup
task before deleting its folder. Another tool's recognized MAX override is left
untouched. CPU PPT values cannot be read back or restored to their prior values;
use an ASUS/G-Helper profile change or reboot to restore platform CPU limits.

## Build and contribute

```powershell
dotnet build -c Release
dotnet test tests/NvTgpHold.Tests -c Release
```

See [Building from source](docs/Building-from-Source.md) and
[CONTRIBUTING.md](CONTRIBUTING.md). Report reproducible bugs through
[GitHub issues](https://github.com/pablo906/NvTgpHold/issues); report security
issues using the process in [SECURITY.md](SECURITY.md).

## License

NvTgpHold is [MIT licensed](LICENSE). The bundled PawnIO `LpcACPIEC.bin` module is
LGPL-2.1-or-later; see [third-party notices](third_party/PawnIO.Modules.md).
