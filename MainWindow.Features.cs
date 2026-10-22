using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MinecraftProjectLauncher.Services;
using CmlLib.Core.Auth.Microsoft;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace MinecraftProjectLauncher;

public partial class MainWindow
{
    private static readonly HttpClient FeatureHttpClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    private bool CreatePreChangeBackup(string version, string? instancePath = null)
    {
        var instance = instancePath ?? GameInstanceManager.GetInstancePath(version);
        if (!Directory.Exists(instance)) return true;
        try { InstanceBackupService.Create(version, "antes-alteracao", instancePath); return true; }
        catch (Exception ex)
        {
            MessageBox.Show($"Não foi possível criar o backup automático antes desta alteração. A operação foi cancelada para proteger seus arquivos.\n\n{ex.Message}", "Backup preventivo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private async Task<bool> ConfirmDownloadSpaceAsync(string version, ModrinthInstallEstimate estimate)
    {
        var instance = GameInstanceManager.GetInstancePath(version);
        var driveRoot = Path.GetPathRoot(instance);
        var available = !string.IsNullOrWhiteSpace(driveRoot) ? new DriveInfo(driveRoot).AvailableFreeSpace : 0;
        static string FormatSize(long bytes) => bytes < 1_000_000_000
            ? $"{bytes / 1_000_000d:0.0} MB"
            : $"{bytes / 1_000_000_000d:0.00} GB";
        var estimateText = estimate.Bytes > 0 ? FormatSize(estimate.Bytes) : "tamanho não informado pelo Modrinth";
        var spaceText = available > 0 ? FormatSize(available) : "não foi possível consultar";
        if (estimate.Bytes > 0 && available > 0 && estimate.Bytes > available)
        {
            MessageBox.Show($"O download estimado ({estimateText}) é maior que o espaço livre nesta unidade ({spaceText}). Libere espaço e tente novamente.", "Espaço insuficiente", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var answer = MessageBox.Show($"Download estimado: {estimateText}\nArquivos previstos: {estimate.Files}\nEspaço livre na unidade: {spaceText}\n\nA instalação também pode exigir espaço temporário para descompactação. Continuar?",
            "Espaço e download", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
        await Task.CompletedTask;
        return answer == MessageBoxResult.Yes;
    }

    private void OpenLatestSessionLog_Click(object sender, RoutedEventArgs e)
    {
        var session = LauncherExtrasStore.LoadSessions().OrderByDescending(item => item.StartedAt).FirstOrDefault();
        if (session is null)
        {
            MessageBox.Show("Ainda não há sessões registradas.", "Log da sessão", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var instance = GameInstanceManager.GetInstancePath(session.Version);
        var crash = Directory.Exists(Path.Combine(instance, "crash-reports"))
            ? Directory.EnumerateFiles(Path.Combine(instance, "crash-reports"), "*.txt").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        var log = crash ?? Path.Combine(instance, "logs", "latest.log");
        if (!File.Exists(log))
        {
            MessageBox.Show($"Ainda não foi gerado um log para Minecraft {session.Version}. A pasta da instância será aberta.", "Log da sessão", MessageBoxButton.OK, MessageBoxImage.Information);
            OpenFolder(instance);
            return;
        }
        Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
    }

    private void InstanceActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is not ContextMenu menu) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ReturnHome_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(HomePage, HomeNavButton);
        RefreshHomeDashboard();
        VersionSelector.Focus();
    }

    private void OpenModsLibrary_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(ModrinthPage, ModsNavButton);
        LibraryTitle.Text = "Biblioteca";
        LibrarySubtitle.Text = "Explore mods e resource packs do Modrinth e organize suas versões.";
        LibraryTabs.Visibility = Visibility.Visible;
        LibraryInstancesView.Visibility = Visibility.Collapsed;
        LibraryTabs.SelectedItem = ExploreModsTab;
    }

    private string? AskForProfileName(string title, string initialValue = "", string fieldLabel = "Nome do perfil")
    {
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            MinHeight = 190,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("PanelBrush"),
            Foreground = (Brush)FindResource("TextBrush")
        };
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = fieldLabel, Margin = new Thickness(0, 0, 0, 7) });
        var nameBox = new TextBox { Text = initialValue, MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
        content.Children.Add(nameBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancelar", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0), Background = (Brush)FindResource("SecondaryButtonBrush") };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var save = new Button { Content = "Continuar", Padding = new Thickness(12, 7, 12, 7), Background = (Brush)FindResource("AccentBrush"), Foreground = Brushes.Black };
        save.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Loaded += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
        return dialog.ShowDialog() == true ? nameBox.Text.Trim() : null;
    }

    private void PromptCreateModpackProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = AskForProfileName("Criar perfil");
        if (string.IsNullOrWhiteSpace(name)) return;
        ProfileNameBox.Text = name;
        CreateModpackProfile_Click(sender, e);
    }

    private void PromptRenameModpackProfile_Click(object sender, RoutedEventArgs e)
    {
        if (VersionSelector.SelectedItem is not string version || ProfileSelector.SelectedValue is not string profileId) return;
        var profile = ModpackProfileStore.GetProfiles(version).FirstOrDefault(item => item.Id == profileId);
        if (profile is null) return;
        var name = AskForProfileName("Renomear perfil", profile.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        ProfileNameBox.Text = name;
        RenameModpackProfile_Click(sender, e);
    }

    private void CreateInstanceBackup_Click(object sender, RoutedEventArgs e)
    {
        if (VersionSelector.SelectedItem is not string version || !GameVersionCatalog.IsSupported(version)) return;
        try
        {
            var archivePath = InstanceBackupService.Create(version, "manual", GetCurrentInstancePath(version));
            LibraryStatusText.Text = $"Backup criado com mods e configurações: {archivePath}";
        }
        catch (Exception ex) { MessageBox.Show($"Não foi possível criar o backup.\n\n{ex.Message}", "Backup", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RestoreInstanceBackup_Click(object sender, RoutedEventArgs e)
    {
        if (VersionSelector.SelectedItem is not string version || !GameVersionCatalog.IsSupported(version)) return;
        var selectedPath = GetCurrentInstancePath(version);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MinecraftProjectLauncher", "backups", version,
            IsCustomInstance(selectedPath) ? Path.GetFileName(selectedPath) : "");
        var dialog = new OpenFileDialog { Title = $"Restaurar backup de Minecraft {version}", Filter = "Backups SAPVP-AI (*.zip)|*.zip", InitialDirectory = Directory.Exists(root) ? root : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        if (MessageBox.Show($"Restaurar este backup substituirá mods, perfis, configurações e opções da instância {version}. Será criado um backup do estado atual antes da restauração. Continuar?", "Restaurar backup", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            if (Directory.Exists(selectedPath)) InstanceBackupService.Create(version, "antes-restauracao", selectedPath);
            InstanceBackupService.Restore(version, dialog.FileName, selectedPath);
            RefreshProfileSelector(version);
            RefreshModrinthInstance();
            RefreshInstanceList();
            RefreshHomeDashboard();
            LibraryStatusText.Text = "Backup restaurado com sucesso.";
        }
        catch (Exception ex) { MessageBox.Show($"Não foi possível restaurar o backup.\n\n{ex.Message}", "Restaurar backup", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ExportModpackProfile_Click(object sender, RoutedEventArgs e)
    {
        if (VersionSelector.SelectedItem is not string version || ProfileSelector.SelectedValue is not string profileId) return;
        var profile = ModpackProfileStore.GetProfiles(version).FirstOrDefault(item => item.Id == profileId);
        if (profile is null) return;
        var dialog = new SaveFileDialog { Title = "Exportar perfil de mods", FileName = $"{profile.Name}.sapvp-profile.zip", Filter = "Perfil SAPVP-AI (*.sapvp-profile.zip)|*.sapvp-profile.zip|Arquivo ZIP (*.zip)|*.zip", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { ModpackProfileStore.Export(version, profileId, dialog.FileName); LibraryStatusText.Text = $"Perfil '{profile.Name}' exportado."; }
        catch (Exception ex) { MessageBox.Show($"Não foi possível exportar o perfil.\n\n{ex.Message}", "Exportar perfil", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ImportModpackProfile_Click(object sender, RoutedEventArgs e)
    {
        if (VersionSelector.SelectedItem is not string version || !GameVersionCatalog.IsSupported(version)) return;
        var dialog = new OpenFileDialog { Title = "Importar perfil SAPVP-AI", Filter = "Perfis SAPVP-AI (*.sapvp-profile.zip;*.zip)|*.sapvp-profile.zip;*.zip|Todos os arquivos (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = ModpackProfileStore.Import(version, dialog.FileName);
            RefreshProfileSelector(version);
            ProfileSelector.SelectedValue = imported.Id;
            RefreshInstanceList();
            LibraryStatusText.Text = $"Perfil '{imported.Name}' importado. Ative-o para usar os mods.";
        }
        catch (Exception ex) { MessageBox.Show($"O perfil não pôde ser importado.\n\n{ex.Message}", "Importar perfil", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void RefreshServiceStatus_Click(object sender, RoutedEventArgs e) => await RefreshServiceStatusesAsync();

    private async Task RefreshServiceStatusesAsync()
    {
        if (ServiceMicrosoftStatus is null) return;
        ServiceMicrosoftStatus.Text = string.IsNullOrWhiteSpace(_preferences.MicrosoftAccountName) ? "Conta não conectada" : $"Conectada: {_preferences.MicrosoftAccountName}";
        ServiceDiscordStatus.Text = Process.GetProcessesByName("Discord").Length > 0 ? "Discord Desktop aberto" : "Discord Desktop fechado";
        ServiceMinecraftStatus.Text = "Verificando…";
        ServiceNewsStatus.Text = "Verificando…";
        var minecraftTask = CheckEndpointAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
        var newsUrl = NewsFeedConfiguration.DefaultFeedUrl;
        var newsTask = string.IsNullOrWhiteSpace(newsUrl) ? Task.FromResult("Feed não configurado") : CheckEndpointAsync(newsUrl);
        ServiceMinecraftStatus.Text = await minecraftTask;
        ServiceNewsStatus.Text = await newsTask;
        ServiceStatusUpdatedAt.Text = $"Verificado em {DateTime.Now:dd/MM/yyyy 'às' HH:mm}. O estado da conta Microsoft indica apenas se há uma conta salva no launcher.";
    }

    private static async Task<string> CheckEndpointAsync(string url)
    {
        try
        {
            using var response = await FeatureHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode ? "Conectado" : $"Indisponível (HTTP {(int)response.StatusCode})";
        }
        catch (TaskCanceledException) { return "Tempo limite excedido"; }
        catch (HttpRequestException) { return "Sem conexão"; }
        catch { return "Indisponível"; }
    }

    private void DiagnoseLatestSession_Click(object sender, RoutedEventArgs e)
    {
        var lastFailure = LauncherExtrasStore.LoadSessions().FirstOrDefault(session => session.ExitCode != 0);
        if (lastFailure is null)
        {
            MessageBox.Show("Ainda não há sessões com falha para analisar.", "Diagnóstico", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var diagnosis = CrashDiagnosticService.Diagnose(lastFailure.Version, lastFailure.ExitCode);
        MessageBox.Show($"Minecraft {lastFailure.Version}\n\n{diagnosis}", "Diagnóstico da última falha", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
