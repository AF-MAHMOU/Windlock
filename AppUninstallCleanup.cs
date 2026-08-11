using System.Diagnostics;
using System.Text;

namespace AppLockerOverlay;

/// <summary>Removes on-disk settings and schedules deletion of the entry executable after this process exits.</summary>
internal static class AppUninstallCleanup
{
    /// <summary>Folder containing the running .exe (not <c>dotnet.exe</c>), or null when running under <c>dotnet run</c>.</summary>
    internal static string? TryGetPublishDirectory()
    {
        var p = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(p) || p.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetDirectoryName(p);
    }

    internal static void TryUnregisterRestartMetadata()
    {
        try
        {
            var hr = NativeMethods.UnregisterApplicationRestart();
            if (hr != 0)
            {
                AppLogger.Log($"UnregisterApplicationRestart returned hr=0x{hr:X8}.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException("UnregisterApplicationRestart failed.", ex);
        }
    }

    /// <summary>Deletes the encrypted store, pointer files, log, legacy JSON, and optional empty default data folder.</summary>
    internal static string TryDeleteConfiguredData(string activeConfigPath)
    {
        var errors = new StringBuilder();
        void tryDeleteFile(string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                errors.AppendLine($"{label}: {path}");
                errors.AppendLine(ex.Message);
            }
        }

        foreach (var path in EnumerateKnownSidecarFilePaths())
        {
            tryDeleteFile(path, "Sidecar file");
        }

        tryDeleteFile(activeConfigPath, "Settings file");

        try
        {
            WatchdogRelaunch.TryCleanupHelperArtifacts(Environment.ProcessPath);
        }
        catch (Exception ex)
        {
            errors.AppendLine("Watchdog helper cleanup:");
            errors.AppendLine(ex.Message);
        }

        TryRemoveEmptyDefaultDataFolder(activeConfigPath);

        return errors.ToString().Trim();
    }

    internal static IReadOnlyList<string> EnumeratePostExitDeletionTargets()
    {
        var list = new List<string>();
        var pub = TryGetPublishDirectory();
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(pub) || string.IsNullOrWhiteSpace(exe))
        {
            return list;
        }

        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return list;
        }

        list.Add(exe);
        var baseName = Path.GetFileNameWithoutExtension(exe);
        foreach (var ext in new[] { "dll", "deps.json", "runtimeconfig.json", "pdb" })
        {
            var candidate = Path.Combine(pub, baseName + "." + ext);
            if (File.Exists(candidate))
            {
                list.Add(candidate);
            }
        }

        return list;
    }

    internal static void TrySchedulePostExitFileDeletion(IEnumerable<string> absolutePaths)
    {
        var paths = absolutePaths
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToList();
        if (paths.Count == 0)
        {
            return;
        }

        var bat = Path.Combine(Path.GetTempPath(), "Windlock_uninstall_" + Guid.NewGuid().ToString("N") + ".cmd");
        try
        {
            using (var w = new StreamWriter(bat, false, Encoding.ASCII))
            {
                w.WriteLine("@echo off");
                w.WriteLine("timeout /t 3 /nobreak >nul");
                foreach (var p in paths)
                {
                    w.WriteLine("if exist " + CmdQuote(p) + " del /f /q " + CmdQuote(p));
                }

                w.WriteLine("del /f /q \"%~f0\" >nul 2>&1");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = bat,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Could not schedule uninstall file deletion.", ex);
        }
    }

    private static string CmdQuote(string path)
    {
        return "\"" + path.Replace("\"", "\"\"") + "\"";
    }

    private static IEnumerable<string> EnumerateKnownSidecarFilePaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void addDir(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                return;
            }

            var d = Path.GetFullPath(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            set.Add(Path.Combine(d, "windlock.log"));
            set.Add(Path.Combine(d, "applocker-overlay.log"));
            set.Add(Path.Combine(d, ConfigPathResolver.NewPointerFileName));
            set.Add(Path.Combine(d, ConfigPathResolver.LegacyPointerFileName));
            set.Add(Path.Combine(d, ConfigPathResolver.ConfigFileName));
        }

        addDir(AppContext.BaseDirectory);
        addDir(TryGetPublishDirectory());
        // Do not delete *.dll / host files here — the process is still running; deferred batch removes them.
        return set;
    }

    private static void TryRemoveEmptyDefaultDataFolder(string deletedConfigPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(deletedConfigPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return;
            }

            if (Directory.EnumerateFileSystemEntries(dir).Any())
            {
                return;
            }

            var suggested = Path.GetFullPath(ConfigPathResolver.SuggestedStorageDirectory);
            if (!string.Equals(Path.GetFullPath(dir), suggested, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Directory.Delete(dir);
        }
        catch
        {
            // best effort
        }
    }
}
