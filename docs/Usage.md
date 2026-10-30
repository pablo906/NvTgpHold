# Usage

NvTgpHold lives in the system tray (red-and-black badge with a GPU chip and lightning bolt). It has no main window; everything is in the
right-click menu. Hovering the icon shows a short tooltip such as `Hold 140W enf 140.0`.

## Key terms

| Term | Meaning |
|---|---|
| **CURRENT** (enforced limit, `enf`) | The power limit the NVIDIA driver is enforcing right now. Stock is 95 W, or 95 + 25 W with Dynamic Boost. |
| **MAX** (`max`) | The highest limit the driver will accept. CURRENT can never go above it. Changing MAX needs a reboot. |
| **Dynamic Boost** | ASUS feature that adds up to 25 W on top of CURRENT. While it is on, the platform keeps overwriting a raised CURRENT, so the tool turns it off while holding. |
| **Hold** | The tool keeps re-applying your CURRENT target so drivers and power events can't quietly reset it. |

## The status line

The first (greyed-out) menu line is the live status, refreshed every 3 seconds:

```
HOLD 140 W  |  enf 140.0 / max 150 W  |  NVPCF 80+60 W (synced 80+60 W (hold))  |  re-asserted 140 W (5-min)
```

1. `HOLD 140 W` or `hold off`: whether a hold is active and its target.
2. `enf 140.0 / max 150 W`: enforced and maximum limits read from `nvidia-smi`. `?` means the GPU
   could not be read (for example, the dGPU is asleep in Eco mode).
3. `NVPCF 80+60 W`: the GPU power the ASUS BIOS reports to NVIDIA's platform controller, as fixed
   base + variable part, followed by the result of the last sync (or `sync off`).
4. The last hold action and its result.

Double-click the tray icon to refresh immediately. Pop-up notifications (balloons) show the
details of tests, MAX changes and failures.

## Menu reference

### Run firmware / driver test
Checks that the tool can talk to the NVIDIA driver on this machine, and re-applies the current
enforced value as a harmless no-op. Result example:

`Driver/transport: PASS | MAX: ready`

- **Driver/transport PASS**: holding CURRENT will work.
- **MAX ready**: the tool already knows where the MAX field is in this GPU's vBIOS.
- **MAX not ready**: you can still hold CURRENT up to the existing MAX. Load a vBIOS dump once
  (see below) before using *Set MAX ceiling*.

Run this first after installing, and again after every NVIDIA driver update.

### Hold CURRENT → 120 / 130 / 140 / 150 / 160 / 175 / 200 / 225 / 250 W
1. Refuses if the target is above the live MAX and tells you to raise MAX first.
2. Turns ASUS Dynamic Boost **off**.
3. Sets the enforced limit to the chosen value.
4. Keeps holding it: checks the enforced limit every **3 seconds** and re-applies it as soon as
   something pulls it down. It also re-applies every 5 minutes, after plugging in or unplugging,
   after sleep/resume, and after unlocking Windows. The status line shows when it last had to
   re-apply and how many times (`dropped to 120 W, #3 14:02:11`), which tells you how often
   something else is overriding it.
5. If **Sync NVPCF TGP to hold** is checked (the default), also sets the ASUS/NVPCF GPU power so
   it adds up to the same value, and puts it back whenever something else changes it (for example
   G-Helper's GPU power setting, or the laptop switching power profile). The status line shows it
   as `NVPCF 80+60 W` (fixed base + variable part) with the result of the last sync.

The chosen value gets a check mark in the submenu. The hold lasts until you pick *Stop holding*,
*Restore defaults*, or exit the tool. It does **not** survive a reboot unless the tool is started
again (see [Installation](Installation.md) for auto-start).

### Load vBIOS dump...
Reads a saved copy of your GPU's vBIOS (`.rom`) and finds the Board-Power MAX field in it. The
file is only read; nothing is flashed. The result is remembered per vBIOS version in
`%ProgramData%\NvTgpHold\vbios-offsets.txt`, so you only do this once (again only after a vBIOS
update). This is the recommended way to get MAX ready.

**Saving a vBIOS dump with GPU-Z**
1. Download [GPU-Z](https://www.techpowerup.com/gpuz/) and run it as administrator.
2. Make sure the NVIDIA GPU is active (Standard or Ultimate GPU mode, not Eco) and selected in the
   drop-down at the bottom.
3. Click the share icon (arrow out of a box) right of **BIOS Version** and choose **Save to file...**.
4. In NvTgpHold choose **Load vBIOS dump...** and pick that file.

### Read vBIOS (nvflash)
Does the same as *Load vBIOS dump*, but reads the vBIOS directly with NVIDIA's `nvflash64.exe`
(read-only `--save`). NvTgpHold does not include nvflash: put `nvflash64.exe` next to
`NvTgpHold.exe` (or in a `tools` subfolder, `C:\NVFlash`, or on `PATH`).

### Set MAX ceiling (reboot) → 120 ... 250 W
Writes a persistent MAX override (`romOverride00`) to the NVIDIA driver's registry key, at the
MAX field found in your vBIOS. **Nothing changes until you reboot.** After rebooting, the status
line should show the new `max`.

If no vBIOS has been loaded yet, this option asks for a vBIOS dump first, then sets MAX.

The 200, 225 and 250 W steps are for GPUs whose stock MAX is already 175 W (for example
Strix models). Don't go far above what your laptop's cooling and power adapter are built for;
see [Safety](Safety.md).

### Sync NVPCF TGP to hold
On by default. NVIDIA's platform controller (NVPCF) reads the GPU's TGP from the BIOS in two
parts, the same two G-Helper shows:

| ATK ID | Part | Notes |
|---|---|---|
| `0x00120099` | Fixed base | Read-only. BIOS 310 on the GA403WW always reports 80 W. |
| `0x00120098` | Variable part, added to the base | G-Helper's GPU power setting. The BIOS profiles set it to 0, 5 or 15 W. |

This option sets the variable part to `hold - base` (Dynamic Boost is off while holding). Holding
140 W on a GA403WW gives `80+60 W`. A hold that would need a variable part outside `0` to
`MAX - base` (the same range G-Helper's slider allows) is not written, and the status line says
why. After every write the tool reads the value back, and the status line says `sync failed` if
the BIOS rejected it or the read-back doesn't match.

The tool remembers the variable part from before its first write (for example G-Helper's 70 W)
and puts it back when you uncheck this option, pick *Stop holding*, or exit. *Restore defaults*
sets it to 0 instead, the value the stock BIOS profile uses.

### Keep Dynamic Boost on (+25 W)
Off by default. When checked, a hold leaves ASUS Dynamic Boost **on** at 25 W instead of turning
it off, and the NVPCF variable part becomes `hold - base - 25` (so holding 150 W gives
`80+45 W +25 boost`, and 175 W gives `80+70 W +25 boost`). Turning boost on makes the platform reset
the enforced limit to 95 + 25 W; the hold notices within about 3 seconds and re-applies your
target, so with this option the status line's re-assert count shows how often that happens.

### Auto re-assert in background
Off by default; the choice is remembered in `%ProgramData%\NvTgpHold\settings.txt`. When checked,
the tool re-sends the hold and the CPU profile on a timer, whether or not it saw anything drop:

- **GPU**: the E633 call at the hold value. This is the same call the firmware test uses as a
  no-op, so when nothing moved it changes nothing. The timer does **not** toggle Dynamic Boost or
  write NVPCF, because both notify the NVIDIA driver (`Notify(NPCF, 0xC0)` in the BIOS). The 3 s
  poll still re-syncs NVPCF and does the full re-assert when the enforced limit drops.
  Skipped while the live MAX is below the hold.
- **CPU**: the selected CPU profile (see below), if any.

The status line shows `every 2 s, auto #57 14:02:11 GPU ok CPU ok`.

**What it can and can't do.**

- **GPU**: it smooths short dips. It does **not** beat a sustained over-threshold event. In the
  2026-10-20 capture the platform forced the GPU to 95 W (boost stripped) after about 50 s of heavy
  load and re-asserts did not win it back until the load ended.
- **CPU**: the EC only sends a CPU limit to the SMU when the value it wants changes (it compares
  against its last-sent copy), so at a steady load, with the ladder parked, the re-applied profile
  should stick. On a load change the EC steps again and the next tick puts the profile back.
  Not yet confirmed on hardware.

The menu item's tooltip says the same.

### Re-assert every → 1 s (minimum) / 2 s (default) / 3 / 5 / 10 / 30 s
How often *Auto re-assert* fires. Why 2 s is the default and 1 s the floor:

| Input | Value | Source |
|---|---|---|
| EC throttle tick | ~4–5 s; ladder down-steps seen 2–5 s apart under load | EC firmware decode (`0x3655C`, 2-tick recovery debounce) and the 2026-10-20 capture (26→19→13→6→5 W at 01:37:22/27/37/39/43 UTC) |
| When the EC writes the CPU limit to the SMU | Only on a ladder step change (suppress flag `0x800A00` bit 6), not every tick | EC firmware, consumer `0x39214` |
| Cost of one CPU write | 3 ATK calls; each runs `ALIB(0x0C)` and an AML `Sleep(20 ms)`, so ≥60 ms of ACPI time per round. No GPU notify. | BIOS 310 DSDT, `WMNB` DEVS `0x001200A0/A3/C1` |
| Cost of one GPU write | One NvAPI session plus one RM control; the firmware test uses the same call as a no-op | `NvApi.cs` |
| Rate limits | None in the AML (no counter or lockout); the SMU mailbox is shared with the EC's own writes | DSDT; EC firmware |

At 2 s the EC's value stays in place for at most one host period after each EC step, which is
shorter than one EC tick, so the host write lands before the EC can move again. Going faster
gains nothing measurable, because the EC only changes the limit on a step. Below 1 s the ALIB
calls take a growing share of ACPI time and contend with the EC's SMU mailbox traffic and with
G-Helper if it runs, so 1 s is the floor.

### CPU power (Strix Point) → Off / 28 W / 35 W / 45 W
Shown only on AMD Strix Point CPUs (family 1Ah, models 0x20–0x2F, e.g. Ryzen AI 9 HX 370). Each
profile sets the three CPU power limits through the ASUS ATK interface, the same calls G-Helper
uses, fast first:

| Profile | SPL (sustained, `0x001200A3`) | SPPT (slow, `0x001200A0`) | FPPT (fast, `0x001200C1`) |
|---|---|---|---|
| 28 W | 28 | 35 | 45 |
| 35 W | 35 | 50 | 65 |
| 45 W | 45 | 65 | 90 |

Picking a profile applies it once, and again after power and resume events. With *Auto re-assert*
on it is re-applied every period, so the platform defaults can't stick after a throttle event.
The SMU still clamps every value to the platform maximum.

The BIOS can't report the CPU limits back (DSTS for these IDs always returns `0x10000`), so the
tool can't verify a write or restore the values from before. **Off** stops writing; the last
values stay until the next ASUS or G-Helper mode change.

**Efficient Enabled boost mode** (on by default) sets Windows' *Processor performance boost mode*
to *Efficient Enabled* on the active power plan, for both AC and battery. The previous mode is put
back when you uncheck it, choose **Off**, use *Restore defaults*, or exit.

**Hold EC CPU ladder at step 0 (GA403WW BIOS 312)** is enabled only on a GA403WW running BIOS 312
with PawnIO installed. Under sustained GPU load the EC walks its own CPU limit table down (80 W at
row 0, then 35/30/26/19 W...) and writes each row to the SMU, independent of the profile above. Every
2 s, on its own timer, this zeroes the EC's throttle accumulators through EC RAM (vendor commands
0x92/0x93 on ports 0x62/0x66 via PawnIO's signed `LpcACPIEC` module), so the table stays at row 0.
It writes only while the EC is in ASUS mode 1 with the OS-present flag set, and never writes that flag.
The status line shows the step it found each tick (`EC ladder was step N (W)`); step 0 means the hold is
keeping up. Measured on 2026-10-30: without it the step sat at 2–4 under a 110–150 W GPU load; with it,
2,400 of 2,400 one-second samples read step 0 with no EC errors.

### Stop holding
Stops re-applying CURRENT. The current value stays until the driver or platform changes it,
which usually happens at the next power event. Dynamic Boost stays off.

### Restore defaults (95 + 25 stock)
Stops the hold, removes the MAX override, sets CURRENT back to 95 W, sets the NVPCF variable GPU power back
to 0 and turns Dynamic Boost back on (95 + 25 W). It also turns the CPU profile off and restores the
Windows boost mode. **Reboot** to fully return MAX to the factory value.

### Exit
Closes the tool. The hold stops; whatever limit is active stays until the next power event or
reboot.

## Recommended workflow

1. Plug in the charger and check the [Windows power settings](#recommended-windows-power-settings) below.
2. **Run firmware / driver test**, confirm PASS.
3. If needed, **Set MAX ceiling** (the first time it asks for a vBIOS dump) to a modest value such as 130 W and reboot.
4. **Hold CURRENT** at or below MAX.
5. Run a sustained GPU load (a game or benchmark for 10+ minutes) and watch temperatures and
   board power in HWiNFO or GPU-Z (see [Safety](Safety.md)).
6. Step up 10 W at a time only while temperatures stay comfortable.
7. When finished experimenting, use **Restore defaults** and reboot.

## Working with G-Helper and Armoury Crate

Those tools can apply their own GPU limits and Dynamic Boost settings on mode changes, which
fight the hold. Either close them while holding, or make sure they don't change GPU power
settings. NvTgpHold notices a drop within about 3 seconds and puts the limit back, so there can be a short gap.

NvTgpHold checks every 30 s whether G-Helper is running with **Auto apply** power limits on
(`auto_apply_power_*` in `%APPDATA%\GHelper\config.json`). If it is, the status line shows
`G-Helper auto-apply ON` and a notification asks you to turn it off or close G-Helper, because it
re-writes the same CPU and GPU limits on every mode or power change.

## Battery

On battery the platform may still cap power regardless of the hold. Raised limits are intended
for use on the charger.

## Recommended Windows power settings

Pair NvTgpHold with these Windows settings so the CPU and GPU get full power on the charger and
the laptop saves energy on battery.

### 1. Control Panel power plan: Balanced

1. Press **Win + R**, type `powercfg.cpl`, press Enter.
2. Select **Balanced**.

Keep the classic plan on Balanced. Windows only applies the per-source power modes below while
the Balanced plan is active; picking *High performance* or *Ultimate Performance* here overrides
them and keeps the laptop at full power on battery too.

### 2. Settings power mode: best performance on AC, best efficiency on battery

1. Open **Settings → System → Power & battery**.
2. Under **Power mode**, set:
   - **Plugged in**: **Best performance**
   - **On battery**: **Best power efficiency**

If your Windows version shows a single Power mode drop-down instead of separate plugged in / on
battery choices, set it to Best performance while on the charger and Best power efficiency while
on battery.

Command-line equivalent for step 1 (elevated terminal): `powercfg /setactive SCHEME_BALANCED`

### Why this matters

- On the charger, *Best performance* lets the CPU boost freely, so the extra GPU power from a
  held CURRENT limit isn't wasted by a CPU bottleneck.
- On battery, *Best power efficiency* keeps drain and heat down; the platform caps GPU power on
  battery anyway (see [Safety](Safety.md)).
- Armoury Crate or G-Helper modes may also change these settings. If you use them, check the
  Power mode again after switching their profile.
