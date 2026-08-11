using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AppLockerOverlay;

/// <summary>
/// Minimal copy of the data needed to keep locks running: rules, their password hashes, and the master hash.
/// Written beside the settings file and protected with Windows DPAPI for the current user, so a hub that was
/// force-killed can restart and re-apply locks immediately without asking anyone for the master password.
/// </summary>
internal sealed class EnforcementManifest
{
    public int SchemaVersion { get; set; } = 1;

    public bool ProtectionEnabled { get; set; }

    /// <summary>SHA-256 hex of the master password, so unlock prompts still work in enforcement-only mode.</summary>
    public string MasterPasswordHash { get; set; } = string.Empty;

    public List<PersistedLockRule> LockRules { get; set; } = new();

    public bool CloseTaskManagerWhileLockRulesActive { get; set; }

    /// <summary>Blacklist Explorer folders, shells, and Run while protection is on.</summary>
    public bool BlockSystemToolsWhileProtected { get; set; }

    /// <summary>Keep unlock dialogs above other windows (same as hub setting).</summary>
    public bool LockDialogsStayOnTop { get; set; } = true;

    public bool HasEnforceableRules => ProtectionEnabled && LockRules.Count > 0;
}

/// <summary>Reads and writes <see cref="EnforcementManifest"/> as a DPAPI-protected blob next to the settings file.</summary>
internal static class EnforcementManifestStore
{
    /// <summary>Random-looking name so the file does not advertise itself.</summary>
    private const string ManifestFileName = "r6bt9wq3";

    /// <summary>Extra DPAPI entropy; keeps the blob tied to this app as well as the Windows user.</summary>
    private static readonly byte[] Entropy = "Windlock.enforcement.v1"u8.ToArray();

    internal static string ResolveManifestPath(string configFilePath)
    {
        var dir = Path.GetDirectoryName(configFilePath);
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = ConfigPathResolver.SuggestedStorageDirectory;
        }

        return Path.Combine(dir, ManifestFileName);
    }

    internal static void TrySave(string configFilePath, LockerConfig config)
    {
        var manifest = new EnforcementManifest
        {
            ProtectionEnabled = config.Enabled,
            MasterPasswordHash = config.MasterPasswordHash,
            LockRules = config.LockRules ?? new List<PersistedLockRule>(),
            CloseTaskManagerWhileLockRulesActive = config.CloseTaskManagerWhileLockRulesActive,
            BlockSystemToolsWhileProtected = config.BlockSystemToolsWhileProtected,
            LockDialogsStayOnTop = config.LockDialogsStayOnTop
        };

        try
        {
            var path = ResolveManifestPath(configFilePath);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(manifest);
            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(json),
                Entropy,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, protectedBytes);
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Could not write the enforcement manifest.", ex);
        }
    }

    internal static EnforcementManifest? TryLoad(string configFilePath)
    {
        try
        {
            var path = ResolveManifestPath(configFilePath);
            if (!File.Exists(path))
            {
                return null;
            }

            var plain = ProtectedData.Unprotect(
                File.ReadAllBytes(path),
                Entropy,
                DataProtectionScope.CurrentUser);
            var manifest = JsonSerializer.Deserialize<EnforcementManifest>(Encoding.UTF8.GetString(plain));
            if (manifest is null)
            {
                return null;
            }

            manifest.LockRules ??= new List<PersistedLockRule>();
            return manifest;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Could not read the enforcement manifest.", ex);
            return null;
        }
    }

    internal static void TryDelete(string configFilePath)
    {
        try
        {
            var path = ResolveManifestPath(configFilePath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Could not delete the enforcement manifest.", ex);
        }
    }
}
