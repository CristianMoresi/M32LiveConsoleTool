using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Modules;
using M32LiveConsoleTool.Osc;

namespace M32LiveConsoleTool.Tests;

public class TransportRecoveryTests
{
    [Fact]
    public async Task Idle_udp_receivers_can_stop_without_incoming_packets()
    {
        var simulator = new M32Simulator(0, new ShowLogger());
        simulator.Start(); Assert.True(simulator.IsRunning);
        var client = new OscClient("127.0.0.1", simulator.LocalPort, 0);
        Assert.True(client.Connect());
        await Task.Delay(350); // both receive loops are waiting; no peer sends a packet
        Assert.Equal(0, client.RejectedCount); // receive timeouts are normal maintenance
        var shutdown = Task.Run(() => { client.Disconnect(); simulator.Stop(); });
        await shutdown.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static bool Until(Func<bool> predicate, int timeoutMs = 6000)
    {
        var watch = Stopwatch.StartNew();
        do { if (predicate()) return true; Thread.Sleep(20); } while (watch.ElapsedMilliseconds < timeoutMs);
        return predicate();
    }

    [Fact]
    public void F11_only_the_selected_peer_can_publish_feedback()
    {
        using var selected = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var stranger = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var client = new OscClient("127.0.0.1", ((IPEndPoint)selected.Client.LocalEndPoint!).Port, 0);
        int delivered = 0;
        client.MessageReceived += _ => Interlocked.Increment(ref delivered);
        try
        {
            Assert.True(client.Connect());
            var destination = new IPEndPoint(IPAddress.Loopback, client.LocalPort);
            // Independent golden OSC bytes: /x, integer 1.
            byte[] packet = { 0x2f, 0x78, 0, 0, 0x2c, 0x69, 0, 0, 0, 0, 0, 1 };
            for (int i = 0; i < 5; i++) stranger.Send(packet, destination);
            Assert.True(Until(() => client.RejectedCount > 0));
            Assert.Equal(0, Volatile.Read(ref delivered));
            Assert.True(Until(() => { selected.Send(packet, destination); return Volatile.Read(ref delivered) > 0; }));
        }
        finally { client.Disconnect(); }
    }

    [Fact]
    public void F07_F23_initial_reply_losses_recover_mute_gain_meters_and_all_required_fx()
    {
        var logger = new ShowLogger();
        var dropped = new ConcurrentDictionary<string, byte>();
        var simulator = new M32Simulator(0, logger)
        {
            FixedInputDb = -15,
            ClipExcursionsEnabled = false,
            ShouldDropReply = message => message.TypeTags != ",b" && dropped.TryAdd(message.Address, 0)
        };
        simulator.Start(); Assert.True(simulator.IsRunning);
        var client = new OscClient("127.0.0.1", simulator.LocalPort, 0);
        var channel = new MixerChannel("Vox", 1, -18, 0);
        var meter = new MeterReader(client, new() { channel }, 1000);
        var delay = new DelayController(client, logger);
        try
        {
            Assert.True(client.Connect()); meter.StartReading(); delay.Start();
            delay.ConfigureSlot(1, DelaySlotConfig.Manual(DelayEffectType.StereoDelay, 750));
            Assert.True(Until(() => channel.ReadyForLeveling && channel.GainInitialized
                && Enumerable.Range(1, 4).All(delay.DetectedEffects.ContainsKey)), "Initial state did not recover after one dropped reply per parameter.");
            Assert.True(Until(() => Math.Abs(FaderMath.Denormalize(simulator.ReadParameter("/fx/1/par/02"), 1, 3000) - 750) < .01));
            Assert.Contains("/ch/01/mix/on", dropped.Keys);
            Assert.Contains("/headamp/000/gain", dropped.Keys);
        }
        finally { delay.Stop(); meter.StopReading(); client.Disconnect(); simulator.Stop(); }
    }

    [Fact]
    public void F21_simulated_preamp_attenuates_both_meters_and_digital_routing_is_observed()
    {
        var logger = new ShowLogger();
        var simulator = new M32Simulator(0, logger) { FixedInputDb = -15, ClipExcursionsEnabled = false };
        simulator.Start(); Assert.True(simulator.IsRunning);
        var client = new OscClient("127.0.0.1", simulator.LocalPort, 0);
        var channel = new MixerChannel("Vox", 1, -18, 0);
        var meter = new MeterReader(client, new() { channel }, 1000);
        try
        {
            Assert.True(client.Connect()); meter.StartReading();
            Assert.True(Until(() => channel.ReadyForLeveling && channel.GainInitialized));
            var before = channel.GetLatestSample()!;
            client.Send(new OscMessage("/headamp/000/gain", ",f", new() { 0f })); // -12 dB
            Assert.True(Until(() => channel.GetLatestSample()?.PostTrimDb < -26));
            var after = channel.GetLatestSample()!;
            Assert.Equal(-12, after.PostTrimDb - before.PostTrimDb, 2);
            Assert.Equal(-12, after.PostFaderDb - before.PostFaderDb, 2);
            simulator.SetChannelSource(1, -1);
            Assert.True(Until(() => channel.HeadAmpIndex == -1 && channel.GainInitialized));
            client.Send(new OscMessage("/ch/01/preamp/trim", ",f", new() { FaderMath.Normalize(-6, -18, 18) }));
            Assert.True(Until(() => channel.GetLatestSample() is { } latest && Math.Abs(latest.PostFaderDb + 21) < .01));
        }
        finally { meter.StopReading(); client.Disconnect(); simulator.Stop(); }
    }

    [Fact]
    public void Independent_osc_fixture_round_trips_and_truncated_strings_are_rejected()
    {
        byte[] golden = { 0x2f, 0x78, 0, 0, 0x2c, 0x66, 0, 0, 0x3f, 0, 0, 0 };
        var message = OscMessage.Decode(golden);
        Assert.Equal("/x", message.Address); Assert.Equal(.5f, message.Arguments[0]);
        Assert.Equal(golden, message.Encode());
        Assert.Throws<FormatException>(() => OscMessage.Decode(new byte[] { 0x2f, 0x78 }));
        Assert.Throws<FormatException>(() => OscMessage.Decode(new byte[] { 0x2f, 0x78, 0, 0, 0x2c, 0 }));
    }

    [Fact]
    public void Logging_flushes_on_close_and_retains_the_memory_ring()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
        var logger = new ShowLogger();
        try
        {
            logger.Open(path);
            for (int i = 0; i < 100; i++) logger.LogEvent($"event {i}", Severity.Warning);
            logger.Close();
            Assert.Null(logger.DiskError);
            Assert.Contains("event 99", File.ReadAllText(path));
            Assert.Equal(50, logger.GetRecentImportantEvents(100).Count);
        }
        finally { logger.Close(); File.Delete(path); }
    }

    [Fact]
    public void Manual_tap_without_watched_channels_survives_subscription_maintenance()
    {
        var logger = new ShowLogger();
        var simulator = new M32Simulator(0, logger);
        simulator.Start(); Assert.True(simulator.IsRunning);
        var client = new OscClient("127.0.0.1", simulator.LocalPort, 0);
        var meter = new MeterReader(client, new(), 1000);
        var delay = new DelayController(client, logger);
        var bpm = new BpmDetector(meter, delay, logger, new MixerChannel("Unused", 1, -18, 0),
            BpmDetectionMode.ManualTap, -20, bpmChangeHysteresis: 0);
        try
        {
            Assert.True(client.Connect()); long generation = client.Generation;
            meter.StartReading(); delay.Start(); bpm.Start();
            delay.ConfigureSlot(1, DelaySlotConfig.Automatic(DelayEffectType.StereoDelay, BeatType.Quarter));
            for (int i = 0; i < 6; i++) { bpm.TapTempo(); Thread.Sleep(500); }
            Assert.Equal(generation, client.Generation);
            Assert.True(bpm.IsTempoFresh);
            // Scheduling jitter changes real tap spacing on a busy runner. The
            // transport must carry the measured tempo, not an assumed Sleep duration.
            bpm.TryUpdateBpm();
            double expectedMs = 60000 / bpm.CurrentBpm;
            Assert.True(Until(() => Math.Abs(FaderMath.Denormalize(
                simulator.ReadParameter("/fx/1/par/02"), 1, 3000) - expectedMs) < .1));
        }
        finally { bpm.Stop(); delay.Stop(); meter.StopReading(); client.Disconnect(); simulator.Stop(); }
    }
}
