using System.Text.Json;
using System.IO;
using System.Collections.Generic;

namespace MinecraftProjectLauncher.Services;

public sealed class LauncherPreferences
{
    public bool LightTheme { get; set; }
    public int MemoryMb { get; set; } = 4096;
    public int MinimumMemoryMb { get; set; } = 1024;
    public bool FullScreen { get; set; }
    public string JavaPath { get; set; } = "";
    public string ExtraJvmArguments { get; set; } = "";
    public string NewsFeedUrl { get; set; } = "";
    public bool LauncherRichPresenceEnabled { get; set; }
    public bool MinecraftRichPresenceEnabled { get; set; }
    public Dictionary<string, DateTime> ReadNewsAt { get; set; } = new();
    public string? MicrosoftAccountName { get; set; }
    public string? MicrosoftAccountUuid { get; set; }
    public List<RecentInstanceLaunch> RecentInstances { get; set; } = [];
}

public sealed record RecentInstanceLaunch(string Directory, string Version, string Name, DateTime LastLaunchedAt);

public sealed record SessionReport(string Version, string Loader, DateTime StartedAt, DateTime EndedAt, int ExitCode, string Result)
{
    public TimeSpan Duration => EndedAt - StartedAt;
    public string? Diagnosis { get; init; }
}

public sealed record NewsItem(string Title, string Message, string? Url, DateTimeOffset? PublishedAt)
{
    public string? Id { get; init; }
}

public static class LauncherExtrasStore
{
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MinecraftProjectLauncher");
    private static string PreferencesPath => Path.Combine(Root, "preferences.json");
    private static string SessionsPath => Path.Combine(Root, "sessions.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static LauncherPreferences LoadPreferences()
    {
        try { return File.Exists(PreferencesPath) ? JsonSerializer.Deserialize<LauncherPreferences>(File.ReadAllText(PreferencesPath)) ?? new() : new(); }
        catch { return new(); }
    }
    public static void SavePreferences(LauncherPreferences value)
    {
        Directory.CreateDirectory(Root); File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(value, JsonOptions));
    }
    public static List<SessionReport> LoadSessions()
    {
        try { return File.Exists(SessionsPath) ? JsonSerializer.Deserialize<List<SessionReport>>(File.ReadAllText(SessionsPath)) ?? [] : []; }
        catch { return []; }
    }
    public static void AddSession(SessionReport report)
    {
        if (report.ExitCode != 0 && string.IsNullOrWhiteSpace(report.Diagnosis))
            report = report with { Diagnosis = CrashDiagnosticService.Diagnose(report.Version, report.ExitCode) };
        var sessions = LoadSessions(); sessions.Insert(0, report); sessions = sessions.Take(200).ToList();
        Directory.CreateDirectory(Root); File.WriteAllText(SessionsPath, JsonSerializer.Serialize(sessions, JsonOptions));
    }
}
