# Linked SYS/XBAR clock offsets

The GPU overclock menu offers one session-only offset for SYS and XBAR. Both are
set to the same requested delta in MHz, independently of core, memory and video.
Equal offsets do not imply equal physical clock frequencies.

## Interface and scope

This uses private NvAPI `ClkDomainsGetControl` (`0xF58938F5`) and
`ClkDomainsSetControl` (`0xD14B69CF`), not public Pstates20 clock-domain IDs.
The implementation accepts only RTX 5080 Laptop / driver 621.01 and V2
`0x000261A4`. Unknown drivers or layouts are rejected without a write.

Layout evidence comes from the author's
[Windows XBAR implementation](https://github.com/SHANAjam/rtx5090-xbar-control/blob/main/src/xbar5090/clk_domains.py).
The domain names XBAR=1 and SYS=2 also appear in
[LACT's clock-domain enum](https://github.com/ilya-zlobintsev/LACT/blob/master/lact-daemon/src/server/gpu_controller/nvidia/nvapi.rs).
These are reverse-engineered interfaces, not an NVIDIA compatibility guarantee.

Read-only probes on the development machine returned success for this version,
the requested domain masks and the expected entry flags. They established that
the getter accepts the layout; they did not validate clock writes or stability.

## Write and failure handling

The setter reads the current control buffer, narrows its domain mask to SYS/XBAR,
and changes only their two frequency deltas. It preserves all other bytes,
including voltage offsets. A fresh readback must confirm both requested offsets;
a partial or ambiguous result leaves no checked selection. It retains reset
eligibility until a zero pair is confirmed, including after a failed setter call.

Automatic rollback with a stale full-buffer snapshot could overwrite another
utility's intervening changes, so it is deliberately avoided. Request zero again
or reboot after an unconfirmed reset. Other tuning tools can still race this
read/modify/write path; close competing automatic clock controls.

The control is opt-in, not persisted and not applied by power/resume events or
profiles. Software buffer-preservation tests do not establish that an offset is
stable on hardware. Follow [Safety](Safety.md) and [Usage](Usage.md).
