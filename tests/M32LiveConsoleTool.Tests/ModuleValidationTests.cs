using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Modules;
using M32LiveConsoleTool.Osc;

namespace M32LiveConsoleTool.Tests;

/// <summary>
/// Tests that the automatic modules validate their parameters in the constructor.
/// The dependencies are created but never started, so no sockets or threads run.
/// </summary>
public class ModuleValidationTests
{
    private static (OscClient client, MeterReader meter, ShowLogger logger) BuildDeps()
    {
        var client = new OscClient("127.0.0.1");
        var logger = new ShowLogger();
        var meter = new MeterReader(client, new List<AudioDevice>(), 1000);
        return (client, meter, logger);
    }

    [Theory]
    [InlineData(0.0)]   // threshold must be positive
    [InlineData(-1.0)]
    public void AutoLeveler_rejects_non_positive_threshold(double thresholdDb)
    {
        var (client, meter, logger) = BuildDeps();

        Assert.Throws<ArgumentOutOfRangeException>(() => new AutoLeveler(
            client, meter, logger, new List<AudioDevice>(),
            thresholdDb, slewUpDbPerSec: 1.0, slewDownDbPerSec: 1.0, gateThresholdDb: -40.0,
            dropLimitDb: 8.0, cycleIntervalMs: 100));
    }

    [Fact]
    public void AutoLeveler_rejects_out_of_range_cycle_interval()
    {
        var (client, meter, logger) = BuildDeps();

        Assert.Throws<ArgumentOutOfRangeException>(() => new AutoLeveler(
            client, meter, logger, new List<AudioDevice>(),
            thresholdDb: 3.0, slewUpDbPerSec: 1.0, slewDownDbPerSec: 1.0, gateThresholdDb: -40.0,
            dropLimitDb: 8.0, cycleIntervalMs: 5)); // below the 10 ms minimum
    }

    [Theory]
    [InlineData(1.0)]    // clip threshold must be <= 0
    [InlineData(-25.0)]  // and >= -20
    public void AntiClipper_rejects_out_of_range_clip_threshold(double clipThresholdDb)
    {
        var (client, meter, logger) = BuildDeps();

        Assert.Throws<ArgumentOutOfRangeException>(() => new AntiClipper(
            client, meter, logger, new List<MixerChannel>(), clipThresholdDb));
    }

    [Fact]
    public void BpmDetector_rejects_positive_transient_threshold()
    {
        var (client, meter, logger) = BuildDeps();
        var target = new MixerChannel("Kick", 1, -18.0, 0.0);

        Assert.Throws<ArgumentOutOfRangeException>(() => new BpmDetector(
            meter, new DelayController(client, logger), logger,
            target, BpmDetectionMode.Click, transientThresholdDb: 5.0));
    }
}
