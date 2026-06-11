using System.Diagnostics;
using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Modules;
using M32LiveConsoleTool.Osc;

namespace M32LiveConsoleTool.Tests;

/// <summary>
/// Behavioral tests for the <see cref="AutoLeveler"/>'s manual-override recovery.
/// The OscClient is never connected, so no socket is opened; the leveler's writes
/// are silently dropped and only the device state is observed.
/// </summary>
public class AutoLevelerTests
{
    /// <summary>
    /// Regression test: the engineer moves a fader during a pause between songs
    /// (meters at the silence floor). The leveler must NOT capture the -90 dB
    /// floor as the new target — that would make it strangle the channel once the
    /// music returns — and must instead wait for real signal and capture that.
    /// </summary>
    [Fact]
    public void Manual_move_during_silence_does_not_capture_a_silent_target()
    {
        var logger = new ShowLogger(); // memory-only
        var client = new OscClient("127.0.0.1", 18201, 18202); // never connected
        var channel = new MixerChannel("Vox", 1, targetLevelDb: -18.0, preampGain: 0.0);
        var devices = new List<AudioDevice> { channel };
        var meter = new MeterReader(client, devices, windowMs: 2000);
        var leveler = new AutoLeveler(
            client, meter, logger, devices,
            thresholdDb: 2.0,
            slewUpDbPerSec: 8.0,
            slewDownDbPerSec: 8.0,
            gateThresholdDb: -40.0,
            dropLimitDb: 40.0,
            cycleIntervalMs: 25);

        try
        {
            channel.UpdateFaderFromConsole(0.75f); // console reports the real fader
            leveler.Start();

            // The engineer moves the fader while the band is NOT playing: the
            // sample history is empty, so the moving average sits at the -90 floor.
            channel.UpdateFaderFromConsole(0.5f);

            // Wait well past the 4 s stabilization window. With no signal, the
            // leveler must keep the previous target instead of capturing silence.
            Thread.Sleep(5500);
            Assert.Equal(-18.0, channel.TargetLevelDb);

            // The music comes back at ~-14 dBFS (0.2 linear). Feed live samples and
            // wait for the re-capture: the new target must track the real level,
            // not the silence floor.
            bool captured = SpinUntilFeeding(
                channel,
                postFaderLinear: 0.2f,
                condition: () => channel.TargetLevelDb > -20.0 && channel.TargetLevelDb < -8.0,
                timeoutMs: 8000);

            Assert.True(captured,
                $"target was never re-captured from live signal (target={channel.TargetLevelDb:F1} dB)");
        }
        finally
        {
            leveler.Stop();
        }
    }

    /// <summary>
    /// Feeds a steady meter sample to the channel every 50 ms (as the console
    /// would) until the condition holds or the timeout expires.
    /// </summary>
    private static bool SpinUntilFeeding(
        MixerChannel channel, float postFaderLinear, Func<bool> condition, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            channel.AddSample(new MeterSample(postFaderLinear));
            if (condition())
            {
                return true;
            }
            Thread.Sleep(50);
        }
        return condition();
    }
}
