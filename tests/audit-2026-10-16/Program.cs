// Audit-only probes: confirm current defects, not a replacement regression suite.
// All UDP traffic stays on loopback. No production session or preset is loaded.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Modules;
using M32LiveConsoleTool.Osc;
using M32LiveConsoleTool.Ui;

static class Probe
{
    const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly List<object> Results = new();
    static object? Call(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, Hidden)!.Invoke(obj, args);
    static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Hidden)!.SetValue(obj, value);
    static object? Get(object obj, string name) => obj.GetType().GetField(name, Hidden)!.GetValue(obj);
    static OscMessage Msg(string address, string types = ",", params object[] args) => new(address, types, args.ToList());
    static float Linear(double db) => (float)FaderMath.DbToLinear(db);
    static MixerChannel Channel(double faderDb = 0) { var c = new MixerChannel("Audit", 1, -18, 0); c.UpdateFaderFromConsole(FaderMath.DbToFader(faderDb)); return c; }
    static MeterReader Reader(OscClient client, params AudioDevice[] channels) => new(client, channels.ToList(), 2500);
    static AutoLeveler Leveler(OscClient client, MeterReader reader, MixerChannel c) => new(client, reader, new(), new(){c}, 3, 0.5, 0.5, -40, 8, 100);
    static AntiClipper Anti(OscClient client, MeterReader reader, params MixerChannel[] channels) => new(client, reader, new(), channels.ToList(), -1, 3, 6, 6, 1, 100);
    static void Record(string id, bool observed, string evidence) {
        Results.Add(new { id, defectObserved = observed, evidence });
        Console.WriteLine($"{id}: {(observed ? "OBSERVED" : "NOT OBSERVED")} | {evidence}");
    }
    static void Run(string id, Action test) { try { test(); } catch(Exception ex) { Results.Add(new{id,error=ex.ToString()}); Console.WriteLine($"{id}: ERROR {ex.GetBaseException().Message}"); } }
    static byte[] Blob(params float[] values) {
        var bytes = new byte[4 + values.Length * 4];
        BitConverter.GetBytes(values.Length).CopyTo(bytes, 0);
        for(int i=0; i<values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(bytes, 4+i*4);
        return bytes;
    }
    sealed class Loopback : IDisposable {
        public UdpClient Sink = new(new IPEndPoint(IPAddress.Loopback, 0));
        public OscClient Client;
        public Loopback() {
            Client = new("127.0.0.1", ((IPEndPoint)Sink.Client.LocalEndPoint!).Port, 0);
            if(!Client.Connect()) throw new Exception(Client.ConnectError);
            // This host drops initial datagrams even with plain UdpClient.
            // Warm only the audit sink so transport loss does not obscure logic probes.
            for(int i=0;i<5;i++){ Client.Send(Msg("/audit/warmup")); Thread.Sleep(50); }
            if(Drain().Count==0) throw new Exception("Loopback sink not receiving");
        }
        public List<OscMessage> Drain() {
            Thread.Sleep(30); var list=new List<OscMessage>();
            while(Sink.Available>0) { var ep=new IPEndPoint(IPAddress.Any,0); list.Add(OscMessage.Decode(Sink.Receive(ref ep))); }
            return list;
        }
        public void Dispose() { Client.Disconnect(); Sink.Dispose(); }
    }
    static void Main(string[] args) {
        if(args.Contains("--benchmark")){ AverageBenchmark(); return; }
        if(!args.Contains("--historical-probes") && !args.Contains("--load")) {
            Console.WriteLine("Historical defect probes target commit 8042990. Use --benchmark for current performance, or the production --self-test for current integration checks.");
            return;
        }
        if(args.Contains("--load")){ LoadProfile(); return; }
        Run("transport-control", () => {
            using var sink=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
            using var sender=new UdpClient(0); var counts=new List<int>();
            for(int i=0;i<6;i++) { sender.Send(new byte[]{1,2,3,4},4,(IPEndPoint)sink.Client.LocalEndPoint!); Thread.Sleep(100); counts.Add(sink.Available/4); }
            Results.Add(new{id="transport-control",sent=6,queuedAfterEachSend=counts});
            Console.WriteLine($"transport-control: plain UdpClient queued after each send: {string.Join(',',counts)}");
        });
        Run("A01-small-step", () => {
            using var io=new Loopback(); var c=Channel(-20); var r=Reader(io.Client,c); var a=Leveler(io.Client,r,c);
            c.AddSample(new MeterSample(Linear(-24)));
            for(int i=0;i<4;i++) Call(a,"ProcessDevice",c);
            var sent=io.Drain(); var state=a.GetDeviceState(c);
            Record("A01-small-step", sent.Count==0 && state==LevelingState.ManualOverride,
                $"default 0.5 dB/s, 100 ms, fader -20 dB: sent={sent.Count}, state={state}, lastSent={c.LastSentFader}, current={c.CurrentFader}");
        });
        Run("A02-unresolved-preamp", () => {
            using var io=new Loopback(); var c=Channel(); c.PreampGain=12;
            var r=Reader(io.Client,c); var a=Anti(io.Client,r,c); c.AddSample(new(1,1,1,0.1f));
            Call(a,"EvaluateChannel",c); var sent=io.Drain();
            Record("A02-unresolved-preamp", !c.HeadAmpResolved && sent.Any(m=>m.Address.Contains("/preamp/trim")),
                $"resolved={c.HeadAmpResolved}, cachedGainBefore=12 dB, outgoing={string.Join(';',sent.Select(m=>$"{m.Address} {m.Arguments[0]}"))}, cachedGainAfter={c.PreampGain}");
        });
        Run("A03-digital-trim", () => {
            using var io=new Loopback(); var c=Channel(); var r=Reader(io.Client,c);
            Call(r,"TryHandleHeadAmpFeedback",Msg("/-ha/00/index",",i",-1));
            var seed=io.Drain(); bool handled=(bool)Call(r,"TryHandleHeadAmpFeedback",Msg("/ch/01/preamp/trim",",f",0f))!;
            Record("A03-digital-trim", seed.Count==0 && !handled && c.PreampGain==0,
                $"digital source resolved={c.HeadAmpResolved}; trim queries={seed.Count}; trim feedback handled={handled}; feedback -18 dB leaves cache at {c.PreampGain} dB");
        });
        Run("A04-shared-preamp", () => {
            using var io=new Loopback(); var a=Channel(); var b=new MixerChannel("B",2,-18,0);
            a.HeadAmpIndex=b.HeadAmpIndex=0; a.HeadAmpResolved=b.HeadAmpResolved=true;
            var r=Reader(io.Client,a,b);
            Call(r,"TryHandleHeadAmpFeedback",Msg("/headamp/000/gain",",f",FaderMath.Normalize(-12,-12,60)));
            Record("A04-shared-preamp", a.PreampGain==-12 && b.PreampGain==0, $"same HA0: channel1={a.PreampGain} dB, channel2={b.PreampGain} dB");
        });
        Run("A05-stale-meters", () => {
            using var io=new Loopback(); var c=Channel(); var r=Reader(io.Client,c); var a=Leveler(io.Client,r,c);
            var sample=new MeterSample(Linear(-24)); Set(sample,"timestamp",DateTime.UtcNow.AddMilliseconds(-2200)); c.AddSample(sample);
            Set(r,"lastMeterTickMs",Environment.TickCount64-2200); Call(a,"ProcessDevice",c); var sent=io.Drain();
            Record("A05-stale-meters", !r.IsReceivingMeters && sent.Count>0, $"sample 2200 ms old; receiving={r.IsReceivingMeters}; outgoing corrections={sent.Count}");
        });
        Run("A06-invalid-meter-health", () => {
            var client=new OscClient("127.0.0.1"); var r=Reader(client,Channel()); Set(r,"isRunning",true);
            Call(r,"OnMessageReceived",Msg("/m32lct_unknown",",b",new byte[]{1}));
            Record("A06-invalid-meter-health",r.IsReceivingMeters,$"unknown alias plus truncated blob marks receiving={r.IsReceivingMeters}");
        });
        Run("A07-nan-contamination", () => {
            var client=new OscClient("127.0.0.1"); var c=Channel(); var r=Reader(client,c);
            c.AddSample(new MeterSample(float.NaN)); c.AddSample(new MeterSample(0.1f));
            var avg=r.GetMovingAverage(c); Record("A07-nan-contamination",double.IsNaN(avg),$"NaN plus valid 0.1 amplitude => average={avg}");
        });
        Run("A08-delay-before-detection", () => {
            using var io=new Loopback(); var d=new DelayController(io.Client,new());
            d.ConfigureSlot(1,DelaySlotConfig.Manual(DelayEffectType.StereoDelay,500)); d.ApplyAllCurrentSettings(0);
            var sent=io.Drain(); Record("A08-delay-before-detection",d.DetectedEffects.Count==0 && sent.Count==1,$"detected={d.DetectedEffects.Count}; outgoing={string.Join(';',sent.Select(m=>m.Address))}");
        });
        Run("A09-unknown-fx-retained", () => {
            using var io=new Loopback(); var d=new DelayController(io.Client,new()); Set(d,"isRunning",true);
            Call(d,"OnMessageReceived",Msg("/fx/1/type",",i",10)); Call(d,"OnMessageReceived",Msg("/fx/1/type",",i",999));
            d.ApplyDelay(1,500,DelayEffectType.StereoDelay); var sent=io.Drain();
            Record("A09-unknown-fx-retained",d.DetectedEffects[1].IsDelay && sent.Count==1,$"new unknown type 999 keeps {d.DetectedEffects[1].Name}; writes={sent.Count}");
        });
        Run("A10-preset-application", () => {
            using var io=new Loopback(); var c=Channel(); var r=Reader(io.Client,c); var log=new ShowLogger(); var d=new DelayController(io.Client,log);
            var al=Leveler(io.Client,r,c); var ac=Anti(io.Client,r,c); var bpm=new BpmDetector(r,d,log,c,BpmDetectionMode.ManualTap,-20);
            var app=new AppOrchestrator(); Set(app,"oscClient",io.Client); Set(app,"logger",log); Set(app,"meterReader",r); Set(app,"delayController",d); Set(app,"autoLeveler",al); Set(app,"antiClipper",ac); Set(app,"bpmDetector",bpm);
            al.Start();
            var loaded=new SessionConfig { MeterWindowMs=900,AutoLevelerEnabled=false,DelaySlots=new(){new(){Slot=1,EffectType=DelayEffectType.StereoDelay,IsAutomatic=false,ManualMs=800}} };
            try {
                Call(app,"ApplyLoadedConfig",loaded); var sent=io.Drain();
                Record("A10-preset-window",r.WindowMs!=loaded.MeterWindowMs,$"config window={loaded.MeterWindowMs}, active window={r.WindowMs}");
                Record("A10-preset-manual-delay",sent.Count==0,$"loaded 800 ms manual slot: outgoing writes={sent.Count}");
                Record("A10-preset-enabled",app.AutoLeveler.IsEnabled && !app.Config.AutoLevelerEnabled,$"loaded OFF, runtime={app.AutoLeveler.IsEnabled}, config={app.Config.AutoLevelerEnabled}");
            } finally { app.Shutdown(false); }
        });
        Run("A11-null-config", () => {
            var cfg=JsonSerializer.Deserialize<SessionConfig>("{\"WatchedDevices\":null}")!;
            string result="none"; try{cfg.Sanitize();}catch(Exception ex){result=ex.GetType().Name;}
            Record("A11-null-config",result=="NullReferenceException",$"valid JSON WatchedDevices:null => {result}");
        });
        Run("A12-history-window", () => {
            var client=new OscClient("127.0.0.1"); var c=Channel(); var r=new MeterReader(client,new(){c},10000);
            var old=new MeterSample(0.1f); Set(old,"timestamp",DateTime.UtcNow.AddSeconds(-7)); c.AddSample(old); c.AddSample(new MeterSample(0.01f));
            Record("A12-history-window",c.SnapshotHistory().Count==1,$"configured window 10000 ms; 7-second sample removed; average={r.GetMovingAverage(c):F1} dB (two-sample expected about -23.0 dB)");
        });
        Run("A13-substep-gain", () => {
            using var io=new Loopback(); var c=Channel(); c.HeadAmpIndex=0; c.HeadAmpResolved=true;
            var r=Reader(io.Client,c); var a=new AntiClipper(io.Client,r,new(),new(){c},-1,0.1,6,6,1,100);
            c.AddSample(new(1,1,1,0.1f)); Call(a,"EvaluateChannel",c);
            Record("A13-substep-gain",c.PreampGain==0,$"valid reduction 0.1 dB, HA step 0.5 dB: gain after action={c.PreampGain}; writes={io.Drain().Count}");
        });
        Run("A14-delayed-echo", () => {
            using var io=new Loopback(); var c=Channel(); var r=Reader(io.Client,c); var a=Leveler(io.Client,r,c); a.SlewUpDbPerSec=8;
            c.AddSample(new MeterSample(Linear(-24))); Call(a,"ProcessDevice",c); float first=c.LastSentFader; Call(a,"ProcessDevice",c);
            c.UpdateFaderFromConsole(first); Call(a,"ProcessDevice",c);
            Record("A14-delayed-echo",a.GetDeviceState(c)==LevelingState.ManualOverride,$"older echo after two commands enters {a.GetDeviceState(c)}");
        });
        Run("A15-bpm-zero-hysteresis", () => {
            using var io=new Loopback(); var c=Channel(); var r=Reader(io.Client,c); var d=new DelayController(io.Client,new());
            d.ConfigureSlot(1,DelaySlotConfig.Automatic(DelayEffectType.StereoDelay,BeatType.Quarter));
            var b=new BpmDetector(r,d,new(),c,BpmDetectionMode.ManualTap,-20,0);
            var t=DateTime.UtcNow.AddSeconds(-1); Call(b,"RegisterTransient",t); Call(b,"RegisterTransient",t.AddMilliseconds(500));
            for(int i=0;i<10;i++) Call(b,"TryUpdateBpm"); var sent=io.Drain();
            Record("A15-bpm-zero-hysteresis",sent.Count==10,$"two taps, ten recalculations, stable bpm={b.CurrentBpm}, outgoing identical delay writes={sent.Count}");
        });
        Run("A16-foreign-endpoint", () => {
            using var io=new Loopback(); var clientSocket=(UdpClient)Get(io.Client,"udpClient")!;
            var port=((IPEndPoint)clientSocket.Client.LocalEndPoint!).Port; var c=Channel(); var r=Reader(io.Client,c);
            r.StartReading(); using var outsider=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
            byte[] bytes=Msg(c.GetOscAddress(),",f",0.9f).Encode(); outsider.Send(bytes,bytes.Length,new IPEndPoint(IPAddress.Loopback,port));
            SpinWait.SpinUntil(()=>c.CurrentFader==0.9f,1000);
            Record("A16-foreign-endpoint",c.CurrentFader==0.9f,$"packet from unconfigured source port updates fader={c.CurrentFader}"); r.StopReading();
        });
        Run("A17-meter-peak-skipped", () => {
            using var io=new Loopback(); var c=Channel(); c.HeadAmpResolved=true; c.HeadAmpIndex=0;
            var r=Reader(io.Client,c); var a=Anti(io.Client,r,c);
            c.AddSample(new(1,1,1,0.1f)); Thread.Sleep(2); c.AddSample(new(0.01f,1,1,0.01f)); Call(a,"EvaluateChannel",c);
            Record("A17-meter-peak-skipped",io.Drain().Count==0,$"received clipped then quiet sample before worker poll; confirmation=1; gain={c.PreampGain}, no action");
        });
        Run("A18-ip-change-keeps-state", () => {
            var client=new OscClient("127.0.0.1"); var c=Channel(); c.AddSample(new MeterSample(0.2f));
            var r=Reader(client,c); Set(r,"isRunning",true); var d=new DelayController(client,new()); Set(d,"isRunning",true);
            Call(d,"OnMessageReceived",Msg("/fx/1/type",",i",10)); var app=new AppOrchestrator();
            Set(app,"oscClient",client); Set(app,"meterReader",r); Set(app,"delayController",d); app.SetMixerIp("127.0.0.2");
            Record("A18-ip-change-keeps-state",c.FaderInitialized && c.SnapshotHistory().Count==1 && d.DetectedEffects.Count==1,
                $"after endpoint change: faderInitialized={c.FaderInitialized}, history={c.SnapshotHistory().Count}, effectsCached={d.DetectedEffects.Count}");
        });
        Run("A19-simulator-gain", () => {
            var s=new M32Simulator(0,new()); var st=typeof(M32Simulator).GetNestedType("Subscription",BindingFlags.NonPublic)!;
            var sub=Activator.CreateInstance(st)!; st.GetProperty("Param1")!.SetValue(sub,0);
            Set(s,"startTime",DateTime.UtcNow.AddSeconds(-3));
            var a=(float[])Call(s,"GenerateChannelStripFloats",sub)!;
            Call(s,"HandleFaderMessage",Msg("/headamp/000/gain",",f",FaderMath.Normalize(-12,-12,60)));
            var b=(float[])Call(s,"GenerateChannelStripFloats",sub)!;
            double pre=FaderMath.MeterToDb(b[0])-FaderMath.MeterToDb(a[0]); double post=FaderMath.MeterToDb(b[3])-FaderMath.MeterToDb(a[3]);
            Record("A19-simulator-gain",pre < -11.5 && Math.Abs(post)<0.5,$"HA reduced 12 dB: post-trim change={pre:F2} dB, post-fader change={post:F2} dB");
        });
        Run("A20-mute-never-retried", () => {
            using var io=new Loopback(); var c=Channel(); c.HeadAmpResolved=true; var r=Reader(io.Client,c);
            Call(r,"RequeryUninitializedFaders"); Call(r,"RequeryUnresolvedHeadAmps"); var sent=io.Drain();
            Record("A20-mute-never-retried",!c.IsMuted && sent.Count==0,$"lost initial mute reply, fader and headamp known: default muted={c.IsMuted}; retry queries={sent.Count}");
        });
        Run("A21-ipv6", () => {
            var c=new OscClient("::1",10023,0); string error="none";
            try { c.Connect(); c.Send(Msg("/audit/ipv6")); } catch(Exception ex){error=ex.GetType().Name+": "+ex.Message;} finally {c.Disconnect();}
            Record("A21-ipv6",SessionConfig.IsValidMixerIp("::1") && error.StartsWith("ArgumentException"),$"IPv6 validator accepts ::1; Send result: {error}");
        });
        Run("A22-routing-cache", () => {
            using var io=new Loopback(); var c=Channel(); c.HeadAmpIndex=0; c.HeadAmpResolved=true; var r=Reader(io.Client,c); Set(r,"isRunning",true);
            Call(r,"OnMessageReceived",Msg("/ch/01/config/source",",i",33)); Call(r,"RequeryUnresolvedHeadAmps");
            Record("A22-routing-cache",c.HeadAmpIndex==0 && io.Drain().Count==0,$"source-change feedback leaves resolved HA index={c.HeadAmpIndex}; no refresh query");
        });
        Run("perf-moving-average", () => {
            var client=new OscClient("127.0.0.1"); var devices=Enumerable.Range(1,32).Select(i=>(AudioDevice)new MixerChannel("C",i,-18,0)).Concat(Enumerable.Range(1,16).Select(i=>(AudioDevice)new Bus("B",i,-18))).ToList();
            foreach(var c in devices) for(int i=0;i<100;i++) c.AddSample(new MeterSample(0.1f));
            var r=new MeterReader(client,devices,2500); foreach(var c in devices) r.GetMovingAverage(c);
            long before=GC.GetAllocatedBytesForCurrentThread(); var sw=Stopwatch.StartNew();
            for(int i=0;i<1000;i++) foreach(var c in devices) r.GetMovingAverage(c);
            sw.Stop(); long bytes=GC.GetAllocatedBytesForCurrentThread()-before;
            Results.Add(new{id="perf-moving-average",calls=48000,allocatedBytes=bytes,elapsedMs=sw.Elapsed.TotalMilliseconds});
            Console.WriteLine($"perf-moving-average: 48000 calls, {bytes} bytes, {sw.Elapsed.TotalMilliseconds:F2} ms");
        });
        Directory.CreateDirectory("tests/audit-2026-10-16/evidence");
        File.WriteAllText("tests/audit-2026-10-16/evidence/probes.json",JsonSerializer.Serialize(Results,new JsonSerializerOptions{WriteIndented=true}));
    }
    static void AverageBenchmark() {
        var devices=Enumerable.Range(1,32).Select(i=>(AudioDevice)new MixerChannel("C",i,-18,0)).Concat(Enumerable.Range(1,16).Select(i=>(AudioDevice)new Bus("B",i,-18))).ToList();
        foreach(var c in devices) for(int i=0;i<100;i++) c.AddSample(new MeterSample(0.1f));
        var reader=new MeterReader(new OscClient("127.0.0.1"),devices,2500);
        foreach(var c in devices) reader.GetMovingAverage(c);
        long before=GC.GetAllocatedBytesForCurrentThread(); var watch=Stopwatch.StartNew();
        for(int i=0;i<1000;i++) foreach(var c in devices) reader.GetMovingAverage(c);
        watch.Stop(); long bytes=GC.GetAllocatedBytesForCurrentThread()-before;
        string json=JsonSerializer.Serialize(new { devices=48,samplesPerDevice=100,calls=48000,allocatedBytes=bytes,elapsedMs=watch.Elapsed.TotalMilliseconds },new JsonSerializerOptions{WriteIndented=true});
        Console.WriteLine(json);
        Directory.CreateDirectory("output");
        File.WriteAllText("output/average-benchmark-after-2026-10-16.json",json);
    }
    static void LoadProfile() {
        var log=new ShowLogger(); var sim=new M32Simulator(0,log); sim.Start();
        var socket=(UdpClient)Get(sim,"listener")!;
        var client=new OscClient("127.0.0.1",((IPEndPoint)socket.Client.LocalEndPoint!).Port,0);
        var channels=Enumerable.Range(1,32).Select(i=>new MixerChannel("C"+i,i,-18,0)).ToList();
        var devices=channels.Cast<AudioDevice>().Concat(Enumerable.Range(1,16).Select(i=>(AudioDevice)new Bus("B"+i,i,-18))).ToList();
        var reader=new MeterReader(client,devices,2500); var delay=new DelayController(client,log);
        var leveler=new AutoLeveler(client,reader,log,devices,3,0.5,0.5,-40,8,100);
        var anti=new AntiClipper(client,reader,log,channels,-1,3,6,6,2,2000);
        var bpm=new BpmDetector(reader,delay,log,channels[0],BpmDetectionMode.ManualTap,-20);
        long packets=0; client.MessageReceived+=_=>Interlocked.Increment(ref packets);
        try {
            client.Connect(); client.StartKeepAlive();
            for(int i=0;i<5;i++){client.Send(Msg("/audit/warmup"));Thread.Sleep(50);}
            reader.StartReading(); delay.Start(); Thread.Sleep(500); reader.ResubscribeAll(); Thread.Sleep(1000);
            leveler.Start(); anti.Monitor(); bpm.Start();
            var process=Process.GetCurrentProcess(); var cpu=process.TotalProcessorTime; long alloc=GC.GetTotalAllocatedBytes(true); long initialPackets=Interlocked.Read(ref packets);
            int[] gcBefore={GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2)}; var sw=Stopwatch.StartNew();
            Thread.Sleep(12000); sw.Stop();
            var result=new {
                scenario="32 channels + 16 buses; simulator + OSC + all workers; memory logger; no dashboard",
                elapsedSeconds=sw.Elapsed.TotalSeconds,
                cpuMilliseconds=(process.TotalProcessorTime-cpu).TotalMilliseconds,
                allocatedBytes=GC.GetTotalAllocatedBytes(true)-alloc,
                receivedPackets=Interlocked.Read(ref packets)-initialPackets,
                initializedFaders=devices.Count(c=>c.FaderInitialized),
                headAmpsResolved=channels.Count(c=>c.HeadAmpResolved),
                deviceHistories=devices.Count(c=>c.GetLatestSample()!=null),
                gcCollections=new[]{GC.CollectionCount(0)-gcBefore[0],GC.CollectionCount(1)-gcBefore[1],GC.CollectionCount(2)-gcBefore[2]}
            };
            string json=JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}); Console.WriteLine(json);
            File.WriteAllText("tests/audit-2026-10-16/evidence/load-profile.json",json);
        } finally {bpm.Stop();anti.Stop();leveler.Stop();delay.Stop();reader.StopReading();client.Disconnect();sim.Stop();}
    }
}
