# Safety and reverting changes

NvTgpHold runs elevated and changes GPU/CPU power policy beyond manufacturer
defaults. It is unofficial and provided without warranty under the
[MIT license](../LICENSE). Safe power levels have not been established for other
machines, and available menu targets are not recommendations.

## Risks and limits

Higher limits can increase temperatures, fan noise, charger/battery load and
component wear. Instability, data loss and permanent hardware damage are possible.
Warranty coverage may be affected. A successful driver test does not assess
cooling, power delivery or long-term reliability.

The experimental EC hold adds a separate risk: shared raw EC ports cannot be
exclusively owned by this application. A collision can misplace a byte before it
is detected. Retries and readback cannot undo that write. Fan-mode anomalies were
observed in development, without a proven cause. The current retry/breaker path
has not had hardware validation. Leave this feature off unless you accept these
limitations; see [EC CPU ladder](EC-Ladder.md).

## While using the utility

- Use the appropriate charger and keep cooling vents clear on a hard surface.
- Start conservatively. Monitor CPU/GPU temperatures, fans, power and stability
  under sustained load, using hardware monitoring tools you trust.
- Stop and revert if fan behavior changes unexpectedly, temperatures are excessive,
  or you see artifacts, crashes, black screens or abnormal battery discharge.
- Avoid conflicting automatic power writers. Repeated re-asserts do not prove a
  sustained workload is safe or that firmware limits can be overridden reliably.
- Save work and close dGPU applications before a GPU device restart.

## Reverting changes

1. Choose **Reset to defaults (Balanced + Optimized)**. This stops both holds and
   Auto re-assert, selects Balanced power with its default fan curves and Optimized
   GPU mode, clears fan overrides, and requests zero GPU offsets and CPU undervolt.
   It disables experimental tuning and restores captured Windows boost settings.
   Check the status for failed writes; a requested reset is not proof of success.
2. If a MAX override was removed, accept the GPU restart after closing applications,
   or reboot. Another tool's recognized override is left alone; remove it in that tool.
3. Balanced applies its own CPU limits and, when SMU access is available, its CPU
   temperature target. To restore the firmware's unmodified limits instead, use
   an ASUS/G-Helper profile change or reboot. Previous PPT values cannot be read back.
4. Exit NvTgpHold. Disable any scheduled startup task before further troubleshooting.

Simply exiting does not remove MAX or clear saved selections. Disabling the EC
hold stops future slots after the current slot finishes; it does not roll back a
misplaced write. If fan or platform behavior stays abnormal, stop using the feature
and follow ASUS's model-specific recovery guidance rather than repeatedly retrying.

If the application cannot start, prevent its startup task from running and reboot.
Its MAX override is named `romOverride00` under the matching NVIDIA adapter key in
`HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}`.
Do not delete an override belonging to another tool or edit unrelated adapter keys.
Seek assistance if you cannot identify the owner. Deleting application files or
the settings folder alone does not remove this registry value.
