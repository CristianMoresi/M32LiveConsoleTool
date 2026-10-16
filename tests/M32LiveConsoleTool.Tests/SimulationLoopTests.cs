using System.Diagnostics;
using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Modules;
using M32LiveConsoleTool.Osc;

namespace M32LiveConsoleTool.Tests;

/// <summary>
/// End-to-end test of the control loop over the loopback simulator: the client
/// must learn the real fader position from the console and the AutoLeveler must
/// then pull the post-fader level toward its target. Exercises the full chain
/// (OscClient + M32Simulator + MeterReader + AutoLeveler) over real UDP.
/// </summary>
public class SimulationLoopTests
{
    [Fact]
    public void AutoLeveler_pulls_level_toward_target_against_the_simulator()
    {
        var logger = new ShowLogger(); // memory-only: Open() is never called
        var simulator = new M32Simulator(0, logger) { ClipExcursionsEnabled = false };
        simulator.Start();
        Assert.True(simulator.IsRunning);
        var client = new OscClient("127.0.0.1", simulator.LocalPort, 0);
        var channel = new MixerChannel("Test", 1, targetLevelDb: -25.0, preampGain: 0.0);
        var devices = new List<AudioDevice> { channel };
        var meter = new MeterReader(client, devices, windowMs: 2000);
        var leveler = new AutoLeveler(
            client, meter, logger, devices,
            thresholdDb: 2.0,
            slewUpDbPerSec: 8.0,
            slewDownDbPerSec: 8.0,
            gateThresholdDb: -60.0,
            dropLimitDb: 40.0,
            cycleIntervalMs: 50);

        try
        {
            simulator.Start();
            Assert.True(client.Connect(), client.ConnectError);
            client.StartKeepAlive();
            meter.StartReading();

            // The console must report the real fader position before the
            // leveler is allowed to act (this is what was broken before).
            Assert.True(
                SpinUntil(() => channel.FaderInitialized, 3000),
                "the simulator never reported the fader position");

            float faderAtStart = channel.LastSentFader;

            leveler.Start();

            // The simulated signal sits around -15 dBFS; the target is -25, so
            // the leveler must pull the fader (and the level) down.
            Assert.True(
                SpinUntil(() => AverageLevel(meter, channel, 30, 50) < -20.0, 8000),
                $"level never converged toward target; avg={AverageLevel(meter, channel, 5, 20):F1} dB");

            Assert.True(
                channel.LastSentFader < faderAtStart,
                $"leveler should have pulled the fader down (start={faderAtStart:F3}, now={channel.LastSentFader:F3})");
        }
        finally
        {
            leveler.Stop();
            meter.StopReading();
            client.Disconnect();
            simulator.Stop();
        }
    }

    [Fact]
    public void DelayController_detects_fx_types_from_the_simulator()
    {
        var logger = new ShowLogger();
        var simulator = new M32Simulator(0, logger);
        simulator.Start();
        Assert.True(simulator.IsRunning);
        var client = new OscClient("127.0.0.1", simulator.LocalPort, 0);
        var delay = new DelayController(client, logger);

        try
        {
            simulator.Start();
            Assert.True(client.Connect(), client.ConnectError);
            delay.Start(); // subscribes and queries /fx/N/type

            Assert.True(
                SpinUntil(() => Enumerable.Range(1, 4).All(delay.DetectedEffects.ContainsKey), 5000),
                "the simulator never reported any FX types");

            // Simulator layout: slot 1 = Stereo Delay, 2 = 3-Tap, 3 = Modulation, 4 = Hall (no delay).
            Assert.Equal(DelayEffectType.StereoDelay, delay.DetectedEffects[1].DelayType);
            Assert.Equal(DelayEffectType.ThreeTapDelay, delay.DetectedEffects[2].DelayType);
            Assert.Equal(DelayEffectType.ModulationDelay, delay.DetectedEffects[3].DelayType);
            Assert.False(delay.DetectedEffects[4].IsDelay);
            Assert.Equal("Hall Reverb", delay.DetectedEffects[4].Name);
        }
        finally
        {
            delay.Stop();
            client.Disconnect();
            simulator.Stop();
        }
    }

    /// <summary>Averages several moving-average reads to cancel the simulator's oscillation.</summary>
    private static double AverageLevel(MeterReader meter, AudioDevice device, int samples, int intervalMs)
    {
        double sum = 0.0;
        for (int i = 0; i < samples; i++)
        {
            sum += meter.GetMovingAverage(device);
            Thread.Sleep(intervalMs);
        }
        return sum / samples;
    }

    private static bool SpinUntil(Func<bool> condition, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(50);
        }
        return condition();
    }
}
