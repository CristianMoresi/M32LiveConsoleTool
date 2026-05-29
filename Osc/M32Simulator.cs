using System.Net;
using System.Net.Sockets;
using M32LiveConsoleTool.Infrastructure;

namespace M32LiveConsoleTool.Osc;

/// <summary>
/// Local M32 simulator. Listens on the console's usual OSC port (10023 by
/// default) and replies with synthetic meters so the app can be developed and
/// demonstrated without a physical console.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Usage</strong>: the simulator starts when the user picks simulation
/// mode at the initial menu. The <see cref="Ui.AppOrchestrator"/> then points the
/// <see cref="OscClient"/> at <see cref="LOOPBACK_IP"/> (127.0.0.1) instead of the
/// real console IP. Every message the app sends is received by this simulator,
/// which processes it and replies as the real console would.
/// </para>
/// <para>
/// <strong>Behavior</strong>:
/// </para>
/// <list type="bullet">
///   <item>Accepts <c>/batchsubscribe</c> and <c>/unsubscribe</c> and keeps a list of active aliases.</item>
///   <item>Accepts <c>/renew</c> (a no-op here, since the simulator's subscriptions never expire) and <c>/xremote</c>.</item>
///   <item>Stores fader writes, echoes them back and answers fader queries, so the app's manual-change detection and fader seeding work against it.</item>
///   <item>Every 50 ms (the M32's maximum rate) it generates a synthetic meter blob for each active subscription. The post-fader value is shifted by the device's current fader, so the AutoLeveler's corrections visibly move the level.</item>
///   <item>The synthetic signal is a slow sine in the -25 to -5 dBFS range.</item>
/// </list>
/// <para>
/// <strong>Limitations</strong>: the simulator models the fader's effect on the
/// post-fader meter, the channel head amp gain on the post-trim meter, and a periodic
/// clip excursion so the <see cref="Modules.AntiClipper"/> can be exercised. It does
/// not model the gate or compressor. Its purpose is to test OSC communication, the
/// control loops and the UI — not the full audio processing chain.
/// </para>
/// </remarks>
public class M32Simulator
{
    // ─────────────────────────────────────────────────────────────────────
    // Public constants
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Loopback IP used to point the client at the local simulator.</summary>
    public const string LOOPBACK_IP = "127.0.0.1";

    // ─────────────────────────────────────────────────────────────────────
    // Private constants
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Meter send period in ms. The real M32 allows down to 50 ms.</summary>
    private const int METER_PERIOD_MS = 50;

    /// <summary>Peak level of the simulated signal (top of the sine).</summary>
    private const double SIM_PEAK_DB = -5.0;

    /// <summary>Valley level of the simulated signal (bottom of the sine).</summary>
    private const double SIM_VALLEY_DB = -25.0;

    /// <summary>Period of the simulation sine in seconds.</summary>
    private const double SIM_PERIOD_SECONDS = 8.0;

    /// <summary>Period of the periodic clip excursion on the post-trim meter, in seconds.</summary>
    private const double SIM_CLIP_PERIOD_SECONDS = 20.0;

    /// <summary>How long each clip excursion lasts, in seconds.</summary>
    private const double SIM_CLIP_BURST_SECONDS = 1.5;

    /// <summary>
    /// Extra dB added to the post-trim during a clip excursion, enough to push it well
    /// over a typical clip threshold so the AntiClipper engages and its head amp
    /// reductions visibly bring it back down.
    /// </summary>
    private const double SIM_CLIP_BURST_DB = 10.0;

    /// <summary>
    /// Reference fader position the simulated signal is defined at. Moving a
    /// fader away from this shifts that device's post-fader meter, so the
    /// AutoLeveler's corrections actually change the level it reads back.
    /// </summary>
    private const float SIM_REFERENCE_FADER = 0.75f;

    // ─────────────────────────────────────────────────────────────────────
    // Private fields
    // ─────────────────────────────────────────────────────────────────────

    private readonly int listenPort;
    private readonly ShowLogger logger;

    private UdpClient? listener;
    private Thread? receiveThread;
    private Thread? meterThread;
    private volatile bool isRunning;

    /// <summary>Active subscriptions: alias → subscription data.</summary>
    private readonly Dictionary<string, Subscription> subscriptions;
    private readonly object subscriptionsLock = new object();

    /// <summary>Last fader position written or queried, keyed by OSC fader address.</summary>
    private readonly Dictionary<string, float> faderByAddress = new Dictionary<string, float>();
    private readonly object faderLock = new object();

    /// <summary>Remote endpoint of the last received message, used to send replies back.</summary>
    private IPEndPoint? lastSenderEndpoint;

    /// <summary>Simulator start time, the base for computing the sine phase.</summary>
    private DateTime startTime;

    // ─────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a simulator that will listen on the given port once
    /// <see cref="Start"/> is called. Takes the logger to record simulator
    /// events (subscriptions, errors) with the same structure as the rest of the
    /// modules.
    /// </summary>
    public M32Simulator(int listenPort, ShowLogger logger)
    {
        this.listenPort = listenPort;
        this.logger = logger;
        this.listener = null;
        this.receiveThread = null;
        this.meterThread = null;
        this.isRunning = false;
        this.subscriptions = new Dictionary<string, Subscription>();
        this.lastSenderEndpoint = null;
        this.startTime = DateTime.UtcNow;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the simulator: opens the UDP socket on the configured port and
    /// starts the message-receive thread and the periodic meter-send thread.
    /// </summary>
    public void Start()
    {
        if (isRunning)
        {
            return;
        }

        try
        {
            listener = new UdpClient(listenPort);
        }
        catch (SocketException)
        {
            // Port already in use (another instance, or a real console tool bound to
            // it). Do not crash startup: log it and stay stopped. The app then simply
            // receives no meters and shows NO SIGNAL, exactly as with an unreachable
            // real console.
            listener = null;
            logger.LogEvent(
                $"Simulator: could not open port {listenPort} (already in use?)",
                Severity.Error);
            return;
        }

        startTime = DateTime.UtcNow;
        isRunning = true;

        receiveThread = new Thread(ReceiveLoop);
        receiveThread.IsBackground = true;
        receiveThread.Name = "M32Simulator-Receive";
        receiveThread.Start();

        meterThread = new Thread(MeterLoop);
        meterThread.IsBackground = true;
        meterThread.Name = "M32Simulator-Meters";
        meterThread.Start();

        logger.LogEvent($"Simulator: listening on port {listenPort}", Severity.Info);
    }

    /// <summary>
    /// Stops the simulator: clears all subscriptions, closes the UDP socket and
    /// waits for the threads to finish.
    /// </summary>
    public void Stop()
    {
        if (!isRunning)
        {
            return;
        }

        isRunning = false;

        int subCount;
        lock (subscriptionsLock)
        {
            subCount = subscriptions.Count;
            subscriptions.Clear();
        }
        logger.LogEvent($"Simulator: global unsubscribe ({subCount} aliases)", Severity.Info);

        try
        {
            listener?.Close();
        }
        catch
        {
        }

        if (receiveThread != null && receiveThread.IsAlive)
        {
            receiveThread.Join(500);
        }
        if (meterThread != null && meterThread.IsAlive)
        {
            meterThread.Join(500);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Receive thread: parses incoming messages
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// UDP receive loop. Blocks until a datagram arrives, parses it as an OSC
    /// message and dispatches it to the right handler by address. If
    /// <see cref="Stop"/> closes the socket during a wait, the resulting
    /// exception is caught and the loop ends cleanly.
    /// </summary>
    private void ReceiveLoop()
    {
        IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (isRunning)
        {
            try
            {
                if (listener == null)
                {
                    break;
                }

                byte[] data = listener.Receive(ref remote);
                lastSenderEndpoint = new IPEndPoint(remote.Address, remote.Port);

                OscMessage message = OscMessage.Decode(data);
                DispatchMessage(message);
            }
            catch
            {
                // Socket exceptions during Stop: exit cleanly.
                if (!isRunning)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Dispatches an incoming OSC message to the right handler by address. Only
    /// the addresses the real console supports for subscription management are
    /// processed; others are handled silently so they do not spam the log.
    /// </summary>
    private void DispatchMessage(OscMessage message)
    {
        switch (message.Address)
        {
            case "/batchsubscribe":
                HandleBatchSubscribe(message);
                break;
            case "/unsubscribe":
                HandleUnsubscribe(message);
                break;
            case "/renew":
                // The simulator's subscriptions never expire, so /renew is a
                // no-op. The real console would renew them.
                break;
            case "/xremote":
                // On a real console this starts forwarding parameter changes to
                // the client. The simulator already echoes fader writes to the
                // last sender, so here it is just an acknowledgement.
                break;
            default:
                string addr = message.Address;
                if (addr.EndsWith("/mix/fader", StringComparison.Ordinal)
                    || (addr.StartsWith("/headamp/", StringComparison.Ordinal)
                        && addr.EndsWith("/gain", StringComparison.Ordinal)))
                {
                    HandleFaderMessage(message);
                }
                else if (addr.StartsWith("/-ha/", StringComparison.Ordinal)
                         && addr.EndsWith("/index", StringComparison.Ordinal))
                {
                    HandleHeadAmpIndexQuery(message);
                }
                else if (addr.StartsWith("/fx/", StringComparison.Ordinal)
                         && addr.EndsWith("/type", StringComparison.Ordinal))
                {
                    HandleFxTypeQuery(message);
                }
                else if (addr.EndsWith("/mix/on", StringComparison.Ordinal))
                {
                    HandleMuteQuery(message);
                }
                // Other control messages (fx params, etc.) are accepted silently:
                // the simulator does not model them.
                break;
        }
    }

    /// <summary>
    /// Registers a new subscription with the alias, path and parameters sent by
    /// the client. Replaces the alias if it already existed.
    /// </summary>
    private void HandleBatchSubscribe(OscMessage message)
    {
        if (message.Arguments.Count < 5)
        {
            return;
        }
        if (message.Arguments[0] is not string alias) return;
        if (message.Arguments[1] is not string sourcePath) return;
        if (message.Arguments[2] is not int param1) return;
        if (message.Arguments[3] is not int param2) return;

        Subscription sub = new Subscription
        {
            Alias = alias,
            SourcePath = sourcePath,
            Param1 = param1,
            Param2 = param2
        };

        lock (subscriptionsLock)
        {
            subscriptions[alias] = sub;
        }

        logger.LogEvent($"Simulator: subscribe '{alias}' to {sourcePath} ({param1}, {param2})", Severity.Info);
    }

    /// <summary>
    /// Removes a subscription. With no arguments it drops ALL subscriptions (the
    /// global form, equivalent to closing the connection). With a specific alias
    /// it drops only that one.
    /// </summary>
    private void HandleUnsubscribe(OscMessage message)
    {
        if (message.Arguments.Count == 0)
        {
            int count;
            lock (subscriptionsLock)
            {
                count = subscriptions.Count;
                subscriptions.Clear();
            }
            logger.LogEvent($"Simulator: global unsubscribe ({count} aliases)", Severity.Info);
            return;
        }

        if (message.Arguments[0] is string alias)
        {
            lock (subscriptionsLock)
            {
                if (subscriptions.Remove(alias))
                {
                    logger.LogEvent($"Simulator: unsubscribe '{alias}'", Severity.Info);
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Fader handling: store writes, echo them, and answer queries
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Handles a float parameter address (a fader or a head amp gain). With a
    /// float argument it is a write: the value is stored and echoed back (as the
    /// real console does under /xremote). With no argument it is a query: the
    /// current value is sent back.
    /// </summary>
    private void HandleFaderMessage(OscMessage message)
    {
        string address = message.Address;

        if (message.Arguments.Count > 0 && message.Arguments[0] is float value)
        {
            lock (faderLock)
            {
                faderByAddress[address] = value;
            }
            SendFaderReply(address, value);
            return;
        }

        SendFaderReply(address, GetFader(address));
    }

    /// <summary>
    /// Returns the stored fader position for an address, or
    /// <see cref="SIM_REFERENCE_FADER"/> if it has never been written.
    /// </summary>
    private float GetFader(string address)
    {
        lock (faderLock)
        {
            if (faderByAddress.TryGetValue(address, out float value))
            {
                return value;
            }
            return DefaultNormalizedFor(address);
        }
    }

    /// <summary>
    /// Default normalized value for an address that has never been written. Faders
    /// default to <see cref="SIM_REFERENCE_FADER"/> (unity), but a head amp gain
    /// defaults to the normalized value of 0 dB so a freshly queried channel shows a
    /// realistic preamp instead of the +42 dB that 0.75 would map to over the
    /// -12..+60 dB head amp range.
    /// </summary>
    private static float DefaultNormalizedFor(string address)
    {
        if (address.StartsWith("/headamp/", StringComparison.Ordinal)
            && address.EndsWith("/gain", StringComparison.Ordinal))
        {
            return FaderMath.Normalize(0.0, M32Buckets.HeadAmpGainMinDb, M32Buckets.HeadAmpGainMaxDb);
        }
        return SIM_REFERENCE_FADER;
    }

    /// <summary>Sends a fader value back to the last sender for the given address.</summary>
    private void SendFaderReply(string address, float value)
    {
        if (lastSenderEndpoint == null)
        {
            return;
        }

        OscMessage reply = new OscMessage(address, ",f", new List<object> { value });
        byte[] datagram = reply.Encode();

        try
        {
            listener?.Send(datagram, datagram.Length, lastSenderEndpoint);
        }
        catch
        {
            // Socket closing during shutdown; ignore.
        }
    }

    /// <summary>
    /// Fader gain relative to <see cref="SIM_REFERENCE_FADER"/>, in dB. Used to
    /// shift a device's post-fader meter so the AutoLeveler's writes are
    /// reflected in the level it reads back.
    /// </summary>
    private double FaderGainDb(float fader)
    {
        return FaderMath.FaderToDb(fader) - FaderMath.FaderToDb(SIM_REFERENCE_FADER);
    }

    /// <summary>
    /// Answers a <c>/-ha/NN/index</c> query: reports that input NN is fed by head
    /// amp NN (an all-local routing), so the client has a valid head amp index to
    /// target. A real console would return the actual routing, including AES50.
    /// </summary>
    private void HandleHeadAmpIndexQuery(OscMessage message)
    {
        if (lastSenderEndpoint == null)
        {
            return;
        }

        int inputIndex = ParseAddressNumber(message.Address);
        if (inputIndex < 0)
        {
            return;
        }

        OscMessage reply = new OscMessage(message.Address, ",i", new List<object> { inputIndex });
        byte[] datagram = reply.Encode();

        try
        {
            listener?.Send(datagram, datagram.Length, lastSenderEndpoint);
        }
        catch
        {
            // Socket closing during shutdown; ignore.
        }
    }

    /// <summary>
    /// Answers a <c>/ch|bus/XX/mix/on</c> query. The simulator never mutes anything,
    /// so it reports 1 (on/audible) unless a value was explicitly written, which it
    /// then echoes back.
    /// </summary>
    private void HandleMuteQuery(OscMessage message)
    {
        if (lastSenderEndpoint == null)
        {
            return;
        }

        int on = 1;
        if (message.Arguments.Count > 0 && message.Arguments[0] is int written)
        {
            on = written;
        }

        OscMessage reply = new OscMessage(message.Address, ",i", new List<object> { on });
        byte[] datagram = reply.Encode();

        try
        {
            listener?.Send(datagram, datagram.Length, lastSenderEndpoint);
        }
        catch
        {
            // Socket closing during shutdown; ignore.
        }
    }

    /// <summary>Extracts the numeric segment from an address like "/-ha/05/index" or "/fx/3/type".</summary>
    private static int ParseAddressNumber(string address)
    {
        string[] parts = address.Split('/');
        if (parts.Length >= 3 && int.TryParse(parts[2], out int number))
        {
            return number;
        }
        return -1;
    }

    /// <summary>
    /// Answers a <c>/fx/N/type</c> query with a representative effect layout so the
    /// client's effect detection is observable without hardware: delays in slots
    /// 1-3, a reverb in slot 4 (to exercise the "no delay" warning) and inserts in
    /// 5-8.
    /// </summary>
    private void HandleFxTypeQuery(OscMessage message)
    {
        if (lastSenderEndpoint == null)
        {
            return;
        }

        int slot = ParseAddressNumber(message.Address);
        if (slot < 1 || slot > 8)
        {
            return;
        }

        int typeIndex = slot switch
        {
            1 => 10,  // DLY  (Stereo Delay)
            2 => 11,  // 3TAP (3-Tap Delay)
            3 => 26,  // MODD (Modulation Delay)
            4 => 0,   // HALL (a reverb: no delay)
            _ => 1    // a graphic EQ insert on slots 5-8
        };

        OscMessage reply = new OscMessage(message.Address, ",i", new List<object> { typeIndex });
        byte[] datagram = reply.Encode();

        try
        {
            listener?.Send(datagram, datagram.Length, lastSenderEndpoint);
        }
        catch
        {
            // Socket closing during shutdown; ignore.
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Meter-send thread: emits periodic blobs
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Meter-send loop. Every <see cref="METER_PERIOD_MS"/> ms, for each active
    /// subscription, it generates a synthetic meter blob and sends it to the
    /// client. The blob uses the M32's native format: a 4-byte header with the
    /// float count followed by the floats in little-endian.
    /// </summary>
    private void MeterLoop()
    {
        while (isRunning)
        {
            try
            {
                List<Subscription> snapshot;
                lock (subscriptionsLock)
                {
                    snapshot = new List<Subscription>(subscriptions.Values);
                }

                foreach (Subscription sub in snapshot)
                {
                    if (lastSenderEndpoint == null)
                    {
                        continue;
                    }

                    float[] meters = GenerateMetersForSubscription(sub);
                    byte[] blob = EncodeMeterBlob(meters);

                    OscMessage reply = new OscMessage(
                        sub.Alias,
                        ",b",
                        new List<object> { blob });
                    byte[] datagram = reply.Encode();

                    listener?.Send(datagram, datagram.Length, lastSenderEndpoint);
                }
            }
            catch
            {
                if (!isRunning)
                {
                    break;
                }
            }

            Thread.Sleep(METER_PERIOD_MS);
        }
    }

    /// <summary>
    /// Generates the meter floats for a subscription based on its source path.
    /// Channel meters have a different structure from bus meters (float count and
    /// field positions), hence two separate generators.
    /// </summary>
    private float[] GenerateMetersForSubscription(Subscription sub)
    {
        if (sub.SourcePath == M32Buckets.ChannelStrip)
        {
            return GenerateChannelStripFloats(sub);
        }
        if (sub.SourcePath == M32Buckets.MixBusPage)
        {
            return GenerateMixBusFloats();
        }
        return Array.Empty<float>();
    }

    /// <summary>
    /// Generates the channel-strip float array (post-trim, gate reduction, comp
    /// reduction, post-fader). Drives the positions the MeterReader reads as level
    /// with the simulated signal.
    /// </summary>
    private float[] GenerateChannelStripFloats(Subscription sub)
    {
        int size = (int)ChannelStripFloat.PostFader + 1;
        float[] result = new float[size];

        double inputDb = CurrentSimulatedDb();
        int userChannel = sub.Param1 + 1;

        // Post-trim is pre-fader. It carries a periodic clip excursion and reflects
        // this channel's head amp gain, so the AntiClipper engages on the clip and
        // its head amp reductions visibly bring the post-trim back down.
        double postTrimDb = inputDb + CurrentClipExcursionDb() + HeadAmpGainDb(userChannel);

        // The post-fader value is shifted by this channel's current fader gain, so the
        // AutoLeveler sees its own corrections take effect on the meter. It is kept
        // independent of the clip excursion so the leveling demo stays clean.
        double postFaderDb = inputDb + FaderGainDb(GetFader($"/ch/{userChannel:D2}/mix/fader"));

        result[(int)ChannelStripFloat.PostTrim] = DbToLinear((float)postTrimDb);
        result[(int)ChannelStripFloat.GateGainReduction] = 0f;
        result[(int)ChannelStripFloat.CompressorGainReduction] = 0f;
        result[(int)ChannelStripFloat.PostFader] = DbToLinear((float)postFaderDb);

        return result;
    }

    /// <summary>
    /// Generates the bus-page float array (16 floats, one per bus). Each bus's
    /// post-fader value is shifted by its own fader gain.
    /// </summary>
    private float[] GenerateMixBusFloats()
    {
        int size = M32Buckets.MixBusPageFloatCount;
        float[] result = new float[size];

        double inputDb = CurrentSimulatedDb();

        for (int i = (int)MixBusFloat.Bus1; i <= (int)MixBusFloat.Bus16; i++)
        {
            if (i >= size)
            {
                break;
            }
            int userBus = i - (int)MixBusFloat.Bus1 + 1;
            double postFaderDb = inputDb + FaderGainDb(GetFader($"/bus/{userBus:D2}/mix/fader"));
            result[i] = DbToLinear((float)postFaderDb);
        }

        return result;
    }

    /// <summary>
    /// Returns the current value of the simulation sine in decibels. Oscillates
    /// between <see cref="SIM_VALLEY_DB"/> and <see cref="SIM_PEAK_DB"/> with a
    /// period of <see cref="SIM_PERIOD_SECONDS"/> seconds.
    /// </summary>
    private double CurrentSimulatedDb()
    {
        double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
        double phase = (elapsed % SIM_PERIOD_SECONDS) / SIM_PERIOD_SECONDS;
        double angle = phase * 2.0 * Math.PI;

        double center = (SIM_PEAK_DB + SIM_VALLEY_DB) / 2.0;
        double halfRange = (SIM_PEAK_DB - SIM_VALLEY_DB) / 2.0;

        return center + halfRange * Math.Sin(angle);
    }

    /// <summary>
    /// Extra dB applied to the post-trim during a periodic clip excursion (a short
    /// burst every <see cref="SIM_CLIP_PERIOD_SECONDS"/>), so the AntiClipper has
    /// something to react to. 0 outside the burst window.
    /// </summary>
    private double CurrentClipExcursionDb()
    {
        double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
        double phase = elapsed % SIM_CLIP_PERIOD_SECONDS;
        return phase < SIM_CLIP_BURST_SECONDS ? SIM_CLIP_BURST_DB : 0.0;
    }

    /// <summary>
    /// Current head amp gain (dB) for a channel, read back from what the client has
    /// written to <c>/headamp/NNN/gain</c> (the head amp index equals the input index,
    /// channel-1). Defaults to 0 dB. Lets the AntiClipper's reductions take effect on
    /// the simulated post-trim meter.
    /// </summary>
    private double HeadAmpGainDb(int userChannel)
    {
        string address = M32Buckets.BuildHeadAmpGainAddress(userChannel - 1);
        float normalized;
        lock (faderLock)
        {
            normalized = faderByAddress.TryGetValue(address, out float value)
                ? value
                : DefaultNormalizedFor(address);
        }
        return FaderMath.Denormalize(
            normalized, M32Buckets.HeadAmpGainMinDb, M32Buckets.HeadAmpGainMaxDb);
    }

    /// <summary>
    /// Converts decibels to a linear float magnitude (roughly 0-1), as the client
    /// expects when decoding the meter blob.
    /// </summary>
    private float DbToLinear(float db)
    {
        return (float)Math.Pow(10.0, db / 20.0);
    }

    /// <summary>
    /// Encodes a float array into the M32's meter blob format: 4 bytes with the
    /// float count in little-endian, followed by each float in little-endian.
    /// </summary>
    private byte[] EncodeMeterBlob(float[] meters)
    {
        byte[] result = new byte[4 + meters.Length * 4];

        byte[] count = BitConverter.GetBytes(meters.Length);
        Buffer.BlockCopy(count, 0, result, 0, 4);

        for (int i = 0; i < meters.Length; i++)
        {
            byte[] f = BitConverter.GetBytes(meters[i]);
            Buffer.BlockCopy(f, 0, result, 4 + i * 4, 4);
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helper type
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Data of an active subscription in the simulator. Equivalent to what the
    /// real console stores internally when it receives a <c>/batchsubscribe</c>.
    /// </summary>
    private class Subscription
    {
        public string Alias { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public int Param1 { get; set; }
        public int Param2 { get; set; }
    }
}
