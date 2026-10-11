using System.IO;
using System.Text.Json;

namespace ImpressionFEA.App.Infrastructure;

/// <summary>
/// Small per-user preferences (units, last folders used by each save / open dialog), kept in
/// %APPDATA%\ImpressionFEA\settings.json. Failures to read or write are ignored: preferences are a convenience.
/// </summary>
public static class AppSettings
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImpressionFEA", "settings.json");
    private static readonly Dictionary<string, string> Values = Load();

    public static string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;

    public static void Set(string key, string value)
    {
        if (Get(key) == value) return;
        Values[key] = value;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Values, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Last folder used for this kind of file (e.g. "video", "report", "setup"), if it still exists; else <paramref name="fallback"/>.</summary>
    public static string? Folder(string kind, string? fallback = null)
    {
        string? f = Get("folder." + kind);
        return f is not null && Directory.Exists(f) ? f : fallback;
    }

    /// <summary>Remembers the folder of a chosen file (or the folder itself) for this kind of dialog.</summary>
    public static void Remember(string kind, string? fileOrFolder)
    {
        if (string.IsNullOrWhiteSpace(fileOrFolder)) return;
        string folder = Directory.Exists(fileOrFolder) ? fileOrFolder : Path.GetDirectoryName(Path.GetFullPath(fileOrFolder)) ?? "";
        if (folder.Length > 0) Set("folder." + kind, folder);
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) is { } d) return d;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return [];
    }
}
