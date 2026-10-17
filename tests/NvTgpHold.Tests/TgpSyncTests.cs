using System.Collections.Generic;
using Xunit;

namespace NvTgpHold.Tests
{
    public class TgpSyncTests
    {
        // Fake ATK 0x98: DEVS stores the value unless told to reject it or to read back something else.
        private sealed class FakeAtk
        {
            public int Base = 80, Power = 70;
            public string Reject;          // non-null: DEVS fails with this detail
            public int? ReadbackOverride;  // non-null: DSTS 0x98 returns this after a write
            public readonly List<int> Writes = new List<int>();
            public int ReadBase() => Base;
            public int ReadPower() => Power;
            public string Write(int w) { Writes.Add(w); if (Reject != null) return Reject; Power = ReadbackOverride ?? w; return null; }
        }

        [Theory]
        [InlineData(120, 80, 175, 40)]
        [InlineData(175, 80, 175, 95)]
        [InlineData(80, 80, 175, 0)]
        [InlineData(140, 80, -1, 60)]   // MAX unknown: no upper bound
        public void Additional_is_target_minus_base(int target, int fixedBase, int max, int expected)
            => Assert.Equal(expected, TgpSync.Additional(target, fixedBase, 0, max, out _));

        [Theory]
        [InlineData(150, 45)]   // 80 + 45 + 25 boost
        [InlineData(175, 70)]   // 80 + 70 + 25 boost
        [InlineData(105, 0)]
        public void Additional_subtracts_boost(int target, int expected)
            => Assert.Equal(expected, TgpSync.Additional(target, 80, 25, 175, out _));

        [Fact]
        public void Additional_with_boost_rejects_above_range_and_below_base()
        {
            Assert.Equal(-1, TgpSync.Additional(200, 80, 25, 175, out string above));   // needs +95, supported +70
            Assert.Contains("+70", above);
            Assert.Equal(-1, TgpSync.Additional(100, 80, 25, 175, out _));
        }

        [Fact]
        public void Apply_with_boost_reports_three_parts()
        {
            var atk = new FakeAtk();
            Assert.True(TgpSync.Apply(150, 25, 175, atk.ReadBase, atk.ReadPower, atk.Write, out string note));
            Assert.Equal(new[] { 45 }, atk.Writes);
            Assert.Equal("synced 80+45 W +25 boost", note);
        }

        [Fact]
        public void Additional_rejects_above_supported_range()
        {
            // MAX 150 with an 80 W base supports +70; 175 would need +95.
            Assert.Equal(-1, TgpSync.Additional(175, 80, 0, 150, out string error));
            Assert.Contains("+70", error);
        }

        [Fact]
        public void Additional_rejects_underflow()
        {
            Assert.Equal(-1, TgpSync.Additional(60, 80, 0, 175, out string error));
            Assert.Contains("below", error);
        }

        [Fact]
        public void Additional_rejects_unreadable_base()
            => Assert.Equal(-1, TgpSync.Additional(140, -1, 0, 175, out _));

        [Theory]
        [InlineData(0x00010050u, 80)]
        [InlineData(0x00010000u, 0)]
        [InlineData(0xFFFFFFFEu, -1)]   // ASUS "unsupported"
        [InlineData(0x00000000u, -1)]
        [InlineData(0x00030050u, -1)]   // other status bits set
        public void Decode_accepts_only_simple_values(uint dsts, int expected)
            => Assert.Equal(expected, TgpSync.Decode(dsts));

        [Fact]
        public void Apply_writes_and_verifies()
        {
            var atk = new FakeAtk();
            Assert.True(TgpSync.Apply(140, 0, 175, atk.ReadBase, atk.ReadPower, atk.Write, out string note));
            Assert.Equal(new[] { 60 }, atk.Writes);
            Assert.Equal("synced 80+60 W", note);
        }

        [Fact]
        public void Apply_skips_write_when_already_in_sync()
        {
            var atk = new FakeAtk { Power = 60 };
            Assert.True(TgpSync.Apply(140, 0, 175, atk.ReadBase, atk.ReadPower, atk.Write, out _));
            Assert.Empty(atk.Writes);
        }

        [Fact]
        public void Apply_fails_on_readback_mismatch()
        {
            var atk = new FakeAtk { ReadbackOverride = 70 };
            Assert.False(TgpSync.Apply(140, 0, 175, atk.ReadBase, atk.ReadPower, atk.Write, out string note));
            Assert.Contains("wrote 60 W, reads 70 W", note);
        }

        [Fact]
        public void Apply_fails_on_unreadable_readback()
        {
            var atk = new FakeAtk { ReadbackOverride = -1 };
            Assert.False(TgpSync.Apply(140, 0, 175, atk.ReadBase, atk.ReadPower, atk.Write, out string note));
            Assert.Contains("reads ?", note);
        }

        [Fact]
        public void Apply_reports_rejected_write()
        {
            var atk = new FakeAtk { Reject = "ASUS rejected the GPU power request" };
            Assert.False(TgpSync.Apply(140, 0, 175, atk.ReadBase, atk.ReadPower, atk.Write, out string note));
            Assert.Contains("ASUS rejected", note);
        }

        [Fact]
        public void Apply_does_not_write_when_out_of_range_or_base_invalid()
        {
            var atk = new FakeAtk();
            Assert.False(TgpSync.Apply(175, 0, 150, atk.ReadBase, atk.ReadPower, atk.Write, out _));
            atk.Base = -1;
            Assert.False(TgpSync.Apply(140, 0, 175, atk.ReadBase, atk.ReadPower, atk.Write, out _));
            Assert.Empty(atk.Writes);
        }

        [Fact]
        public void Restore_writes_captured_prior_value()
        {
            var atk = new FakeAtk { Power = 60 };
            Assert.True(TgpSync.Restore(70, atk.ReadPower, atk.Write, out _));
            Assert.Equal(70, atk.Power);
        }
    }
}
