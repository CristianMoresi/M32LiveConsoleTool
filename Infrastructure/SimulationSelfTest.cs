using System.Diagnostics;
using System.Text.Json;
using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Modules;
using M32LiveConsoleTool.Osc;

namespace M32LiveConsoleTool.Infrastructure;

/// <summary>Loopback-only smoke test and repeatable load profile for portable releases.</summary>
public static class SimulationSelfTest
{
    public static int Run(int durationSeconds)
    {
        var logger = new ShowLogger();
        var simulator = new M32Simulator(0, logger) { FixedInputDb = -15, ClipExcursionsEnabled = false };
        simulator.Start();
        if (!simulator.IsRunning) { Console.Error.WriteLine("Simulator could not bind loopback."); return 1; }
        var client = new OscClient("127.0.0.1", simulator.LocalPort, 0);
        var devices = Enumerable.Range(1, 32).Select(n => (AudioDevice)new MixerChannel($"Channel {n}", n, -18, 0))
            .Concat(Enumerable.Range(1, 16).Select(n => new Bus($"Bus {n}", n, -18))).ToList();
        var meter = new MeterReader(client, devices, 2500);
        var leveler = new AutoLeveler(client, meter, logger, devices, 3, .5, .5, -40, 8, 100);
        var clip = new AntiClipper(client, meter, logger, devices.OfType<MixerChannel>().ToList(), -1);
        var delay = new DelayController(client, logger);
        var bpm = new BpmDetector(meter, delay, logger, (MixerChannel)devices[0], BpmDetectionMode.ManualTap, -20);
        try
        {
            if (!client.Connect()) throw new IOException(client.ConnectError);
            client.StartKeepAlive(); meter.StartReading(); delay.Start();
            var warmup = Stopwatch.StartNew();
            while (warmup.ElapsedMilliseconds < 8000 && !Ready()) Thread.Sleep(25);
            if (!Ready()) throw new IOException("Initial synchronization did not complete within 8 seconds.");
            delay.ConfigureSlot(1, DelaySlotConfig.Automatic(DelayEffectType.StereoDelay, BeatType.Quarter));
            leveler.Start(); clip.Monitor(); bpm.Start();
            var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            long allocations = GC.GetTotalAllocatedBytes(true);
            long received = client.ReceivedCount;
            int[] gc = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            var ageHistogram = new long[1001];
            int degradedChecks = 0;
            var watch = Stopwatch.StartNew();
            long nextTap = 0;
            while (watch.Elapsed.TotalSeconds < durationSeconds)
            {
                if (watch.ElapsedMilliseconds >= nextTap) { bpm.TapTempo(); nextTap += 500; }
                if (!Ready()) degradedChecks++;
                foreach (var device in devices)
                {
                    double age = device.GetLatestSample()?.AgeMs() ?? 1000;
                    ageHistogram[Math.Clamp((int)age, 0, 1000)]++;
                }
                Thread.Sleep(50);
            }
            double seconds = watch.Elapsed.TotalSeconds;
            double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            long allocated = GC.GetTotalAllocatedBytes(true) - allocations;
            var panic = Stopwatch.StartNew(); client.InhibitWrites(); panic.Stop();
            long writesAtPanic = client.ControlSentCount;
            Thread.Sleep(150);
            bool panicPassed = writesAtPanic == client.ControlSentCount;
            // Averages must not allocate one history snapshot per read.
            long beforeAverage = GC.GetAllocatedBytesForCurrentThread();
            var averageWatch = Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++) foreach (var device in devices) meter.GetMovingAverage(device);
            averageWatch.Stop();
            long averageAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeAverage;
            var result = new
            {
                durationSeconds = seconds,
                cpuMs,
                oneCoreCpuPercent = cpuMs / (seconds * 10),
                allocatedBytes = allocated,
                receivedMessages = client.ReceivedCount - received,
                controlWrites = client.ControlSentCount,
                gcCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - gc[i]).ToArray(),
                devicesReady = devices.Count(d => d.FaderInitialized && d.MuteInitialized && d.HasFreshMeters),
                gainsReady = devices.OfType<MixerChannel>().Count(c => c.GainInitialized),
                degradedChecks,
                observedMeterAgeMs = new { p50 = Percentile(.5), p95 = Percentile(.95), p99 = Percentile(.99) },
                panicMs = panic.Elapsed.TotalMilliseconds,
                panicPassed,
                average48000CallsMs = averageWatch.Elapsed.TotalMilliseconds,
                average48000CallsAllocatedBytes = averageAllocated,
                client.SendFailureCount,
                client.RejectedCount,
                logger.DroppedEvents
            };
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return panicPassed && Ready() ? 0 : 1;

            int Percentile(double fraction)
            {
                double threshold = ageHistogram.Sum() * fraction;
                long sum = 0;
                for (int i = 0; i < ageHistogram.Length; i++) { sum += ageHistogram[i]; if (sum >= threshold) return i; }
                return 1000;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"Self-test failed: {ex.Message}"); return 1; }
        finally { client.InhibitWrites(); bpm.Stop(); clip.Stop(); leveler.Stop(); delay.Stop(); meter.StopReading(); client.Disconnect(); simulator.Stop(); }

        bool Ready() => devices.All(d => d.FaderInitialized && d.MuteInitialized && d.HasFreshMeters)
            && devices.OfType<MixerChannel>().All(c => c.GainInitialized);
    }
}
