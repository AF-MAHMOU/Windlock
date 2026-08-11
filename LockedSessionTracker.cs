using System.Text.Json;

namespace AppLockerOverlay;

/// <summary>
/// Persists per-app lock session generations under LocalAppData (same folder as the randomized helper name).
/// Used to detect "app ended in Task Manager / restarted" and force hub-only unlock (no password overlay).
/// </summary>
internal static class LockedSessionTracker
{
    private const string StateFileName = "lock-sessions.json";

    private static readonly object Sync = new();
    private static SessionStateFile _state = new();

    private static string StatePath => Path.Combine(WatchdogRelaunch.HelperStateDirectory, StateFileName);

    internal static void Load()
    {
        lock (Sync)
        {
            try
            {
                if (!File.Exists(StatePath))
                {
                    _state = new SessionStateFile();
                    return;
                }

                var loaded = JsonSerializer.Deserialize<SessionStateFile>(File.ReadAllText(StatePath));
                _state = loaded ?? new SessionStateFile();
                _state.Sessions ??= new Dictionary<string, SessionEntry>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                AppLogger.LogException("LockedSessionTracker: load failed.", ex);
                _state = new SessionStateFile();
            }
        }
    }

    internal static void Save()
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(WatchdogRelaunch.HelperStateDirectory);
                var json = JsonSerializer.Serialize(
                    _state,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(StatePath, json);
            }
            catch (Exception ex)
            {
                AppLogger.LogException("LockedSessionTracker: save failed.", ex);
            }
        }
    }

    internal static IReadOnlyCollection<string> GetHubOnlyProcessKeys()
    {
        lock (Sync)
        {
            return _state.Sessions
                .Where(kv => kv.Value.HubOnlyUnlock)
                .Select(kv => kv.Key)
                .ToList();
        }
    }

    internal static bool IsHubOnly(string processKey)
    {
        processKey = NormalizeKey(processKey);
        if (string.IsNullOrWhiteSpace(processKey))
        {
            return false;
        }

        lock (Sync)
        {
            return _state.Sessions.TryGetValue(processKey, out var e) && e.HubOnlyUnlock;
        }
    }

    /// <summary>Marks that this app currently has a live locked session (window/PID observed). Returns true if state changed.</summary>
    internal static bool NoteLiveSession(string processKey, int rootPid)
    {
        processKey = NormalizeKey(processKey);
        if (string.IsNullOrWhiteSpace(processKey))
        {
            return false;
        }

        lock (Sync)
        {
            if (!_state.Sessions.TryGetValue(processKey, out var e))
            {
                e = new SessionEntry();
                _state.Sessions[processKey] = e;
            }

            var changed = !e.WasLive || (rootPid > 0 && e.LastRootPid != rootPid);
            e.LastRootPid = rootPid > 0 ? rootPid : e.LastRootPid;
            e.WasLive = true;
            e.LastSeenUtc = DateTime.UtcNow;
            // HubOnlyUnlock stays set across reopen so the password overlay does not return
            // until the user clears the lock from the hub.
            return changed;
        }
    }

    /// <summary>
    /// Returns true once when a previously live session has no processes left (Task Manager End task / full exit).
    /// </summary>
    internal static bool TryMarkSessionEnded(string processKey, out int restartCount, out int generation)
    {
        processKey = NormalizeKey(processKey);
        restartCount = 0;
        generation = 0;
        if (string.IsNullOrWhiteSpace(processKey))
        {
            return false;
        }

        lock (Sync)
        {
            if (!_state.Sessions.TryGetValue(processKey, out var e) || !e.WasLive)
            {
                return false;
            }

            ApplyEnded(e);
            restartCount = e.RestartCount;
            generation = e.Generation;
            return true;
        }
    }

    /// <summary>
    /// Forces hub-only unlock (no password overlay) when lock host windows disappear — even if WasLive was never persisted.
    /// </summary>
    internal static int ForceHubOnlyUnlock(string processKey)
    {
        processKey = NormalizeKey(processKey);
        if (string.IsNullOrWhiteSpace(processKey))
        {
            return 0;
        }

        lock (Sync)
        {
            if (!_state.Sessions.TryGetValue(processKey, out var e))
            {
                e = new SessionEntry();
                _state.Sessions[processKey] = e;
            }

            if (e.HubOnlyUnlock && !e.WasLive)
            {
                return e.RestartCount;
            }

            ApplyEnded(e);
            return e.RestartCount;
        }
    }

    private static void ApplyEnded(SessionEntry e)
    {
        e.WasLive = false;
        e.HubOnlyUnlock = true;
        e.RestartCount++;
        e.Generation++;
        e.EndedAtUtc = DateTime.UtcNow;
        e.LastRootPid = 0;
    }

    internal static void ClearHubOnly(string processKey)
    {
        processKey = NormalizeKey(processKey);
        if (string.IsNullOrWhiteSpace(processKey))
        {
            return;
        }

        lock (Sync)
        {
            if (!_state.Sessions.TryGetValue(processKey, out var e))
            {
                return;
            }

            e.HubOnlyUnlock = false;
            e.WasLive = false;
            e.LastRootPid = 0;
        }
    }

    internal static void ClearAll()
    {
        lock (Sync)
        {
            _state.Sessions.Clear();
        }

        Save();
    }

    internal static void TryDeleteStateFile()
    {
        lock (Sync)
        {
            _state = new SessionStateFile();
            try
            {
                if (File.Exists(StatePath))
                {
                    File.Delete(StatePath);
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    private static string NormalizeKey(string processKey)
    {
        var s = (processKey ?? string.Empty).Trim();
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^4];
        }

        return s;
    }

    private sealed class SessionStateFile
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, SessionEntry> Sessions { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SessionEntry
    {
        public int Generation { get; set; }
        public int RestartCount { get; set; }
        public int LastRootPid { get; set; }
        public bool WasLive { get; set; }
        public bool HubOnlyUnlock { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public DateTime? EndedAtUtc { get; set; }
    }
}
