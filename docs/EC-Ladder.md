# EC CPU ladder technical reference

This reference describes the experimental CPU-ladder hold on **GA403WW BIOS 312**.
For enabling, stopping and interpreting status, see [Usage](Usage.md#experimental-ec-cpu-ladder-hold).
The feature is off by default. Its raw-port transport is not exclusive against
Windows/ASUS firmware and can misplace writes; see [Safety](Safety.md).

## Scope and evidence

The implementation derives its address set from maintainer analysis of the ITE
IT5xxx EC firmware on a GA403WW (Ryzen AI 9 HX 370, RTX 5080 Laptop GPU).
BIOS 310 was statically traced and emulated; BIOS 312 supplied address/table checks
and earlier live observations. Only `GA403WW.312` is accepted by the runtime gate.

Earlier development captures reported ladder steps 2–4 under roughly 100–150 W
GPU load, with CPU limits of 30/26/19 W. A one-shot reset was reported to return
step 3 to 0, followed by step 1 around four seconds later. These observations
motivated the 2-second cadence; they do not establish reliable exclusion or safety.

The final readback/retry/breaker path has not been validated on hardware. Earlier
captures do not provide endurance evidence for this implementation. Software
validation of worker cancellation and target gates does not validate EC writes.

## The mechanism

```
ITE HW-monitor 0xF0193E (signed 16-bit, inferred dGPU/shared-rail metric; units unknown)
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

## What NvTgpHold writes

Each background slot attempts the following under the `Global\Access_EC` mutex; only bytes not already reset are written:

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

## Worker and failure handling

`Program.StartEcHold` schedules slots on a background worker at a 2-second cadence.
`EcLadder.RunSlot` retries attempts with 5–50 ms jitter within a 1.5-second retry
budget; an in-flight attempt can extend beyond that budget. The worker checks
cancellation after a slot, and breaker callbacks check ownership of their captured
cancellation token before changing shared state.

Two consecutive failed slots disable the hold, clear its saved opt-in and report
a reason. Skipped slots (wrong mode or OS-present flag absent) do not count toward
the breaker. Failure does not prove a collision: live counters can change between
a write and readback, and the EC can be busy for other reasons.

The model/BIOS gate is checked on launch. The PawnIO library and module must be
present. A slot then checks the traced mode and source flags before attempting
writes. These gates reduce exposure but cannot provide a transaction lock.

## Transport limitations

The code takes `Global\Access_EC` to coordinate with cooperating applications.
It waits for IBF, OBF, BURST and SCI_EVT to clear before starting a transaction,
bounds handshake waits and reads back writes. An unexpected OBF/BURST state or a
readback mismatch abandons the attempt. It does not drain unsolicited output bytes.

ACPI.sys and ASUS AML do not take this application mutex. Another command can
arrive between command/address/value bytes without setting a detectable status
bit. Readback can detect some mismatches after the fact; it cannot establish that
no other address was touched or undo a misplaced write. No collision-detection
coverage percentage has been established.

The maintainer's BIOS 312 DSDT/SSDT analysis found no exposed ATK/WMNB method for
arbitrary 0x92/0x93 EC RAM transactions. Reported findings:

- `ECMD` sends 0xAE/0xAD for DNOT and 0x97/0x99 for PEP, under `MUT0`.
- `ECM1` uses the separate SystemIO 0x68/0x6C channel and sends 0x98/0x86.
- `RMEM` reads physical memory, not arbitrary EC RAM outside the mapped H2RAM page.
- ASUS's `ECCP`/`ECDP` AML fields declare `Lock`, involving the ACPI Global Lock.

That investigation did not identify a coordinated route available to this app.
A kernel/firmware transport with appropriate coordination would need separate
implementation and hardware validation; this release does not provide one.
Fan-mode anomalies observed during development are not proof of a specific race.

## Source references

- Runtime: [EcLadder.cs](../EcLadder.cs), [EcLadderTarget.cs](../EcLadderTarget.cs),
  [Program.cs](../Program.cs).
- Module provenance, checksum, license and corresponding source:
  [PawnIO.Modules](../third_party/PawnIO.Modules.md).
- Related EC implementations for further study:
  [Linux ACPI EC](https://github.com/torvalds/linux/blob/master/drivers/acpi/ec.c),
  [ASUS EC sensors](https://github.com/torvalds/linux/blob/master/drivers/hwmon/asus-ec-sensors.c),
  [LibreHardwareMonitor EC I/O](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Hardware/Motherboard/Lpc/EC/WindowsEmbeddedControllerIO.cs).

These references provide context; they do not establish that NvTgpHold has the
same synchronization properties or validation as those projects.
