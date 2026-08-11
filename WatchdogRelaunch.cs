using System.Diagnostics;
using System.Text;

namespace AppLockerOverlay;

/// <summary>Optional helper process: detects abrupt hub exit and may relaunch the hub or schedule a system reboot (user opt-in).</summary>
internal static class WatchdogRelaunch
{
    internal const int DefaultSuicideDelaySeconds = 60;

    /// <summary>Passed to the hub the helper starts, so it can tell recovery apart from a normal launch.</summary>
    internal const string RestoredAfterKillArgument = "--restored";

    /// <summary>Legacy fixed helper name (cleaned up when rotating to a random name).</summary>
    internal const string LegacyHelperExecutableFileName = "wlguard.exe";

    private const string HelperNameStateFileName = "helper-name.txt";

    /// <summary>Set once at startup from the command line.</summary>
    internal static bool StartedAfterAbruptExit { get; set; }

    internal static string HelperStateDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Windlock");

    internal static bool ArgumentsRequestRestoredStart(string[]? args) =>
        args is not null && args.Any(a => a.Equals(RestoredAfterKillArgument, StringComparison.OrdinalIgnoreCase));

    internal static string CleanShutdownMarkerPath(int pid) =>
        Path.Combine(Path.GetTempPath(), $"Windlock_clean_{pid}.ok");

    internal static void WriteCleanShutdownMarker(int pid)
    {
        try
        {
            File.WriteAllText(CleanShutdownMarkerPath(pid), "1");
        }
        catch
        {
            // ignored
        }
    }

    private static string HelperNameStatePath => Path.Combine(HelperStateDirectory, HelperNameStateFileName);

    /// <summary>
    /// Ensures a randomly named helper exe exists next to the hub (or a LocalAppData bundle with dependencies).
    /// The name changes whenever <paramref name="rotateName"/> is true (hub/watcher handoff).
    /// </summary>
    internal static string? EnsureHelperExecutable(string hubExecutablePath, bool rotateName)
    {
        if (string.IsNullOrWhiteSpace(hubExecutablePath) || !File.Exists(hubExecutablePath))
        {
            return null;
        }

        if (hubExecutablePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hubDir = Path.GetDirectoryName(hubExecutablePath);
        if (string.IsNullOrWhiteSpace(hubDir))
        {
            return null;
        }

        if (rotateName)
        {
            TryDeleteTrackedHelperFiles(hubDir);
            var freshName = GenerateRandomHelperFileName();
            TrySaveHelperFileName(freshName);
            AppLogger.Log($"Watchdog: rotated helper name to {freshName}.");
        }

        var helperFileName = TryLoadHelperFileName();
        if (string.IsNullOrWhiteSpace(helperFileName))
        {
            helperFileName = GenerateRandomHelperFileName();
            TrySaveHelperFileName(helperFileName);
        }

        var besideHub = Path.Combine(hubDir, helperFileName);
        if (TryPlaceHelperBesideHub(hubExecutablePath, besideHub, helperFileName))
        {
            return besideHub;
        }

        return TryPlaceHelperBundleInLocalAppData(hubExecutablePath, hubDir, helperFileName);
    }

    /// <summary>Deletes the current helper binary and clears the saved name (clean hub exit / uninstall).</summary>
    internal static void TryCleanupHelperArtifacts(string? hubExecutablePath)
    {
        try
        {
            var hubDir = string.IsNullOrWhiteSpace(hubExecutablePath)
                ? null
                : Path.GetDirectoryName(hubExecutablePath);
            TryDeleteTrackedHelperFiles(hubDir);
            TryDeleteLegacyHelpers(hubDir);
            if (File.Exists(HelperNameStatePath))
            {
                File.Delete(HelperNameStatePath);
            }

            LockedSessionTracker.TryDeleteStateFile();
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Watchdog: helper cleanup failed.", ex);
        }
    }

    private static string GenerateRandomHelperFileName()
    {
        // Short random name — not Windlock.exe — so End task on the hub does not kill the watcher.
        Span<byte> bytes = stackalloc byte[4];
        Random.Shared.NextBytes(bytes);
        return $"w{Convert.ToHexString(bytes).ToLowerInvariant()}.exe";
    }

    private static string? TryLoadHelperFileName()
    {
        try
        {
            if (!File.Exists(HelperNameStatePath))
            {
                return null;
            }

            var name = File.ReadAllText(HelperNameStatePath).Trim();
            if (name.Length is < 5 or > 64 ||
                !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                string.Equals(name, "Windlock.exe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return name;
        }
        catch
        {
            return null;
        }
    }

    private static void TrySaveHelperFileName(string fileName)
    {
        try
        {
            Directory.CreateDirectory(HelperStateDirectory);
            File.WriteAllText(HelperNameStatePath, fileName);
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Watchdog: could not save helper file name.", ex);
        }
    }

    private static void TryDeleteTrackedHelperFiles(string? hubDir)
    {
        var name = TryLoadHelperFileName();
        if (string.IsNullOrWhiteSpace(name))
        {
            TryDeleteLegacyHelpers(hubDir);
            return;
        }

        TryDeleteFileQuiet(hubDir is null ? null : Path.Combine(hubDir, name));
        TryDeleteFileQuiet(Path.Combine(HelperStateDirectory, "guard", name));
        TryDeleteLegacyHelpers(hubDir);
    }

    private static void TryDeleteLegacyHelpers(string? hubDir)
    {
        TryDeleteFileQuiet(hubDir is null ? null : Path.Combine(hubDir, LegacyHelperExecutableFileName));
        TryDeleteFileQuiet(Path.Combine(HelperStateDirectory, LegacyHelperExecutableFileName));
        TryDeleteFileQuiet(Path.Combine(HelperStateDirectory, "guard", LegacyHelperExecutableFileName));
    }

    private static void TryDeleteFileQuiet(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Still running or locked — next rotate/startup will retry.
        }
    }

    private static bool TryPlaceHelperBesideHub(string hubExecutablePath, string helperPath, string helperFileName)
    {
        try
        {
            if (File.Exists(helperPath))
            {
                var src = new FileInfo(hubExecutablePath);
                var dst = new FileInfo(helperPath);
                // Hardlinks share length/time; a stale copy from an older build should be refreshed.
                if (src.Length == dst.Length &&
                    Math.Abs((src.LastWriteTimeUtc - dst.LastWriteTimeUtc).TotalSeconds) < 2)
                {
                    return true;
                }

                try
                {
                    File.Delete(helperPath);
                }
                catch (IOException)
                {
                    // Helper still running — keep the existing file; same folder still has the DLLs.
                    return File.Exists(helperPath);
                }
            }

            if (NativeMethods.CreateHardLink(helperPath, hubExecutablePath, nint.Zero))
            {
                AppLogger.Log($"Watchdog: helper hard-linked beside hub ({helperFileName}).");
                return true;
            }

            File.Copy(hubExecutablePath, helperPath, overwrite: false);
            AppLogger.Log($"Watchdog: helper copied beside hub ({helperFileName}).");
            return File.Exists(helperPath);
        }
        catch (UnauthorizedAccessException)
        {
            AppLogger.Log("Watchdog: cannot write helper beside the hub (permissions); trying LocalAppData bundle.");
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Watchdog: could not place helper beside hub.", ex);
            return false;
        }
    }

    /// <summary>When the hub folder is not writable, stage exe + app assemblies under LocalAppData.</summary>
    private static string? TryPlaceHelperBundleInLocalAppData(
        string hubExecutablePath,
        string hubDir,
        string helperFileName)
    {
        try
        {
            var dir = Path.Combine(HelperStateDirectory, "guard");
            Directory.CreateDirectory(dir);
            var helperPath = Path.Combine(dir, helperFileName);

            CopyIfNewer(hubExecutablePath, helperPath);
            foreach (var name in new[]
                     {
                         "Windlock.dll",
                         "Windlock.deps.json",
                         "Windlock.runtimeconfig.json",
                         "Windlock.pdb",
                         "System.Management.dll"
                     })
            {
                var src = Path.Combine(hubDir, name);
                if (File.Exists(src))
                {
                    CopyIfNewer(src, Path.Combine(dir, name));
                }
            }

            var srcRuntimes = Path.Combine(hubDir, "runtimes");
            var dstRuntimes = Path.Combine(dir, "runtimes");
            if (Directory.Exists(srcRuntimes))
            {
                CopyDirectoryIfNewer(srcRuntimes, dstRuntimes);
            }

            if (!File.Exists(Path.Combine(dir, "Windlock.dll")))
            {
                AppLogger.Log("Watchdog: LocalAppData helper bundle is missing Windlock.dll; cannot use it.");
                return null;
            }

            AppLogger.Log($"Watchdog: helper bundle ready at {dir} ({helperFileName})");
            return helperPath;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Watchdog: could not build LocalAppData helper bundle.", ex);
            return null;
        }
    }

    private static void CopyIfNewer(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return;
        }

        if (File.Exists(destination))
        {
            var src = new FileInfo(source);
            var dst = new FileInfo(destination);
            if (src.Length == dst.Length &&
                Math.Abs((src.LastWriteTimeUtc - dst.LastWriteTimeUtc).TotalSeconds) < 2)
            {
                return;
            }

            try
            {
                File.Delete(destination);
            }
            catch (IOException)
            {
                return;
            }
        }

        File.Copy(source, destination, overwrite: true);
    }

    private static void CopyDirectoryIfNewer(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            CopyIfNewer(file, Path.Combine(destDir, Path.GetFileName(file)));
        }

        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            CopyDirectoryIfNewer(sub, Path.Combine(destDir, Path.GetFileName(sub)));
        }
    }

    internal static bool TryParseWatchdogArguments(
        string[] args,
        out int parentPid,
        out bool suicideReboot,
        out int suicideDelaySeconds,
        out string? hubExecutablePath)
    {
        parentPid = 0;
        suicideReboot = false;
        suicideDelaySeconds = DefaultSuicideDelaySeconds;
        hubExecutablePath = null;
        if (args is null || args.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--watchdog-parent", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length &&
                int.TryParse(args[i + 1], out var pid))
            {
                parentPid = pid;
                i++;
            }
            else if (args[i].Equals("--hub-exe", StringComparison.OrdinalIgnoreCase) &&
                     i + 1 < args.Length)
            {
                hubExecutablePath = args[i + 1].Trim().Trim('"');
                i++;
            }
            else if (args[i].Equals("--suicide-reboot", StringComparison.OrdinalIgnoreCase))
            {
                suicideReboot = true;
            }
            else if (args[i].Equals("--suicide-delay-sec", StringComparison.OrdinalIgnoreCase) &&
                     i + 1 < args.Length &&
                     int.TryParse(args[i + 1], out var dly))
            {
                suicideDelaySeconds = dly;
                i++;
            }
        }

        return parentPid > 0;
    }

    /// <summary>Returns the same .exe the hub is running from, or null when running under <c>dotnet</c> (app relaunch disabled for dev).</summary>
    internal static string? TryGetHubRelaunchExecutablePath()
    {
        var pp = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(pp))
        {
            return null;
        }

        if (pp.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!pp.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Helper process should never relaunch itself; callers must pass --hub-exe.
        // Helpers use a random file name; only Windlock.exe is a valid hub path here.
        if (!string.Equals(Path.GetFileName(pp), "Windlock.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return pp;
    }

    internal static void TryScheduleSystemReboot(int delaySeconds)
    {
        delaySeconds = Math.Clamp(delaySeconds, 0, 31536000);
        var shutdown = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
        var when = delaySeconds == 0 ? "now" : $"{delaySeconds}s";
        var comment =
            delaySeconds == 0
                ? "Windlock: immediate restart (suicide mode). Run shutdown /a to try to cancel."
                : $"Windlock: hub was force-terminated (suicide mode, {when}). Run shutdown /a before the timer ends to try to cancel.";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = shutdown,
                Arguments = $"/r /t {delaySeconds} /f /c \"{comment}\"",
                UseShellExecute = true
            });
            AppLogger.Log($"Suicide mode: scheduled system restart (/t {delaySeconds}).");
        }
        catch (Exception ex)
        {
            try
            {
                AppLogger.LogException("Suicide mode: scheduling system reboot failed.", ex);
            }
            catch
            {
                // ignored
            }
        }
    }

    private static bool IsHubProcessAlive(string hubExe)
    {
        if (string.IsNullOrWhiteSpace(hubExe))
        {
            return false;
        }

        var baseName = Path.GetFileNameWithoutExtension(hubExe);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return false;
        }

        try
        {
            foreach (var p in Process.GetProcessesByName(baseName))
            {
                try
                {
                    using (p)
                    {
                        if (p.HasExited)
                        {
                            continue;
                        }

                        // Prefer path match; MainModule can throw for elevated/other-session processes.
                        try
                        {
                            var path = p.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(path) &&
                                string.Equals(path, hubExe, StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }
                        catch
                        {
                            // Same process name as the hub (Windlock) is enough — helpers use random names.
                            return true;
                        }
                    }
                }
                catch
                {
                    // ignored
                }
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }

    private static bool TryStartHubProcess(string hubExe)
    {
        var workDir = Path.GetDirectoryName(hubExe);
        if (string.IsNullOrWhiteSpace(workDir))
        {
            workDir = Environment.CurrentDirectory;
        }

        if (NativeMethods.TryStartDetachedProcess(
                hubExe,
                RestoredAfterKillArgument,
                workDir,
                out var pid) &&
            pid > 0)
        {
            AppLogger.Log($"Watchdog: relaunched hub process detached (PID {pid}).");
            return true;
        }

        try
        {
            using var relaunch = Process.Start(new ProcessStartInfo
            {
                FileName = hubExe,
                Arguments = RestoredAfterKillArgument,
                UseShellExecute = true,
                WorkingDirectory = workDir
            });
            AppLogger.Log("Watchdog: relaunched hub process.");
            return relaunch is not null;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Watchdog relaunch failed.", ex);
            return false;
        }
    }

    /// <summary>Blocks until <paramref name="parentPid"/> exits; reboots or relaunches the hub unless a clean-shutdown marker was written.</summary>
    internal static void RunParentWatchdog(
        int parentPid,
        bool suicideRebootOnAbruptExit,
        int suicideDelaySeconds,
        string? hubExecutablePath)
    {
        AppLogger.Log(
            $"Watchdog helper started for parent PID {parentPid} (suicide={suicideRebootOnAbruptExit}, delaySec={suicideDelaySeconds}, hub='{hubExecutablePath}').");

        try
        {
            using var p = Process.GetProcessById(parentPid);
            p.WaitForExit();
        }
        catch (ArgumentException)
        {
            // Parent already exited before we attached (slow helper start or race). Still run marker / abrupt-exit logic.
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Watchdog: could not wait on parent process.", ex);
            return;
        }

        Thread.Sleep(800);

        try
        {
            var marker = CleanShutdownMarkerPath(parentPid);
            if (File.Exists(marker))
            {
                File.Delete(marker);
                AppLogger.Log("Watchdog: clean shutdown marker found; not relaunching or rebooting.");
                return;
            }
        }
        catch
        {
            // continue to abrupt-exit handling
        }

        if (suicideRebootOnAbruptExit)
        {
            TryScheduleSystemReboot(suicideDelaySeconds);
            return;
        }

        var exe = hubExecutablePath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            exe = TryGetHubRelaunchExecutablePath();
        }

        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            AppLogger.Log("Watchdog: no hub executable path; cannot relaunch.");
            return;
        }

        // Hub may already have been restarted by another helper; do not stack instances.
        if (IsHubProcessAlive(exe))
        {
            AppLogger.Log("Watchdog: hub is already running; skip relaunch.");
            return;
        }

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            AppLogger.Log($"Watchdog: relaunch attempt {attempt}/5.");
            if (TryStartHubProcess(exe))
            {
                Thread.Sleep(1200);
                if (IsHubProcessAlive(exe))
                {
                    AppLogger.Log("Watchdog: hub process is alive after relaunch.");
                    return;
                }
            }

            Thread.Sleep(700 * attempt);
        }

        AppLogger.Log("Watchdog: hub did not stay up after relaunch attempts.");
    }

    /// <summary>Builds the command-line arguments for a helper that watches <paramref name="parentPid"/>.</summary>
    internal static string BuildHelperArguments(
        int parentPid,
        string hubExecutablePath,
        bool suicideReboot,
        int suicideDelaySeconds)
    {
        var sb = new StringBuilder();
        sb.Append("--watchdog-parent ").Append(parentPid);
        sb.Append(" --hub-exe \"").Append(hubExecutablePath).Append('"');
        if (suicideReboot)
        {
            sb.Append(" --suicide-reboot --suicide-delay-sec ").Append(suicideDelaySeconds);
        }

        return sb.ToString();
    }
}
