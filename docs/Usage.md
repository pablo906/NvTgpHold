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

## Power profiles (replacing G-Helper)

**Power profile** applies a full set of limits in one click, so the tool can stand
in for G-Helper. Each profile sets, through ASUS ATK (the same calls G-Helper uses,
not direct EC writes): the ASUS platform mode, CPU PPT (SPL / SPPT / FPPT), GPU
added power, Dynamic Boost, GPU temperature target, and the CPU and GPU fan curves.
Values come from the owner's own G-Helper config.

| Profile | ASUS mode | CPU SPL/SPPT/FPPT | GPU added / boost / temp |
|---|---|---|---|
| Silent | Silent | 28 / 45 / 65 W | +0 W / 5 W / 85 °C |
| Balanced | Balanced | 35 / 50 / 65 W | +15 W / 25 W / 85 °C |
| Turbo | Turbo | 35 / 45 / 55 W | +70 W / 25 W / 87 °C |
| NvTgpHold-Manual | Turbo base | 18 / 28 / 45 W | +70 W / 25 W / 85 °C |

NvTgpHold-Manual uses a max-cooling fan curve (linear to 50 °C / 50%, then up to
100% at 70 °C, flat above), CPU limit 90 °C, GPU target 85 °C, and -25 CO undervolt
(the undervolt applies only with the experimental CPU tuning on). A separate **Fan
profile** submenu overrides the fan curve independently of the power profile: Silent,
Balanced, Turbo, or Max cooling (or "Follow power profile" to clear the override).

Apply order mirrors G-Helper: the ASUS mode first (so the EC loads that mode's
baseline), then the fan curves, then CPU PPT, then GPU temp / boost / power — GPU
power is written last so an NVPCF recompute from the earlier calls does not leave it
low. CPU PPT is re-asserted in the background like the holds; GPU added power is put
back if it drifts; fan curves are re-sent on power and resume events. A GPU **Hold
CURRENT**, if active, owns the GPU power and boost while the profile owns the rest.

Close G-Helper and turn off its **Auto apply** power option first, or the two will
overwrite each other. The active profile and its CPU limits are restored on launch.

Selecting a standalone CPU preset (including CPU Off) deactivates the full profile,
so power events and startup cannot silently replace that CPU choice. Other hardware
settings remain at their last-applied values. Stopping a GPU CURRENT hold returns
both Dynamic Boost and added GPU power to the active full profile, if one remains.

### Experimental expansions

These are **off by default and opt-in**, each behind a consent dialog, like the EC
ladder hold. They are under **Power profile → Experimental expansions**.

- **GPU overclock** (NvAPI public clock domains). The **GPU overclock** picker applies a
  core offset (25 MHz steps, 100-300), a memory offset (300-2000 in set steps), or a
  video-clock offset (100-300) immediately; or the "Apply GPU OC from profile" toggle
  applies the active profile's core/memory offsets (NvTgpHold-Manual is +100 core /
  +1000 memory). Offsets reset on reboot and "0" clears them. Nothing is flashed. Too
  high an offset causes artifacts or a reset. A driver can reject a requested domain;
  an error is reported rather than establishing that the offset was applied.
  Changing core or memory retains the other domain's last accepted offset, including
  an offset applied by a profile. Rejected changes leave the previous menu values in place.
  With profile OC enabled, a full profile apply also clears video and SYS/XBAR
  picker offsets. Unknown values after relaunch are reset too; failures are reported
  as an incomplete profile apply. With profile OC off, these picker offsets are left alone.
  **SYS / XBAR offset (linked)** applies one shared 0 / +100 / +200 / +300 MHz offset
  to SYS and XBAR, separately from core, memory and video. It uses a private interface,
  currently restricted to RTX 5080 Laptop on NVIDIA driver 621.01. Both offsets must
  read back as requested before the menu shows success. Voltage offsets are preserved.
  This control is session-only and is not reapplied by power profiles. Its separate
  experimental consent describes the validation limits. **0 (off)** or **Restore defaults**
  requests zero for both domains; reboot if resetting fails. Per-domain voltage-demand
  undervolting is not supported.
  See the [SYS/XBAR reference](SYS-XBAR.md) for interface details and validation limits.
- **CPU undervolt + temperature** (Ryzen SMU, via PawnIO and the signed `RyzenSMU.bin`
  module next to the exe). While on, selecting a profile also sets its CPU temperature
  limit and all-core Curve Optimizer offset through the SMU. Applied on profile select
  and power/resume only, never in the fast re-assert. Turning it off clears the
  undervolt. The SMU mailbox is shared with ASUS services, so a write can collide; an
  over-aggressive undervolt destabilises under load. Only Strix Point (Ryzen AI 300) is
  enabled.
  Disabling tuning clears Curve Optimizer, but does not restore an earlier SMU
  temperature limit; reboot to reset it. The previous limit cannot be read back by
  this implementation.
- **GPU display mode** (ASUS ATK): Eco / Standard / Ultimate / Optimized. Switching into
  or out of Ultimate (MUX) needs a reboot, which NvTgpHold offers after you confirm; Eco
  and Standard re-initialise the display (sign out or reboot for a clean switch).
  Optimized keeps the dGPU on while on AC and switches to Eco (iGPU only) on battery,
  applied automatically on power and resume events.
  A failed mode selection leaves the existing automatic policy enabled or disabled
  as before. An accepted MUX change still needs the offered reboot.

Do not run the experimental EC CPU ladder hold at the same time as a profile — both can
touch the EC fan state.

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

Selecting a target, re-asserting it and applying a profile all prepare Dynamic
Boost and NVPCF first, allow the notifications to settle, then send CURRENT last.
Failure of any required step is reported as incomplete, even if CURRENT itself
was accepted. An accepted target stays managed for later recovery; a rejected
CURRENT request does not replace the previously managed target.

**Sync NVPCF TGP to hold** is on by default. It writes the variable ASUS GPU power
setting so that `fixed base + variable = target`, then verifies readback. It saves
the variable setting before its first write and restores it on stop, uncheck or
exit. Reset to defaults instead selects Balanced's GPU power settings. Unsupported or out-of-range values
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
| 18 W | 18 W | 23 W | 28 W |
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

## Hardware overlay

Enable **Show hardware overlay (G-Helper style)** in the tray menu for a top-left,
click-through panel. The choice is saved and restored on launch. Turn the same
item off to close it. The overlay collects telemetry; it does not change power
limits, fan curves or the active hold.

GPU temperature, power and usage come from NVIDIA; CPU usage comes from Windows.
Fan speeds use ASUS ATK readings. CPU temperature is the available ACPI thermal
zone (preferably THRM), not a verified CPU-die sensor; CPU/APU power uses the
Windows Energy Meter when available. Missing sensors show placeholders.

FPS is experimental and counts DirectX kernel present events for the foreground
application. It is not a measurement of displayed frames or frame latency, and
may be unavailable for some applications. The desktop window is not an injected
game overlay; exclusive fullscreen can obscure it. NVIDIA collection pauses
during an application-managed GPU restart. Live fullscreen/FPS accuracy and
long-running sensor behavior have not been validated.

## Stop, restore and exit

| Action | Effect |
|---|---|
| Stop holding | Stops the GPU CURRENT hold and restores the captured NVPCF variable value. Leaves CPU/EC selections and MAX override in place. |
| Reset to defaults (Balanced + Optimized) | Stops GPU/EC holds and Auto re-assert, selects Balanced with its CPU limits and fan curves, clears fan overrides, disables experimental tuning, requests zero core/memory/video/SYS/XBAR offsets and CPU undervolt, and selects Optimized GPU mode. Removes this tool's MAX override and restores captured Windows boost settings. Offers a GPU restart for MAX removal and a reboot when leaving Ultimate. Failed resets remain visible. |
| Exit | Stops workers and restores captured NVPCF/Windows boost values. Leaves the MAX override and saved selections intact. |

CPU PPT limits cannot be restored to their previous values. A successful GPU
restart or reboot is needed to reload MAX after override removal. See
[Safety](Safety.md#reverting-changes) for a full revert.

## Saved settings and other utilities

`%ProgramData%\NvTgpHold\settings.txt` stores auto re-assert, its interval, CPU
profile, Efficient Enabled selection, EC-hold opt-in and overlay visibility. The GPU CURRENT target
and Dynamic Boost/sync menu choices are not persisted. A saved CPU profile or EC
hold resumes on launch when its hardware checks pass.

G-Helper and Armoury Crate may re-apply their own limits after profile or power
changes. NvTgpHold warns when it detects G-Helper's **Auto apply** power limits.
Avoid having multiple tools repeatedly write competing settings. Windows power
plans and vendor modes can also affect results; NvTgpHold does not require a
particular Windows plan. Raised limits are intended for charger use, not battery use.
