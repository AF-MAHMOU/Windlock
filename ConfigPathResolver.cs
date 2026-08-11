namespace AppLockerOverlay;

/// <summary>
/// Resolves the settings file path: legacy JSON next to EXE, new opaque pointer, legacy pointer, or default per-user path.
/// </summary>
internal static class ConfigPathResolver
{
    public const string LegacyPointerFileName = "app-locker-data.path";
    public const string NewPointerFileName = "v4m8qx2k.dat";
    public const string ConfigFileName = "locker-config.json";

    /// <summary>Random-looking folder under LocalAppData (no product name).</summary>
    public const string DefaultDataSubdirectory = "w9kq2m7x";

    /// <summary>Random-looking filename (no extension); whole file is encrypted binary.</summary>
    public const string OpaqueStoreFileName = "p7knm4q2";

    public static string SuggestedStorageDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DefaultDataSubdirectory);

    public static string NewPointerFileFullPath =>
        Path.Combine(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), NewPointerFileName);

    public static string ResolveConfigFilePath()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var legacyBesideExe = Path.Combine(baseDir, ConfigFileName);
        if (File.Exists(legacyBesideExe))
        {
            return legacyBesideExe;
        }

        var newPointer = Path.Combine(baseDir, NewPointerFileName);
        if (File.Exists(newPointer))
        {
            var fromNew = TryReadFullPathFromPointer(newPointer);
            if (!string.IsNullOrEmpty(fromNew))
            {
                return fromNew;
            }
        }

        var legacyPointer = Path.Combine(baseDir, LegacyPointerFileName);
        if (File.Exists(legacyPointer))
        {
            try
            {
                foreach (var raw in File.ReadAllLines(legacyPointer))
                {
                    var dir = raw.Trim().Trim('"');
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    {
                        continue;
                    }

                    return Path.Combine(dir, ConfigFileName);
                }
            }
            catch
            {
                // fall through
            }
        }

        return Path.Combine(SuggestedStorageDirectory, OpaqueStoreFileName);
    }

    private static string? TryReadFullPathFromPointer(string pointerFilePath)
    {
        try
        {
            foreach (var raw in File.ReadAllLines(pointerFilePath))
            {
                var path = raw.Trim().Trim('"');
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                return path;
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }
}
