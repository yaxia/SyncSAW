using SyncSAW.Core;

namespace SyncSAW.Tests;

public sealed class AzCopyProcessRunnerTests
{
    [Fact]
    public async Task CancelAll_TerminatesActiveChildProcess()
    {
        var runner = new AzCopyProcessRunner();
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        var running = runner.RunAsync(
            powershell,
            ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"],
            CancellationToken.None);
        await Task.Delay(500);

        runner.CancelAll();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task RunAsync_WritesCapturedProcessOutputToOperationLog()
    {
        var directory = Directory.CreateTempSubdirectory("SyncSAW.RunnerLogs.");
        try
        {
            var runner = new AzCopyProcessRunner(new OperationLog(directory.FullName));
            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");

            var result = await runner.RunAsync(
                powershell,
                [
                    "-NoProfile",
                    "-Command",
                    "[Console]::Out.WriteLine('out-line'); [Console]::Error.WriteLine('err-line'); exit 3"
                ],
                CancellationToken.None);

            Assert.Equal(3, result.ExitCode);
            var path = Assert.Single(Directory.GetFiles(directory.FullName, "*.log"));
            var content = await File.ReadAllTextAsync(path);
            Assert.Contains("out-line", content);
            Assert.Contains("err-line", content);
            Assert.Contains("END exit=3", content);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
