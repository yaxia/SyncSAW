using System.Diagnostics;
using System.Collections.Concurrent;

namespace SyncSAW.Core;

public enum AzCopyProcessMode
{
    Captured,
    Interactive
}

public interface IAzCopyRunner
{
    Task<AzCopyCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        AzCopyProcessMode mode = AzCopyProcessMode.Captured,
        IReadOnlyDictionary<string, string?>? environmentVariables = null);

    void CancelAll();
}

public sealed class AzCopyProcessRunner : IAzCopyRunner
{
    private readonly ConcurrentDictionary<int, Process> _activeProcesses = new();
    private readonly OperationLog? _operationLog;

    public AzCopyProcessRunner(OperationLog? operationLog = null)
    {
        _operationLog = operationLog;
    }

    public async Task<AzCopyCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        AzCopyProcessMode mode = AzCopyProcessMode.Captured,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var captureOutput = mode == AzCopyProcessMode.Captured;
        var stopwatch = Stopwatch.StartNew();
        if (_operationLog is not null)
        {
            await _operationLog.WriteCommandStartedAsync(executablePath, arguments);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = captureOutput,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environmentVariables is not null)
        {
            foreach (var (name, value) in environmentVariables)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(name);
                }
                else
                {
                    startInfo.Environment[name] = value;
                }
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("AzCopy failed to start.");
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (_operationLog is not null)
            {
                await _operationLog.WriteEventAsync(
                    $"START FAILED {Path.GetFileName(executablePath)}: {exception.Message}");
            }
            throw new InvalidOperationException(
                $"Unable to start '{executablePath}'. Verify the configured path or PATH environment variable.",
                exception);
        }

        _activeProcesses.TryAdd(process.Id, process);
        var stdoutTask = captureOutput
            ? process.StandardOutput.ReadToEndAsync(CancellationToken.None)
            : Task.FromResult(string.Empty);
        var stderrTask = captureOutput
            ? process.StandardError.ReadToEndAsync(CancellationToken.None)
            : Task.FromResult(string.Empty);

        try
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryTerminate(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                var cancelledResult = new AzCopyCommandResult(
                    process.ExitCode,
                    await stdoutTask.ConfigureAwait(false),
                    await stderrTask.ConfigureAwait(false),
                    WasCancelled: true);
                if (_operationLog is not null)
                {
                    await _operationLog.WriteCommandCompletedAsync(
                        cancelledResult,
                        stopwatch.Elapsed);
                }
                return cancelledResult;
            }

            var result = new AzCopyCommandResult(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
            if (_operationLog is not null)
            {
                await _operationLog.WriteCommandCompletedAsync(result, stopwatch.Elapsed);
            }
            return result;
        }
        finally
        {
            _activeProcesses.TryRemove(process.Id, out _);
        }
    }

    public void CancelAll()
    {
        foreach (var process in _activeProcesses.Values)
        {
            TryTerminate(process);
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the checks.
        }
    }
}

public static class AzCopyLocator
{
    public static string Find(string? configuredPath = null)
    {
        var candidates = new[]
        {
            configuredPath,
            Environment.GetEnvironmentVariable("AZCOPY_PATH"),
            Path.Combine(AppContext.BaseDirectory, "azcopy.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "AzCopy",
                "azcopy.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "AzCopy",
                "azcopy.exe")
        };

        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var fullPath = Path.GetFullPath(candidate!);
            if (File.Exists(fullPath) &&
                Path.GetFileNameWithoutExtension(fullPath).Equals("azcopy", StringComparison.OrdinalIgnoreCase))
            {
                return fullPath;
            }
        }

        foreach (var candidate in FindVersionedProgramFilesInstalls())
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "azcopy.exe");
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                // Ignore malformed PATH entries and continue discovery.
            }
        }

        throw new FileNotFoundException(
            "AzCopy was not found. Set an AzCopy path in Settings, set AZCOPY_PATH, or add azcopy.exe to PATH.");
    }

    private static IEnumerable<string> FindVersionedProgramFilesInstalls()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(
                     StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(
                        root,
                        "azcopy_windows_amd64_*",
                        SearchOption.TopDirectoryOnly)
                    .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                yield return Path.Combine(directory, "azcopy.exe");
            }
        }
    }
}

public static class AzureCliLocator
{
    public sealed record Command(string ExecutablePath, IReadOnlyList<string> PrefixArguments);

    public static string Find(string? configuredPath = null)
    {
        var candidates = new[]
        {
            configuredPath,
            Environment.GetEnvironmentVariable("AZURE_CLI_PATH"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft SDKs",
                "Azure",
                "CLI2",
                "wbin",
                "az.cmd")
        };

        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var fullPath = Path.GetFullPath(candidate!);
            if (File.Exists(fullPath) &&
                Path.GetFileNameWithoutExtension(fullPath).Equals("az", StringComparison.OrdinalIgnoreCase))
            {
                return fullPath;
            }
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var fileName in new[] { "az.exe", "az.cmd" })
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), fileName);
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                {
                    // Ignore malformed PATH entries and continue discovery.
                }
            }
        }

        throw new FileNotFoundException(
            "Azure CLI was not found. Install Azure CLI 2.61 or later, set AZURE_CLI_PATH, " +
            "or select device-code login if your Conditional Access policy allows it.");
    }

    public static Command ResolveCommand(string? configuredPath = null)
    {
        var commandPath = Find(configuredPath);
        if (!Path.GetExtension(commandPath).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            return new Command(commandPath, []);
        }

        var pythonPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(commandPath)!,
            "..",
            "python.exe"));
        if (!File.Exists(pythonPath))
        {
            throw new FileNotFoundException(
                $"Azure CLI's Python executable was not found beside '{commandPath}'. " +
                "Repair the Azure CLI installation or configure AzureCliPath.",
                pythonPath);
        }

        return new Command(pythonPath, ["-IBm", "azure.cli"]);
    }
}
