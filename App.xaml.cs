using System.Windows;
using System.IO;
using System.Text;

namespace MinecraftProjectLauncher;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MinecraftProjectLauncher", "launcher-crash.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, $"[{DateTime.Now:O}] {e.Exception}{Environment.NewLine}{Environment.NewLine}", Encoding.UTF8);
            }
            catch { }
            MessageBox.Show($"O launcher encontrou um erro ao abrir. Um registro foi salvo em AppData\\Roaming\\MinecraftProjectLauncher\\launcher-crash.log.\n\n{e.Exception.Message}", "Erro ao abrir", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }

}
