using M32LiveConsoleTool.Osc;

namespace M32LiveConsoleTool.Tests;

/// <summary>
/// Tests for the M32 OSC address builders and id conversions, verified against
/// the protocol documentation.
/// </summary>
public class M32AddressingTests
{
    [Fact]
    public void Head_amp_addresses_are_formatted_per_protocol()
    {
        Assert.Equal("/-ha/00/index", M32Buckets.BuildHeadAmpIndexAddress(0));
        Assert.Equal("/-ha/31/index", M32Buckets.BuildHeadAmpIndexAddress(31));
        Assert.Equal("/headamp/000/gain", M32Buckets.BuildHeadAmpGainAddress(0));
        Assert.Equal("/headamp/034/gain", M32Buckets.BuildHeadAmpGainAddress(34));
        Assert.Equal("/headamp/127/gain", M32Buckets.BuildHeadAmpGainAddress(127));
        Assert.Equal("/ch/05/preamp/trim", M32Buckets.BuildChannelTrimAddress(5));
    }

    [Fact]
    public void Channel_alias_and_osc_id_match_the_protocol()
    {
        Assert.Equal("/m32lct_ch05", M32Buckets.BuildChannelAlias(5));
        Assert.Equal(0, M32Buckets.UserChannelToOscId(1));
        Assert.Equal(31, M32Buckets.UserChannelToOscId(32));
        Assert.Throws<ArgumentOutOfRangeException>(() => M32Buckets.UserChannelToOscId(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => M32Buckets.UserChannelToOscId(33));
    }
}
