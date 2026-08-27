using System.Diagnostics;
using SyncSAW.Core;

namespace SyncSAW.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public void SecondCoordinator_NotifiesPrimaryAndDoesNotAcquireInstance()
    {
        var instanceKey = $"SyncSAW.Tests.{Guid.NewGuid():N}";
        using var activationReceived = new ManualResetEventSlim();
        using var primary = new SingleInstanceCoordinator(instanceKey);

        Assert.Equal(
            SingleInstanceStartResult.Primary,
            primary.Start(() => activationReceived.Set()));

        var authorizedProcessId = 0;
        using var secondary = new SingleInstanceCoordinator(instanceKey);
        var result = secondary.Start(
            () => throw new UnreachableException(),
            processId => authorizedProcessId = processId);

        Assert.Equal(SingleInstanceStartResult.ActivationSent, result);
        Assert.Equal(Environment.ProcessId, authorizedProcessId);
        Assert.True(activationReceived.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void DisposingPrimary_ReleasesInstanceOwnership()
    {
        var instanceKey = $"SyncSAW.Tests.{Guid.NewGuid():N}";
        using (var primary = new SingleInstanceCoordinator(instanceKey))
        {
            Assert.Equal(
                SingleInstanceStartResult.Primary,
                primary.Start(() => { }));
        }

        using var replacement = new SingleInstanceCoordinator(instanceKey);
        Assert.Equal(
            SingleInstanceStartResult.Primary,
            replacement.Start(() => { }));
    }
}
