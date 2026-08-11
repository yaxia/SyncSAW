using SyncSAW.Core;

namespace SyncSAW.Tests;

public sealed class OperationLogTests
{
    [Fact]
    public async Task OperationLog_CapturesOutputAndRedactsSas()
    {
        var directory = Directory.CreateTempSubdirectory("SyncSAW.LogTests.");
        try
        {
            var log = new OperationLog(directory.FullName);
            await log.WriteCommandStartedAsync(
                "azcopy.exe",
                [
                    "copy",
                    "https://account.blob.core.windows.net/container?sv=1&sig=secret"
                ]);
            await log.WriteCommandCompletedAsync(
                new AzCopyCommandResult(
                    1,
                    "upload summary",
                    "request failed https://account.blob.core.windows.net/container?sig=secret"),
                TimeSpan.FromSeconds(2));

            var path = Assert.Single(Directory.GetFiles(directory.FullName, "*.log"));
            var content = await File.ReadAllTextAsync(path);
            Assert.Contains("START azcopy.exe copy", content);
            Assert.Contains("END exit=1", content);
            Assert.Contains("upload summary", content);
            Assert.Contains("request failed", content);
            Assert.DoesNotContain("secret", content);
            Assert.Contains("<redacted>", content);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
