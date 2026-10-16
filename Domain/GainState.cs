namespace M32LiveConsoleTool.Domain;

/// <summary>A physical gain control shared by every channel routed to it.</summary>
public sealed class GainState
{
    public double ObservedDb { get; private set; }
    public bool IsConfirmed { get; private set; }
    public long? LastReductionAt { get; private set; }
    public double TotalReductionDb { get; private set; }
    public long ObservedAt { get; private set; }
    public double LastReportedDb { get; private set; }
    public double? PendingDb => pendingDb;
    private double? pendingDb;

    public void Observe(double value, long timestamp)
    {
        if (!double.IsFinite(value)) return;
        ObservedAt = timestamp;
        LastReportedDb = value;
        // A delayed pre-command reply must not undo a reduction in the cache.
        if (pendingDb is double pending && value > pending + 0.01) return;
        ObservedDb = value;
        pendingDb = null;
        IsConfirmed = true;
    }

    public void RecordRetry(long timestamp) => LastReductionAt = timestamp;

    public void RecordReduction(double value, long timestamp)
    {
        TotalReductionDb += Math.Max(0, ObservedDb - value);
        pendingDb = value;
        IsConfirmed = false;
        LastReductionAt = timestamp;
    }

    public void Invalidate()
    {
        IsConfirmed = false;
        pendingDb = null;
    }
}
