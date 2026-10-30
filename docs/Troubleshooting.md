# Troubleshooting

Start with the status line and **Run firmware / driver test**. A driver update,
GPU sleep state or another utility's power policy can change results.

| Message or symptom | What to check |
|---|---|
| `nvapi64.dll could not be loaded` | Install or repair the NVIDIA driver. |
| `NvAPI_EnumPhysicalGPUs ... count=0` | Activate the NVIDIA GPU; leave Eco/iGPU-only mode. The app expects exactly one NVIDIA physical GPU. |
| Transport/resolver failure | The driver build may be unsupported. Report its exact version and test output. |
| `ATKACPI open failed` | Check ASUS System Control Interface / ATK ACPI installation. |
| Dynamic Boost request rejected | The model or firmware may not accept the request. Do not assume a driver PASS validates ATK writes. |
| Target exceeds live MAX | Change MAX, apply with the offered GPU restart or reboot, and confirm the new live value before holding. |
| MAX not ready | Use **Load vBIOS dump…** with a dump from this GPU. See [Usage](Usage.md#read-the-vbios). |
| `no NVIDIA legacy image` / MAX record not found uniquely | Check that the dump matches the GPU. Report the vBIOS identity and error for unsupported layouts. |
| `nvflash64.exe not found` | Use a saved vBIOS dump instead, or supply nvflash as described in Usage. |
| `enf ? / max ?` | The GPU may be asleep or nvidia-smi may have failed or timed out. Activate the dGPU and retry. |
| MAX unchanged after setting it | A GPU restart/reboot is required. If restart readback does not confirm the target, reboot and check again. |
| Override belongs to another tool | Change/remove that tool's override there. NvTgpHold leaves it intact. |
| GPU restart timed out | Save work and reboot. Report the message rather than repeatedly restarting the device. |
| Hold keeps dropping | Check firmware limits and competing automatic power settings in G-Helper/Armoury Crate. Sustained clamps may not be overcome. |
| EC hold unavailable | Requires GA403WW BIOS 312, PawnIO and `LpcACPIEC.bin` beside the executable. Run the test for the detection reason. |
| PawnIO setup finishes but hold stays unavailable | Check the detection reason, complete installation, then relaunch. Do not download substitute DLLs from third parties. |
| EC hold reports skipped | EC mode or OS-present flags are outside the traced state. The slot writes nothing; this is not a failed driver test. |
| EC hold stopped after two slots | Retry/readback attempts did not complete successfully. Leave it off and report the exact reason and concurrent EC-monitoring tools. |
| Unexpected fan behavior | Stop the EC hold, revert settings and follow [Safety](Safety.md). A clean retry count does not prove the EC transport is collision-free. |

## Reporting a bug

Use [GitHub issues](https://github.com/pablo906/NvTgpHold/issues). Include the app
version, laptop model, BIOS, NVIDIA driver and vBIOS versions; enabled features;
power source and vendor profile; other power/EC-monitoring tools; reproduction
steps; and the status/notification text. State whether the issue also occurs with
the experimental EC hold disabled. Remove personal information from screenshots
or logs before posting.

Use private reporting for security vulnerabilities as described in
[SECURITY.md](../SECURITY.md).
