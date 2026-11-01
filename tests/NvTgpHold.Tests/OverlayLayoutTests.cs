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
            Assert.True((def & OverlayCols.Chart) != 0 && (light & OverlayCols.Chart) == 0);
            Assert.True((full & OverlayCols.Usage) != 0 && (def & OverlayCols.Usage) == 0);
            Assert.True((complete & (OverlayCols.Names | OverlayCols.Memory)) == (OverlayCols.Names | OverlayCols.Memory));
            Assert.True((full & (OverlayCols.Names | OverlayCols.Memory)) == 0);
        }

        [Fact]
        public void Battery_column_needs_default_or_higher_and_being_on_battery()
        {
            Assert.True((OverlayLayout.ColumnsFor(OverlayMode.Default, true) & OverlayCols.Battery) != 0);
            Assert.True((OverlayLayout.ColumnsFor(OverlayMode.Complete, true) & OverlayCols.Battery) != 0);
            Assert.True((OverlayLayout.ColumnsFor(OverlayMode.Default, false) & OverlayCols.Battery) == 0);   // on AC: hidden
            Assert.True((OverlayLayout.ColumnsFor(OverlayMode.Light, true) & OverlayCols.Battery) == 0);      // Light never shows it
        }

        [Theory]
        [InlineData(-12.34, "-12.3W")]
        [InlineData(5.0, "+5.0W")]
        [InlineData(0.0, "0.0W")]
        [InlineData(double.NaN, "--W")]
        [InlineData(double.PositiveInfinity, "--W")]
        public void Battery_rate_is_signed(double w, string expected) => Assert.Equal(expected, OverlayLayout.FormatRate(w));

        [Theory]
        [InlineData(87, "87%")]
        [InlineData(100, "100%")]
        [InlineData(130, "100%")]
        [InlineData(-1, "--%")]
        public void Battery_percent_text(int pct, string expected) => Assert.Equal(expected, OverlayLayout.FormatPercent(pct));

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
        public void Power_history_keeps_the_newest_60_samples_oldest_first()
        {
            var h = new PowerHistory();
            for (int i = 1; i <= 65; i++) h.Add(i, i * 2);
            Assert.Equal(6.0, h.Cpu(0));                                   // samples 1-5 fell off the front
            Assert.Equal(65.0, h.Cpu(PowerHistory.Samples - 1));
            Assert.Equal(130.0, h.Gpu(PowerHistory.Samples - 1));
        }

        [Fact]
        public void Power_history_peak_has_a_floor_and_follows_the_stacked_maximum()
        {
            var h = new PowerHistory();
            Assert.Equal(PowerHistory.MinPeak, h.Peak);                    // empty: never below 10 W
            h.Add(3, 4);
            Assert.Equal(PowerHistory.MinPeak, h.Peak);                    // 7 W stacked is still under the floor
            h.Add(30, 90);
            Assert.Equal(120.0, h.Peak);                                   // CPU + GPU stacked
            for (int i = 0; i < PowerHistory.Samples; i++) h.Add(1, 1);
            Assert.Equal(PowerHistory.MinPeak, h.Peak);                    // the spike scrolled off
        }

        [Fact]
        public void Power_history_treats_unavailable_readings_as_zero()
        {
            var h = new PowerHistory();
            h.Add(double.NaN, -5);
            Assert.Equal(0.0, h.Cpu(PowerHistory.Samples - 1));
            Assert.Equal(0.0, h.Gpu(PowerHistory.Samples - 1));
            h.Add(double.PositiveInfinity, 12);
            Assert.Equal(0.0, h.Cpu(PowerHistory.Samples - 1));
            Assert.Equal(12.0, h.Gpu(PowerHistory.Samples - 1));
        }

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
