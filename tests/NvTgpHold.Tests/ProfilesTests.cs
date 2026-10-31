using Xunit;

namespace NvTgpHold.Tests
{
    public class ProfilesTests
    {
        [Fact]
        public void Fan_curves_are_16_bytes_and_not_all_zero()
        {
            foreach (var p in PowerProfiles.Presets)
            {
                Assert.Equal(16, p.CpuFan.Length);
                Assert.Equal(16, p.GpuFan.Length);
                Assert.Contains(p.CpuFan, b => b != 0);   // SetFanCurve rejects an all-zero curve
                Assert.Contains(p.GpuFan, b => b != 0);
            }
        }

        [Fact]
        public void Fan_curve_temperatures_are_non_decreasing()
        {
            foreach (var p in PowerProfiles.Presets)
                for (int i = 1; i < 8; i++)
                {
                    Assert.True(p.CpuFan[i] >= p.CpuFan[i - 1], p.Name + " cpu temps");
                    Assert.True(p.GpuFan[i] >= p.GpuFan[i - 1], p.Name + " gpu temps");
                }
        }

        [Fact]
        public void Fan_curve_percents_are_within_0_100()
        {
            foreach (var p in PowerProfiles.Presets)
                for (int i = 8; i < 16; i++)
                {
                    Assert.InRange(p.CpuFan[i], 0, 100);
                    Assert.InRange(p.GpuFan[i], 0, 100);
                }
        }

        [Fact]
        public void Ppt_keeps_fast_ge_slow_ge_sustained_within_bounds()
        {
            foreach (var p in PowerProfiles.Presets)
            {
                Assert.True(p.Fppt >= p.Sppt && p.Sppt >= p.Spl, p.Name);   // SetCpuPpt expects fast >= slow >= sustained
                Assert.InRange(p.Spl, CpuProfiles.MinW, CpuProfiles.MaxW);
                Assert.InRange(p.Fppt, CpuProfiles.MinW, CpuProfiles.MaxW);
            }
        }

        [Fact]
        public void Asus_modes_are_balanced_turbo_or_silent()
        {
            foreach (var p in PowerProfiles.Presets) Assert.InRange(p.AsusMode, 0, 2);
            Assert.Equal(2, PowerProfiles.Find("Silent").AsusMode);
            Assert.Equal(0, PowerProfiles.Find("Balanced").AsusMode);
            Assert.Equal(1, PowerProfiles.Find("Turbo").AsusMode);
        }

        [Fact]
        public void Matches_joses_ghelper_config_values()
        {
            var silent = PowerProfiles.Find("Silent");
            Assert.Equal((28, 45, 65), (silent.Spl, silent.Sppt, silent.Fppt));   // limit_total/slow/fast mode 2
            Assert.Equal((0, 5, 85), (silent.GpuPower, silent.GpuBoost, silent.GpuTemp));

            var turbo = PowerProfiles.Find("Turbo");
            Assert.Equal((35, 45, 55), (turbo.Spl, turbo.Sppt, turbo.Fppt));       // mode 1
            Assert.Equal((70, 25, 87), (turbo.GpuPower, turbo.GpuBoost, turbo.GpuTemp));
        }

        [Fact]
        public void Find_is_case_insensitive_and_null_safe()
        {
            Assert.NotNull(PowerProfiles.Find("balanced"));
            Assert.Null(PowerProfiles.Find("nope"));
            Assert.Null(PowerProfiles.Find(""));
            Assert.Null(PowerProfiles.Find(null));
        }

        [Fact]
        public void Only_manual_carries_gpu_oc_and_each_profile_has_a_cpu_temp()
        {
            foreach (var p in PowerProfiles.Presets) Assert.InRange(p.CpuTemp, 70, 100);
            var manual = PowerProfiles.Find("NvTgpHold-Manual");
            Assert.Equal((100, 1000), (manual.GpuCore, manual.GpuMem));   // GHelper gpu_core_3 / gpu_memory_3
            Assert.Equal(90, manual.CpuTemp);
            foreach (var p in new[] { "Silent", "Balanced", "Turbo" })
            {
                var pr = PowerProfiles.Find(p);
                Assert.Equal((0, 0), (pr.GpuCore, pr.GpuMem));             // no OC on the stock-mirroring profiles
            }
        }

        [Fact]
        public void Settings_round_trip_the_profile_and_experimental_flags()
        {
            var back = Settings.Parse(new Settings { Profile = "Turbo", GpuOc = true, SmuTuning = true, FanProfile = "Max cooling", GpuOptimized = true }.Serialize());
            Assert.Equal("Turbo", back.Profile);
            Assert.True(back.GpuOc);
            Assert.True(back.SmuTuning);
            Assert.Equal("Max cooling", back.FanProfile);
            Assert.True(back.GpuOptimized);
            Assert.Equal("", Settings.Parse(new[] { "profile=bogus" }).Profile);        // unknown name rejected
            Assert.Equal("", Settings.Parse(new[] { "fanprofile=bogus" }).FanProfile);  // unknown fan name rejected
            var def = Settings.Parse(new string[0]);
            Assert.False(def.GpuOc); Assert.False(def.SmuTuning); Assert.False(def.GpuOptimized);   // experimental off by default
        }

        [Fact]
        public void Fan_profiles_cover_the_four_options_with_valid_curves()
        {
            Assert.Equal(new[] { "Silent", "Balanced", "Turbo", "Max cooling" }, System.Array.ConvertAll(FanProfiles.Presets, f => f.Name));
            foreach (var f in FanProfiles.Presets)
                foreach (var curve in new[] { f.Cpu, f.Gpu })
                {
                    Assert.Equal(16, curve.Length);
                    for (int i = 1; i < 8; i++) Assert.True(curve[i] >= curve[i - 1]);        // temps non-decreasing
                    for (int i = 8; i < 16; i++) Assert.InRange(curve[i], 0, 100);            // percents 0-100
                }
            Assert.NotNull(FanProfiles.Find("max cooling"));
            Assert.Null(FanProfiles.Find("nope"));
        }

        [Fact]
        public void Max_cooling_hits_50pct_at_50C_and_100pct_by_70C()
        {
            var mc = FanProfiles.Find("Max cooling");
            // temps 30/40/50/60/70/80/90/100, duties 30/40/50/75/100/100/100/100
            Assert.Equal(50, mc.Cpu[8 + 2]);    // 50 C -> 50%
            Assert.Equal(100, mc.Cpu[8 + 4]);   // 70 C -> 100%
            Assert.Equal(100, mc.Cpu[8 + 7]);   // 100 C flat at 100%
        }

        [Fact]
        public void Manual_profile_uses_max_cooling_and_gpu_85()
        {
            var m = PowerProfiles.Find("NvTgpHold-Manual");
            Assert.Equal((18, 28, 45), (m.Spl, m.Sppt, m.Fppt));  // Jose's spec: CPU 18/28/45 W
            Assert.Equal(85, m.GpuTemp);                         // Jose's spec: GPU max 85 C
            Assert.Equal(90, m.CpuTemp);                         // CPU max 90 C
            Assert.Equal(-25, m.CpuUv);                          // -25 CO
            Assert.Equal(50, m.CpuFan[8 + 2]);                   // max-cooling curve (50 C -> 50%)
            Assert.Equal(100, m.GpuFan[8 + 4]);                  // 70 C -> 100%
        }
    }
}
