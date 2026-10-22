using System;
using System.IO;
using System.Text.Json;

namespace MinecraftProjectLauncher.Services;

public sealed class LauncherLibraryState
{
    public HashSet<string> Favorites { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Collections { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class LauncherLibraryStore
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MinecraftProjectLauncher", "library.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static LauncherLibraryState Load()
    {
        try
        {
            if (!File.Exists(StatePath)) return new LauncherLibraryState();
            return JsonSerializer.Deserialize<LauncherLibraryState>(File.ReadAllText(StatePath), Options) ?? new LauncherLibraryState();
        }
        catch (JsonException) { return new LauncherLibraryState(); }
    }

    public static void Save(LauncherLibraryState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, Options));
        File.Move(temporary, StatePath, true);
    }
}
