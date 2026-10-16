using System.Net;
using System.Text.Json;
using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Modules;
using M32LiveConsoleTool.Osc;
using M32LiveConsoleTool.Ui;

namespace M32LiveConsoleTool.Tests;

public sealed class AuditClock : TimeProvider
{
    private long tick = 1;
    private DateTimeOffset utc = DateTimeOffset.UnixEpoch;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => tick;
    public override DateTimeOffset GetUtcNow() => utc;
    public void Advance(int milliseconds) { tick += milliseconds; utc += TimeSpan.FromMilliseconds(milliseconds); }
    public void JumpCivilClock(int hours) => utc += TimeSpan.FromHours(hours);
}

public sealed class RecordingOscClient(TimeProvider clock) : OscClient("127.0.0.1", 10023, 0, clock)
{
    public List<OscMessage> Messages { get; } = new();
    public bool FailSends { get; set; }
    protected override bool SendCore(OscMessage message, IPEndPoint endpoint)
    {
        if (FailSends) return false;
        Messages.Add(message);
        return true;
    }
    public OscMessage[] Writes { get { lock (ControlLock) return Messages.Where(x => x.TypeTags == ",f").ToArray(); } }
}

public class AuditRegressionTests
{
    private sealed class Rig : IDisposable
    {
        public readonly AuditClock Clock = new();
        public readonly RecordingOscClient Client;
        public readonly MixerChannel Channel = new("Vox", 1, -18, 0);
        public readonly MixerChannel Second = new("Shared", 2, -18, 0);
        public readonly MeterReader Meter;
        public readonly AutoLeveler Leveler;
        public readonly AntiClipper Clip;
        public readonly DelayController Delay;
        public readonly BpmDetector Bpm;
        public Rig(double slew = .5, int cycle = 100, double reduction = 3, int confirmation = 1)
        {
            Client = new(Clock);
            var logger = new ShowLogger();
            var devices = new List<AudioDevice> { Channel, Second };
            Meter = new(Client, devices, 1000);
            Leveler = new(Client, Meter, logger, devices, 1, slew, slew, -60, 20, cycle);
            Clip = new(Client, Meter, logger, new() { Channel, Second }, -1, reduction, 6, 6, confirmation, 1000);
            Delay = new(Client, logger);
            Bpm = new(Meter, Delay, logger, Channel, BpmDetectionMode.ManualTap, -20, 0);
            Meter.StartReading();
            Delay.Start();
        }
        public void Feedback(string address, object value)
        {
            var message = new OscMessage(address, value is float ? ",f" : ",i", new() { value });
            Meter.OnMessageReceived(message);
            Delay.OnMessageReceived(message);
        }
        public void Ready(MixerChannel? device = null, int headAmp = 0, double gain = 0, bool muteKnown = true)
        {
            device ??= Channel;
            Feedback(device.GetOscAddress(), FaderMath.DbToFader(-20));
            if (muteKnown) Feedback(device.GetMuteOscAddress(), 1);
            Feedback($"/-ha/{device.OscNumber - 1:D2}/index", headAmp);
            Feedback(headAmp >= 0 ? $"/headamp/{headAmp:D3}/gain" : $"/ch/{device.OscNumber:D2}/preamp/trim",
                FaderMath.Normalize(gain, headAmp >= 0 ? -12 : -18, headAmp >= 0 ? 60 : 18));
            Sample(device);
        }
        public void Sample(MixerChannel? device = null, double postTrim = 0, double postFader = -24)
        {
            (device ?? Channel).AddSample(new MeterSample((float)FaderMath.DbToLinear(postTrim), 0, 0,
                (float)FaderMath.DbToLinear(postFader), Clock));
        }
        public void Dispose() { Leveler.Stop(); Clip.Stop(); Bpm.Stop(); Delay.Stop(); Meter.StopReading(); }
    }

    [Theory]
    [InlineData(-80, .1, 10)]
    [InlineData(-45, .1, 25)]
    [InlineData(-20, .5, 50)]
    [InlineData(0, .5, 100)]
    [InlineData(-80, 2, 100)]
    [InlineData(-45, .5, 100)]
    [InlineData(-20, .1, 10)]
    [InlineData(0, 2, 25)]
    public void F01_F24_smooth_steps_accumulate_without_false_manual_override(double start, double slew, int cycle)
    {
        using var rig = new Rig(slew, cycle);
        rig.Ready();
        rig.Channel.ResetObservedState();
        rig.Channel.UpdateFaderFromConsole(FaderMath.DbToFader(start));
        rig.Channel.UpdateMuteFromConsole(1);
        int iterations = 10000 / cycle;
        for (int i = 0; i < iterations; i++)
        {
            rig.Sample();
            rig.Channel.UpdateMuteFromConsole(1);
            rig.Leveler.ProcessDevice(rig.Channel);
            rig.Channel.UpdateFaderFromConsole(rig.Channel.LastSentFader);
            Assert.NotEqual(LevelingState.ManualOverride, rig.Leveler.GetDeviceState(rig.Channel));
            rig.Clock.Advance(cycle);
        }
        double actual = FaderMath.FaderToDb(rig.Channel.LastSentFader) - start;
        double expected = Math.Min(10 - start, slew * 10);
        Assert.InRange(actual, expected - .5, expected + .001);
        Assert.NotEmpty(rig.Client.Writes);
    }

    [Fact]
    public void F02_no_gain_write_until_source_and_gain_are_confirmed()
    {
        using var rig = new Rig();
        rig.Feedback(rig.Channel.GetMuteOscAddress(), 1); rig.Sample();
        rig.Clip.EvaluateChannel(rig.Channel);
        rig.Feedback("/-ha/00/index", 0); rig.Sample();
        rig.Clip.EvaluateChannel(rig.Channel);
        Assert.Empty(rig.Client.Writes);
        rig.Feedback("/headamp/000/gain", FaderMath.Normalize(0, -12, 60)); rig.Sample();
        rig.Clip.EvaluateChannel(rig.Channel);
        Assert.Single(rig.Client.Writes);
    }

    [Theory]
    [InlineData(-6, -9)]
    [InlineData(0, -3)]
    [InlineData(18, 15)]
    public void F03_digital_trim_reduces_the_observed_value(double initial, double expected)
    {
        using var rig = new Rig();
        rig.Ready(headAmp: -1, gain: initial);
        rig.Clip.EvaluateChannel(rig.Channel);
        var write = Assert.Single(rig.Client.Writes);
        Assert.Equal("/ch/01/preamp/trim", write.Address);
        Assert.Equal(expected, FaderMath.Denormalize((float)write.Arguments[0], -18, 18), 3);
    }

    [Fact]
    public void F04_shared_gain_and_cooldown_prevent_conflicting_reductions()
    {
        using var rig = new Rig();
        rig.Ready(gain: -6); rig.Ready(rig.Second, gain: -6);
        Assert.Same(rig.Channel.Gain, rig.Second.Gain);
        rig.Clip.EvaluateChannel(rig.Channel);
        rig.Clip.EvaluateChannel(rig.Second);
        Assert.Single(rig.Client.Writes);
        rig.Feedback("/headamp/000/gain", FaderMath.Normalize(-9, -12, 60));
        Assert.Equal(-9, rig.Second.PreampGain, 3);
        rig.Sample(rig.Second); rig.Clip.EvaluateChannel(rig.Second);
        Assert.Single(rig.Client.Writes);
    }

    [Fact]
    public void F05_F06_routing_and_peer_changes_invalidate_observed_state()
    {
        using var rig = new Rig();
        rig.Ready(); rig.Feedback("/fx/1/type", 10);
        rig.Feedback("/ch/01/config/source", 33);
        Assert.False(rig.Channel.GainInitialized); Assert.False(rig.Channel.FaderInitialized);
        rig.Clip.EvaluateChannel(rig.Channel); Assert.Empty(rig.Client.Writes);
        rig.Ready(); rig.Client.MixerIp = "127.0.0.2";
        Assert.False(rig.Channel.MuteInitialized); Assert.Empty(rig.Channel.SnapshotHistory());
        Assert.Empty(rig.Delay.DetectedEffects);
    }

    [Fact]
    public void F07_unknown_mute_blocks_both_controllers()
    {
        using var rig = new Rig(); rig.Ready(muteKnown: false);
        rig.Leveler.ProcessDevice(rig.Channel); rig.Clip.EvaluateChannel(rig.Channel);
        Assert.Empty(rig.Client.Writes);
    }

    [Fact]
    public void F08_unknown_changed_or_stale_effects_do_not_receive_time_writes()
    {
        using var rig = new Rig();
        rig.Delay.ConfigureSlot(1, DelaySlotConfig.Manual(DelayEffectType.StereoDelay, 500));
        Assert.Empty(rig.Client.Writes);
        rig.Feedback("/fx/1/type", 10); // Stereo Delay, as specified in the independent protocol table.
        Assert.Equal("/fx/1/par/02", Assert.Single(rig.Client.Writes).Address);
        rig.Feedback("/fx/1/type", 9999);
        rig.Delay.ApplyDelay(1, 600, DelayEffectType.StereoDelay);
        Assert.Single(rig.Client.Writes);
        rig.Feedback("/fx/1/type", 10);
        int count = rig.Client.Writes.Length;
        rig.Clock.Advance(3001);
        rig.Delay.ApplyDelay(1, 700, DelayEffectType.StereoDelay);
        Assert.Equal(count, rig.Client.Writes.Length);
    }

    [Fact]
    public void F09_stale_samples_suspend_controls_and_unknown_blobs_do_not_mark_health()
    {
        using var rig = new Rig(); rig.Ready(); rig.Clock.Advance(1001);
        rig.Leveler.ProcessDevice(rig.Channel); rig.Clip.EvaluateChannel(rig.Channel);
        Assert.Empty(rig.Client.Writes);
        rig.Meter.OnMessageReceived(new OscMessage("/m32live/unknown", ",b", new() { new byte[4] }));
        Assert.False(rig.Meter.IsReceivingMeters);
    }

    [Fact]
    public void F10_out_of_order_echoes_are_distinct_from_manual_changes()
    {
        var channel = new MixerChannel("Vox", 1, -18, 0);
        channel.UpdateFaderFromConsole(.5f);
        channel.RecordFaderSent(.51f); channel.RecordFaderSent(.52f);
        channel.UpdateFaderFromConsole(.51f); Assert.False(channel.DetectManualChange());
        channel.UpdateFaderFromConsole(.52f); Assert.False(channel.DetectManualChange());
        channel.UpdateFaderFromConsole(.8f); Assert.True(channel.DetectManualChange());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void F12_non_finite_parameters_are_rejected(double value)
    {
        using var rig = new Rig();
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Leveler.SlewUpDbPerSec = value);
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Leveler.GateThresholdDb = value);
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Clip.ReductionStepDb = value);
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Bpm.BpmChangeHysteresis = value);
        Assert.Throws<ArgumentOutOfRangeException>(() => DelaySlotConfig.Manual(DelayEffectType.StereoDelay, value));
        rig.Channel.UpdateFaderFromConsole((float)value); Assert.False(rig.Channel.FaderInitialized);
        rig.Delay.ApplyAllFromBpm(value); Assert.Empty(rig.Client.Writes);
        rig.Channel.AddSample(new MeterSample((float)value, rig.Clock)); Assert.Null(rig.Channel.GetLatestSample());
    }

    [Fact]
    public void F13_preset_applies_window_flags_and_manual_delay_configuration()
    {
        var app = new AppOrchestrator(); app.Initialize(true);
        try
        {
            app.AutoLeveler.Start();
            var loaded = new SessionConfig { MeterWindowMs = 10000, AutoLevelerEnabled = false, AntiClipperEnabled = true };
            loaded.DelaySlots.Add(new() { Slot = 1, IsAutomatic = false, ManualMs = 750 });
            app.ApplyLoadedConfig(loaded);
            Assert.Equal(10000, app.MeterReader.WindowMs);
            Assert.False(app.AutoLeveler.IsEnabled); Assert.True(app.AntiClipper.IsEnabled);
            Assert.Equal(750, app.DelayController.DelaySlots[1].ManualMs);
            Assert.Equal(app.Config.AntiClipperEnabled, app.AntiClipper.IsEnabled);
        }
        finally { app.Shutdown(false); }
    }

    [Theory]
    [InlineData("{\"WatchedDevices\":null,\"DelaySlots\":null}")]
    [InlineData("{\"WatchedDevices\":[null],\"DelaySlots\":[null]}")]
    public void F14_null_collections_and_elements_are_repaired(string json)
    {
        var config = JsonSerializer.Deserialize<SessionConfig>(json)!; config.Sanitize();
        Assert.Empty(config.WatchedDevices); Assert.Empty(config.DelaySlots);
    }

    [Fact]
    public void F15_failed_serialization_preserves_the_previous_session()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            AppOrchestrator.WriteSession(new SessionConfig(), path);
            string before = File.ReadAllText(path);
            Assert.Throws<ArgumentException>(() => AppOrchestrator.WriteSession(new SessionConfig { BpmChangeHysteresis = double.NaN }, path));
            Assert.Equal(before, File.ReadAllText(path));
            AppOrchestrator.WriteSession(new SessionConfig { MeterWindowMs = 5000 }, path);
            Assert.Equal(5000, AppOrchestrator.ReadSession(path).MeterWindowMs);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void F16_ten_second_window_and_average_without_history_copy()
    {
        var clock = new AuditClock();
        var bus = new Bus("Bus", 1, -18) { Clock = clock };
        bus.AddSample(new MeterSample(1, clock)); clock.Advance(6000);
        bus.AddSample(new MeterSample(0, clock));
        Assert.Equal(-3.0103, bus.GetAverageDb(10000), 3);
        Assert.Equal(-90, bus.GetAverageDb(5000));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) bus.GetAverageDb(10000);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 128);
    }

    [Theory]
    [InlineData(0, .5)]
    [InlineData(-1, .25)]
    public void F17_small_reductions_remove_at_least_one_physical_gain_step(int headAmp, double step)
    {
        using var rig = new Rig(reduction: .1); rig.Ready(headAmp: headAmp);
        rig.Clip.EvaluateChannel(rig.Channel);
        var write = Assert.Single(rig.Client.Writes);
        double value = FaderMath.Denormalize((float)write.Arguments[0], headAmp == 0 ? -12 : -18, headAmp == 0 ? 60 : 18);
        Assert.Equal(-step, value, 3);
    }

    [Fact]
    public void F18_peaks_between_poll_cycles_are_processed()
    {
        using var rig = new Rig(confirmation: 2); rig.Ready();
        rig.Sample(); rig.Sample(postTrim: -30);
        rig.Clip.EvaluateChannel(rig.Channel);
        Assert.Single(rig.Client.Writes);
        rig.Clip.EvaluateChannel(rig.Channel); Assert.Single(rig.Client.Writes);
    }

    [Fact]
    public void F18_bpm_consumes_every_new_edge_in_a_batch()
    {
        using var rig = new Rig(); rig.Bpm.Mode = BpmDetectionMode.Click;
        for (int i = 0; i < 5; i++)
        {
            rig.Sample(postTrim: -40); rig.Clock.Advance(100);
            rig.Sample(postTrim: 0); rig.Sample(postTrim: -40);
            rig.Bpm.DetectTransientsFromChannel();
            rig.Clock.Advance(400);
        }
        // The latest sample is always below threshold; polling only it would miss every edge.
        rig.Bpm.DetectTransientsFromChannel(); rig.Bpm.TryUpdateBpm();
        Assert.True(rig.Bpm.HasEverBeenValid);
        Assert.Equal(120, rig.Bpm.CurrentBpm, 1);
    }

    [Fact]
    public void F19_ipv6_is_rejected_before_a_worker_can_start()
    {
        Assert.False(SessionConfig.IsValidMixerIp("::1"));
        Assert.Throws<ArgumentException>(() => new OscClient("::1"));
    }

    [Fact]
    public void F20_zero_hysteresis_does_not_reprocess_old_taps_and_tempo_expires()
    {
        using var rig = new Rig();
        rig.Feedback("/fx/1/type", 10);
        rig.Delay.ConfigureSlot(1, DelaySlotConfig.Automatic(DelayEffectType.StereoDelay, BeatType.Quarter));
        rig.Bpm.TapTempo(); rig.Clock.Advance(500); rig.Bpm.TapTempo(); rig.Bpm.TryUpdateBpm();
        var write = Assert.Single(rig.Client.Writes);
        rig.Feedback(write.Address, write.Arguments[0]);
        for (int i = 0; i < 100; i++) rig.Bpm.TryUpdateBpm();
        Assert.Single(rig.Client.Writes);
        rig.Clock.Advance(4001); Assert.False(rig.Bpm.IsTempoFresh);
        rig.Bpm.Mode = BpmDetectionMode.Click; Assert.False(rig.Bpm.HasEverBeenValid);
    }

    [Fact]
    public void F22_emergency_key_closes_the_gate_and_cancels_the_form()
    {
        using var rig = new Rig(); rig.Ready();
        var input = new OperatorInput(rig.Client.InhibitWrites);
        var result = input.Handle(new ConsoleKeyInfo('\0', ConsoleKey.F12, false, false, false));
        Assert.Equal(ConsoleKey.Escape, result.Key);
        rig.Leveler.ProcessDevice(rig.Channel); rig.Clip.EvaluateChannel(rig.Channel);
        rig.Feedback("/fx/1/type", 10); rig.Delay.ApplyDelay(1, 500, DelayEffectType.StereoDelay);
        Assert.Empty(rig.Client.Writes);
    }

    [Fact]
    public void F24_civil_clock_changes_do_not_age_samples_or_change_cooldowns()
    {
        using var rig = new Rig(); rig.Ready();
        var sample = rig.Channel.GetLatestSample()!;
        rig.Clip.EvaluateChannel(rig.Channel);
        rig.Clock.JumpCivilClock(24);
        Assert.Equal(0, sample.AgeMs()); Assert.True(rig.Clip.WasRecentlyReduced(rig.Channel));
        rig.Clock.Advance(2501); Assert.False(rig.Clip.WasRecentlyReduced(rig.Channel));
    }

    [Fact]
    public void Failed_sends_do_not_commit_fader_state()
    {
        using var rig = new Rig(slew: 2); rig.Ready();
        float before = rig.Channel.LastSentFader;
        rig.Client.FailSends = true; rig.Leveler.ProcessDevice(rig.Channel);
        Assert.Equal(before, rig.Channel.LastSentFader); Assert.False(rig.Channel.DetectManualChange());
    }

    [Fact]
    public void Lost_gain_write_retries_the_same_attenuation_after_a_fresh_reply()
    {
        using var rig = new Rig(); rig.Ready(); rig.Clip.EvaluateChannel(rig.Channel);
        var first = Assert.Single(rig.Client.Writes);
        rig.Clock.Advance(1001);
        rig.Feedback("/headamp/000/gain", FaderMath.Normalize(0, -12, 60)); // original write was lost
        rig.Sample(); rig.Clip.EvaluateChannel(rig.Channel);
        Assert.Equal(2, rig.Client.Writes.Length);
        Assert.Equal(first.Arguments[0], rig.Client.Writes[1].Arguments[0]);
        rig.Feedback("/headamp/000/gain", (float)first.Arguments[0]);
        Assert.True(rig.Channel.GainInitialized);
        Assert.InRange(rig.Channel.Gain.TotalReductionDb, 2.99, 3.01);
    }

    [Fact]
    public void Late_gain_reply_cannot_undo_a_pending_reduction()
    {
        using var rig = new Rig(); rig.Ready(); rig.Clip.EvaluateChannel(rig.Channel);
        rig.Feedback("/headamp/000/gain", FaderMath.Normalize(6, -12, 60));
        Assert.False(rig.Channel.GainInitialized);
        rig.Sample(); rig.Clip.EvaluateChannel(rig.Channel);
        Assert.Single(rig.Client.Writes);
    }

    [Fact]
    public void Lost_parameter_feedback_expires_instead_of_remaining_ready_forever()
    {
        using var rig = new Rig(); rig.Ready();
        rig.Clock.Advance(3001); rig.Sample();
        Assert.False(rig.Channel.GainInitialized);
        rig.Clip.EvaluateChannel(rig.Channel); Assert.Empty(rig.Client.Writes);
        rig.Clock.Advance(3000); rig.Sample();
        Assert.False(rig.Channel.FaderInitialized); Assert.False(rig.Channel.MuteInitialized);
        rig.Leveler.ProcessDevice(rig.Channel); Assert.Empty(rig.Client.Writes);
    }

    [Fact]
    public void Per_device_exclusions_and_shared_gain_budget_are_enforced()
    {
        using var rig = new Rig(); rig.Ready(); rig.Ready(rig.Second);
        rig.Channel.AutoLevelEnabled = false;
        rig.Second.MaxGainReductionDb = 0;
        rig.Leveler.ProcessDevice(rig.Channel); rig.Clip.EvaluateChannel(rig.Channel);
        Assert.Empty(rig.Client.Writes);
        rig.Channel.ProtectionEnabled = false; rig.Second.MaxGainReductionDb = 24;
        rig.Sample(); rig.Clip.EvaluateChannel(rig.Channel); Assert.Empty(rig.Client.Writes);
    }

    [Fact]
    public void Lowering_a_fader_ceiling_still_respects_the_slew_limit()
    {
        using var rig = new Rig(); rig.Ready(); rig.Channel.MaxFaderDb = -30;
        double before = FaderMath.FaderToDb(rig.Channel.LastSentFader);
        for (int i = 0; i < 4; i++) { rig.Leveler.ProcessDevice(rig.Channel); rig.Clock.Advance(100); }
        double change = FaderMath.FaderToDb(rig.Channel.LastSentFader) - before;
        Assert.InRange(change, -.201, -.05);
    }

    [Fact]
    public void Long_pause_does_not_cause_a_large_catch_up_step()
    {
        using var rig = new Rig(slew: 2); rig.Ready(); rig.Leveler.ProcessDevice(rig.Channel);
        rig.Channel.UpdateFaderFromConsole(rig.Channel.LastSentFader);
        float before = rig.Channel.LastSentFader;
        rig.Clock.Advance(10000); rig.Sample();
        rig.Channel.UpdateFaderFromConsole(before); rig.Channel.UpdateMuteFromConsole(1);
        rig.Leveler.ProcessDevice(rig.Channel);
        Assert.InRange(FaderMath.FaderToDb(rig.Channel.LastSentFader) - FaderMath.FaderToDb(before), 0, .401);
    }

    [Fact]
    public void Accumulated_small_steps_cannot_cross_a_nearby_fader_ceiling()
    {
        using var rig = new Rig(); rig.Ready();
        rig.Channel.MaxFaderDb = -19.97;
        for (int i = 0; i < 8; i++)
        {
            rig.Sample(); rig.Leveler.ProcessDevice(rig.Channel); rig.Clock.Advance(100);
        }
        Assert.True(FaderMath.FaderToDb(rig.Channel.LastSentFader) <= rig.Channel.MaxFaderDb);
        Assert.All(rig.Client.Writes, message =>
            Assert.True(FaderMath.FaderToDb((float)message.Arguments[0]) <= rig.Channel.MaxFaderDb));
    }

    [Fact]
    public void Resuming_an_unrelated_module_does_not_rearm_delays_after_panic()
    {
        using var rig = new Rig();
        rig.Delay.ConfigureSlot(1, DelaySlotConfig.Manual(DelayEffectType.StereoDelay, 500));
        rig.Client.InhibitWrites(); rig.Delay.SuspendControl();
        rig.Client.ResumeWrites(); // the operator enabled only the leveler
        rig.Feedback("/fx/1/type", 10);
        Assert.Empty(rig.Client.Writes);
        rig.Delay.ResumeControl(); rig.Delay.ApplyAllCurrentSettings(0);
        Assert.Single(rig.Client.Writes);
    }

    [Fact]
    public void New_device_options_round_trip_with_a_preset()
    {
        var channel = new MixerChannel("Vox", 1, -15, 0)
        { AutoLevelEnabled = false, ProtectionEnabled = false, MaxFaderDb = -5, MaxGainReductionDb = 12 };
        var copy = AudioDeviceDescriptor.FromAudioDevice(channel).ToAudioDevice();
        Assert.False(copy.AutoLevelEnabled); Assert.False(copy.ProtectionEnabled);
        Assert.Equal(-5, copy.MaxFaderDb); Assert.Equal(12, copy.MaxGainReductionDb);
    }

    [Fact]
    public void Missing_routing_replies_block_gain_control_even_when_old_gain_keeps_arriving()
    {
        using var rig = new Rig(); rig.Ready(); rig.Clock.Advance(3001);
        rig.Feedback("/headamp/000/gain", FaderMath.Normalize(0, -12, 60));
        rig.Sample(); rig.Clip.EvaluateChannel(rig.Channel);
        Assert.False(rig.Channel.HeadAmpResolved); Assert.Empty(rig.Client.Writes);
        rig.Feedback("/-ha/00/index", 0); rig.Sample(); rig.Clip.EvaluateChannel(rig.Channel);
        Assert.Single(rig.Client.Writes);
    }
}
