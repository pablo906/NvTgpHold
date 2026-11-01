# CPU undervolting

NvTgpHold's undervolt control applies to **all AMD CPU cores**, using Curve
Optimizer. It does not undervolt NVIDIA GPU core, SYS, XBAR, video or memory.

## Enable it and select a value

1. Right-click the **NvTgpHold** tray icon and open **Power profile (replaces G-Helper)**.
2. Look below **Fan profile**. **Experimental expansions** is a disabled section
   heading, not a submenu. The next two checkboxes are GPU OC and
   **Apply CPU undervolt + temp from profile**.
3. Check the CPU option and accept its experimental-tuning prompt.
4. Select a full NvTgpHold power profile if none is active. With an active profile,
   enabling the option immediately sends its CO and temperature requests.

The option is off by default. It requires supported Strix Point hardware, PawnIO
and the signed `RyzenSMU.bin` beside the executable. It stays visible but greyed
out when detection fails; hover for the **Unavailable:** reason. If it is absent,
confirm that you opened NvTgpHold's full Power profile menu, not the standalone
CPU power menu, and are running the expected executable/version.

On a successful immediate apply, status reports `SMU CO=-20` or `SMU CO=-25` for
the active profile. This reports command acceptance, not measured voltage.

The active power profile supplies both values; there is no separate millivolt or
per-core voltage picker:

| Power profile | All-core Curve Optimizer | CPU temperature limit |
|---|---|---|
| Silent | −25 steps | 96 °C |
| Balanced | −20 steps | 95 °C |
| Turbo | −25 steps | 96 °C |
| NvTgpHold-Manual | −25 steps | 90 °C |

**Balanced does include CPU undervolting:** with **Apply CPU undervolt + temp
from profile** enabled, it requests **−20 all-core CO steps and a 95 °C CPU limit**.
Selecting Balanced with that option disabled makes no CO or SMU temperature
request; a previously applied setting may remain. Explicitly switching the option
off requests CO = 0. These are separate actions.

These are Curve Optimizer counts, **not millivolts**. For example, −25 does not
mean a fixed −25 mV. Negative Curve Optimizer values request a shift toward lower
voltages in the CPU's voltage/frequency curve; actual operating voltage depends
on the operating point. See [AMD's Curve Optimizer explanation](https://docs.amd.com/r/en-US/68886-ryzen-master-user-guide/Curve-Optimizer).
The presets are not stability guarantees; see [Safety](Safety.md).

## Does it follow G-Helper's profile?

No. NvTgpHold's profiles are bundled values defined in `Profiles.cs`, originally
based on the owner's G-Helper configuration. They are not imported from the live
G-Helper profile or updated when G-Helper changes.

With CPU tuning enabled, NvTgpHold writes its own selected profile's CO value.
With it disabled, applying a profile does not write CO, so an earlier value from
G-Helper, firmware or another tool may remain. NvTgpHold does not read that value
back or adopt it as its own. The initially unchecked option is therefore not
proof of CO = 0; explicitly turning it off sends the zero request described below.

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
