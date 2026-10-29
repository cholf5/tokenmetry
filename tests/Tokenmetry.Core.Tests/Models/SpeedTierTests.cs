namespace Tokenmetry.Core.Tests.Models;

using Tokenmetry.Core.Models;

public class SpeedTierTests
{
    [Fact]
    public void Record_defaults_to_standard()
    {
        new UsageRecord().Speed.Should().Be(SpeedTier.Standard);
    }

    [Fact]
    public void Usage_detail_copies_speed_from_record()
    {
        var record = new UsageRecord { Speed = SpeedTier.Fast, InputTokens = 10 };

        var detail = TokenUsageDetail.FromRecord(record);

        detail.Speed.Should().Be(SpeedTier.Fast);
        detail.InputTokens.Should().Be(10);
    }
}
