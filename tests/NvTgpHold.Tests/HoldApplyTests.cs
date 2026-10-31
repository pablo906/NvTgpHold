using System.Collections.Generic;
using Xunit;
namespace NvTgpHold.Tests
{
    public class HoldApplyTests
    {
        [Theory]
        [InlineData(true, true, true)]
        [InlineData(false, true, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        [InlineData(false, false, false)]
        public void All_stages_run_in_order_and_all_failures_affect_result(bool boost, bool sync, bool current)
        {
            var calls = new List<string>();
            bool result = HoldApply.Apply(
                (out string d) => { calls.Add("boost"); d = "boost result"; return boost; },
                (out string d) => { calls.Add("sync"); d = "sync result"; return sync; },
                () => calls.Add("settle"),
                (out string d) => { calls.Add("current"); d = "current result"; return current; },
                out bool accepted, out string detail);
            Assert.Equal(new[] { "boost", "sync", "settle", "current" }, calls);
            Assert.Equal(boost && sync && current, result);
            Assert.Equal(current, accepted); // incomplete preparation must not lose ownership of an accepted CURRENT
            Assert.Equal(!boost, detail.Contains("boost FAIL:"));
            Assert.Equal(!sync, detail.Contains("NVPCF FAIL:"));
            Assert.Equal(!current, detail.Contains("CURRENT FAIL:"));
            Assert.Contains("boost result", detail);
            Assert.Contains("sync result", detail);
            Assert.Contains("current result", detail);
        }
    }
}
