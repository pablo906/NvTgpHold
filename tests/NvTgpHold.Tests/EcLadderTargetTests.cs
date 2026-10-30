using Xunit;

namespace NvTgpHold.Tests
{
    public class EcLadderTargetTests
    {
        [Theory]
        [InlineData("ROG Zephyrus G14 GA403WW_GA403WW", "GA403WW.312", true)]
        [InlineData("ROG Zephyrus G14 GA403WW_GA403WW", "GA403WW.310", false)]   // addresses traced on 312 only
        [InlineData("ROG Zephyrus G14 GA403UV_GA403UV", "GA403UV.312", false)]
        [InlineData("ROG Zephyrus G16 GA605WV", "GA605WV.312", false)]
        [InlineData("", "GA403WW.312", false)]
        [InlineData("ROG Zephyrus G14 GA403WW_GA403WW", "", false)]
        public void Gated_on_model_and_bios(string model, string bios, bool expected)
            => Assert.Equal(expected, EcLadderTarget.Supported(model, bios));

        [Theory]
        [InlineData(1, 0x0B, true)]
        [InlineData(1, 0x09, true)]     // bit 1 clears at times; only bit 0 matters
        [InlineData(1, 0x0A, false)]    // OS-present clear
        [InlineData(2, 0x0B, false)]    // other ASUS mode: different table family
        [InlineData(1, -1, false)]
        public void Writes_only_in_traced_state(int mode, int source, bool expected)
            => Assert.Equal(expected, EcLadderTarget.SafeToWrite(mode, source));

        [Fact]
        public void Reset_set_matches_the_ec_reset_routine_and_never_touches_the_gate()
        {
            Assert.Equal(19, EcLadderTarget.ZeroAddrs.Length);
            Assert.Equal(0x8009ACu, EcLadderTarget.ZeroAddrs[0]);
            Assert.Equal(0x8009BBu, EcLadderTarget.ZeroAddrs[15]);
            Assert.Contains(0x8009B1u, EcLadderTarget.ZeroAddrs);   // debounce
            Assert.Contains(0x8009DCu, EcLadderTarget.ZeroAddrs);
            Assert.Contains(0x800A0Bu, EcLadderTarget.ZeroAddrs);
            Assert.Contains(0x800A0Cu, EcLadderTarget.ZeroAddrs);
            Assert.DoesNotContain(EcLadderTarget.SourceAddr, EcLadderTarget.ZeroAddrs);
            Assert.DoesNotContain(EcLadderTarget.SourceAddr, EcLadderTarget.Bit2Addrs);
            Assert.DoesNotContain(EcLadderTarget.AppliedAddr, EcLadderTarget.ZeroAddrs);
        }

        [Fact]
        public void Bit2_clear_leaves_other_bits()
        {
            Assert.Equal(0x00, EcLadderTarget.ClearBit2(0x04));
            Assert.Equal(0xFB, EcLadderTarget.ClearBit2(0xFF));
            Assert.Equal(0x03, EcLadderTarget.ClearBit2(0x03));
        }

        [Fact]
        public void Settings_round_trip_ec_ladder()
        {
            Assert.True(Settings.Parse(new Settings { EcLadder = true }.Serialize()).EcLadder);
            Assert.False(Settings.Parse(new[] { "ecladder=yes" }).EcLadder);
            Assert.False(Settings.Parse(new string[0]).EcLadder);
        }
    }
}
