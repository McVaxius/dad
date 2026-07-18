using System.ComponentModel;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadConfigurationPersistenceCoordinatorTests
{
    [Fact]
    public void DirtyRequestsCoalesceAfterQuietPeriod()
    {
        var now = UtcStart();
        var writes = 0;
        var coordinator = Create(() => writes++, () => now);

        coordinator.MarkDirty();
        now = now.AddMilliseconds(100);
        coordinator.MarkDirty();
        now = now.AddMilliseconds(249);
        Assert.False(coordinator.Update());
        now = now.AddMilliseconds(1);
        Assert.True(coordinator.Update());

        Assert.Equal(1, writes);
        Assert.False(coordinator.GetState().IsDirty);
    }

    [Fact]
    public void ContinuousChangesFlushAtMaximumDelay()
    {
        var now = UtcStart();
        var writes = 0;
        var coordinator = Create(() => writes++, () => now);
        coordinator.MarkDirty();

        foreach (var milliseconds in new[] { 200, 400, 600, 800 })
        {
            now = UtcStart().AddMilliseconds(milliseconds);
            coordinator.MarkDirty();
            Assert.False(coordinator.Update());
        }

        now = UtcStart().AddSeconds(1);
        coordinator.MarkDirty();
        Assert.True(coordinator.Update());
        Assert.Equal(1, writes);
    }

    [Fact]
    public void ForceFlushIgnoresQuietPeriod()
    {
        var now = UtcStart();
        var writes = 0;
        var coordinator = Create(() => writes++, () => now);
        coordinator.MarkDirty();

        Assert.True(coordinator.ForceFlush());
        Assert.Equal(1, writes);
        Assert.False(coordinator.GetState().IsDirty);
    }

    [Fact]
    public void ForceFlushMakesFinalAttemptAfterFaultWasLatched()
    {
        var now = UtcStart();
        var fail = true;
        var attempts = 0;
        var coordinator = Create(
            () =>
            {
                attempts++;
                if (fail)
                    throw new Win32Exception(DadConfigurationPersistenceCoordinator.InvalidHandleNativeError);
            },
            () => now);
        coordinator.MarkDirty();
        now = now.AddMilliseconds(250);
        Assert.True(coordinator.Update());
        Assert.True(coordinator.GetState().IsLatched);

        fail = false;
        Assert.True(coordinator.ForceFlush());

        Assert.Equal(2, attempts);
        Assert.False(coordinator.GetState().HasFault);
        Assert.False(coordinator.GetState().IsDirty);
    }

    [Fact]
    public void ConcurrentDirtyRequestsProduceOneWrite()
    {
        var now = UtcStart();
        var writes = 0;
        var coordinator = Create(() => Interlocked.Increment(ref writes), () => now);

        Parallel.For(0, 256, _ => coordinator.MarkDirty());
        now = now.AddMilliseconds(250);
        Assert.True(coordinator.Update());

        Assert.Equal(1, writes);
        Assert.False(coordinator.GetState().IsDirty);
    }

    [Fact]
    public void DirtyRequestDuringWriteIsRetainedForNextFlush()
    {
        var now = UtcStart();
        var writes = 0;
        DadConfigurationPersistenceCoordinator? coordinator = null;
        coordinator = Create(
            () =>
            {
                writes++;
                if (writes == 1)
                    coordinator!.MarkDirty();
            },
            () => now);

        coordinator.MarkDirty();
        now = now.AddMilliseconds(250);
        Assert.True(coordinator.Update());
        Assert.True(coordinator.GetState().IsDirty);

        now = now.AddMilliseconds(250);
        Assert.True(coordinator.Update());
        Assert.Equal(2, writes);
        Assert.False(coordinator.GetState().IsDirty);
    }

    [Fact]
    public void OrdinaryFailureGetsThreeAutomaticRetriesThenLatches()
    {
        var now = UtcStart();
        var attempts = 0;
        var coordinator = Create(
            () =>
            {
                attempts++;
                throw new IOException("ordinary failure");
            },
            () => now);
        coordinator.MarkDirty();
        now = now.AddMilliseconds(250);

        Assert.True(coordinator.Update());
        for (var retry = 0; retry < 3; retry++)
        {
            now = coordinator.GetState().NextRetryAtUtc!.Value;
            Assert.True(coordinator.Update());
        }

        var state = coordinator.GetState();
        Assert.Equal(4, attempts);
        Assert.True(state.HasFault);
        Assert.True(state.IsLatched);
        Assert.Null(state.NextRetryAtUtc);
        now = now.AddMinutes(1);
        Assert.False(coordinator.Update());
        Assert.Equal(4, attempts);
    }

    [Fact]
    public void InvalidHandleLatchesImmediatelyWithoutLogSpamLoop()
    {
        var now = UtcStart();
        var attempts = 0;
        var failures = 0;
        var coordinator = Create(
            () =>
            {
                attempts++;
                throw new Win32Exception(DadConfigurationPersistenceCoordinator.InvalidHandleNativeError);
            },
            () => now,
            _ => failures++);
        coordinator.MarkDirty();
        now = now.AddMilliseconds(250);

        Assert.True(coordinator.Update());
        Assert.True(coordinator.GetState().IsLatched);
        now = now.AddMinutes(1);
        Assert.False(coordinator.Update());
        Assert.Equal(1, attempts);
        Assert.Equal(1, failures);
    }

    [Fact]
    public void ManualRetryRunsAtEndOfFrameAndSuccessfulWriteClearsFault()
    {
        var now = UtcStart();
        var fail = true;
        var attempts = 0;
        var coordinator = Create(
            () =>
            {
                attempts++;
                if (fail)
                    throw new Win32Exception(DadConfigurationPersistenceCoordinator.InvalidHandleNativeError);
            },
            () => now);
        coordinator.MarkDirty();
        now = now.AddMilliseconds(250);
        Assert.True(coordinator.Update());
        Assert.True(coordinator.GetState().HasFault);

        fail = false;
        coordinator.QueueManualRetry();
        Assert.True(coordinator.GetState().ManualRetryQueued);
        Assert.True(coordinator.Update());

        var recovered = coordinator.GetState();
        Assert.Equal(2, attempts);
        Assert.False(recovered.HasFault);
        Assert.False(recovered.IsLatched);
        Assert.False(recovered.IsDirty);
        Assert.NotNull(recovered.LastSuccessfulSaveUtc);
    }

    [Fact]
    public void PersistenceAndFailureObserverExceptionsNeverEscapeUpdate()
    {
        var now = UtcStart();
        var coordinator = Create(
            () => throw new InvalidOperationException("storage failed"),
            () => now,
            _ => throw new InvalidOperationException("observer failed"));
        coordinator.MarkDirty();
        now = now.AddMilliseconds(250);

        var exception = Record.Exception(() => coordinator.Update());
        Assert.Null(exception);
        Assert.True(coordinator.GetState().HasFault);
    }

    private static DadConfigurationPersistenceCoordinator Create(
        Action persist,
        Func<DateTime> utcNow,
        Action<DadConfigurationPersistenceFailure>? onFailure = null)
        => new(persist, utcNow, onFailure: onFailure);

    private static DateTime UtcStart()
        => new(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
}
