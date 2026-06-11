using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Ui;

namespace M32LiveConsoleTool.Tests;

/// <summary>
/// Round-trip tests for the session/preset persistence: what
/// <see cref="AppOrchestrator.WriteSession"/> writes,
/// <see cref="AppOrchestrator.ReadSession"/> must restore identically — and a
/// corrupt file on disk must come back repaired, never throw.
/// </summary>
public class SessionPersistenceTests
{
    private static string TempSessionPath()
    {
        return Path.Combine(
            Path.GetTempPath(), $"m32lct_session_{Guid.NewGuid():N}.json");
    }

    [Fact]
    public void Write_then_read_round_trips_devices_slots_and_parameters()
    {
        SessionConfig config = new SessionConfig
        {
            MixerIp = "10.0.0.7",
            MeterWindowMs = 1800,
            AutoLevelerEnabled = true,
            AutoLevelerThresholdDb = 2.5,
            AutoLevelerSlewUpDbPerSec = 1.0,
            AutoLevelerSlewDownDbPerSec = 5.0,
            AntiClipperClipThresholdDb = -2.0,
            BpmDetectorMode = BpmDetectionMode.Click,
            BpmTargetChannelOscNumber = 7
        };
        config.WatchedDevices.Add(new AudioDeviceDescriptor
        {
            Type = "channel", Name = "Lead Vocal", OscNumber = 6, TargetLevelDb = -16.5, PreampGain = 24.0
        });
        config.WatchedDevices.Add(new AudioDeviceDescriptor
        {
            Type = "bus", Name = "Drums", OscNumber = 1, TargetLevelDb = -14.0
        });
        config.DelaySlots.Add(new DelaySlotDescriptor
        {
            Slot = 1, EffectType = DelayEffectType.StereoDelay, IsAutomatic = true, Beat = BeatType.DottedEighth
        });
        config.DelaySlots.Add(new DelaySlotDescriptor
        {
            Slot = 3, EffectType = DelayEffectType.ThreeTapDelay, IsAutomatic = false, ManualMs = 80.0
        });

        string path = TempSessionPath();
        try
        {
            AppOrchestrator.WriteSession(config, path);
            SessionConfig loaded = AppOrchestrator.ReadSession(path);

            Assert.Equal("10.0.0.7", loaded.MixerIp);
            Assert.Equal(1800, loaded.MeterWindowMs);
            Assert.True(loaded.AutoLevelerEnabled);
            Assert.Equal(2.5, loaded.AutoLevelerThresholdDb);
            Assert.Equal(1.0, loaded.AutoLevelerSlewUpDbPerSec);
            Assert.Equal(5.0, loaded.AutoLevelerSlewDownDbPerSec);
            Assert.Equal(-2.0, loaded.AntiClipperClipThresholdDb);
            Assert.Equal(BpmDetectionMode.Click, loaded.BpmDetectorMode);
            Assert.Equal(7, loaded.BpmTargetChannelOscNumber);

            Assert.Equal(2, loaded.WatchedDevices.Count);
            AudioDeviceDescriptor vocal = loaded.WatchedDevices[0];
            Assert.Equal("channel", vocal.Type);
            Assert.Equal("Lead Vocal", vocal.Name);
            Assert.Equal(6, vocal.OscNumber);
            Assert.Equal(-16.5, vocal.TargetLevelDb);
            Assert.Equal(24.0, vocal.PreampGain);
            Assert.Equal("bus", loaded.WatchedDevices[1].Type);

            Assert.Equal(2, loaded.DelaySlots.Count);
            DelaySlotDescriptor auto = loaded.DelaySlots[0];
            Assert.Equal(1, auto.Slot);
            Assert.Equal(DelayEffectType.StereoDelay, auto.EffectType);
            Assert.True(auto.IsAutomatic);
            Assert.Equal(BeatType.DottedEighth, auto.Beat);
            DelaySlotDescriptor manual = loaded.DelaySlots[1];
            Assert.False(manual.IsAutomatic);
            Assert.Equal(80.0, manual.ManualMs);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_repairs_a_corrupt_file_instead_of_throwing()
    {
        // A hand-edited session: invalid IP, a delay slot the console does not
        // have, a manual slot with a time that would make DelaySlotConfig.Manual
        // throw, and an out-of-range module parameter. Loading this used to be
        // able to crash startup; it must now come back repaired.
        const string corruptJson = """
        {
          "MixerIp": "not an ip",
          "AutoLevelerThresholdDb": -5,
          "DelaySlots": [
            { "Slot": 9, "EffectType": "StereoDelay", "IsAutomatic": false, "ManualMs": 100 },
            { "Slot": 2, "EffectType": "StereoDelay", "IsAutomatic": false, "ManualMs": 0 }
          ]
        }
        """;

        string path = TempSessionPath();
        try
        {
            File.WriteAllText(path, corruptJson);

            SessionConfig loaded = AppOrchestrator.ReadSession(path);

            Assert.True(SessionConfig.IsValidMixerIp(loaded.MixerIp));
            Assert.True(loaded.AutoLevelerThresholdDb > 0);
            Assert.Single(loaded.DelaySlots);                  // slot 9 dropped
            Assert.Equal(2, loaded.DelaySlots[0].Slot);
            Assert.True(loaded.DelaySlots[0].ManualMs >= 1.0); // repaired, buildable
        }
        finally
        {
            File.Delete(path);
        }
    }
}
