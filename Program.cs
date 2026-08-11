namespace AppLockerOverlay;

static class Program
{
    private const string HubMutexName = @"Local\Windlock.Hub.SingleInstance.v1";

    /// <summary>
    /// Registers restart metadata with the OS (helps recovery scenarios; Task Manager "End task" may still terminate the process).
    /// </summary>
    private static void TryRegisterApplicationRestartRecovery()
    {
        try
        {
            var hr = NativeMethods.RegisterApplicationRestart(
                null,
                NativeMethods.RestartNoCrash | NativeMethods.RestartNoHang);
            if (hr != 0)
            {
                AppLogger.Log($"RegisterApplicationRestart returned hr=0x{hr:X8}.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException("RegisterApplicationRestart failed.", ex);
        }
    }

    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        if (WatchdogRelaunch.TryParseWatchdogArguments(
                args,
                out var parentPid,
                out var suicideReboot,
                out var suicideDelaySec,
                out var hubExe))
        {
            WatchdogRelaunch.RunParentWatchdog(parentPid, suicideReboot, suicideDelaySec, hubExe);
            return;
        }

        // Spam End-task + helper relaunch can start a second hub while the first is still dying.
        // Extra hubs stack overlays and fight over the master-password dialog.
        using var hubMutex = new Mutex(initiallyOwned: false, name: HubMutexName, createdNew: out _);
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = hubMutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // Previous hub was force-killed; the mutex is ours now.
                ownsMutex = true;
                AppLogger.Log("Took over abandoned hub mutex after a previous force-kill.");
            }

            if (!ownsMutex)
            {
                AppLogger.Log("Another Windlock hub is already running; exiting this instance.");
                return;
            }

            WatchdogRelaunch.StartedAfterAbruptExit = WatchdogRelaunch.ArgumentsRequestRestoredStart(args);
            AppLogger.Log(
                WatchdogRelaunch.StartedAfterAbruptExit
                    ? "Application starting after an abrupt shutdown (restored by the watchdog helper)."
                    : "Application starting.");
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, threadArgs) =>
            {
                AppLogger.LogException("UI thread exception", threadArgs.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, domainArgs) =>
            {
                if (domainArgs.ExceptionObject is Exception ex)
                {
                    AppLogger.LogException("Unhandled domain exception", ex);
                }
                else
                {
                    AppLogger.Log($"Unhandled domain exception: {domainArgs.ExceptionObject}");
                }
            };
            TaskScheduler.UnobservedTaskException += (_, taskArgs) =>
            {
                AppLogger.LogException("Unobserved task exception", taskArgs.Exception);
                taskArgs.SetObserved();
            };

            ApplicationConfiguration.Initialize();
            TryRegisterApplicationRestartRecovery();
            Application.Run(new Form1());
            AppLogger.Log("Application exiting normally.");
        }
        finally
        {
            if (ownsMutex)
            {
                try
                {
                    hubMutex.ReleaseMutex();
                }
                catch
                {
                    // ignored
                }
            }
        }
    }
}
