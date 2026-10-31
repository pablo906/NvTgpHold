using System;
using Xunit;

namespace NvTgpHold.Tests
{
    public class ClockDomainLayoutTests
    {
        private static byte[] Snapshot()
        {
            var data = new byte[ClockDomainLayout.Size];
            new Random(123).NextBytes(data);
            BitConverter.GetBytes(ClockDomainLayout.Version).CopyTo(data, 0);
            BitConverter.GetBytes(0xFF).CopyTo(data, 8);
            foreach (int domain in new[] { 1, 2 }) BitConverter.GetBytes(0xF).CopyTo(data, 0x124 + domain * 0x304);
            return data;
        }

        [Theory]
        [InlineData(0)] [InlineData(100)] [InlineData(200)] [InlineData(300)]
        public void Request_changes_only_domain_mask_and_two_equal_frequency_offsets(int mhz)
        {
            var original = Snapshot(); var preserved = (byte[])original.Clone();
            var request = ClockDomainLayout.WithLinkedOffset(original, mhz);
            Assert.Equal(preserved, original);
            Assert.Equal(6, BitConverter.ToInt32(request, 8));
            Assert.Equal(mhz * 1000, BitConverter.ToInt32(request, ClockDomainLayout.XbarOffset));
            Assert.Equal(mhz * 1000, BitConverter.ToInt32(request, ClockDomainLayout.SysOffset));
            for (int i = 0; i < request.Length; i++)
                if (!(i >= 8 && i < 12) && !(i >= ClockDomainLayout.XbarOffset && i < ClockDomainLayout.XbarOffset + 4) &&
                    !(i >= ClockDomainLayout.SysOffset && i < ClockDomainLayout.SysOffset + 4)) Assert.Equal(original[i], request[i]);
        }

        [Theory]
        [InlineData(-1)] [InlineData(301)] [InlineData(int.MaxValue)]
        public void Rejects_offsets_outside_bounds(int mhz) => Assert.Throws<ArgumentOutOfRangeException>(() => ClockDomainLayout.WithLinkedOffset(Snapshot(), mhz));

        [Fact]
        public void Rejects_unrecognized_layouts_without_mutating_them()
        {
            Assert.False(ClockDomainLayout.Validate(null, out _));
            Assert.False(ClockDomainLayout.Validate(new byte[12], out _));
            foreach (int offset in new[] { 0, 8, 0x428, 0x72C })
            {
                var data = Snapshot(); BitConverter.GetBytes(0).CopyTo(data, offset);
                var before = (byte[])data.Clone();
                Assert.Throws<ArgumentException>(() => ClockDomainLayout.WithLinkedOffset(data, 100));
                Assert.Equal(before, data);
            }
        }
    }
}
