using System.Collections.Concurrent;
using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Osc;

namespace M32LiveConsoleTool.Modules;

/// <summary>
/// Applies delay times to the M32's effects slots. Each slot has its own
/// <see cref="DelaySlotConfig"/> that decides whether it updates automatically on
/// every BPM change or holds a fixed manual value. Automatic and manual modes
/// coexist without conflict in the same show.
/// </summary>
/// <remarks>
/// <para>
/// The M32 has 8 effects slots numbered 1-8. The delay time lives on a different
/// FX parameter depending on the effect type (Stereo Delay uses parameter 2, the
/// other delays parameter 1), and the value is sent normalized to [0,1] over the
/// 1-3000 ms range.
/// </para>
/// <para>
/// <strong>Effect auto-detection</strong>: on start (and on demand via
/// <see cref="RefreshEffects"/>), the controller queries <c>/fx/N/type</c> for
/// every slot and resolves the loaded effect through <see cref="FxEffectCatalog"/>.
/// It then uses the detected delay type to target the right time parameter; if the
/// console reports a slot has no delay, it does not write a time and logs a
/// warning. Only FX slots 1-4 can hold delays; slots 5-8 are insert-only. The
/// effect type stored in <see cref="DelaySlotConfig"/> is used only as a fallback
/// when the console has not reported the slot's effect yet.
/// </para>
/// <para>
/// <strong>Automatic/manual coexistence</strong>: when the
/// <see cref="BpmDetector"/> reports a BPM change via
/// <see cref="ApplyAllFromBpm"/>, only the automatic slots are recomputed and
/// sent; manual slots keep their fixed value.
/// </para>
/// <para>
/// <strong>Concurrency</strong>: both dictionaries are
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. The OSC receive thread updates
/// the detected effects while the main and BpmDetector threads read configs and
/// apply delays, with no external locks.
/// </para>
/// </remarks>
public class DelayController
{
    // ─────────────────────────────────────────────────────────────────────
    // Constants
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Minimum slot number supported by the M32.</summary>
    public const int MIN_SLOT_NUMBER = 1;

    /// <summary>Maximum slot number supported by the M32.</summary>
    public const int MAX_SLOT_NUMBER = 8;

    // ─────────────────────────────────────────────────────────────────────
    // Private fields
    // ─────────────────────────────────────────────────────────────────────

    private OscClient oscClient;
    private ShowLogger logger;

    /// <summary>
    /// Per-slot configurations (mode and figure/ms). A
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> for safe access between the
    /// main thread (configuration) and the BpmDetector thread (application).
    /// </summary>
    private ConcurrentDictionary<int, DelaySlotConfig> delaySlots;

    /// <summary>
    /// Effect detected in each FX slot from <c>/fx/N/type</c>. Empty until the
    /// console answers. Updated by the OSC receive thread.
    /// </summary>
    private ConcurrentDictionary<int, DetectedEffect> detectedEffects;

    private volatile bool isRunning;

    // ─────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an empty delay controller bound to an OSC client and a logger.
    /// </summary>
    public DelayController(OscClient oscClient, ShowLogger logger)
    {
        this.oscClient = oscClient;
        this.logger = logger;
        this.delaySlots = new ConcurrentDictionary<int, DelaySlotConfig>();
        this.detectedEffects = new ConcurrentDictionary<int, DetectedEffect>();
        this.isRunning = false;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public properties
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Read-only view of the configured slots. To modify, use
    /// <see cref="ConfigureSlot"/> or <see cref="ClearSlot"/>.
    /// </summary>
    public IReadOnlyDictionary<int, DelaySlotConfig> DelaySlots
    {
        get { return delaySlots; }
    }

    /// <summary>
    /// Read-only view of the effect detected in each FX slot (from the console).
    /// Used by the UI to show what is loaded and whether each slot is a delay.
    /// </summary>
    public IReadOnlyDictionary<int, DetectedEffect> DetectedEffects
    {
        get { return detectedEffects; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Lifecycle: subscribe to OSC and detect effects
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts listening for <c>/fx/N/type</c> replies and queries the current
    /// effects. Call once after the OSC client is connected.
    /// </summary>
    public void Start()
    {
        if (isRunning)
        {
            return;
        }

        isRunning = true;
        oscClient.MessageReceived += OnMessageReceived;
        RefreshEffects();
    }

    /// <summary>Stops listening for effect replies.</summary>
    public void Stop()
    {
        if (!isRunning)
        {
            return;
        }

        isRunning = false;
        oscClient.MessageReceived -= OnMessageReceived;
    }

    /// <summary>
    /// Asks the console for the effect type loaded in every FX slot
    /// (<c>/fx/N/type</c>). Call it at startup and whenever the engineer changes
    /// effects during the show, so the time is mapped to the right parameter.
    /// </summary>
    public void RefreshEffects()
    {
        for (int slot = MIN_SLOT_NUMBER; slot <= MAX_SLOT_NUMBER; slot++)
        {
            OscMessage query = new OscMessage($"/fx/{slot}/type", ",", new List<object>());
            oscClient.Send(query);
        }
    }

    private void OnMessageReceived(OscMessage message)
    {
        if (!isRunning)
        {
            return;
        }

        string address = message.Address;
        if (!address.StartsWith("/fx/", StringComparison.Ordinal)
            || !address.EndsWith("/type", StringComparison.Ordinal))
        {
            return;
        }

        if (message.Arguments.Count == 0 || message.Arguments[0] is not int typeIndex)
        {
            return;
        }

        int slot = ParseSlotNumber(address);
        if (slot < MIN_SLOT_NUMBER || slot > MAX_SLOT_NUMBER)
        {
            return;
        }

        DetectedEffect? detected = FxEffectCatalog.Resolve(slot, typeIndex);
        if (detected != null)
        {
            detectedEffects[slot] = detected;
            logger.LogEvent($"DelayController|slot{slot}|detected|{detected.Name}", Severity.Info);
        }
    }

    /// <summary>Extracts the slot number from an address like "/fx/3/type".</summary>
    private static int ParseSlotNumber(string address)
    {
        string[] parts = address.Split('/');
        if (parts.Length >= 3 && int.TryParse(parts[2], out int slot))
        {
            return slot;
        }
        return -1;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Slot configuration
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Configures a slot with the given configuration, replacing any previous one.
    /// If the console has reported that this slot holds a non-delay effect, a
    /// warning is logged so the engineer notices the mismatch.
    /// </summary>
    public void ConfigureSlot(int slot, DelaySlotConfig config)
    {
        ValidateSlotNumber(slot);
        delaySlots[slot] = config;
        logger.LogEvent(
            $"DelayController|slot{slot}|configured|{config}",
            Severity.Info);

        if (detectedEffects.TryGetValue(slot, out DetectedEffect? detected) && !detected.IsDelay)
        {
            logger.LogEvent(
                $"DelayController|slot{slot}|no delay loaded on console ({detected.Name})",
                Severity.Warning);
        }
    }

    /// <summary>
    /// Removes a slot's configuration. The slot is no longer managed.
    /// </summary>
    public void ClearSlot(int slot)
    {
        ValidateSlotNumber(slot);
        if (delaySlots.TryRemove(slot, out _))
        {
            logger.LogEvent(
                $"DelayController|slot{slot}|cleared",
                Severity.Info);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Applying delays
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends the given time to the slot, regardless of whether it is automatic or
    /// manual. Useful for initialization or one-off overrides.
    /// </summary>
    public void ApplyDelay(int slot, double timeMs, DelayEffectType fallbackType)
    {
        ValidateSlotNumber(slot);

        if (timeMs <= 0)
        {
            return;
        }

        SendDelayMessage(slot, timeMs, fallbackType);
    }

    /// <summary>
    /// Recomputes and sends the delay time for all <strong>automatic</strong>
    /// slots from the given BPM. Manual slots are respected: they receive no
    /// message.
    /// </summary>
    public void ApplyAllFromBpm(double bpm)
    {
        if (bpm <= 0)
        {
            return;
        }

        foreach (KeyValuePair<int, DelaySlotConfig> entry in delaySlots)
        {
            int slot = entry.Key;
            DelaySlotConfig config = entry.Value;

            if (!config.IsAutomatic)
            {
                continue;
            }

            double timeMs = config.GetDelayMs(bpm);
            SendDelayMessage(slot, timeMs, config.EffectType);
        }
    }

    /// <summary>
    /// Applies each slot's current value to the console without recomputing.
    /// Useful at startup to sync the console with manual slots and with automatic
    /// slots at the last known BPM.
    /// </summary>
    /// <param name="lastKnownBpm">BPM to use for automatic slots. 0 skips the automatic ones.</param>
    public void ApplyAllCurrentSettings(double lastKnownBpm)
    {
        foreach (KeyValuePair<int, DelaySlotConfig> entry in delaySlots)
        {
            int slot = entry.Key;
            DelaySlotConfig config = entry.Value;

            if (config.IsAutomatic)
            {
                if (lastKnownBpm > 0)
                {
                    SendDelayMessage(slot, config.GetDelayMs(lastKnownBpm), config.EffectType);
                }
            }
            else
            {
                SendDelayMessage(slot, config.ManualMs, config.EffectType);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Private methods
    // ─────────────────────────────────────────────────────────────────────

    private void ValidateSlotNumber(int slot)
    {
        if (slot < MIN_SLOT_NUMBER || slot > MAX_SLOT_NUMBER)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slot),
                $"Slot number must be between {MIN_SLOT_NUMBER} and {MAX_SLOT_NUMBER}.");
        }
    }

    /// <summary>
    /// Builds and sends the OSC message that sets a slot's delay time. Uses the
    /// effect type detected on the console when available (falling back to
    /// <paramref name="fallbackType"/> otherwise) to pick the time parameter
    /// (Stereo Delay uses parameter 2, the rest parameter 1). If the console
    /// reports the slot has no delay, nothing is sent and a warning is logged. The
    /// value is normalized to [0,1] over the 1-3000 ms span.
    /// </summary>
    private void SendDelayMessage(int slot, double timeMs, DelayEffectType fallbackType)
    {
        DelayEffectType effectType = fallbackType;

        if (detectedEffects.TryGetValue(slot, out DetectedEffect? detected))
        {
            if (!detected.IsDelay)
            {
                logger.LogEvent(
                    $"DelayController|slot{slot}|skipped (no delay loaded: {detected.Name})",
                    Severity.Warning);
                return;
            }
            effectType = detected.DelayType!.Value;
        }

        int timeParameter = DelayEffects.TimeParameterNumber(effectType);
        string oscAddress = $"/fx/{slot}/par/{timeParameter:D2}";

        float normalized = FaderMath.Normalize(timeMs, DelayEffects.TimeMinMs, DelayEffects.TimeMaxMs);

        OscMessage message = new OscMessage(
            oscAddress,
            ",f",
            new List<object> { normalized });

        oscClient.Send(message);
    }
}
