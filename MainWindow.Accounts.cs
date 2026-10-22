using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using MinecraftProjectLauncher.Services;
using Microsoft.Win32;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace MinecraftProjectLauncher;

public partial class MainWindow
{
    private string? _selectedSkinPath;

    private async void ConnectAccount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AccountStatus.Text = "Aguardando autenticação Microsoft…";
            var session = await JELoginHandlerBuilder.BuildDefault().AuthenticateInteractively();
            _preferences.MicrosoftAccountName = session.Username;
            _preferences.MicrosoftAccountUuid = session.UUID;
            LauncherExtrasStore.SavePreferences(_preferences);
            RefreshMicrosoftAccounts(session.UUID);
            AccountStatus.Text = $"Conta adicionada: {session.Username}";
            SkinStatus.Text = "Conta conectada. Selecione uma skin ou atualize a lista de capas.";
        }
        catch (Exception ex)
        {
            AccountStatus.Text = "Não foi possível conectar a conta.";
            MessageBox.Show(ex.Message, "Conta Microsoft", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (HomeAccountSelector.SelectedItem is not MicrosoftAccountChoice selected)
        {
            AccountStatus.Text = "Selecione uma conta salva para removê-la.";
            return;
        }
        if (MessageBox.Show($"Remover '{selected.Username}' deste launcher? Os arquivos, instâncias e configurações ficam no computador.", "Remover conta", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            await JELoginHandlerBuilder.BuildDefault().Signout(selected.Account);
            _preferences.MicrosoftAccountUuid = null;
            _preferences.MicrosoftAccountName = null;
            RefreshMicrosoftAccounts();
            SkinPreviewImage.Source = null;
            CapePreviewImage.Source = null;
            SkinPreviewLabel.Text = "Nenhuma conta selecionada";
            CapePreviewLabel.Text = "Nenhuma conta selecionada";
            OwnedCapeBox.ItemsSource = null;
            SkinStatus.Text = "Selecione uma conta para gerenciar skin e capa.";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Remover conta", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async Task<string?> GetSelectedMinecraftAccessTokenAsync()
    {
        if (HomeAccountSelector.SelectedItem is not MicrosoftAccountChoice selected)
        {
            MessageBox.Show("Adicione e selecione uma conta Microsoft com Minecraft Java.", "Conta necessária", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        var handler = JELoginHandlerBuilder.BuildDefault();
        var account = handler.AccountManager.GetAccounts().OfType<JEGameAccount>()
            .FirstOrDefault(item => string.Equals(item.Profile?.UUID, selected.Uuid, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            MessageBox.Show("A conta selecionada não está mais salva. Adicione-a novamente.", "Conta não encontrada", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshMicrosoftAccounts();
            return null;
        }
        var session = await handler.Authenticate(account);
        _preferences.MicrosoftAccountUuid = session.UUID;
        _preferences.MicrosoftAccountName = session.Username;
        LauncherExtrasStore.SavePreferences(_preferences);
        return session.AccessToken;
    }

    private void ChooseSkin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Escolher skin do Minecraft", Filter = "Imagem PNG (*.png)|*.png", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        _selectedSkinPath = dialog.FileName;
        SkinStatus.Text = $"Skin escolhida: {Path.GetFileName(_selectedSkinPath)}. Selecione o modelo e envie.";
    }

    private async void UploadSkin_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedSkinPath) || !File.Exists(_selectedSkinPath))
        {
            ChooseSkin_Click(sender, e);
            if (string.IsNullOrWhiteSpace(_selectedSkinPath) || !File.Exists(_selectedSkinPath)) return;
        }
        try
        {
            var accessToken = await GetSelectedMinecraftAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(accessToken)) return;
            SkinStatus.Text = "Enviando skin para o perfil Minecraft…";
            using var image = File.OpenRead(_selectedSkinPath);
            using var multipart = new MultipartFormDataContent();
            var model = (SkinModelBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "classic";
            multipart.Add(new StringContent(model), "variant");
            var fileContent = new StreamContent(image);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            multipart.Add(fileContent, "file", Path.GetFileName(_selectedSkinPath));
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.minecraftservices.com/minecraft/profile/skins") { Content = multipart };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await FeatureHttpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(FormatMinecraftApiError(response.StatusCode, body));
            await RefreshAppearanceAsync(showErrors: false);
            SkinStatus.Text = "Skin atualizada. Pode levar alguns minutos para aparecer no jogo.";
        }
        catch (Exception ex) { SkinStatus.Text = "Não foi possível atualizar a skin."; MessageBox.Show(ex.Message, "Skin do Minecraft", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void RefreshCapes_Click(object sender, RoutedEventArgs e) => await RefreshAppearanceAsync(showErrors: true);

    private async Task RefreshAppearanceAsync(bool showErrors)
    {
        try
        {
            if (HomeAccountSelector.SelectedItem is not MicrosoftAccountChoice account)
            {
                SkinPreviewImage.Source = null;
                CapePreviewImage.Source = null;
                SkinPreviewLabel.Text = "Nenhuma conta selecionada";
                CapePreviewLabel.Text = "Nenhuma conta selecionada";
                return;
            }
            var accessToken = await GetSelectedMinecraftAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(accessToken)) return;
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await FeatureHttpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(FormatMinecraftApiError(response.StatusCode, body));
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("skins", out var skins) && skins.ValueKind == JsonValueKind.Array)
            {
                var skin = skins.EnumerateArray().FirstOrDefault(item => item.TryGetProperty("state", out var state) && state.GetString() == "ACTIVE");
                if (skin.ValueKind == JsonValueKind.Undefined) skin = skins.EnumerateArray().FirstOrDefault();
                var skinUrl = skin.ValueKind == JsonValueKind.Object && skin.TryGetProperty("url", out var url) ? url.GetString() : null;
                var skinVariant = skin.ValueKind == JsonValueKind.Object && skin.TryGetProperty("variant", out var variant) ? variant.GetString() : null;
                SkinPreviewImage.Source = await LoadMinecraftTextureAsync($"https://crafatar.com/renders/body/{account.Uuid}?overlay&scale=4")
                    ?? await LoadMinecraftTextureAsync(skinUrl);
                SkinPreviewLabel.Text = SkinPreviewImage.Source is null
                    ? "Não foi possível carregar a imagem da skin"
                    : string.IsNullOrWhiteSpace(skinUrl) ? "Skin padrão" : $"{account.Username} · {(skinVariant == "SLIM" ? "Slim" : "Clássica")}";
            }
            else
            {
                SkinPreviewImage.Source = null;
                SkinPreviewLabel.Text = "Skin não informada pelo perfil";
            }
            var capes = document.RootElement.TryGetProperty("capes", out var capeArray) && capeArray.ValueKind == JsonValueKind.Array
                ? capeArray.EnumerateArray().Select(cape => new OwnedCape(
                    cape.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    cape.TryGetProperty("alias", out var alias) ? alias.GetString() ?? "Capa do Minecraft" : "Capa do Minecraft",
                    cape.TryGetProperty("state", out var state) && state.GetString() == "ACTIVE",
                    cape.TryGetProperty("url", out var imageUrl) ? imageUrl.GetString() : null))
                    .Where(cape => !string.IsNullOrWhiteSpace(cape.Id)).ToList()
                : [];
            OwnedCapeBox.ItemsSource = capes;
            OwnedCapeBox.SelectedItem = capes.FirstOrDefault(cape => cape.Active) ?? capes.FirstOrDefault();
            var activeCape = capes.FirstOrDefault(cape => cape.Active);
            CapePreviewImage.Source = activeCape is null
                ? null
                : await LoadMinecraftTextureAsync($"https://crafatar.com/capes/{account.Uuid}")
                    ?? await LoadMinecraftTextureAsync(activeCape.ImageUrl);
            CapePreviewLabel.Text = activeCape is null ? "Sem capa equipada"
                : CapePreviewImage.Source is null ? "Não foi possível carregar a imagem da capa" : activeCape.Name;
            SkinStatus.Text = capes.Count == 0 ? "Esta conta não possui capas equipáveis." : $"Visualização atualizada · {capes.Count} capa(s) disponível(is).";
        }
        catch (Exception ex)
        {
            SkinStatus.Text = $"Não foi possível atualizar a aparência: {ex.Message}";
            if (showErrors) MessageBox.Show(ex.Message, "Aparência do Minecraft", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static async Task<BitmapImage?> LoadMinecraftTextureAsync(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Host != "textures.minecraft.net" && uri.Host != "crafatar.com")) return null;
        // Minecraft's profile API still returns texture URLs with http://. Fetch the same
        // trusted texture host over HTTPS instead of rejecting the URL and showing nothing.
        if (uri.Host == "textures.minecraft.net")
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        else if (uri.Scheme != Uri.UriSchemeHttps) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("SAPVP-AI/1.0");
            request.Headers.Accept.ParseAdd("image/png,image/*;q=0.9");
            using var response = await AppearanceHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 2_000_000) return null;
            if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length > 2_000_000) return null;
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.DecodePixelWidth = 256;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
        catch (Exception) { return null; }
    }

    private static readonly HttpClient AppearanceHttpClient = new() { Timeout = TimeSpan.FromSeconds(25) };

    private sealed record OwnedCape(string Id, string Name, bool Active, string? ImageUrl)
    {
        public string Label => Active ? $"{Name} · equipada" : Name;
    }

    private async void EquipCape_Click(object sender, RoutedEventArgs e)
    {
        if (OwnedCapeBox.SelectedItem is not OwnedCape cape)
        {
            MessageBox.Show("Atualize a lista e escolha uma capa que pertença à conta.", "Capa", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await ChangeCapeAsync(cape.Id);
    }

    private async void HideCape_Click(object sender, RoutedEventArgs e) => await ChangeCapeAsync(null);

    private async Task ChangeCapeAsync(string? capeId)
    {
        try
        {
            var accessToken = await GetSelectedMinecraftAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(accessToken)) return;
            using var request = new HttpRequestMessage(capeId is null ? HttpMethod.Delete : HttpMethod.Put, "https://api.minecraftservices.com/minecraft/profile/capes/active");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            if (capeId is not null) request.Content = new StringContent(JsonSerializer.Serialize(new { capeId }), Encoding.UTF8, "application/json");
            using var response = await FeatureHttpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(FormatMinecraftApiError(response.StatusCode, body));
            await RefreshAppearanceAsync(showErrors: false);
            SkinStatus.Text = capeId is null ? "Capa ocultada do perfil." : "Capa equipada no perfil.";
        }
        catch (Exception ex) { SkinStatus.Text = "Não foi possível alterar a capa."; MessageBox.Show(ex.Message, "Capas do Minecraft", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static string FormatMinecraftApiError(System.Net.HttpStatusCode status, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var message = document.RootElement.TryGetProperty("errorMessage", out var error) ? error.GetString() : null;
            return string.IsNullOrWhiteSpace(message) ? $"O serviço do Minecraft respondeu HTTP {(int)status}." : message;
        }
        catch { return $"O serviço do Minecraft respondeu HTTP {(int)status}."; }
    }
}
