using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace SyncSAW.Core;

public enum SingleInstanceStartResult
{
    Primary,
    ActivationSent,
    ExistingInstanceUnresponsive
}

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const byte ActivateCommand = 1;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(5);
    private readonly string mutexName;
    private readonly string pipeName;
    private readonly CancellationTokenSource lifetime = new();
    private Mutex? mutex;
    private Task? listener;
    private bool ownsMutex;
    private bool started;
    private bool disposed;

    public SingleInstanceCoordinator(string instanceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceKey);
        var identifier = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(instanceKey)))[..32];
        mutexName = OperatingSystem.IsWindows()
            ? $@"Local\SyncSAW.{identifier}"
            : $"SyncSAW.{identifier}";
        pipeName = $"SyncSAW.{identifier}";
    }

    public SingleInstanceStartResult Start(
        Action activationRequested,
        Action<int>? authorizeForeground = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(activationRequested);
        if (started)
        {
            throw new InvalidOperationException(
                "The single-instance coordinator has already been started.");
        }
        started = true;

        mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (createdNew)
        {
            ownsMutex = true;
            listener = ListenAsync(activationRequested, lifetime.Token);
            return SingleInstanceStartResult.Primary;
        }

        mutex.Dispose();
        mutex = null;
        if (SendActivation(authorizeForeground))
        {
            return SingleInstanceStartResult.ActivationSent;
        }

        mutex = new Mutex(initiallyOwned: true, mutexName, out createdNew);
        if (createdNew)
        {
            ownsMutex = true;
            listener = ListenAsync(activationRequested, lifetime.Token);
            return SingleInstanceStartResult.Primary;
        }

        mutex.Dispose();
        mutex = null;
        return SingleInstanceStartResult.ExistingInstanceUnresponsive;
    }

    private async Task ListenAsync(
        Action activationRequested,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var options = PipeOptions.Asynchronous;
                if (OperatingSystem.IsWindows())
                {
                    options |= PipeOptions.CurrentUserOnly;
                }

                using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    options);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var processId = BitConverter.GetBytes(Environment.ProcessId);
                await server.WriteAsync(processId, cancellationToken).ConfigureAwait(false);
                await server.FlushAsync(cancellationToken).ConfigureAwait(false);

                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                requestTimeout.CancelAfter(ConnectionTimeout);
                var command = new byte[1];
                var bytesRead = await server.ReadAsync(
                    command,
                    requestTimeout.Token).ConfigureAwait(false);
                if (bytesRead == 1 && command[0] == ActivateCommand)
                {
                    activationRequested();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is IOException or
                TimeoutException or
                UnauthorizedAccessException or
                OperationCanceledException)
            {
                Trace.TraceWarning(
                    $"SyncSAW single-instance activation listener failed: {exception.Message}");
                try
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private bool SendActivation(Action<int>? authorizeForeground)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.None);
            client.Connect((int)ConnectionTimeout.TotalMilliseconds);

            var processIdBytes = new byte[sizeof(int)];
            var offset = 0;
            while (offset < processIdBytes.Length)
            {
                var bytesRead = client.Read(
                    processIdBytes,
                    offset,
                    processIdBytes.Length - offset);
                if (bytesRead == 0)
                {
                    throw new EndOfStreamException(
                        "The existing SyncSAW instance closed the activation channel.");
                }
                offset += bytesRead;
            }

            var processId = BitConverter.ToInt32(processIdBytes);
            authorizeForeground?.Invoke(processId);
            client.WriteByte(ActivateCommand);
            client.Flush();
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
            TimeoutException or
            UnauthorizedAccessException)
        {
            Trace.TraceWarning(
                $"Could not notify the existing SyncSAW instance: {exception.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        lifetime.Cancel();
        try
        {
            listener?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
        }

        if (ownsMutex)
        {
            mutex?.ReleaseMutex();
            ownsMutex = false;
        }
        mutex?.Dispose();
        lifetime.Dispose();
    }
}
