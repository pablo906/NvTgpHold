using System;
using System.Threading;

namespace NvTgpHold
{
    // GPU display mode (Eco / Standard / Ultimate) through ASUS ATK, mirroring G-Helper's GPUModeControl:
    //   GPUMux 0 = Ultimate (MUX routed to the dGPU), != 0 = hybrid/Optimus; GPUEco 1 = Eco (dGPU off, iGPU only).
    // A MUX change (into or out of Ultimate) only takes effect after a reboot. An Eco toggle does not reboot, but the
    // display stack re-initialises, so a sign-out or reboot is recommended for a clean switch. Experimental: NvTgpHold
    // does not do G-Helper's full NVIDIA-service teardown around an Eco change.
    internal enum GpuDisplayMode { Eco, Standard, Ultimate }

    internal static class GpuMode
    {
        // Reads the current mode, or null if the platform doesn't report MUX/Eco.
        public static GpuDisplayMode? Current(out string detail)
        {
            int mux = AtkAcpi.ReadGpuMux(), eco = AtkAcpi.ReadGpuEco();
            if (mux < 0) { detail = "GPU mode not reported (mux unreadable)"; return null; }
            if (mux == 0) { detail = "Ultimate"; return GpuDisplayMode.Ultimate; }
            if (eco == 1) { detail = "Eco"; return GpuDisplayMode.Eco; }
            detail = "Standard"; return GpuDisplayMode.Standard;
        }

        // Applies the target mode. Returns false with a reason on failure. needsReboot is set when a MUX change was made
        // (the change only applies after a reboot); the caller offers the reboot. From Ultimate to Eco needs two steps
        // (switch to Standard + reboot first), which is reported rather than done silently.
        public static bool Apply(GpuDisplayMode target, out bool needsReboot, out string detail)
        {
            needsReboot = false; detail = "";
            var cur = Current(out string curDetail);
            if (cur == null) { detail = curDetail; return false; }
            if (cur == target) { detail = "already " + target; return true; }
            int mux = AtkAcpi.ReadGpuMux();

            switch (target)
            {
                case GpuDisplayMode.Ultimate:
                    // Turn Eco off first (dGPU must be on), then route the MUX to the dGPU.
                    AtkAcpi.SetGpuEco(0, out _);
                    Thread.Sleep(500);
                    if (AtkAcpi.ReadGpuEco() == 1) { detail = "could not disable Eco before switching to Ultimate"; return false; }
                    if (!AtkAcpi.SetGpuMux(0, out detail)) return false;
                    needsReboot = true; detail = "Ultimate set (" + detail + ") - reboot to apply"; return true;

                case GpuDisplayMode.Standard:
                    if (mux == 0)   // leaving Ultimate: MUX change + reboot
                    {
                        if (!AtkAcpi.SetGpuMux(1, out detail)) return false;
                        needsReboot = true; detail = "Standard set (" + detail + ") - reboot to apply"; return true;
                    }
                    if (!AtkAcpi.SetGpuEco(0, out detail)) return false;   // hybrid, just turn Eco off
                    detail = "Standard set (" + detail + ")"; return true;

                case GpuDisplayMode.Eco:
                    if (mux == 0) { detail = "switch to Standard first (needs a reboot), then Eco"; return false; }
                    if (!AtkAcpi.SetGpuEco(1, out detail)) return false;
                    detail = "Eco set (" + detail + ") - sign out or reboot for a clean switch"; return true;
            }
            detail = "unknown mode"; return false;
        }
    }
}
