using Series4.Desktop;
using Xunit;

namespace Series4.Desktop.Tests;

public sealed class RecordingCaptureDiagnosticsTests
{
    [Fact]
    public void SnapshotBecomesReadyOnlyAfterVideoAndHookAreReady()
    {
        var diagnostics = new RecordingCaptureDiagnostics();
        diagnostics.Reset(42, RecordingCapturePhase.VideoStarting);

        diagnostics.SetVideoReady(true);
        Assert.False(diagnostics.Snapshot().CaptureReady);

        diagnostics.SetPhase(RecordingCapturePhase.InputHookStarting);
        diagnostics.SetHookReady(true);
        var snapshot = diagnostics.Snapshot();

        Assert.True(snapshot.CaptureReady);
        Assert.True(snapshot.VideoReady);
        Assert.True(snapshot.HookReady);
        Assert.Equal(42, snapshot.SessionId);
    }

    [Fact]
    public void CountsInputPipelineAndRejectionReasons()
    {
        var diagnostics = new RecordingCaptureDiagnostics();
        diagnostics.Reset(7, RecordingCapturePhase.Recording);
        var observedAt = DateTimeOffset.Parse("2026-10-03T07:20:11+09:00");

        diagnostics.RecordRawInput(observedAt);
        diagnostics.RecordRawInput(observedAt.AddMilliseconds(10));
        diagnostics.RecordEnqueued();
        diagnostics.RecordCommitted();
        diagnostics.RecordRejected("outside_capture");

        var snapshot = diagnostics.Snapshot();
        Assert.Equal(2, snapshot.RawInputCount);
        Assert.Equal(1, snapshot.EnqueuedCount);
        Assert.Equal(1, snapshot.CommittedCount);
        Assert.Equal(1, snapshot.RejectedCount);
        Assert.Equal(1, snapshot.RejectionReasons["outside_capture"]);
        Assert.Equal(observedAt.AddMilliseconds(10), snapshot.LastInputAt);
    }

    [Fact]
    public void ResetIsolatesCountersBySession()
    {
        var diagnostics = new RecordingCaptureDiagnostics();
        diagnostics.Reset(1, RecordingCapturePhase.Recording);
        diagnostics.RecordRawInput(DateTimeOffset.UtcNow);
        diagnostics.RecordEnqueued();
        diagnostics.RecordCommitted();

        diagnostics.Reset(2, RecordingCapturePhase.VideoStarting);
        var snapshot = diagnostics.Snapshot();

        Assert.Equal(2, snapshot.SessionId);
        Assert.Equal(0, snapshot.RawInputCount);
        Assert.Equal(0, snapshot.EnqueuedCount);
        Assert.Equal(0, snapshot.CommittedCount);
        Assert.Empty(snapshot.RejectionReasons);
    }

    [Fact]
    public void FailureClearsReadinessAndKeepsDiagnosticMessage()
    {
        var diagnostics = new RecordingCaptureDiagnostics();
        diagnostics.Reset(9, RecordingCapturePhase.InputHookStarting);
        diagnostics.SetVideoReady(true);
        diagnostics.SetHookReady(true);

        diagnostics.Fail("hook failed");
        var snapshot = diagnostics.Snapshot();

        Assert.Equal(RecordingCapturePhase.Failed, snapshot.Phase);
        Assert.False(snapshot.CaptureReady);
        Assert.False(snapshot.HookReady);
        Assert.Equal("hook failed", snapshot.Failure);
    }
}
