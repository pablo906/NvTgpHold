# Usage

NvTgpHold has no main window. Right-click its system-tray icon for the menu;
double-click it to refresh status. Read [Safety](Safety.md) before changing limits.

## First run

1. Connect the charger, keep the vents clear and activate the NVIDIA GPU
   (Standard or Ultimate GPU mode, rather than Eco/iGPU-only).
2. Choose **Run firmware / driver test**. A PASS means the driver transport and
   no-op request succeeded; it does not validate cooling or sustained power.
3. To change the ceiling, choose **Set MAX ceiling**. If prompted, load a dump of
   this GPU's vBIOS. Close GPU applications before accepting a GPU restart, or
   choose No and reboot later.
4. Choose **Hold CURRENT** at or below the live MAX. Start conservatively within
   your laptop's power and cooling capability; the menu is not a safety rating.
5. Monitor temperatures, fan behavior, power draw and stability under sustained
   load. Stop and revert if behavior is unexpected.

The EC hold is optional and independent. Leave it disabled unless you understand
and accept its [additional risks](#experimental-ec-cpu-ladder-hold).

## GPU limits and status

| Term | Meaning |
|---|---|
| CURRENT / `enf` | GPU power limit currently enforced by the NVIDIA driver |
| MAX / `max` | Highest power limit currently advertised by the driver |
| Dynamic Boost | Platform-managed extra GPU power allocation; default restore requests 25 W |
| NVPCF | NVIDIA platform controller; the status shows ASUS fixed base plus variable GPU power |

Example status:

```text
HOLD 140 W | enf 140.0 / max 150 W | NVPCF 80+60 W | re-asserted 140 W
```

Status normally refreshes every 3 seconds. `?` means a value could not be read,
often because the dGPU is asleep. Pop-up notifications contain test and failure
details. A requested power limit is a ceiling, not a promise of actual power draw.

### Hold CURRENT

Targets: **120 / 130 / 140 / 150 / 160 / 175 / 200 / 225 / 250 W**. The tool rejects
a target above a readable live MAX. By default it disables Dynamic Boost, sets
CURRENT and checks for a drop every 3 seconds. It also re-applies the target every
5 minutes and on power/resume/unlock events. Firmware can still override it.

**Sync NVPCF TGP to hold** is on by default. It writes the variable ASUS GPU power
setting so that `fixed base + variable = target`, then verifies readback. It saves
the variable setting before its first write and restores it on stop, uncheck or
exit. Restore defaults requests 0 W instead. Unsupported or out-of-range values
are skipped and reported in status.

**Keep Dynamic Boost on (+25 W)** is off by default. When enabled, NVPCF sync uses
`target - fixed base - 25` for the variable part. Dynamic Boost can cause repeated
platform resets of CURRENT, visible as additional re-asserts.

### Set MAX ceiling

This saves `romOverride00` in the NVIDIA display-driver registry key. It persists
after exit and reboot. The driver must reload the vBIOS for the new ceiling to
apply: accept the offered GPU device restart or reboot. Save work and close games
and other dGPU applications first; a restart can interrupt them and flicker the display.

During restart, other NVIDIA operations are paused. The restart process has a
60-second timeout, followed by an approximately 30-second readback window with
bounded queries. If the new MAX is not confirmed, check status and reboot.

An override recognized as another tool's (for example mVolt+) is neither replaced
nor deleted. Change or remove that override in its owning tool.

### Read the vBIOS

**Load vBIOS dump…** reads a `.rom` or `.bin` saved from this GPU and locates the
Board-Power MAX field. Nothing is flashed. The offset is cached per vBIOS identity
in `%ProgramData%\NvTgpHold\vbios-offsets.txt`.

To save a dump, use [GPU-Z](https://www.techpowerup.com/gpuz/) with the NVIDIA GPU
active and selected. Use the save control beside **BIOS Version**, save the ROM,
then select it in NvTgpHold. Reload a matching dump after a vBIOS update.

**Read vBIOS (nvflash)** uses `nvflash64.exe --save` instead. nvflash is not bundled;
place it next to the executable, in a `tools` subfolder, in `C:\NVFlash`, or on PATH.

## Auto re-assert

Off by default. Resends the GPU hold and selected CPU profile even when no GPU
drop has been detected. **Re-assert every** offers 1 / 2 / 3 / 5 / 10 / 30 seconds;
the default is 2 seconds. GPU re-assert is skipped if the live MAX is below its
target. This timer does not replace the ordinary 3-second status checks.

Auto re-assert may shorten transient dips; it cannot guarantee that a sustained
firmware clamp will be overcome. Faster intervals also increase ACPI/driver
traffic. The experimental EC hold has its own cadence and does not require this option.

## CPU power (Strix Point)

Shown only on detected AMD Strix Point CPUs, such as Ryzen AI 9 HX 370.

| Profile | SPL: sustained | SPPT: slow | FPPT: fast |
|---|---|---|---|
| 28 W | 28 W | 35 W | 45 W |
| 35 W | 35 W | 50 W | 65 W |
| 45 W | 45 W | 65 W | 90 W |

Selecting a profile sends the three ASUS ATK limits once, then again on power and
resume events. Auto re-assert resends it at the chosen interval. The firmware can
still clamp CPU power. These writes cannot be verified through the available ATK
readback, and prior PPT limits cannot be restored. **Off** stops future writes;
use an ASUS/G-Helper profile change or reboot to restore platform CPU limits.

**Efficient Enabled boost mode** is on by default when a CPU profile is active.
It changes Windows processor boost mode for AC and battery on the active power
plan. Captured prior values are restored on uncheck, CPU Off, Restore defaults or
exit. This is distinct from the CPU PPT limits above.

## Experimental EC CPU ladder hold

**GA403WW BIOS 312 only. Off by default.** Requires PawnIO and the bundled
`LpcACPIEC.bin`. Enabling it presents a separate consent warning. Detection runs
on launch; the menu explains why it is unavailable on other configurations.

Under GPU load, the EC can lower CPU power through its own ladder of limits. This
feature attempts to keep that ladder at step 0 by resetting its accumulators on a
separate background worker every 2 seconds. Writes are attempted only in the traced
EC mode with the OS-present flag set. It does not set an 80 W CPU profile: actual
CPU limits still depend on the other platform limits and workload.

**The transport is not exclusive.** Windows and ASUS firmware share the EC ports
without taking the application's mutex. Readback, jittered retries and state checks
do not prevent every collision or undo a write that lands elsewhere. Fan-mode
anomalies occurred during development; causation was not established. The final
retry/breaker path has not been tested on hardware.

Each slot has a retry budget of 1.5 seconds. Two consecutive failed slots stop the
hold, clear its saved opt-in and report a reason. A slot skipped because the EC is
outside the supported mode does not trip the breaker. A busy EC or changing live
counters can also cause failures; the message is not proof of hardware damage.

Status reports the step and shadow limit read before that attempt, plus write and
retry counts. A reported step 0 is an observation, not proof that no collision
occurred. Uncheck the item to stop, or use Restore defaults. An in-flight slot can
finish before the worker observes cancellation. See the [technical reference](EC-Ladder.md).

## Stop, restore and exit

| Action | Effect |
|---|---|
| Stop holding | Stops the GPU CURRENT hold and restores the captured NVPCF variable value. Leaves CPU/EC selections and MAX override in place. |
| Restore defaults | Stops GPU and EC holds, clears the CPU selection, restores Windows boost settings, removes this tool's MAX override, and requests stock GPU settings (95 W CURRENT, 0 W variable, 25 W Dynamic Boost). Offers a GPU restart if it removed an override. |
| Exit | Stops workers and restores captured NVPCF/Windows boost values. Leaves the MAX override and saved selections intact. |

CPU PPT limits cannot be restored to their previous values. A successful GPU
restart or reboot is needed to reload MAX after override removal. See
[Safety](Safety.md#reverting-changes) for a full revert.

## Saved settings and other utilities

`%ProgramData%\NvTgpHold\settings.txt` stores auto re-assert, its interval, CPU
profile, Efficient Enabled selection and EC-hold opt-in. The GPU CURRENT target
and Dynamic Boost/sync menu choices are not persisted. A saved CPU profile or EC
hold resumes on launch when its hardware checks pass.

G-Helper and Armoury Crate may re-apply their own limits after profile or power
changes. NvTgpHold warns when it detects G-Helper's **Auto apply** power limits.
Avoid having multiple tools repeatedly write competing settings. Windows power
plans and vendor modes can also affect results; NvTgpHold does not require a
particular Windows plan. Raised limits are intended for charger use, not battery use.
