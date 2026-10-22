namespace MinecraftProjectLauncher;

public static class GameVersionCatalog
{
    private static readonly string[] Versions =
    [
        "1.21.4",
        "1.21.8",
        "1.21.11",
        "26.2"
    ];

    public static IReadOnlyList<string> SupportedVersions { get; } = Array.AsReadOnly(Versions);

    public static string DefaultVersion => "1.21.11";

    public static bool IsSupported(string version) =>
        SupportedVersions.Contains(version, StringComparer.Ordinal);
}
