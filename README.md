# NvTgpHold

A small Windows tray tool for the ASUS ROG Zephyrus G14 (GA403WW, RTX 50-series laptop GPU)
that raises and holds the NVIDIA GPU's power limit above the stock platform cap.

> ## ⚠️ SAFETY DISCLAIMER — READ BEFORE USE
>
> **This tool changes GPU power and boost limits beyond the manufacturer's defaults.**
> Running hardware outside its stock power envelope can cause higher temperatures,
> instability, crashes, accelerated wear, battery/charger stress, or **permanent hardware damage**.
> It writes to the NVIDIA driver registry key and sends undocumented NvAPI / ASUS ATK ACPI calls.
>
> - It is **unofficial** and not affiliated with or endorsed by NVIDIA or ASUS.
> - It may **void your warranty**.
> - It was developed and tested on **one machine** (GA403WW). Other models, BIOS versions or
>   driver versions may behave differently or not at all.
> - Monitor temperatures and power draw while using it.
>
> **You use this software entirely at your own risk.** The authors accept no liability for any
> damage, data loss or other consequences (see [LICENSE](LICENSE)).

## What it does

Right-click the tray icon for the options below. See the [usage guide](docs/Usage.md) for details.

| Menu item | Effect |
|---|---|
| **Run firmware / driver test** | Resolves the driver transport and performs a no-op set; reports PASS/FAIL. |
| **Hold CURRENT** (120–250 W) | Turns ASUS Dynamic Boost off, sets the enforced GPU power limit, and re-applies it within about 3 seconds whenever something pulls it down (plus every 5 minutes and on power/resume events). |
| **Sync NVPCF TGP to hold** | On by default. Sets the variable part of the ASUS/NVPCF GPU power (ATK `0x00120098`, G-Helper's GPU power setting) so that fixed base (`0x00120099`) + variable equals the hold target, verifies it by reading it back, and puts it back if something changes it. The previous value is restored on stop, uncheck or exit. |
| **Load vBIOS dump...** | Reads a vBIOS dump (for example saved with GPU-Z) and finds the MAX power field. Read-only, needed once. |
| **Read vBIOS (nvflash)** | Same, but reads the vBIOS directly with `nvflash64.exe` (not included). |
| **Set MAX ceiling (reboot)** | Writes a persistent ceiling override to the NVIDIA driver registry key at that field. Asks for a vBIOS dump if none is loaded. Takes effect after reboot. |
| **Stop holding** | Stops re-asserting the CURRENT value. |
| **Restore defaults** | Removes the MAX override, sets CURRENT back to stock 95 W, sets the NVPCF variable GPU power back to 0 and re-enables Dynamic Boost (95 + 25 W). |

Dynamic Boost must stay **off** for a raised CURRENT limit to hold; the tool handles this for you.

## Download

Get the latest `NvTgpHold-vX.Y.Z-win-x64.zip` from the [Releases](../../releases) page, check its
SHA-256 against the release notes, extract it and run `NvTgpHold.exe`. Full guide in the
[docs](docs/README.md) (also mirrored to the [wiki](../../wiki)).

## Requirements

- Windows 11 x64, administrator rights (the manifest requests elevation)
- [.NET 9 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/9.0)
- NVIDIA driver with `nvapi64.dll`
- ASUS ATK ACPI driver (installed with Armoury Crate / ASUS System Control Interface)
- .NET 9 SDK to build

## Build

```
dotnet build -c Release
```

The executable is written to `bin/Release/NvTgpHold.exe`.

Hardware-free tests for the NVPCF TGP arithmetic: `dotnet test tests/NvTgpHold.Tests`.

## Uninstall / revert

Use **Restore defaults**, then reboot. Closing the tool stops the hold; the MAX override persists in
the registry until removed by **Restore defaults**.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). All changes go through pull requests and require the
maintainer's approval before merging.

## License

[MIT](LICENSE)
