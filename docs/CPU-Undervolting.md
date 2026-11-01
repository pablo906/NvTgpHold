# CPU undervolting

NvTgpHold's undervolt control applies to **all AMD CPU cores**, using Curve
Optimizer. It does not undervolt NVIDIA GPU core, SYS, XBAR, video or memory.

## Enable it and select a value

In the tray menu, open **Power profile → Experimental expansions** and enable
**Apply CPU undervolt + temp from profile**. This is off by default and requires
supported Strix Point hardware, PawnIO and the signed `RyzenSMU.bin` beside the
executable. An unavailable control reports why detection failed.

The active power profile supplies both values; there is no separate millivolt or
per-core voltage picker:

| Power profile | All-core Curve Optimizer | CPU temperature limit |
|---|---|---|
| Silent | −25 steps | 96 °C |
| Balanced | −20 steps | 95 °C |
| Turbo | −25 steps | 96 °C |
| NvTgpHold-Manual | −25 steps | 90 °C |

These are Curve Optimizer counts, **not millivolts**. For example, −25 does not
mean a fixed −25 mV. Negative Curve Optimizer values request a shift toward lower
voltages in the CPU's voltage/frequency curve; actual operating voltage depends
on the operating point. See [AMD's Curve Optimizer explanation](https://docs.amd.com/r/en-US/68886-ryzen-master-user-guide/Curve-Optimizer).
The presets are not stability guarantees; see [Safety](Safety.md).

## When the request is sent

With the option enabled, a full profile apply sends the profile's temperature
limit and Curve Optimizer value. This includes selecting a profile, restoring a
saved profile on startup, and reapplying it on power/resume/unlock events.
Enabling the option also applies it immediately if a full power profile is active.
If no full profile is active, enabling it alone does not select a CO value.

The fast Auto re-assert loop and GPU CURRENT watchdog do not repeatedly send CPU
undervolt requests. Selecting a standalone CPU wattage preset changes CPU power
limits and stops full-profile management; it does not itself clear or replace CO.

| Action | Effect on CPU tuning |
|---|---|
| Disable the CPU tuning option | Requests all-core CO = 0. Does not restore the previous temperature limit. |
| Reset to defaults | Disables tuning and requests CO = 0 and Balanced's 95 °C CPU limit when SMU support is available. |
| Select power profile Off, choose CPU watts, or exit | Does not send a CO-clear request. The last accepted setting can remain until firmware resets it or another request replaces it. |

The option and profile selection are saved; the voltage curve is not flashed into
the BIOS. Check failure messages if a clear request is rejected; reboot if needed.

## How it reaches the CPU

`Program.ApplyProfile` passes the profile's `CpuUv` to
[`RyzenSmu.SetUndervolt`](../RyzenSmu.cs). That method uses PawnIO's signed RyzenSMU
module to access the Strix Point SMU mailbox through SMN/PCI register access.
It encodes the CO count and sends **SMU command 0x4C**, the all-core CO request;
the temperature limit is a separate **0x19** request.

The code stages the mailbox arguments before submitting a command and reports
success only for response **0x01**. That is command acceptance, not voltage
readback or a stability test. A shared PCI mutex coordinates cooperating tools'
individual transactions; it does not make the whole mailbox exchange exclusive
against firmware or every other utility.

## GPU controls are separate

Core, memory and video controls change NVIDIA **clock offsets in MHz**, without
requesting voltage offsets. Linked SYS/XBAR also changes clocks only and preserves
existing voltage offsets, including ones set by another tool. Setting those
clocks to zero or using Reset to defaults does not implement a GPU voltage reset.
See the [SYS/XBAR reference](SYS-XBAR.md).

Profile values are defined in [Profiles.cs](../Profiles.cs); activation and reset
paths are in [Program.cs](../Program.cs). This page describes v1.1.9 behavior.
