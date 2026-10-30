using Xunit;

namespace NvTgpHold.Tests
{
    public class AutoAssertTests
    {
        [Theory]
        [InlineData("AMD64 Family 26 Model 36 Stepping 0", "AuthenticAMD", true)]    // Ryzen AI 9 HX 370 (Strix Point)
        [InlineData("AMD64 Family 26 Model 32 Stepping 0", "AuthenticAMD", true)]
        [InlineData("AMD64 Family 26 Model 112 Stepping 0", "AuthenticAMD", false)]  // Strix Halo
        [InlineData("AMD64 Family 26 Model 96 Stepping 0", "AuthenticAMD", false)]   // Krackan Point
        [InlineData("AMD64 Family 25 Model 117 Stepping 2", "AuthenticAMD", false)]  // Hawk Point (Zen 4)
        [InlineData("Intel64 Family 6 Model 197 Stepping 2", "GenuineIntel", false)]
        [InlineData("", "AuthenticAMD", false)]
        public void Strix_point_detection(string id, string vendor, bool expected)
            => Assert.Equal(expected, CpuProfiles.IsStrixPoint(id, vendor));

        [Fact]
        public void Profiles_keep_fast_ge_slow_ge_sustained_within_bounds()
        {
            foreach (var p in CpuProfiles.Presets)
            {
                Assert.True(p.Fppt >= p.Sppt && p.Sppt >= p.Spl, p.Name);
                Assert.InRange(p.Spl, CpuProfiles.MinW, CpuProfiles.MaxW);
                Assert.InRange(p.Fppt, CpuProfiles.MinW, CpuProfiles.MaxW);
            }
            Assert.Equal(new[] { 28, 35, 45 }, System.Array.ConvertAll(CpuProfiles.Presets, p => p.Spl));
            Assert.Equal(new[] { 35, 50, 65 }, System.Array.ConvertAll(CpuProfiles.Presets, p => p.Sppt));
            Assert.Equal(new[] { 45, 65, 90 }, System.Array.ConvertAll(CpuProfiles.Presets, p => p.Fppt));
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(-5, 1)]
        [InlineData(2, 2)]
        [InlineData(9999, 300)]
        public void Interval_is_clamped_to_floor(int s, int expected) => Assert.Equal(expected, ReassertInterval.Clamp(s));

        [Fact]
        public void Settings_round_trip_and_reject_bad_values()
        {
            var s = new Settings { Auto = true, IntervalS = 3, CpuProfile = "45 W", EfficientBoost = false };
            var back = Settings.Parse(s.Serialize());
            Assert.True(back.Auto); Assert.Equal(3, back.IntervalS); Assert.Equal("45 W", back.CpuProfile); Assert.False(back.EfficientBoost);

            var bad = Settings.Parse(new[] { "interval=0", "cpu=200 W", "junk", "auto=yes" });
            Assert.Equal(ReassertInterval.FloorS, bad.IntervalS);
            Assert.Equal("", bad.CpuProfile);
            Assert.False(bad.Auto);
            Assert.True(bad.EfficientBoost);   // default on

            var empty = Settings.Parse(new string[0]);
            Assert.Equal(ReassertInterval.DefaultS, empty.IntervalS);
        }

        [Theory]
        [InlineData("{\"auto_apply_power_1\": 1, \"gpu_power_1\": 70}", true)]
        [InlineData("{\"auto_apply_power_0\": 0, \"auto_apply_power_1\": 0}", false)]
        [InlineData("{\"auto_apply_1\": 1}", false)]
        [InlineData("not json", false)]
        [InlineData("", false)]
        public void Ghelper_auto_apply_power(string json, bool expected) => Assert.Equal(expected, GHelperConfig.AutoApplyPower(json));
    }
}
