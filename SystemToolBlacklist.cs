namespace AppLockerOverlay;

/// <summary>
/// Process / window surfaces that are safe to lock while protection is on without breaking the Windows shell
/// (desktop, taskbar, Start). Used as a blacklist: unlocked apps stay usable, but these entry points do not.
/// </summary>
internal static class SystemToolBlacklist
{
    /// <summary>Synthetic lock-rule root for File Explorer folder windows + Run dialog.</summary>
    public const int ExplorerRuleRootPid = -901;

    /// <summary>Synthetic lock-rule root for Command Prompt / PowerShell / Windows Terminal.</summary>
    public const int ShellsRuleRootPid = -902;

    public static readonly HashSet<string> ShellProcessBaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd",
        "powershell",
        "pwsh",
        "windowsterminal",
        "windowsterminalpreview",
        "wt"
    };

    public static readonly HashSet<string> ExplorerFolderWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CabinetWClass",
        "ExploreWClass"
    };

    public static bool IsShellProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var baseName = processName.Trim();
        if (baseName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName[..^4];
        }

        return ShellProcessBaseNames.Contains(baseName);
    }

    public static bool IsExplorerProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var baseName = processName.Trim();
        if (baseName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName[..^4];
        }

        return baseName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsExplorerFolderWindowClass(string? className) =>
        !string.IsNullOrEmpty(className) && ExplorerFolderWindowClasses.Contains(className);

    /// <summary>Win+R Run box (English title). Hosted by explorer; must not touch desktop/tray classes.</summary>
    public static bool IsRunDialog(string? className, string? title)
    {
        if (!string.Equals(className, "#32770", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var t = title.Trim();
        return t.Equals("Run", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Ausführen", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Exécuter", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Ejecutar", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Explorer-hosted Open / Browse For Folder style dialogs (not in-app Electron pickers owned by Cursor, etc.).
    /// </summary>
    public static bool IsExplorerBrowseDialog(string? className, string? title)
    {
        if (!string.Equals(className, "#32770", StringComparison.Ordinal))
        {
            return false;
        }

        if (IsRunDialog(className, title))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var t = title.Trim();
        return t.Contains("folder", StringComparison.OrdinalIgnoreCase)
               || t.Contains("open", StringComparison.OrdinalIgnoreCase)
               || t.Contains("browse", StringComparison.OrdinalIgnoreCase)
               || t.Contains("save", StringComparison.OrdinalIgnoreCase);
    }
}
