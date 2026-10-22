using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftProjectLauncher.Services;

public sealed class LauncherUpdateService
{
    public const string RepositoryOwner = "jaozinx1533";
    public const string RepositoryName = "SAPVP-AI";
    public const string InstallerAssetName = "SAPVP-AI-Setup-x64.exe";

    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Version CurrentVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SAPVP-AI/1.0 (+https://github.com/jaozinx1533/SAPVP-AI)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public async Task<LauncherRelease?> GetLatestReleaseAsync()
    {
        var url = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";
        using var response = await Http.GetAsync(url);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("Ainda não há uma Release publicada neste repositório. Quando a primeira versão do launcher for publicada, ela aparecerá aqui.");
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(JsonOptions)
            ?? throw new InvalidOperationException("O GitHub retornou dados de Release inválidos.");
        if (!Version.TryParse(release.TagName.TrimStart('v', 'V'), out var version))
            throw new InvalidDataException($"A tag '{release.TagName}' não segue o formato de versão esperado (exemplo: v1.0.1).");
        var normalized = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        var installer = release.Assets.FirstOrDefault(asset => asset.Name.Equals(InstallerAssetName, StringComparison.OrdinalIgnoreCase));
        if (installer is null) throw new InvalidDataException($"A Release {release.TagName} ainda não contém o instalador {InstallerAssetName}.");
        if (!Uri.TryCreate(installer.BrowserDownloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps || !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O GitHub retornou um endereço de download inesperado.");
        if (string.IsNullOrWhiteSpace(installer.Digest) || !installer.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O instalador não contém a assinatura SHA-256 da Release; o download foi bloqueado.");
        return new LauncherRelease(normalized, release.Name ?? release.TagName, release.Body ?? string.Empty,
            installer.BrowserDownloadUrl, installer.Digest[7..], release.HtmlUrl);
    }

    public async Task<string> DownloadAndOpenInstallerAsync(LauncherRelease release)
    {
        var uri = new Uri(release.DownloadUrl);
        var updatesFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MinecraftProjectLauncher", "updates");
        Directory.CreateDirectory(updatesFolder);
        var finalPath = Path.Combine(updatesFolder, InstallerAssetName);
        var temporaryPath = finalPath + ".download";
        try
        {
            using (var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = File.Create(temporaryPath);
                await input.CopyToAsync(output);
            }

            await using (var input = File.OpenRead(temporaryPath))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(release.Sha256)))
                    throw new InvalidDataException("A verificação SHA-256 do instalador falhou. O arquivo foi descartado.");
            }

            File.Move(temporaryPath, finalPath, true);
            Process.Start(new ProcessStartInfo(finalPath) { UseShellExecute = true });
            return finalPath;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; init; } = string.Empty;
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("body")] public string? Body { get; init; }
        [JsonPropertyName("html_url")] public string HtmlUrl { get; init; } = string.Empty;
        [JsonPropertyName("draft")] public bool Draft { get; init; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }
        [JsonPropertyName("assets")] public List<GitHubReleaseAsset> Assets { get; init; } = [];
    }

    private sealed class GitHubReleaseAsset
    {
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; init; } = string.Empty;
        [JsonPropertyName("digest")] public string? Digest { get; init; }
    }
}

public sealed record LauncherRelease(Version Version, string Name, string Notes, string DownloadUrl, string Sha256, string ReleaseUrl);
