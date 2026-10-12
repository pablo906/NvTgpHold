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
HOLD 140 W  |  enf 140.0 / max 150 W  |  re-asserted 140 W (5-min)
```

1. `HOLD 140 W` or `hold off`: whether a hold is active and its target.
2. `enf 140.0 / max 150 W`: enforced and maximum limits read from `nvidia-smi`. `?` means the GPU
   could not be read (for example, the dGPU is asleep in Eco mode).
3. The last action and its result.

Double-click the tray icon to refresh immediately. Pop-up notifications (balloons) show the
details of tests, MAX changes and failures.

## Menu reference

### Run firmware / driver test
Checks that the tool can talk to the NVIDIA driver on this machine, and re-applies the current
enforced value as a harmless no-op. Result example:

`Driver/transport: PASS | MAX: ready`

- **Driver/transport PASS**: holding CURRENT will work.
- **MAX ready**: this machine already has a MAX override the tool can adjust (see below).
- **MAX not ready**: you can still hold CURRENT up to the existing MAX, but *Set MAX ceiling* is
  unavailable until a one-time MAX override exists.

Run this first after installing, and again after every NVIDIA driver update.

### Hold CURRENT → 120 / 130 / 140 / 150 / 160 / 175 W
1. Refuses if the target is above the live MAX and tells you to raise MAX first.
2. Turns ASUS Dynamic Boost **off**.
3. Sets the enforced limit to the chosen value.
4. Keeps holding it: re-applies every **5 minutes**, after plugging in or unplugging, after
   sleep/resume, and after unlocking Windows.

The chosen value gets a check mark in the submenu. The hold lasts until you pick *Stop holding*,
*Restore defaults*, or exit the tool. It does **not** survive a reboot unless the tool is started
again (see [Installation](Installation.md) for auto-start).

### Set MAX ceiling (reboot) → 120 ... 175 W
Writes a persistent MAX override to the NVIDIA driver's registry key. **Nothing changes until you
reboot.** After rebooting, the status line should show the new `max`.

The override reuses a value from an existing override on this machine. If the test says
*MAX: not ready*, this option fails with a message explaining that a MAX has to be set once with
another tool first. After that one-time setup the tool remembers the value (in
`shadow-offset.txt` next to the exe), so keep the tool in a folder it can write to.

### Stop holding
Stops re-applying CURRENT. The current value stays until the driver or platform changes it,
which usually happens at the next power event. Dynamic Boost stays off.

### Restore defaults (95 + 25 stock)
Stops the hold, removes the MAX override, sets CURRENT back to 95 W and turns Dynamic Boost back
on (95 + 25 W). **Reboot** to fully return MAX to the factory value.

### Exit
Closes the tool. The hold stops; whatever limit is active stays until the next power event or
reboot.

## Recommended workflow

1. Plug in the charger and check the [Windows power settings](#recommended-windows-power-settings) below.
2. **Run firmware / driver test**, confirm PASS.
3. If needed, **Set MAX ceiling** to a modest value such as 130 W and reboot.
4. **Hold CURRENT** at or below MAX.
5. Run a sustained GPU load (a game or benchmark for 10+ minutes) and watch temperatures and
   board power in HWiNFO or GPU-Z (see [Safety](Safety.md)).
6. Step up 10 W at a time only while temperatures stay comfortable.
7. When finished experimenting, use **Restore defaults** and reboot.

## Working with G-Helper and Armoury Crate

Those tools can apply their own GPU limits and Dynamic Boost settings on mode changes, which
fight the hold. Either close them while holding, or make sure they don't change GPU power
settings. NvTgpHold re-asserts on its 5-minute cycle, but there can be a short gap.

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
