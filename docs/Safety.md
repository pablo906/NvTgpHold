# Safety

**You use NvTgpHold entirely at your own risk.** The software is provided "as is" with no warranty
(see the [LICENSE](../LICENSE)). The authors are not responsible for damage to your hardware or data.

## Risks

- Higher GPU and VRM temperatures and fan noise
- Driver crashes, black screens, or system instability
- Higher battery drain and charger load; on battery the platform may still throttle
- Accelerated component wear or permanent damage
- Possible warranty implications

## Good practice

- Plug in the original charger when running raised limits.
- Monitor with a tool like HWiNFO or GPU-Z: GPU temperature, hotspot, board power.
- Raise limits in small steps (10 W) and test each one under sustained load.
- Back off immediately if you see artifacts, crashes, or temperatures near the throttle point.
- Do not run raised limits on a soft surface or with blocked vents.

## Reverting

Tray menu → **Restore defaults**, then reboot. This removes the registry override and turns
Dynamic Boost back on. If the tool will not start, the override lives under the NVIDIA display
adapter key in `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}`;
reinstalling the NVIDIA driver with a clean install also clears it.
