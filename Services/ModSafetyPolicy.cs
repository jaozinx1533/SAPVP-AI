using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinecraftProjectLauncher.Services;

/// <summary>Best-effort client-side blocklist for known PvP cheat clients.</summary>
public static class ModSafetyPolicy
{
    private static readonly HashSet<string> BlockedSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "wurst", "wurst-7", "wurst-client", "meteor-client", "liquidbounce", "impact-client",
        "aristois", "future-client", "sigma-client", "inertia-client", "fdp-client", "vape-client",
        "hacker-menu", "advanced-xray", "baritone", "xray"
    };

    private static readonly HashSet<string> BlockedMetadataIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "wurst", "wurstclient", "wurst7", "meteorclient", "liquidbounce", "impactclient",
        "aristois", "futureclient", "sigmaclient", "inertiaclient", "fdpclient", "vapeclient",
        "hackermenu", "advancedxray", "baritone", "xray"
    };

    public static bool IsBlockedProject(string projectId, string slug, string title) =>
        BlockedSlugs.Contains(slug) || BlockedMetadataIds.Contains(Normalize(projectId)) ||
        BlockedMetadataIds.Contains(Normalize(slug)) || BlockedMetadataIds.Contains(Normalize(title));

    public static string? GetBlockedJarReason(string jarPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            foreach (var entry in archive.Entries.Where(entry =>
                         entry.FullName.Equals("fabric.mod.json", StringComparison.OrdinalIgnoreCase) ||
                         entry.FullName.Equals("quilt.mod.json", StringComparison.OrdinalIgnoreCase) ||
                         entry.FullName.Equals("mcmod.info", StringComparison.OrdinalIgnoreCase) ||
                         entry.FullName.Equals("META-INF/mods.toml", StringComparison.OrdinalIgnoreCase)))
            {
                if (entry.Length > 1024 * 1024) continue;
                using var reader = new StreamReader(entry.Open());
                var content = reader.ReadToEnd();
                foreach (var value in ExtractMetadataIdentifiers(entry.FullName, content))
                {
                    var normalized = Normalize(value);
                    if (BlockedMetadataIds.Contains(normalized) || BlockedMetadataIds.Any(blocked =>
                            normalized.Length > blocked.Length && normalized.StartsWith(blocked, StringComparison.OrdinalIgnoreCase)))
                        return $"{Path.GetFileName(jarPath)} identifica o mod/client bloqueado “{value}”.";
                }
            }
        }
        catch (InvalidDataException) { return null; }
        catch (IOException) { return null; }
        return null;
    }

    public static IReadOnlyList<string> FindBlockedJars(string modsDirectory)
    {
        if (!Directory.Exists(modsDirectory)) return [];
        var warnings = new List<string>();
        foreach (var path in Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly))
        {
            var reason = GetBlockedJarReason(path);
            if (reason is not null) warnings.Add(reason);
        }
        return warnings;
    }

    private static IEnumerable<string> ExtractMetadataIdentifiers(string path, string content)
    {
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            JsonDocument? document = null;
            try { document = JsonDocument.Parse(content); }
            catch (JsonException) { }
            if (document is not null)
            {
                using (document)
                {
                    foreach (var value in EnumerateJsonIdentifiers(document.RootElement)) yield return value;
                }
            }
        }
        else
        {
            foreach (Match match in Regex.Matches(content, "(?im)^\\s*(?:modId|modid|displayName)\\s*=\\s*[\\\"']([^\\\"']+)[\\\"']"))
                yield return match.Groups[1].Value;
        }
    }

    private static IEnumerable<string> EnumerateJsonIdentifiers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("modid", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("displayName", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind == JsonValueKind.String) yield return property.Value.GetString() ?? "";
                }
                foreach (var nested in EnumerateJsonIdentifiers(property.Value)) yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var nested in EnumerateJsonIdentifiers(item)) yield return nested;
        }
    }

    private static string Normalize(string value) => Regex.Replace(value ?? "", "[^a-z0-9]", "", RegexOptions.IgnoreCase).ToLowerInvariant();
}
