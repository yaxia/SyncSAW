using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using SyncSAW.Core;

namespace SyncSAW.App;

public partial class App : System.Windows.Application
{
    private SingleInstanceCoordinator? singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User?.Value
            ?? $"{Environment.UserDomainName}\\{Environment.UserName}";
        singleInstance = new SingleInstanceCoordinator($"SyncSAW.App|{userSid}");
        var result = singleInstance.Start(
            RequestForegroundActivation,
            processId => NativeMethods.AllowSetForegroundWindow(processId));
        if (result != SingleInstanceStartResult.Primary)
        {
            singleInstance.Dispose();
            singleInstance = null;
            Shutdown(result == SingleInstanceStartResult.ActivationSent ? 0 : 1);
            return;
        }

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        singleInstance?.Dispose();
        singleInstance = null;
        base.OnExit(e);
    }

    private void RequestForegroundActivation()
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is MainWindow window)
            {
                window.BringToForeground();
            }
        });
    }

    internal static class NativeMethods
    {
        internal const int RestoreWindow = 9;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr windowHandle, int command);
    }
}
