namespace Series4.Desktop;

public enum RecordingCapturePhase
{
    Idle,
    Countdown,
    VideoStarting,
    InputHookStarting,
    CaptureReady,
    Recording,
    Draining,
    Saving,
    Completed,
    Failed,
}

public sealed record RecordingCaptureSnapshot(
    long SessionId,
    RecordingCapturePhase Phase,
    bool CaptureReady,
    bool VideoReady,
    bool HookReady,
    long RawInputCount,
    long EnqueuedCount,
    long CommittedCount,
    long RejectedCount,
    IReadOnlyDictionary<string, long> RejectionReasons,
    DateTimeOffset? LastInputAt,
    string? Failure
);

public sealed class RecordingCaptureDiagnostics
{
    private readonly object gate = new();
    private readonly Dictionary<string, long> rejectionReasons = new(
        StringComparer.Ordinal
    );
    private long sessionId;
    private RecordingCapturePhase phase = RecordingCapturePhase.Idle;
    private bool videoReady;
    private bool hookReady;
    private long rawInputCount;
    private long enqueuedCount;
    private long committedCount;
    private long rejectedCount;
    private DateTimeOffset? lastInputAt;
    private string? failure;

    public void Reset(long newSessionId, RecordingCapturePhase initialPhase)
    {
        lock (gate)
        {
            sessionId = newSessionId;
            phase = initialPhase;
            videoReady = false;
            hookReady = false;
            rawInputCount = 0;
            enqueuedCount = 0;
            committedCount = 0;
            rejectedCount = 0;
            rejectionReasons.Clear();
            lastInputAt = null;
            failure = null;
        }
    }

    public void SetPhase(RecordingCapturePhase value)
    {
        lock (gate)
        {
            phase = value;
        }
    }

    public void SetVideoReady(bool value)
    {
        lock (gate)
        {
            videoReady = value;
        }
    }

    public void SetHookReady(bool value)
    {
        lock (gate)
        {
            hookReady = value;
        }
    }

    public void RecordRawInput(DateTimeOffset at)
    {
        lock (gate)
        {
            rawInputCount++;
            lastInputAt = at;
        }
    }

    public void RecordEnqueued()
    {
        lock (gate)
        {
            enqueuedCount++;
        }
    }

    public void RecordCommitted()
    {
        lock (gate)
        {
            committedCount++;
        }
    }

    public void RecordRejected(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (gate)
        {
            rejectedCount++;
            rejectionReasons[reason] = rejectionReasons.GetValueOrDefault(reason) + 1;
        }
    }

    public void Fail(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        lock (gate)
        {
            failure = message;
            phase = RecordingCapturePhase.Failed;
            hookReady = false;
        }
    }

    public RecordingCaptureSnapshot Snapshot()
    {
        lock (gate)
        {
            return new RecordingCaptureSnapshot(
                sessionId,
                phase,
                videoReady && hookReady && failure is null,
                videoReady,
                hookReady,
                rawInputCount,
                enqueuedCount,
                committedCount,
                rejectedCount,
                new Dictionary<string, long>(rejectionReasons),
                lastInputAt,
                failure
            );
        }
    }
}
