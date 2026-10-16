using System.Collections.Concurrent;
using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Osc;

namespace M32LiveConsoleTool.Modules;

/// <summary>
/// Reads the M32's meters. Subscribes to the periodic meter stream the console
/// emits, decodes the binary blob that arrives over OSC, and routes each value
/// to the matching device's history.
/// </summary>
/// <remarks>
/// <para>
/// The exact structure of the meter bundles comes from the official manual and
/// lives in <see cref="M32Buckets"/>, <see cref="MixBusFloat"/> and
/// <see cref="ChannelStripFloat"/>. This class only applies that information to
/// build subscriptions, receive the blobs and slice out the floats that belong
/// to each device.
/// </para>
/// <para>
/// <strong>Granular subscriptions</strong>: the module supports adding and
/// removing watched devices during the show via <see cref="AddDevice"/>,
/// <see cref="RemoveDevice"/> and <see cref="ReplaceDevices"/>. Subscription
/// management is granular: adding a channel sends only its <c>/batchsubscribe</c>;
/// removing it sends <c>/unsubscribe ,s &lt;alias&gt;</c>, which drops only that
/// subscription without touching the others. This avoids overloading the console
/// with needless mass re-subscriptions and lets it stop generating blobs nobody
/// will consume.
/// </para>
/// <para>
/// All buses share a single subscription to the bus page. That subscription is
/// created when there is at least one bus in the list and is dropped explicitly
/// when the last one is removed, so the console stops publishing the bus blob
/// once it is no longer needed.
/// </para>
/// <para>
/// <strong>Concurrency</strong>: the internal device list follows a copy-on-write
/// pattern. The OSC receive thread and the renew thread always read an atomic
/// snapshot of the list; modifications (rare, on the order of one per minute)
/// build a new list and assign it to the shared reference, with no locks on the
/// critical path. Each device's sample history is accessed via
/// <see cref="AudioDevice.SnapshotHistory"/>, which returns a copy under a lock to
/// avoid conflicts with the OSC receive thread appending samples.
/// </para>
/// </remarks>
public class MeterReader
{
    // ─────────────────────────────────────────────────────────────────────
    // Constants
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>/batchsubscribe</c> time factor: 1 = one update every 50 ms (the M32's
    /// maximum rate).
    /// </summary>
    private const int FULL_SPEED_TIME_FACTOR = 1;

    /// <summary>
    /// Interval between <c>/renew</c> sends, in milliseconds. Subscriptions expire
    /// after 10 s; we renew every 8 s with margin.
    /// </summary>
    private const int RENEW_INTERVAL_MS = 1000;

    /// <summary>Sleep granularity inside the renew loop.</summary>
    private const int RENEW_SLEEP_TICK_MS = 100;

    /// <summary>If no meter blob arrives within this many ms, the console link is reported as lost.</summary>
    private const int METER_SIGNAL_TIMEOUT_MS = 2000;

    // ─────────────────────────────────────────────────────────────────────
    // Private fields
    // ─────────────────────────────────────────────────────────────────────

    private OscClient oscClient;

    /// <summary>
    /// Watched devices. Marked <c>volatile</c> so the copy-on-write assignments
    /// are visible across threads without a lock.
    /// </summary>
    private volatile List<AudioDevice> devices;

    private int windowMs;
    private volatile bool isRunning;

    /// <summary>Map OSC alias → channel. A <see cref="ConcurrentDictionary{TKey,TValue}"/> for safe cross-thread access.</summary>
    private ConcurrentDictionary<string, MixerChannel> channelByAlias;

    /// <summary>Alias of the bus-page subscription, empty if no bus is watched.</summary>
    private volatile string busesAlias;

    private Thread? renewThread;

    /// <summary>Lock serializing modifications to the device list and the matching OSC subscription sync.</summary>
    private object devicesLock => oscClient.ControlLock;
    private readonly Dictionary<int, GainState> headAmps = new();
    private long lastVerification;
    public OscClient Client => oscClient;

    /// <summary>
    /// Tick (<see cref="Environment.TickCount64"/>) of the last meter blob
    /// received, used by the dashboard's live/no-signal indicator. 0 until the
    /// first blob arrives.
    /// </summary>
    private long lastMeterTickMs;

    // ─────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a meter reader ready to watch an initial list of devices. The list
    /// can be changed later via <see cref="AddDevice"/>, <see cref="RemoveDevice"/>
    /// or <see cref="ReplaceDevices"/>.
    /// </summary>
    public MeterReader(OscClient oscClient, List<AudioDevice> devices, int windowMs)
    {
        if (windowMs < 100 || windowMs > 10000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowMs), "Meter window must be between 100 and 10000 ms.");
        }

        this.oscClient = oscClient;
        this.devices = new List<AudioDevice>(devices);
        this.windowMs = windowMs;
        this.isRunning = false;
        this.channelByAlias = new ConcurrentDictionary<string, MixerChannel>();
        this.busesAlias = "";
        this.renewThread = null;
        foreach (var device in devices) device.Clock = oscClient.Clock;
        oscClient.SessionInvalidated += ResetObservedState;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public properties
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Read-only view of the current device list. To modify it, use
    /// <see cref="AddDevice"/>, <see cref="RemoveDevice"/> or
    /// <see cref="ReplaceDevices"/>.
    /// </summary>
    public IReadOnlyList<AudioDevice> Devices
    {
        get { return devices.AsReadOnly(); }
    }

    /// <summary>
    /// Moving-average window in milliseconds. The setter validates the same
    /// 100-10000 ms range <see cref="Infrastructure.SessionConfig.Sanitize"/>
    /// enforces when loading from disk.
    /// </summary>
    public int WindowMs
    {
        get { return windowMs; }
        set
        {
            if (value < 100 || value > 10000)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), "Meter window must be between 100 and 10000 ms.");
            }
            windowMs = value;
        }
    }

    /// <summary>
    /// <c>true</c> if a meter blob has arrived from the console within the last
    /// <see cref="METER_SIGNAL_TIMEOUT_MS"/> ms. The dashboard uses this to show
    /// whether the console link is alive.
    /// </summary>
    public bool IsReceivingMeters
    {
        get
        {
            long last = Interlocked.Read(ref lastMeterTickMs);
            return last != 0 && (Environment.TickCount64 - last) < METER_SIGNAL_TIMEOUT_MS;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public methods: lifecycle
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Subscribes to every current device, registers the receive handler, queries
    /// the initial fader positions and starts the renew thread.
    /// </summary>
    public void StartReading()
    {
        if (isRunning)
        {
            return;
        }

        oscClient.MessageReceived += OnMessageReceived;
        isRunning = true;

        lock (devicesLock)
        {
            BuildAllSubscriptions();
            QueryAllInitialState();
        }

        renewThread = new Thread(RenewLoop);
        renewThread.IsBackground = true;
        renewThread.Name = "MeterReader-Renew";
        renewThread.Start();
    }

    /// <summary>
    /// Drops ALL subscriptions on the console, stops processing messages and stops
    /// the renew thread. Uses <c>/unsubscribe</c> with no arguments: a full, not
    /// granular, teardown.
    /// </summary>
    public void StopReading()
    {
        if (!isRunning)
        {
            return;
        }

        isRunning = false;
        oscClient.MessageReceived -= OnMessageReceived;
        SendUnsubscribeAll();

        if (renewThread != null && renewThread.IsAlive)
        {
            renewThread.Join(1000);
            renewThread = null;
        }

        channelByAlias.Clear();
        busesAlias = "";
    }

    /// <summary>
    /// Re-sends every subscription and initial-state query from scratch. Used after
    /// the mixer IP changes at runtime: the new console's subscription table is empty
    /// and <c>/renew</c> only refreshes existing entries, so the subscriptions must
    /// be rebuilt for meters to start flowing from the new IP. No-op if not running.
    /// </summary>
    public void ResubscribeAll()
    {
        lock (devicesLock)
        {
            oscClient.InvalidateSession();
            if (!isRunning) return;
            BuildAllSubscriptions();
            QueryAllInitialState();
        }
    }

    private void ResetObservedState()
    {
        lock (devicesLock)
        {
            headAmps.Clear();
            Interlocked.Exchange(ref lastMeterTickMs, 0);
            foreach (var device in devices) device.ResetObservedState();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public methods: granular list modification
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a device to the watched list. If the module is running, sends the
    /// minimal OSC subscriptions needed to start receiving its meters without
    /// touching the others.
    /// </summary>
    public void AddDevice(AudioDevice device)
    {
        lock (devicesLock)
        {
            if (devices.Contains(device))
            {
                return;
            }

            bool hadAnyBusBefore = ContainsAnyBus(devices);

            device.Clock = oscClient.Clock;
            List<AudioDevice> updated = new List<AudioDevice>(devices);
            updated.Add(device);
            devices = updated;

            if (!isRunning)
            {
                return;
            }

            if (device is MixerChannel channel)
            {
                SubscribeSingleChannel(channel);
            }
            else if (device is Bus && !hadAnyBusBefore)
            {
                // First bus in the list: create the subscription to the whole bus
                // page.
                SubscribeBusPage();
            }
            // Other buses added to a list that already has buses need no OSC
            // action: the page is already subscribed and the blobs already arrive;
            // only the internal float-to-bus routing changes.

            QueryDeviceFader(device);
            QueryDeviceMute(device);
        }
    }

    /// <summary>
    /// Removes a device from the watched list. If the module is running, sends the
    /// minimal OSC unsubscribes needed to stop receiving its meters without
    /// affecting the others.
    /// </summary>
    public void RemoveDevice(AudioDevice device)
    {
        lock (devicesLock)
        {
            if (!devices.Contains(device))
            {
                return;
            }

            List<AudioDevice> updated = new List<AudioDevice>(devices);
            updated.Remove(device);
            devices = updated;

            if (!isRunning)
            {
                return;
            }

            if (device is MixerChannel channel)
            {
                UnsubscribeSingleChannel(channel);
            }
            else if (device is Bus && !ContainsAnyBus(updated))
            {
                // We just removed the last bus: drop the bus-page subscription so
                // the console stops sending a blob we no longer need.
                UnsubscribeBusPage();
            }
        }
    }

    /// <summary>
    /// Replaces the whole watched list at once. Computes the delta against the
    /// previous list and sends only the strictly necessary subscribes and
    /// unsubscribes. Useful when loading a whole new configuration without a mass
    /// re-subscription.
    /// </summary>
    public void ReplaceDevices(List<AudioDevice> newDevices)
    {
        lock (devicesLock)
        {
            List<AudioDevice> oldDevices = devices;
            List<AudioDevice> updated = new List<AudioDevice>(newDevices);
            foreach (var device in updated) device.Clock = oscClient.Clock;
            devices = updated;

            if (!isRunning)
            {
                return;
            }

            // Compute the delta to send the minimum number of OSC messages.
            HashSet<AudioDevice> oldSet = new HashSet<AudioDevice>(oldDevices);
            HashSet<AudioDevice> newSet = new HashSet<AudioDevice>(newDevices);

            // Channels no longer present: unsubscribe individually.
            foreach (AudioDevice device in oldDevices)
            {
                if (newSet.Contains(device))
                {
                    continue;
                }
                if (device is MixerChannel channel)
                {
                    UnsubscribeSingleChannel(channel);
                }
            }

            // New channels: subscribe individually. Also query the fader position
            // of any new device (channel or bus).
            foreach (AudioDevice device in newDevices)
            {
                if (oldSet.Contains(device))
                {
                    continue;
                }
                if (device is MixerChannel channel)
                {
                    SubscribeSingleChannel(channel);
                }
                QueryDeviceFader(device);
                QueryDeviceMute(device);
            }

            // Manage the bus subscription based on before/after.
            bool hadBusesBefore = ContainsAnyBus(oldDevices);
            bool hasBusesNow = ContainsAnyBus(newDevices);

            if (!hadBusesBefore && hasBusesNow)
            {
                SubscribeBusPage();
            }
            else if (hadBusesBefore && !hasBusesNow)
            {
                UnsubscribeBusPage();
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Meter queries
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the device's moving average level in decibels, considering only
    /// history samples younger than <see cref="WindowMs"/>. The average is taken in
    /// the linear power domain and converted back to dB. Iterates an atomic copy of
    /// the history returned by <see cref="AudioDevice.SnapshotHistory"/>, so there is
    /// no risk of colliding with the OSC receive thread appending samples.
    /// </summary>
    /// <returns>Average level in dB, or -90 dB if there are no valid samples.</returns>
    public double GetMovingAverage(AudioDevice device)
    {
        return device.GetAverageDb(windowMs);
    }

    /// <summary>
    /// Returns the device's most recent sample, or <c>null</c> if none has arrived
    /// yet.
    /// </summary>
    public MeterSample? GetLatestSample(AudioDevice device)
    {
        return device.GetLatestSample();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Private methods: building and maintaining subscriptions
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds ALL subscriptions from a clean state. Only called from
    /// <see cref="StartReading"/>. Granular maintenance uses the individual
    /// subscribe/unsubscribe methods.
    /// </summary>
    private void BuildAllSubscriptions()
    {
        channelByAlias.Clear();
        busesAlias = "";

        bool hasAnyBus = false;

        foreach (AudioDevice device in devices)
        {
            if (device is MixerChannel channel)
            {
                SubscribeSingleChannel(channel);
            }
            else if (device is Bus)
            {
                hasAnyBus = true;
            }
        }

        if (hasAnyBus)
        {
            SubscribeBusPage();
        }
    }

    /// <summary>
    /// Registers the alias and sends the <c>/batchsubscribe</c> for one channel.
    /// </summary>
    private void SubscribeSingleChannel(MixerChannel channel)
    {
        string alias = M32Buckets.BuildChannelAlias(channel.OscNumber);
        channelByAlias[alias] = channel;

        int oscChannelId = M32Buckets.UserChannelToOscId(channel.OscNumber);

        OscMessage subscribe = new OscMessage(
            "/batchsubscribe",
            ",ssiii",
            new List<object>
            {
                alias,
                M32Buckets.CHANNEL_STRIP,
                oscChannelId,
                0,
                FULL_SPEED_TIME_FACTOR
            });

        oscClient.Send(subscribe);

        // Ask the console which head amp feeds this channel, so the AntiClipper
        // can target the right preamp (local XLR or AES50 stage box).
        QueryChannelHeadAmp(channel);
    }

    /// <summary>
    /// Drops one channel's subscription using <c>/unsubscribe ,s &lt;alias&gt;</c>
    /// and clears its entry from the map.
    /// </summary>
    private void UnsubscribeSingleChannel(MixerChannel channel)
    {
        string alias = M32Buckets.BuildChannelAlias(channel.OscNumber);

        OscMessage unsubscribe = new OscMessage(
            "/unsubscribe",
            ",s",
            new List<object> { alias });

        oscClient.Send(unsubscribe);

        channelByAlias.TryRemove(alias, out _);
    }

    /// <summary>
    /// Subscribes to the bus page with the shared alias.
    /// </summary>
    private void SubscribeBusPage()
    {
        busesAlias = $"{M32Buckets.APP_ALIAS_PREFIX}{M32Buckets.BUSES_ALIAS_SUFFIX}";

        OscMessage subscribe = new OscMessage(
            "/batchsubscribe",
            ",ssiii",
            new List<object>
            {
                busesAlias,
                M32Buckets.MIX_BUS_PAGE,
                0,
                0,
                FULL_SPEED_TIME_FACTOR
            });

        oscClient.Send(subscribe);
    }

    /// <summary>
    /// Drops the bus-page subscription once no bus remains in the list, so the
    /// console stops sending the blob.
    /// </summary>
    private void UnsubscribeBusPage()
    {
        if (string.IsNullOrEmpty(busesAlias))
        {
            return;
        }

        OscMessage unsubscribe = new OscMessage(
            "/unsubscribe",
            ",s",
            new List<object> { busesAlias });

        oscClient.Send(unsubscribe);

        busesAlias = "";
    }

    /// <summary>
    /// <c>/unsubscribe</c> with no argument: cancels all subscriptions at once.
    /// Only used in <see cref="StopReading"/>.
    /// </summary>
    private void SendUnsubscribeAll()
    {
        OscMessage unsubscribe = new OscMessage("/unsubscribe", ",", new List<object>());
        oscClient.Send(unsubscribe);
    }

    /// <summary>
    /// Refreshes every active subscription by sending <c>/renew ,s &lt;alias&gt;</c>
    /// once per alias. Invoked periodically by the renew thread.
    /// </summary>
    /// <remarks>
    /// The protocol documents <c>/renew</c> only with an alias argument (an
    /// argument-less form exists for <c>/unsubscribe</c>, but not for renew), so
    /// each alias is renewed individually. The cost is one tiny datagram per
    /// watched channel plus one for the bus page every cycle — negligible — and it
    /// removes any dependence on undocumented firmware behavior.
    /// </remarks>
    private void SendRenewAll()
    {
        foreach (string alias in channelByAlias.Keys)
        {
            SendRenewAlias(alias);
        }

        string buses = busesAlias;
        if (!string.IsNullOrEmpty(buses))
        {
            SendRenewAlias(buses);
        }
    }

    /// <summary>Sends a single <c>/renew ,s &lt;alias&gt;</c> message.</summary>
    private void SendRenewAlias(string alias)
    {
        OscMessage renew = new OscMessage("/renew", ",s", new List<object> { alias });
        oscClient.Send(renew);
    }

    /// <summary>
    /// Returns <c>true</c> if the list contains at least one <see cref="Bus"/>.
    /// </summary>
    private bool ContainsAnyBus(List<AudioDevice> list)
    {
        foreach (AudioDevice device in list)
        {
            if (device is Bus)
            {
                return true;
            }
        }
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Renew thread
    // ─────────────────────────────────────────────────────────────────────

    private void RenewLoop()
    {
        int ticksPerInterval = RENEW_INTERVAL_MS / RENEW_SLEEP_TICK_MS;

        while (isRunning)
        {
            for (int i = 0; i < ticksPerInterval && isRunning; i++)
            {
                Thread.Sleep(RENEW_SLEEP_TICK_MS);
            }

            if (!isRunning)
            {
                break;
            }

            lock (devicesLock)
            {
                // Manual tap + FX can run with no watched audio devices.
                // Absence of subscriptions is not a connection failure.
                if (devices.Count == 0) { oscClient.EnsureConnected(); continue; }
                if (IsReceivingMeters)
                {
                    // Healthy link: just refresh the existing subscriptions cheaply.
                    SendRenewAll();
                    RequeryUninitializedFaders();
                    RequeryUnresolvedHeadAmps();
                }
                else
                {
                    // No meters arriving — the console may have rebooted (its
                    // subscription table is empty, and /renew only refreshes existing
                    // entries), just come online, or the socket may have failed to open
                    // at startup. Reopen the socket if needed and rebuild the
                    // subscriptions so streaming recovers automatically without a restart.
                    oscClient.EnsureConnected();
                    ResubscribeAll();
                }
            }
        }
    }

    /// <summary>
    /// Re-sends the fader query for any watched device that has not yet received
    /// its position from the console, in case a startup query was lost over UDP.
    /// Normally a no-op once every device is initialized.
    /// </summary>
    private void RequeryUninitializedFaders()
    {
        foreach (AudioDevice device in devices)
        {
            device.ExpireUnconfirmedFader();
            if (!device.FaderInitialized) QueryDeviceFader(device);
            if (!device.MuteInitialized) QueryDeviceMute(device);
            if (!device.HasFreshMeters && device is MixerChannel channel) SubscribeSingleChannel(channel);
            if (!device.HasFreshMeters && device is Bus) SubscribeBusPage();
        }
    }

    /// <summary>
    /// Re-sends the head amp query for any watched channel whose
    /// <c>/-ha/NN/index</c> reply has not arrived, in case the startup query was
    /// lost over UDP. Without the retry, a single lost datagram would leave the
    /// AntiClipper attenuating the channel's digital trim instead of its real
    /// analog head amp for the rest of the session. Normally a no-op once every
    /// channel is resolved (a genuine -1 "digital source" answer also counts as
    /// resolved).
    /// </summary>
    private void RequeryUnresolvedHeadAmps()
    {
        bool verify = oscClient.Clock.GetElapsedTime(lastVerification).TotalMilliseconds >= 900;
        if (verify) lastVerification = oscClient.Clock.GetTimestamp();
        foreach (var device in devices)
        {
            if (verify) { QueryDeviceFader(device); QueryDeviceMute(device); }
            if (device is not MixerChannel channel) continue;
            if (!channel.HeadAmpResolved || verify) QueryChannelHeadAmp(channel);
            if (channel.HeadAmpResolved && (!channel.GainInitialized || verify)) QueryGain(channel);
        }
    }

    private void QueryGain(MixerChannel channel)
    {
        string address = channel.HeadAmpIndex >= 0
            ? M32Buckets.BuildHeadAmpGainAddress(channel.HeadAmpIndex)
            : M32Buckets.BuildChannelTrimAddress(channel.OscNumber);
        oscClient.Send(new OscMessage(address, ",", new()));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Receive and parse
    // ─────────────────────────────────────────────────────────────────────

    internal void OnMessageReceived(OscMessage message)
    {
        lock (devicesLock) HandleMessage(message);
    }

    private void HandleMessage(OscMessage message)
    {
        if (!isRunning)
        {
            return;
        }

        // Fader position feedback from the console (/xremote echoes plus replies
        // to our initial queries). These messages do not carry the app alias
        // prefix, so they are handled before the meter-blob path below.
        if (message.Address.EndsWith("/config/source", StringComparison.Ordinal)
            || message.Address.StartsWith("/config/routing", StringComparison.Ordinal)
            || message.Address.StartsWith("/config/scene", StringComparison.Ordinal)
            || message.Address.StartsWith("/-show/", StringComparison.Ordinal))
        {
            oscClient.InvalidateSession();
            BuildAllSubscriptions();
            QueryAllInitialState();
            return;
        }

        if (TryHandleFaderFeedback(message))
        {
            return;
        }

        if (TryHandleMuteFeedback(message))
        {
            return;
        }

        if (TryHandleHeadAmpFeedback(message))
        {
            return;
        }

        if (!message.Address.StartsWith(M32Buckets.APP_ALIAS_PREFIX))
        {
            return;
        }

        if (message.Arguments.Count == 0 || message.Arguments[0] is not byte[] blob)
        {
            return;
        }

        float[] meters = ParseMeterBlob(blob);

        // Only known aliases with complete, finite payloads count as live meters.
        int required = message.Address == busesAlias ? M32Buckets.MIX_BUS_PAGE_FLOAT_COUNT : 4;
        if ((message.Address != busesAlias && !channelByAlias.ContainsKey(message.Address))
            || meters.Length < required || meters.Any(x => !float.IsFinite(x) || x < 0)) return;
        Interlocked.Exchange(ref lastMeterTickMs, Environment.TickCount64);

        if (message.Address == busesAlias)
        {
            DistributeBusMeters(meters);
        }
        else if (channelByAlias.TryGetValue(message.Address, out MixerChannel? channel))
        {
            DistributeChannelMeter(channel, meters);
        }
    }

    /// <summary>
    /// Handles a fader-position message echoed by the console for one of the
    /// watched devices. Returns <c>true</c> if the message was a fader update for
    /// a watched device (and was consumed), <c>false</c> otherwise.
    /// </summary>
    private bool TryHandleFaderFeedback(OscMessage message)
    {
        if (!message.Address.EndsWith("/mix/fader", StringComparison.Ordinal))
        {
            return false;
        }

        List<AudioDevice> snapshot = devices;
        foreach (AudioDevice device in snapshot)
        {
            if (device.GetOscAddress() == message.Address)
            {
                if (message.Arguments.Count > 0 && message.Arguments[0] is float fader)
                {
                    device.UpdateFaderFromConsole(fader);
                }
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Handles a mute (on/off) message echoed by the console for one of the watched
    /// devices. Returns <c>true</c> if the message was a mute update for a watched
    /// device (and was consumed), <c>false</c> otherwise.
    /// </summary>
    private bool TryHandleMuteFeedback(OscMessage message)
    {
        if (!message.Address.EndsWith("/mix/on", StringComparison.Ordinal))
        {
            return false;
        }

        List<AudioDevice> snapshot = devices;
        foreach (AudioDevice device in snapshot)
        {
            if (device.GetMuteOscAddress() == message.Address)
            {
                if (message.Arguments.Count > 0 && message.Arguments[0] is int on)
                {
                    device.UpdateMuteFromConsole(on);
                }
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Asks the console for a device's current fader position by sending its fader
    /// address with no arguments. The console replies with the value, which
    /// <see cref="TryHandleFaderFeedback"/> stores. This seeds the real fader
    /// position so the leveler starts from where the fader actually sits.
    /// </summary>
    private void QueryDeviceFader(AudioDevice device)
    {
        OscMessage query = new OscMessage(device.GetOscAddress(), ",", new List<object>());
        oscClient.Send(query);
    }

    /// <summary>
    /// Asks the console for a device's current mute (on/off) state by sending its
    /// mute address with no arguments. The reply is stored by
    /// <see cref="TryHandleMuteFeedback"/>, seeding the real state so the modules
    /// know from the start whether the device is muted.
    /// </summary>
    private void QueryDeviceMute(AudioDevice device)
    {
        OscMessage query = new OscMessage(device.GetMuteOscAddress(), ",", new List<object>());
        oscClient.Send(query);
    }

    /// <summary>Queries the current fader position and mute state of every watched device.</summary>
    private void QueryAllInitialState()
    {
        foreach (AudioDevice device in devices)
        {
            QueryDeviceFader(device);
            QueryDeviceMute(device);
        }
    }

    /// <summary>
    /// Asks the console which head amp feeds a channel (<c>/-ha/NN/index</c>), so
    /// the AntiClipper can target the right preamp whether it is a local XLR or a
    /// stage box on AES50.
    /// </summary>
    private void QueryChannelHeadAmp(MixerChannel channel)
    {
        int inputIndex = channel.OscNumber - 1; // channels 1..32 -> input index 0..31
        OscMessage query = new OscMessage(
            M32Buckets.BuildHeadAmpIndexAddress(inputIndex), ",", new List<object>());
        oscClient.Send(query);
    }

    /// <summary>Asks the console for a head amp's current gain, to seed the real value.</summary>


    /// <summary>
    /// Handles the console's replies to head amp queries: <c>/-ha/NN/index</c>
    /// (which head amp feeds an input) and <c>/headamp/NNN/gain</c> (its current
    /// gain). Returns <c>true</c> if the message was consumed.
    /// </summary>
    private bool TryHandleHeadAmpFeedback(OscMessage message)
    {
        string address = message.Address;
        if (address.StartsWith("/-ha/", StringComparison.Ordinal) && address.EndsWith("/index", StringComparison.Ordinal))
        {
            if (message.Arguments.FirstOrDefault() is int index && index >= -1 && index <= 127)
            {
                var channel = FindChannelByOscNumber(ParseAddressNumber(address) + 1);
                if (channel != null)
                {
                    bool changed = !channel.HeadAmpResolved || channel.HeadAmpIndex != index;
                    if (changed)
                    {
                        channel.InvalidateRouting();
                        channel.HeadAmpIndex = index;
                        if (index >= 0)
                        {
                            if (!headAmps.TryGetValue(index, out var gain)) headAmps[index] = gain = new GainState();
                            channel.Gain = gain;
                        }
                    }
                    channel.HeadAmpResolved = true;
                    if (changed || !channel.GainInitialized) QueryGain(channel);
                }
            }
            return true;
        }
        if (message.Arguments.FirstOrDefault() is not float value || !float.IsFinite(value) || value < 0 || value > 1) return false;
        if (address.StartsWith("/headamp/", StringComparison.Ordinal) && address.EndsWith("/gain", StringComparison.Ordinal))
        {
            if (headAmps.TryGetValue(ParseAddressNumber(address), out var gain))
                gain.Observe(FaderMath.Denormalize(value, M32Buckets.HEAD_AMP_GAIN_MIN_DB, M32Buckets.HEAD_AMP_GAIN_MAX_DB), oscClient.Clock.GetTimestamp());
            return true;
        }
        if (address.StartsWith("/ch/", StringComparison.Ordinal) && address.EndsWith("/preamp/trim", StringComparison.Ordinal))
        {
            var channel = FindChannelByOscNumber(ParseAddressNumber(address));
            if (channel is { HeadAmpResolved: true, HeadAmpIndex: -1 })
                channel.Gain.Observe(FaderMath.Denormalize(value, M32Buckets.CHANNEL_TRIM_MIN_DB, M32Buckets.CHANNEL_TRIM_MAX_DB), oscClient.Clock.GetTimestamp());
            return true;
        }
        return false;
    }

    /// <summary>Extracts the numeric segment from "/-ha/05/index" or "/headamp/034/gain".</summary>
    private static int ParseAddressNumber(string address)
    {
        string[] parts = address.Split('/');
        if (parts.Length >= 3 && int.TryParse(parts[2], out int number))
        {
            return number;
        }
        return -1;
    }

    /// <summary>Finds a watched channel by its OSC number, or <c>null</c>.</summary>
    private MixerChannel? FindChannelByOscNumber(int oscNumber)
    {
        foreach (AudioDevice device in devices)
        {
            if (device is MixerChannel channel && channel.OscNumber == oscNumber)
            {
                return channel;
            }
        }
        return null;
    }

    /// <summary>Finds a watched channel by its head amp index, or <c>null</c>.</summary>


    /// <summary>
    /// Decodes an M32 meter blob: a 32-bit little-endian float count followed by
    /// that many little-endian floats. Returns an empty array for any malformed
    /// blob (truncated, negative count, or a declared count larger than the
    /// payload). Internal so the malformed-packet guards are unit-testable.
    /// </summary>
    internal static float[] ParseMeterBlob(byte[] blob)
    {
        if (blob.Length < 4)
        {
            return Array.Empty<float>();
        }

        int floatCount = BitConverter.ToInt32(blob, 0);

        // Bound floatCount by what actually fits in the blob. Written as a division
        // (not "4 + floatCount * 4 > blob.Length") so a corrupt packet declaring a
        // huge count cannot overflow the multiplication and slip past the guard into
        // a multi-gigabyte allocation.
        if (floatCount < 0 || floatCount > (blob.Length - 4) / 4)
        {
            return Array.Empty<float>();
        }

        float[] result = new float[floatCount];
        for (int i = 0; i < floatCount; i++)
        {
            result[i] = BitConverter.ToSingle(blob, 4 + i * 4);
        }

        return result;
    }

    private void DistributeChannelMeter(MixerChannel channel, float[] meters)
    {
        int postTrimIdx = (int)ChannelStripFloat.PostTrim;
        int gateIdx = (int)ChannelStripFloat.GateGainReduction;
        int compIdx = (int)ChannelStripFloat.CompressorGainReduction;
        int postFaderIdx = (int)ChannelStripFloat.PostFader;

        if (meters.Length <= postFaderIdx)
        {
            return;
        }

        MeterSample sample = new MeterSample(
            meters[postTrimIdx],
            meters[gateIdx],
            meters[compIdx],
            meters[postFaderIdx], oscClient.Clock);

        channel.AddSample(sample);
    }

    private void DistributeBusMeters(float[] meters)
    {
        int firstBusIndex = (int)MixBusFloat.Bus1;
        int lastBusIndex = (int)MixBusFloat.Bus16;

        // Atomic snapshot: if the list changes mid-iteration, this cycle finishes
        // on the old version; the next one will see the new list.
        List<AudioDevice> snapshot = devices;

        foreach (AudioDevice device in snapshot)
        {
            if (device is not Bus bus)
            {
                continue;
            }

            int positionInBlob = firstBusIndex + (bus.OscNumber - 1);

            if (positionInBlob < firstBusIndex || positionInBlob > lastBusIndex)
            {
                continue;
            }

            if (positionInBlob >= meters.Length)
            {
                continue;
            }

            bus.AddSample(new MeterSample(meters[positionInBlob], oscClient.Clock));
        }
    }
}
