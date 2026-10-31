using Xunit;

namespace NvTgpHold.Tests
{
    public class AtkAcpiTests
    {
        [Fact]
        public void DecodeFan_valid_reply_scales_rpm_by_100()
        {
            // 0x0001003C: presence flag set, low word 0x3C = 60 -> 6000 RPM
            Assert.Equal(6000, AtkAcpi.DecodeFan(0x0001003Cu));
        }

        [Fact]
        public void DecodeFan_unsupported_sentinel_is_unavailable()
        {
            // 0xFFFFFFFE passes the IOCTL but is the unsupported-device sentinel: must be -1, not 6,553,400 RPM
            Assert.Equal(-1, AtkAcpi.DecodeFan(0xFFFFFFFEu));
        }

        [Fact]
        public void DecodeFan_zero_rpm_is_valid()
        {
            // Fan stopped: presence flag set, low word 0 -> 0 RPM (distinct from unavailable)
            Assert.Equal(0, AtkAcpi.DecodeFan(0x00010000u));
        }
    }
}
