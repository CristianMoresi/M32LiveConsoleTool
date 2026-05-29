using M32LiveConsoleTool.Domain;

namespace M32LiveConsoleTool.Tests;

/// <summary>
/// Tests for the domain value objects: device descriptors and meter samples.
/// </summary>
public class DomainTests
{
    [Fact]
    public void Channel_descriptor_round_trips()
    {
        var channel = new MixerChannel("Lead Vocal", 5, -12.0, 3.0);

        AudioDeviceDescriptor descriptor = AudioDeviceDescriptor.FromAudioDevice(channel);

        Assert.Equal("channel", descriptor.Type);
        Assert.Equal("Lead Vocal", descriptor.Name);
        Assert.Equal(5, descriptor.OscNumber);
        Assert.Equal(-12.0, descriptor.TargetLevelDb);
        Assert.Equal(3.0, descriptor.PreampGain);

        var rebuilt = Assert.IsType<MixerChannel>(descriptor.ToAudioDevice());
        Assert.Equal("Lead Vocal", rebuilt.Name);
        Assert.Equal(5, rebuilt.OscNumber);
        Assert.Equal(3.0, rebuilt.PreampGain);
    }

    [Fact]
    public void Bus_descriptor_round_trips()
    {
        var bus = new Bus("Drums", 3, -18.0);

        AudioDeviceDescriptor descriptor = AudioDeviceDescriptor.FromAudioDevice(bus);

        Assert.Equal("bus", descriptor.Type);
        Assert.IsType<Bus>(descriptor.ToAudioDevice());
    }

    [Fact]
    public void Channel_and_bus_build_their_own_osc_addresses()
    {
        Assert.Equal("/ch/07/mix/fader", new MixerChannel("X", 7, -18.0, 0.0).GetOscAddress());
        Assert.Equal("/bus/03/mix/fader", new Bus("Y", 3, -18.0).GetOscAddress());
    }

    [Fact]
    public void Channel_and_bus_build_their_own_mute_addresses()
    {
        Assert.Equal("/ch/07/mix/on", new MixerChannel("X", 7, -18.0, 0.0).GetMuteOscAddress());
        Assert.Equal("/bus/03/mix/on", new Bus("Y", 3, -18.0).GetMuteOscAddress());
    }

    [Fact]
    public void Mute_state_maps_the_console_on_off_value()
    {
        var channel = new MixerChannel("X", 1, -18.0, 0.0);
        Assert.False(channel.IsMuted);

        channel.UpdateMuteFromConsole(0); // 0 = muted on the M32
        Assert.True(channel.IsMuted);

        channel.UpdateMuteFromConsole(1); // 1 = on/audible
        Assert.False(channel.IsMuted);
    }

    [Fact]
    public void Bus_meter_sample_has_only_post_fader()
    {
        var sample = new MeterSample(1f); // bus form: only post-fader

        Assert.Equal(1f, sample.PostFaderLinear);
        Assert.Equal(0.0, sample.PostFaderDb, 6);   // 1.0 linear == 0 dB
        Assert.Equal(sample.PostFaderDb, sample.AsDb(), 6);
        Assert.True(float.IsNaN(sample.PostTrimLinear));
        Assert.True(double.IsNaN(sample.PostTrimDb));
    }

    [Fact]
    public void Channel_meter_sample_keeps_all_four_values()
    {
        var sample = new MeterSample(0.5f, 0f, 0f, 0.25f);

        Assert.Equal(0.5f, sample.PostTrimLinear);
        Assert.Equal(0.25f, sample.PostFaderLinear);
        Assert.False(double.IsNaN(sample.PostTrimDb));
    }
}
