# Troubleshooting

| Message | Likely cause / fix |
|---|---|
| `nvapi64.dll could not be loaded` | NVIDIA driver missing or not installed correctly. Reinstall the driver. |
| `NvAPI_EnumPhysicalGPUs ... count=0` | The dGPU is off (Eco mode / iGPU only). Switch to Standard or Ultimate GPU mode. |
| `resolver: ...` | Unsupported NVIDIA driver build. Open an issue with your driver version. |
| `ATKACPI open failed` | ASUS ATK ACPI driver missing. Install ASUS System Control Interface. |
| `ASUS rejected the Dynamic Boost request` | Model/BIOS does not accept the boost toggle. |
| `Target N W exceeds live MAX` | Set MAX higher and reboot first. |
| `MAX: not ready` in the test | No vBIOS loaded for this vBIOS version yet. Use **Load vBIOS dump...** (see [Usage](Usage.md#load-vbios-dump)). |
| `no NVIDIA legacy image for DEV_xxxx` | The dump is from a different GPU. Save a dump of this GPU's vBIOS. |
| `Board-Power MAX record not found uniquely` | Unrecognized vBIOS layout. Open an issue with your GPU and vBIOS version. |
| `nvflash64.exe not found` | Put `nvflash64.exe` next to `NvTgpHold.exe`, or use **Load vBIOS dump...** instead. |
| `enf ? / max ?` in the status line | `nvidia-smi` could not read the GPU, usually because the dGPU is asleep (Eco mode). |
| MAX didn't change after setting it | MAX only applies after a reboot. |
| Hold keeps dropping back | Another tool (G-Helper, Armoury Crate) is re-applying its own limits. Close it or set it to match. |

## Reporting a bug

Open an issue with: laptop model, BIOS version, NVIDIA driver version, the tray status line, and
the text of any balloon messages. For security problems use private reporting (see [SECURITY.md](../SECURITY.md)).
