using System.IO.Compression;
using System.IO;
using System.Text.Json;

namespace MinecraftProjectLauncher.Services;

public static class InstanceBackupService
{
    private static readonly string[] BackupFolders = ["mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks", "kubejs", "scripts", "profiles"];
    private static readonly string[] BackupFiles = ["options.txt", "modrinth-pack.json", "modpack-profiles.json"];
    private static string BackupRoot(string version, string? instancePath = null)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MinecraftProjectLauncher", "backups", version);
        if (string.IsNullOrWhiteSpace(instancePath) || string.Equals(Path.GetFullPath(instancePath), Path.GetFullPath(GameInstanceManager.GetInstancePath(version)), StringComparison.OrdinalIgnoreCase)) return root;
        return Path.Combine(root, Path.GetFileName(Path.GetFullPath(instancePath)));
    }

    public static string Create(string version, string reason = "manual", string? instancePath = null)
    {
        var instance = instancePath ?? GameInstanceManager.GetInstancePath(version);
        var root = BackupRoot(version, instancePath);
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{reason}.zip");
        using (var archive = ZipFile.Open(target, ZipArchiveMode.Create))
        {
            foreach (var folder in BackupFolders)
            {
                var source = Path.Combine(instance, folder);
                if (!Directory.Exists(source)) continue;
                foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                    archive.CreateEntryFromFile(file, Path.GetRelativePath(instance, file).Replace('\\', '/'), CompressionLevel.Optimal);
            }
            foreach (var file in BackupFiles)
            {
                var source = Path.Combine(instance, file);
                if (File.Exists(source)) archive.CreateEntryFromFile(source, file, CompressionLevel.Optimal);
            }
        }
        foreach (var old in Directory.GetFiles(root, "*.zip").OrderByDescending(File.GetLastWriteTimeUtc).Skip(7)) File.Delete(old);
        return target;
    }

    public static string? CreateAutomaticIfDue(string version, string? instancePath = null)
    {
        var instance = instancePath ?? GameInstanceManager.GetInstancePath(version);
        var hasUserFiles = BackupFolders.Any(folder => Directory.Exists(Path.Combine(instance, folder)) && Directory.EnumerateFiles(Path.Combine(instance, folder), "*", SearchOption.AllDirectories).Any())
            || BackupFiles.Any(file => File.Exists(Path.Combine(instance, file)));
        if (!hasUserFiles) return null;
        var root = BackupRoot(version, instancePath);
        Directory.CreateDirectory(root);
        var latest = Directory.GetFiles(root, "*.zip").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        return latest is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(latest) < TimeSpan.FromHours(24)
            ? null
            : Create(version, "automatico");
    }

    public static void Restore(string version, string archivePath, string? instancePath = null)
    {
        var instance = instancePath ?? GameInstanceManager.EnsureInstance(version);
        using var archive = ZipFile.OpenRead(archivePath);
        long totalBytes = 0;
        var allowed = new HashSet<string>(BackupFolders.Concat(BackupFiles), StringComparer.OrdinalIgnoreCase);
        var destinations = new List<(ZipArchiveEntry Entry, string Path)>();
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (destinations.Count >= 10_000 || (totalBytes += entry.Length) > 2_000_000_000) throw new InvalidDataException("O backup excede os limites permitidos.");
            var relative = entry.FullName.Replace('\\', '/');
            var first = relative.Split('/')[0];
            if (!allowed.Contains(first) || relative.Contains("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)) throw new InvalidDataException("O backup contém um caminho de arquivo inválido.");
            var destination = Path.GetFullPath(Path.Combine(instance, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(Path.GetFullPath(instance) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("O backup contém um caminho inválido.");
            destinations.Add((entry, destination));
        }
        foreach (var folder in BackupFolders)
        {
            var path = Path.Combine(instance, folder);
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        foreach (var file in BackupFiles)
        {
            var path = Path.Combine(instance, file);
            if (File.Exists(path)) File.Delete(path);
        }
        foreach (var (entry, destination) in destinations)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, true);
        }
    }
}

public static class CrashDiagnosticService
{
    public static string Diagnose(string version, int exitCode, string? exception = null)
    {
        if (!string.IsNullOrWhiteSpace(exception)) return Classify(exception);
        var instance = GameInstanceManager.GetInstancePath(version);
        var crash = Directory.Exists(Path.Combine(instance, "crash-reports"))
            ? Directory.GetFiles(Path.Combine(instance, "crash-reports"), "*.txt").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        var log = crash ?? Path.Combine(instance, "logs", "latest.log");
        var content = "";
        try { if (File.Exists(log)) content = File.ReadAllText(log); } catch { }
        if (content.Length > 250_000) content = content[^250_000..];
        var diagnosis = Classify(content);
        return $"{diagnosis} (código de saída {exitCode}; log: {log})";
    }

    private static string Classify(string text)
    {
        if (text.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase) || text.Contains("Could not reserve enough space", StringComparison.OrdinalIgnoreCase)) return "Memória insuficiente. Reduza os mods ou ajuste a RAM nas configurações.";
        if (text.Contains("UnsupportedClassVersionError", StringComparison.OrdinalIgnoreCase) || text.Contains("class file version", StringComparison.OrdinalIgnoreCase)) return "Java incompatível com esta versão. Use Java automático ou escolha uma versão compatível.";
        if (text.Contains("NoClassDefFoundError", StringComparison.OrdinalIgnoreCase) || text.Contains("ModResolutionException", StringComparison.OrdinalIgnoreCase) || text.Contains("requires version", StringComparison.OrdinalIgnoreCase)) return "Possível dependência ausente ou mod incompatível. Revise os mods e suas versões.";
        if (text.Contains("MixinApplyError", StringComparison.OrdinalIgnoreCase) || text.Contains("Mixin apply failed", StringComparison.OrdinalIgnoreCase)) return "Conflito entre mods detectado. Remova ou atualize os mods citados no relatório de falha.";
        if (text.Contains("Invalid maximum heap size", StringComparison.OrdinalIgnoreCase) || text.Contains("Unrecognized VM option", StringComparison.OrdinalIgnoreCase)) return "Argumento Java inválido. Revise os argumentos avançados nas configurações.";
        if (text.Contains("Failed to download", StringComparison.OrdinalIgnoreCase) || text.Contains("Connection timed out", StringComparison.OrdinalIgnoreCase)) return "Falha de rede durante o download. Confira a conexão e tente novamente.";
        return "O Minecraft encerrou com erro. Consulte o arquivo mais recente em crash-reports ou logs da instância.";
    }
}
