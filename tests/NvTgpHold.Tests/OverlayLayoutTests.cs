using Xunit;

namespace NvTgpHold.Tests
{
    public class OverlayLayoutTests
    {
        [Fact]
        public void Light_shows_only_fps_temp_and_power()
        {
            Assert.Equal(OverlayCols.Fps | OverlayCols.Temp | OverlayCols.Power, OverlayLayout.ColumnsFor(OverlayMode.Light));
        }

        [Fact]
        public void Each_mode_adds_to_the_previous_one()
        {
            var light = OverlayLayout.ColumnsFor(OverlayMode.Light);
            var def = OverlayLayout.ColumnsFor(OverlayMode.Default);
            var full = OverlayLayout.ColumnsFor(OverlayMode.Full);
            var complete = OverlayLayout.ColumnsFor(OverlayMode.Complete);
            Assert.Equal(light, light & def);
            Assert.Equal(def, def & full);
            Assert.Equal(full, full & complete);
            Assert.True((def & OverlayCols.Fan) != 0 && (light & OverlayCols.Fan) == 0);
            Assert.True((full & OverlayCols.Usage) != 0 && (def & OverlayCols.Usage) == 0);
            Assert.True((complete & (OverlayCols.Names | OverlayCols.Memory)) == (OverlayCols.Names | OverlayCols.Memory));
            Assert.True((full & (OverlayCols.Names | OverlayCols.Memory)) == 0);
        }

        [Fact]
        public void Click_cycle_visits_all_four_modes_and_wraps()
        {
            var m = OverlayMode.Light;
            var seen = new System.Collections.Generic.List<OverlayMode>();
            for (int i = 0; i < 5; i++) { seen.Add(m); m = OverlayLayout.Next(m); }
            Assert.Equal(new[] { OverlayMode.Light, OverlayMode.Default, OverlayMode.Full, OverlayMode.Complete, OverlayMode.Light }, seen);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(3, 3)]
        [InlineData(-1, 1)]
        [InlineData(4, 1)]
        public void FromInt_rejects_out_of_range(int v, int expected)
            => Assert.Equal(expected, (int)OverlayLayout.FromInt(v, OverlayMode.Default));

        [Theory]
        [InlineData("NVIDIA GeForce RTX 5080 Laptop GPU", "RTX 5080")]
        [InlineData("NVIDIA GeForce RTX 4070 Ti", "RTX 4070 Ti")]
        [InlineData("", "")]
        public void Short_gpu_name(string full, string expected) => Assert.Equal(expected, OverlayLayout.ShortGpuName(full));

        [Theory]
        [InlineData("AMD Ryzen AI 9 HX 370 w/ Radeon 890M", "Ryzen AI 9 HX 370")]
        [InlineData("AMD Ryzen 9 7940HS w/ Radeon 780M Graphics", "Ryzen 9 7940HS")]
        [InlineData("", "")]
        public void Short_cpu_name(string full, string expected) => Assert.Equal(expected, OverlayLayout.ShortCpuName(full));

        [Theory]
        [InlineData(8.0, 16.0, 50)]
        [InlineData(20.0, 16.0, 100)]
        [InlineData(0.0, 16.0, 0)]
        [InlineData(double.NaN, 16.0, -1)]
        [InlineData(4.0, 0.0, -1)]
        public void Used_percent_is_clamped_and_unknown_is_minus_one(double used, double total, int expected)
            => Assert.Equal(expected, OverlayLayout.UsedPercent(used, total));

        [Fact]
        public void Overlay_mode_round_trips_through_settings_and_bad_values_fall_back()
        {
            var back = Settings.Parse(new Settings { OverlayModeIndex = 3 }.Serialize());
            Assert.Equal(3, back.OverlayModeIndex);
            Assert.Equal(1, Settings.Parse(new[] { "overlaymode=9" }).OverlayModeIndex);
            Assert.Equal(1, Settings.Parse(new[] { "overlaymode=abc" }).OverlayModeIndex);
            Assert.Equal(1, Settings.Parse(new string[0]).OverlayModeIndex);
        }
    }
}
