using System.Text;
using System.Text.RegularExpressions;

namespace SyncSAW.Core;

public sealed partial class OperationLog
{
    private readonly string _directory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public OperationLog(string? directory = null)
    {
        _directory = directory ?? DefaultDirectory;
    }

    public static string DefaultDirectory => ApplicationDataPaths.LogDirectory;

    public async Task WriteCommandStartedAsync(
        string executable,
        IReadOnlyList<string> arguments)
    {
        await WriteAsync(
            $"START {Path.GetFileName(executable)} {FormatArguments(arguments)}");
    }

    public async Task WriteCommandCompletedAsync(
        AzCopyCommandResult result,
        TimeSpan duration)
    {
        var builder = new StringBuilder();
        builder.Append(
            $"END exit={result.ExitCode} cancelled={result.WasCancelled} durationMs={duration.TotalMilliseconds:F0}");
        AppendOutput(builder, "STDOUT", result.StandardOutput);
        AppendOutput(builder, "STDERR", result.StandardError);
        await WriteAsync(builder.ToString());
    }

    public Task WriteEventAsync(string message) => WriteAsync($"EVENT {message}");

    private async Task WriteAsync(string message)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(
            _directory,
            $"syncsaw-{DateTimeOffset.Now:yyyyMMdd}.log");
        var entry = $"{DateTimeOffset.Now:O} {Redact(message)}{Environment.NewLine}";

        await _writeGate.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(path, entry, Encoding.UTF8);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static void AppendOutput(
        StringBuilder builder,
        string label,
        string output)
    {
        if (!string.IsNullOrWhiteSpace(output))
        {
            builder.AppendLine();
            builder.Append(label);
            builder.AppendLine(":");
            builder.Append(output.Trim());
        }
    }

    private static string FormatArguments(IReadOnlyList<string> arguments) =>
        string.Join(
            " ",
            arguments.Select(argument =>
                argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument));

    private static string Redact(string text) =>
        SasUrlRegex().Replace(
            SasSignatureRegex().Replace(text, "$1<redacted>"),
            "$1?<redacted>");

    [GeneratedRegex(@"([?&]sig=)[^&\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex SasSignatureRegex();

    [GeneratedRegex(@"(https://[^\s?""']+)\?[^\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex SasUrlRegex();
}
