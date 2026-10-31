# NvTgpHold documentation

NvTgpHold adjusts and holds GPU power limits on the ASUS ROG Zephyrus G14 GA403WW.
The utility runs in the Windows system tray with administrator rights.

**Raised limits can cause instability or hardware damage.** Read
[Safety](Safety.md) before changing settings. Other laptop models have not been
validated, and the available wattage presets do not establish safe limits.

## Get started

1. [Install or upgrade](Installation.md).
2. Follow the [first-run workflow](Usage.md#first-run).
3. Use [Troubleshooting](Troubleshooting.md) if a test or setting fails.

## Guides and reference

| Page | Contents |
|---|---|
| [Usage](Usage.md) | GPU limits, CPU profiles, experimental EC hold and persistent settings |
| [Safety](Safety.md) | Risks, monitoring, reverting and uninstalling |
| [Building from source](Building-from-Source.md) | Build, tests, packaging and wiki publication |
| [EC CPU ladder](EC-Ladder.md) | Firmware reference, transport limitations and evidence boundaries |
| [Linked SYS/XBAR](SYS-XBAR.md) | Private clock interface, driver restrictions and readback behavior |

## Compatibility and validation

Development hardware: GA403WW with Ryzen AI 9 HX 370, RTX 5080 Laptop GPU and
Windows 11 x64. The EC feature is restricted to BIOS `GA403WW.312`; BIOS 310
research does not imply support. NVIDIA driver compatibility is checked at runtime.
A passing driver test is not a safety test or a guarantee of sustained power.

Earlier hardware measurements informed the EC design. The current retry/breaker
implementation has only software-level validation, not a new hardware endurance
test. The EC hold is experimental, off by default, and requires explicit consent.

These pages are maintained in the repository's `docs/` directory and mirrored to
the [wiki](https://github.com/pablo906/NvTgpHold/wiki) for each release.
