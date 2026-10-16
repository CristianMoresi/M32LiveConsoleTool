using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Modules;

namespace M32LiveConsoleTool.Tests;

public class AutoLevelerTests
{
    [Fact]
    public void Manual_move_during_silence_waits_for_real_signal_before_recapturing()
    {
        var clock = new AuditClock();
        var client = new RecordingOscClient(clock);
        var channel = new MixerChannel("Vox", 1, -18, 0);
        var devices = new List<AudioDevice> { channel };
        var meter = new MeterReader(client, devices, 1000);
        var leveler = new AutoLeveler(client, meter, new ShowLogger(), devices, 2, 8, 8, -40, 40, 100);
        channel.UpdateFaderFromConsole(.75f);
        channel.UpdateFaderFromConsole(.5f);
        for (int i = 0; i < 60; i++)
        {
            channel.UpdateMuteFromConsole(1);
            channel.UpdateFaderFromConsole(.5f);
            channel.AddSample(new MeterSample(0, clock));
            leveler.ProcessDevice(channel);
            clock.Advance(100);
        }
        Assert.Equal(-18, channel.TargetLevelDb);
        Assert.Equal(LevelingState.ManualOverride, leveler.GetDeviceState(channel));
        for (int i = 0; i < 50; i++)
        {
            channel.UpdateMuteFromConsole(1);
            channel.UpdateFaderFromConsole(.5f);
            channel.AddSample(new MeterSample(.2f, clock));
            leveler.ProcessDevice(channel);
            clock.Advance(100);
        }
        Assert.Equal(20 * Math.Log10(.2), channel.TargetLevelDb, 3);
        Assert.Equal(LevelingState.Idle, leveler.GetDeviceState(channel));
        Assert.Empty(client.Writes);
    }
}
