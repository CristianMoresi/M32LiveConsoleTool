using System.Diagnostics;
using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Modules;
using M32LiveConsoleTool.Osc;

namespace M32LiveConsoleTool.Tests;

/// <summary>
/// Tests for the <see cref="BpmDetector"/> that exercise the manual-tap path and the
/// freshness (staleness) tracking without needing a live console.
/// </summary>
public class BpmDetectorTests
{
    private static BpmDetector NewDetector(int sendPort, int receivePort)
    {
        var logger = new ShowLogger(); // memory-only
        var client = new OscClient("127.0.0.1", sendPort, receivePort); // never connected
        var meter = new MeterReader(client, new List<AudioDevice>(), windowMs: 2000);
        var delay = new DelayController(client, logger);
        var channel = new MixerChannel("Click", 1, -18.0, 0.0);
        return new BpmDetector(
            meter, delay, logger, channel, BpmDetectionMode.ManualTap,
            transientThresholdDb: -20.0, bpmChangeHysteresis: 1.0);
    }

    [Fact]
    public void Tap_tempo_at_120_bpm_is_detected()
    {
        BpmDetector bpm = NewDetector(18101, 18102);
        bpm.Start();
        try
        {
            // Five taps spaced ~500 ms apart = 120 BPM.
            for (int i = 0; i < 5; i++)
            {
                bpm.TapTempo();
                Thread.Sleep(500);
            }

            Assert.True(
                SpinUntil(() => bpm.HasEverBeenValid && bpm.CurrentBpm > 0, 1000),
                "no BPM was computed from the taps");

            Assert.InRange(bpm.CurrentBpm, 108.0, 132.0);
        }
        finally
        {
            bpm.Stop();
        }
    }

    [Fact]
    public void Two_taps_lock_a_tempo_in_manual_mode()
    {
        BpmDetector bpm = NewDetector(18105, 18106);
        bpm.Start();
        try
        {
            bpm.TapTempo();
            Thread.Sleep(500);
            bpm.TapTempo(); // only two taps = a single interval

            Assert.True(
                SpinUntil(() => bpm.HasEverBeenValid && bpm.CurrentBpm > 0, 1000),
                "two taps should lock a tempo in manual mode");
            Assert.InRange(bpm.CurrentBpm, 100.0, 140.0);
        }
        finally
        {
            bpm.Stop();
        }
    }

    [Fact]
    public void Bpm_age_grows_when_no_new_transients_arrive()
    {
        BpmDetector bpm = NewDetector(18103, 18104);
        bpm.Start();
        try
        {
            for (int i = 0; i < 4; i++)
            {
                bpm.TapTempo();
                Thread.Sleep(400);
            }

            Assert.True(SpinUntil(() => bpm.HasEverBeenValid, 1000), "no BPM computed");

            double ageEarly = bpm.LastBpmAgeMs;
            Thread.Sleep(400);
            double ageLater = bpm.LastBpmAgeMs;

            // With no new taps, the age must grow (the established BPM is kept but
            // shown as no longer live). The constant refinement must NOT reset it.
            Assert.True(ageLater > ageEarly,
                $"age should grow without new transients (early={ageEarly:F0}, later={ageLater:F0})");
            Assert.True(ageLater >= 380.0, $"age should reflect the elapsed time, got {ageLater:F0}");

            // The established BPM is still kept (persistence), not zeroed.
            Assert.True(bpm.CurrentBpm > 0);
        }
        finally
        {
            bpm.Stop();
        }
    }

    private static bool SpinUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(25);
        }
        return condition();
    }
}
