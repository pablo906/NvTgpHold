# EC CPU ladder: investigation and the runtime hold

For developers picking this up. It covers what the ASUS EC on the GA403WW does to the CPU under GPU load, how
we found it, exactly what NvTgpHold's *Hold EC CPU ladder* writes, and why it is gated the way it is.
Hardware: ROG Zephyrus G14 GA403WW (Ryzen AI 9 HX 370, RTX 5080 Laptop), ITE IT5xxx EC with RV32 firmware.

## Summary

Under sustained dGPU load the EC walks a CPU power-limit table down, from 80 W to 35, 30, 26, 19 W and lower,
and pushes each row to the SMU. G-Helper, ATK and ALIB limits don't stop it, because the EC re-sends its own row
on every step change. The EC firmware already contains a routine that zeroes the ladder's accumulators. NvTgpHold
does the same zeroing from the host every 2 s over the EC's vendor RAM commands, so the table stays at row 0
(80 W). Nothing is flashed, and everything is undone on the next EC reset, sleep or reboot.

## How it was found

1. **EC image.** We carved the EC firmware from the BIOS capsule (`GA403WW.310`, EC image at capsule offset
   `0x81800`; BIOS 312 uses the same base) and disassembled it as RV32IMC.
2. **Static trace.** We followed the CPU PPT the EC sends to the SMU back to a 12-byte-record table, the step
   index that selects the row, the decision function that moves the index, and the sensor it reads.
3. **Emulation.** We ran the decision function `0x3655C` (310) in a unicorn RV32 emulator against the full
   image. That confirmed it's a hysteresis zone classifier comparing the raw sensor to baked doubles, not a
   proportional loop.
4. **Runtime confirmation (BIOS 312).** We read EC RAM live with vendor command `0x92` through PawnIO's signed
   `LpcACPIEC` module, while HWiNFO and nvidia-smi logged CPU and GPU power. The live index, limit shadows and
   reset behaviour matched the static trace (below).

## The mechanism

```
ITE HW-monitor 0xF0193E (signed 16-bit, dGPU/shared-rail power or current; unit NDA)
  -> RAM 0x80078C -> copy 0x800A3C
  -> decision 0x3655C (310): compare vs per-mode doubles, mode byte 0x8009A2
       over trip  -> candidate += 1 immediately each tick
       under recover -> debounce 0x800A61; candidate -= 1 every 2nd tick
  -> candidates 0x800A0B / 0x800A0C -> applied step 0x800A58 = max(candidates)
  -> row = table + step * 12 -> CPU limits copied to shadows 0x800936/938/93A (+0x800950) -> SMU
```

Thresholds (mode 1, which is what the GA403WW runs on AC here): **trip at raw 545, recover at raw 317**. The inner
zones are 613 and 552. Modes 2–4 use 255/254. Between the two thresholds the step holds.

Mode-1 table, verified byte-for-byte in BIOS 312 (record: `idx, limA, limB, limC, ch4, D-notify, out1, out2, FFFF, 0000`):

| Step | CPU limits (W) | 4th channel | GPU outputs (+6/+7) |
|---|---|---|---|
| 0 | 80 / 80 / 80 | 216 | 0 0 |
| 1 | 35 / 35 / 35 | 95 | 0 0 |
| 2 | 30 / 30 / 30 | 82 | 0 0 |
| 3 | 26 / 26 / 26 | 69 | 0 0 |
| 4 | 19 / 19 / 19 | 52 | 0 0 |
| 5 | 13 / 13 / 13 | 35 | 0 0 |
| 6 | 6 / 6 / 7 | 17 | 0 0 |
| 7–9 | 5 / 5 / 7 | 11 | 0 0 |
| 10 | 5 / 5 / 7 | 11 | 1 0 |
| 11–13 | 5 / 5 / 7 | 11 | 1 1 |

Steps 10–13 assert two EC GPIO outputs (descriptors 76 and 48), which is believed to be the dGPU boost strip to
95 W. The D-notify code is `D1` on every row, so the ladder does not move the GPU through the notify path.

## Locations by BIOS

EC-image offsets (base 0). Add `0x81800` for the offset inside the capsule file. EC RAM addresses are the same in
both versions.

| Item | BIOS 310 (static + emulated, **not runtime-tested**) | BIOS 312 (verified in capsule; runtime-tested) |
|---|---|---|
| Recover threshold 317.0 (double) | `0x41F20` | `0x42080` |
| Trip threshold 545.0 | `0x41F30` | `0x42090` |
| Inner 613.0 / 552.0 | `0x41F58` / `0x41F68` | `0x420B8` / `0x420C8` |
| Mode-1 ladder table | `0x460EC` (second copy `0x46194`) | `0x4624C` (second copy `0x462F4`) |
| Decision function | `0x3655C` | `0x366BC` (+0x160, inferred) |
| Reset routine | `0x37CE0` | `0x37E40` |
| Periodic dispatch | `0x37EEA` | `0x3804A` |

Code and constants moved by +0x160 between 310 and 312. The 312 thresholds and table above were read directly
from `GA403WWAS.312`. For 312, the decision-function address is inferred from that shift and wasn't traced.

## Default behaviour we observed (BIOS 312, AC, mode 1, 2026-10-30)

- **Idle:** step 0, shadows 80/80/80 and 216. Mode `0x8009A2` = 1, `0x80128D` = `0x0B` (later `0x09`; only bit 0
  is meaningful here), ACIN `0x800344` = `0x43`, `0x80099E` = 200 (matches the firmware initializer).
- **Under a 100–150 W GPU load:** the step climbs to 2–4 and oscillates between 3 and 4 every 5–30 s, so the CPU
  is held at 26/19 W and CPU package power reads 14–20 W. Only candidate `0x800A0C` climbs, and `0x800A0B` stays 0.
  The output requests (`0x800A00`, `0x800A6E/6F`) stayed 0 and D-notify stayed `D1`. The ladder never reached step
  10, so on this machine the GPU limit came from NVIDIA's NPCF and power limits, not from the ladder.
- **A single reset during load:** step 3 → 0 (80 W) immediately, then 1 at +4 s, 2 at +10 s, 3 at +16 s. A
  one-shot reset is transient, so the hold repeats it.

## What NvTgpHold writes

Every 2 s, on its own timer, under the `Access_EC` mutant:

1. Read mode `0x8009A2` and flags `0x80128D`. **Continue only if mode == 1 and `0x80128D` bit 0 is set.**
2. Zero `0x8009AC`–`0x8009BB` (averaging/timing block, including debounce `0x8009B1`), `0x8009DC` (combined
   result), and `0x800A0B` / `0x800A0C` (step candidates).
3. Clear bit 2 of `0x800A6E` and `0x800A6F` (read-modify-write).

That's the same set the EC's own reset routine (`0x37E40` in 312) clears. The firmware then recomputes the applied
step from the zeroed candidates. We never write `0x800A58`, the shadows, the thresholds or `0x80128D`.

Transport: ASUS vendor commands over the ACPI EC ports, through PawnIO's signed `LpcACPIEC` module (byte I/O on
`0x62/0x66` only).
- Read: `0x92` → addr high → addr low → read the data byte. Write: `0x93` → addr high → addr low → value.
  Addresses are `0x800000 | (high << 8) | low`, one byte per transaction. There is no block read.

## Why it's safe to leave running

- **We don't use the gate.** Clearing `0x80128D` bit 0 makes the EC run its reset *and* skip the decision every
  tick, which would stop the ladder outright. But that bit is the EC's global OS-present flag (about 98 readers,
  13 writers): it selects sensor values, policy branches and SCI/_Qxx event delivery. Holding it clear would put
  fans, charging and ACPI events into OS-absent behaviour, so we read it and never write it.
- **Only the accumulator state is reset.** These are the bytes the firmware itself zeroes on its own path, so the
  worst outcome is the ladder recomputing from zero.
- **Gated three ways:**
  - Model `GA403WW` + BIOS `GA403WW.312`, checked on launch from the registry. On any other model or BIOS the menu
    item is disabled and *Run firmware / driver test* says why.
  - PawnIO and the module present.
  - At runtime, mode 1 with OS-present set. Any other mode uses a different table family and different thresholds,
    so we skip.
- **Bus discipline, and its limit.** Two other owners drive ports `0x62/0x66` without taking `Access_EC`: ACPI.sys's
  EC driver, and ASUS's own AML (`ECMD`, which writes `ECCP`/`ECDP` directly under the AML-private `MUT0`). From user
  mode a multi-byte 0x92/0x93 transaction can't be made atomic against either of them, so a command from one of them
  can land between our address and value bytes. What the code does about it:
  - It takes `Global\Access_EC`, as LibreHardwareMonitor, FanControl and NBFC do, so cooperating tools don't overlap.
  - It starts a transaction only when IBF, OBF, BURST **and SCI_EVT** are all clear. SCI_EVT means ACPI.sys is about
    to issue a query. It never drains an OBF byte it didn't ask for, because that byte belongs to someone else.
  - Every 0x93 write is confirmed with a 0x92 readback. If OBF or BURST appears while we're sending command or address
    bytes, or a readback doesn't match, the attempt is treated as a collision.
  - Retries are bounded by a deadline, not a count. The hold runs on a fixed 2 s slot, off the UI thread. Within a
    slot it retries after a flat random 5–50 ms until 1.5 s into the slot, and the next slot starts on schedule. There
    is no exponential back-off and no multi-second pause, because the ladder climbs a step every ~4–6 s. Jitter keeps
    the cadence from phase-locking with a periodic AML poll.
  - It writes only bytes that aren't already in the reset state, so a retried attempt resumes where the last one
    stopped and writes stay minimal.
  - A breaker trips only on a fault: **two slots in a row with no confirmed reset** (about 4 s). The hold then turns
    itself off and says why. Slots skipped because the EC isn't in mode 1 / OS-present don't count.

  This narrows the window and detects most collisions after the fact. It does not eliminate them.

  **No coordinated route exists on BIOS 312.** We checked the DSDT and all SSDTs for an ATK/WMNB path that could
  run the transaction inside ACPI.sys under `MUT0`:
  - `ECMD(cmd)` only writes one command byte under `MUT0`. Its callers are `DNOT` (GPU D-notify) and AMD PEP `_DSM`.
  - `ECM1(port, cmd, data)` is a generic writer with no mutex, called only from `UPHK` (PEP `_DSM`).
  - WMNB's `RMEM` reads one byte of physical memory, and EC RAM outside the 0x800300 H2RAM page isn't mapped.

  No AML issues 0x92/0x93. Fully coordinated options are a firmware change or a kernel driver. Confirmed with an iasl
  20260929 disassembly of the BIOS 312 DSDT and all SSDTs:
  - `ECMD` is only called with 0xAE/0xAD (`DNOT`) and 0x97/0x99 (SSDR PEP `_DSM`).
  - `ECM1` is on a separate channel, SystemIO `0x68/0x6C`, and only ever sends 0x98/0x86.
  - The AML's `ECCP`/`ECDP` field is declared `Lock`, so ASUS AML serializes on the ACPI Global Lock. A kernel-side
    transaction that took the Global Lock would at least be coordinated with the AML users. User mode can't take it.

  A tick is about 25 transactions from one process. An earlier test fired 70 back-to-back reads from 70 separate
  process launches, and that coincided with the EC's fan mode resetting, so keep EC access in one process at low
  rate.

## Prior art

Nobody on Windows has user-mode exclusion against ACPI.sys. The safe implementations go through the kernel EC
driver or through AML. The rest take a named mutex, keep transactions short and retry.

- **Linux `acpi_ec`** ([drivers/acpi/ec.c](https://github.com/torvalds/linux/blob/master/drivers/acpi/ec.c)):
  owns 0x62/0x66 and serializes with `ec->mutex` plus the ACPI Global Lock (when `_GLK`). Vendor commands go through
  `ec_transaction()` under the same locks. `ec_sys` only does 0x80/0x81, so it has no 0x92/0x93.
- **Linux `asus-ec-sensors`**
  ([drivers/hwmon/asus-ec-sensors.c](https://github.com/torvalds/linux/blob/master/drivers/hwmon/asus-ec-sensors.c)):
  also takes the board's AML mutex or the Global Lock, because ASUS firmware touches the EC from ACPI without
  declaring it.
- **LibreHardwareMonitor / FanControl**
  ([WindowsEmbeddedControllerIO.cs](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Hardware/Motherboard/Lpc/EC/WindowsEmbeddedControllerIO.cs)):
  raw 0x62/0x66 under `Global\Access_EC` only, retried per byte.
- **PawnIO `LpcACPIEC`** ([LpcACPIEC.p](https://github.com/namazso/PawnIO.Modules/blob/main/LpcACPIEC.p)): byte port
  I/O only. The caller is expected to hold `Access_EC`.
- **G-Helper / asusctl** ([AsusACPI.cs](https://github.com/seerge/g-helper/blob/main/app/AsusACPI.cs)): ATK
  DSTS/DEVS through ACPI, so they're serialized for free but can't reach arbitrary EC RAM. On BIOS 312, G-Helper's
  debug "ACPI DEVS Test" is a generic `DEVS(id, value)`, and none of the 93 WMNB DEVS handlers takes an address.
- **Retry shape:** we take the jitter from AWS's
  [Exponential Backoff and Jitter](https://aws.amazon.com/blogs/architecture/exponential-backoff-and-jitter/), but
  not the exponential part. There's one contender, not a herd, and there's a hard deadline: one ladder step.

Still not solved: a 0x93 interleaved after its command byte could land on a wrong address before the readback
catches it. The complete fix is taking the ACPI Global Lock (FACS handshake) around the transaction in kernel
mode, for example in a custom PawnIO module. It's unverified whether PawnIO lets a module map FACS.

## Measured result (BIOS 312)

10-minute hold under a 100–150 W GPU load: the step read 0 in 2,400 of 2,400 one-second samples, with 126,984 EC
transactions and 0 failures (11 retries). In a later 1 Hz session it read 0 in 161 of 161 samples while the GPU
ran at about 148 W. With the ladder held, the CPU limit stays at the 80 W row, and the CPU's actual limit is
whatever SMU/ATK limits you set below that (CPU profile or G-Helper).

## Fan tables (found along the way, not used by the hold)

- `0x800B00`: active custom CPU fan curve, 8 temperatures then 8 duties (exactly what G-Helper writes).
- `0x800B10`: GPU fan curve, same layout.
- `0x800B20` / `0x800B21`: current curve point for CPU and GPU (rises 4 → 6 with temperature).
- `0x801195` / `0x80119E`: working duty copies. Under load the EC raised point 4 from 60% to 100% here while the
  source curve was untouched.
- `0x8011B0` / `0x8011C2`: built-in curve (duties 0–95%).

## Firmware fixes (not done; Secure Flash, needs an SPI programmer)

For completeness: raise the trip double, which stops the ladder for both CPU and GPU; floor the table's CPU rows
(CPU only); or zero `+6/+7` on steps 10–13 (GPU only). The offsets for each BIOS are in the table above.
