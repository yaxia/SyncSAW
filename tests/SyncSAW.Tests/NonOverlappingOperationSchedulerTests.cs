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
}
