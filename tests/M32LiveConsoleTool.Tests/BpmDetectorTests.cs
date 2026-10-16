using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Modules;
using M32LiveConsoleTool.Osc;

namespace M32LiveConsoleTool.Tests;

/// <summary>Tap timing and tempo freshness use a controlled monotonic clock.</summary>
public class BpmDetectorTests
{
    private static BpmDetector NewDetector(AuditClock clock)
    {
        var logger = new ShowLogger();
        var client = new OscClient("127.0.0.1", 10023, 0, clock);
        var meter = new MeterReader(client, new List<AudioDevice>(), windowMs: 2000);
        var delay = new DelayController(client, logger);
        return new BpmDetector(meter, delay, logger, new MixerChannel("Click", 1, -18, 0),
            BpmDetectionMode.ManualTap, -20, 1);
    }

    [Fact]
    public void Tap_tempo_at_120_bpm_is_detected()
    {
        var clock = new AuditClock();
        var bpm = NewDetector(clock);
        for (int i = 0; i < 5; i++)
        {
            bpm.TapTempo(); bpm.TryUpdateBpm(); clock.Advance(500);
        }
        Assert.True(bpm.HasEverBeenValid);
        Assert.Equal(120, bpm.CurrentBpm, 5);
    }

    [Fact]
    public void Two_taps_lock_a_tempo_in_manual_mode()
    {
        var clock = new AuditClock();
        var bpm = NewDetector(clock);
        bpm.TapTempo(); clock.Advance(500); bpm.TapTempo(); bpm.TryUpdateBpm();
        Assert.True(bpm.HasEverBeenValid);
        Assert.Equal(120, bpm.CurrentBpm, 5);
    }

    [Fact]
    public void Bpm_age_grows_when_no_new_transients_arrive()
    {
        var clock = new AuditClock();
        var bpm = NewDetector(clock);
        for (int i = 0; i < 4; i++)
        {
            bpm.TapTempo(); bpm.TryUpdateBpm(); clock.Advance(400);
        }
        Assert.True(bpm.HasEverBeenValid);
        double early = bpm.LastBpmAgeMs;
        clock.Advance(400); bpm.TryUpdateBpm();
        Assert.Equal(400, bpm.LastBpmAgeMs - early, 5);
        Assert.Equal(150, bpm.CurrentBpm, 5);
    }
}
