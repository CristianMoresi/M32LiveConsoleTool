using System.Linq;
using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Infrastructure;

namespace M32LiveConsoleTool.Tests;

/// <summary>
/// Tests for <see cref="SessionConfig"/> helpers, in particular the mixer-IP
/// validation shared by the first-run wizard and the on-the-fly IP change.
/// </summary>
public class SessionConfigTests
{
    [Theory]
    [InlineData("192.168.1.5")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.42")]
    [InlineData("::1")]
    public void Accepts_valid_ip_addresses(string ip)
    {
        Assert.True(SessionConfig.IsValidMixerIp(ip));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not an ip")]
    [InlineData("999.999.999.999")]
    [InlineData("mixer.local")]
    public void Rejects_invalid_or_non_literal_addresses(string? ip)
    {
        Assert.False(SessionConfig.IsValidMixerIp(ip));
    }

    [Fact]
    public void Trims_surrounding_whitespace_before_validating()
    {
        Assert.True(SessionConfig.IsValidMixerIp("  192.168.1.5  "));
    }

    [Fact]
    public void Sanitize_repairs_out_of_range_values()
    {
        SessionConfig config = new SessionConfig
        {
            MixerSendPort = 0,
            MixerReceivePort = 70000,
            MeterWindowMs = -5,
            AutoLevelerThresholdDb = 0,           // must be > 0
            AutoLevelerSlewUpDbPerSec = -1,       // must be > 0
            AutoLevelerSlewDownDbPerSec = -1,     // must be > 0
            AutoLevelerDropLimitDb = 0,           // must be > 0
            AutoLevelerCycleIntervalMs = 1,       // below the 10 ms minimum
            AntiClipperClipThresholdDb = 5,       // above the 0 dB maximum
            AntiClipperReductionStepDb = 50,      // above the 12 dB maximum
            AntiClipperMaxReductionStepDb = 0.05, // below the normal step
            AntiClipperSevereOvershootDb = 100,   // above the 18 dB maximum
            AntiClipperConfirmationSamples = 0,   // below the minimum of 1
            AntiClipperCooldownMs = 10,           // below the 100 ms minimum
            BpmTransientThresholdDb = 5,          // above the 0 dB maximum
            BpmChangeHysteresis = 999             // above the 20 BPM maximum
        };

        config.Sanitize();

        // Every value now sits inside the ranges the module constructors accept,
        // so building the modules from a sanitized config never throws.
        Assert.InRange(config.MixerSendPort, 1, 65535);
        Assert.InRange(config.MixerReceivePort, 1, 65535);
        Assert.InRange(config.MeterWindowMs, 100, 10000);
        Assert.True(config.AutoLevelerThresholdDb > 0);
        Assert.True(config.AutoLevelerSlewUpDbPerSec > 0);
        Assert.True(config.AutoLevelerSlewDownDbPerSec > 0);
        Assert.True(config.AutoLevelerDropLimitDb > 0);
        Assert.InRange(config.AutoLevelerCycleIntervalMs, 10, 5000);
        Assert.InRange(config.AntiClipperClipThresholdDb, -20.0, 0.0);
        Assert.InRange(config.AntiClipperReductionStepDb, 0.1, 12.0);
        Assert.InRange(config.AntiClipperMaxReductionStepDb, config.AntiClipperReductionStepDb, 12.0);
        Assert.InRange(config.AntiClipperSevereOvershootDb, 1.0, 18.0);
        Assert.InRange(config.AntiClipperConfirmationSamples, 1, 20);
        Assert.InRange(config.AntiClipperCooldownMs, 100, 30000);
        Assert.InRange(config.BpmTransientThresholdDb, -60.0, 0.0);
        Assert.InRange(config.BpmChangeHysteresis, 0.0, 20.0);
    }

    [Fact]
    public void Sanitize_drops_duplicate_devices_and_slots()
    {
        SessionConfig config = new SessionConfig();
        config.WatchedDevices.Add(new AudioDeviceDescriptor { Type = "channel", OscNumber = 5, Name = "First" });
        config.WatchedDevices.Add(new AudioDeviceDescriptor { Type = "channel", OscNumber = 5, Name = "Duplicate" });
        config.WatchedDevices.Add(new AudioDeviceDescriptor { Type = "bus", OscNumber = 5, Name = "Bus 5" });
        config.DelaySlots.Add(new DelaySlotDescriptor { Slot = 1 });
        config.DelaySlots.Add(new DelaySlotDescriptor { Slot = 1 });

        config.Sanitize();

        // The duplicate channel 5 is gone (first one kept); the bus 5 stays because
        // it is a different device type.
        Assert.Equal(1, config.WatchedDevices.Count(d => d.Type == "channel" && d.OscNumber == 5));
        Assert.Equal("First", config.WatchedDevices.First(d => d.Type == "channel" && d.OscNumber == 5).Name);
        Assert.Equal(1, config.WatchedDevices.Count(d => d.Type == "bus" && d.OscNumber == 5));
        Assert.Single(config.DelaySlots);
    }

    [Fact]
    public void Sanitize_drops_devices_with_invalid_type_or_number()
    {
        SessionConfig config = new SessionConfig();
        config.WatchedDevices.Add(new AudioDeviceDescriptor { Type = "channel", OscNumber = 33 }); // > 32
        config.WatchedDevices.Add(new AudioDeviceDescriptor { Type = "channel", OscNumber = 0 });  // < 1
        config.WatchedDevices.Add(new AudioDeviceDescriptor { Type = "bus", OscNumber = 17 });     // > 16
        config.WatchedDevices.Add(new AudioDeviceDescriptor { Type = "aux", OscNumber = 1 });      // unknown type
        config.WatchedDevices.Add(new AudioDeviceDescriptor { Type = "channel", OscNumber = 5 });  // valid

        config.Sanitize();

        Assert.Single(config.WatchedDevices);
        Assert.Equal("channel", config.WatchedDevices[0].Type);
        Assert.Equal(5, config.WatchedDevices[0].OscNumber);
    }

    [Fact]
    public void Sanitize_leaves_valid_values_untouched()
    {
        SessionConfig config = new SessionConfig();   // all defaults are valid
        SessionConfig reference = new SessionConfig();

        config.Sanitize();

        Assert.Equal(reference.MixerSendPort, config.MixerSendPort);
        Assert.Equal(reference.MixerReceivePort, config.MixerReceivePort);
        Assert.Equal(reference.MeterWindowMs, config.MeterWindowMs);
        Assert.Equal(reference.AutoLevelerThresholdDb, config.AutoLevelerThresholdDb);
        Assert.Equal(reference.AutoLevelerCycleIntervalMs, config.AutoLevelerCycleIntervalMs);
        Assert.Equal(reference.AntiClipperClipThresholdDb, config.AntiClipperClipThresholdDb);
        Assert.Equal(reference.AntiClipperMaxReductionStepDb, config.AntiClipperMaxReductionStepDb);
        Assert.Equal(reference.BpmChangeHysteresis, config.BpmChangeHysteresis);
    }

    [Fact]
    public void Simulation_demo_is_populated_and_valid()
    {
        SessionConfig demo = SessionConfig.CreateSimulationDemo();

        int channels = 0;
        int buses = 0;
        foreach (AudioDeviceDescriptor d in demo.WatchedDevices)
        {
            if (d.Type == "channel") channels++;
            else if (d.Type == "bus") buses++;
        }

        Assert.True(channels >= 1, "demo should include at least one channel");
        Assert.True(buses >= 1, "demo should include at least one bus");
        Assert.True(SessionConfig.IsValidMixerIp(demo.MixerIp), "demo IP should be valid");
    }
}
