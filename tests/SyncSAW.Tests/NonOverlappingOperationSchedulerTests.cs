using SyncSAW.Core;

namespace SyncSAW.Tests;

public sealed class NonOverlappingOperationSchedulerTests
{
    [Fact]
    public async Task TryRunAsync_SkipsSecondOperationWhileFirstIsRunning()
    {
        using var scheduler = new NonOverlappingOperationScheduler();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = scheduler.TryRunAsync(
            async _ =>
            {
                started.SetResult();
                await release.Task;
            },
            CancellationToken.None);
        await started.Task;

        var secondExecuted = false;
        var second = await scheduler.TryRunAsync(
            _ =>
            {
                secondExecuted = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(second);
        Assert.False(secondExecuted);

        release.SetResult();
        Assert.True(await first);
    }

    [Fact]
    public async Task TryRunAsync_ReleasesGateWhenOperationFails()
    {
        using var scheduler = new NonOverlappingOperationScheduler();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scheduler.TryRunAsync(
                _ => throw new InvalidOperationException("expected"),
                CancellationToken.None));

        Assert.True(await scheduler.TryRunAsync(_ => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_WaitsForActiveOperationThenRuns()
    {
        using var scheduler = new NonOverlappingOperationScheduler();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = scheduler.RunAsync(
            async _ =>
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            },
            CancellationToken.None);
        await firstStarted.Task;

        var second = scheduler.RunAsync(
            _ =>
            {
                secondStarted.SetResult();
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(secondStarted.Task.IsCompleted);
        releaseFirst.SetResult();

        await Task.WhenAll(first, second);
        Assert.True(secondStarted.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RunAsync_QueuedOperationRunsAfterActiveOperationFails()
    {
        using var scheduler = new NonOverlappingOperationScheduler();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondExecuted = false;

        var first = scheduler.RunAsync(
            async _ =>
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
                throw new InvalidOperationException("expected");
            },
            CancellationToken.None);
        await firstStarted.Task;

        var second = scheduler.RunAsync(
            _ =>
            {
                secondExecuted = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        releaseFirst.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await second;
        Assert.True(secondExecuted);
    }
}
