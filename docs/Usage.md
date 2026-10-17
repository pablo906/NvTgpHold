# Usage

NvTgpHold lives in the system tray (shield icon). It has no main window; everything is in the
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

### Stop holding
Stops re-applying CURRENT. The current value stays until the driver or platform changes it,
which usually happens at the next power event. Dynamic Boost stays off.

### Restore defaults (95 + 25 stock)
Stops the hold, removes the MAX override, sets CURRENT back to 95 W, sets the NVPCF variable GPU power back
to 0 and turns Dynamic Boost back on (95 + 25 W). **Reboot** to fully return MAX to the factory value.

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
