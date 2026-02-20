using Playnite.SDK;
using System.Windows;
using System.Windows.Controls;
using QBittorrent.Client;
using System;
using System.Threading.Tasks;

namespace HydraTorrent
{
    public partial class HydraTorrentSettingsView : UserControl
    {
        private readonly HydraTorrentSettingsViewModel viewModel;

        public HydraTorrentSettingsView(HydraTorrentSettingsViewModel vm)
        {
            viewModel = vm;
            InitializeComponent();
            DataContext = vm;

            // Важный момент: когда настройки открываются, PasswordBox пустой.
            // Загружаем в него сохраненный пароль вручную.
            txtPassword.Password = viewModel.Settings.QBittorrentPassword ?? "";
        }

        // 1. СОХРАНЕНИЕ ПАРОЛЯ: вызывается каждый раз при вводе символа
        private void txtPassword_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (viewModel.Settings != null)
            {
                viewModel.Settings.QBittorrentPassword = txtPassword.Password;
            }
        }

        // 2. ВЫБОР ПАПКИ ПО УМОЛЧАНИЮ: вызывается кнопкой "Обзор" в настройках
        private void BrowseDefaultPath_Click(object sender, RoutedEventArgs e)
        {
            var path = API.Instance.Dialogs.SelectFolder();
            if (!string.IsNullOrEmpty(path))
            {
                viewModel.Settings.DefaultDownloadPath = path;
                // Обновляем текст в TextBox вручную, если Binding не успел сработать
                DefaultPathText.Text = path;
            }
        }

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            var settings = viewModel.Settings;

            if (!settings.UseQbittorrent)
            {
                API.Instance.Dialogs.ShowMessage("qBittorrent отключён в настройках плагина.", "Тест подключения");
                return;
            }

            // Здесь берем пароль напрямую из PasswordBox для теста
            string password = txtPassword.Password ?? "";

            var url = new Uri($"http://{settings.QBittorrentHost}:{settings.QBittorrentPort}");
            var client = new QBittorrentClient(url);

            try
            {
                await client.LoginAsync(settings.QBittorrentUsername, password);
                var version = await client.GetApiVersionAsync();

                API.Instance.Dialogs.ShowMessage(
                    $"✅ Подключение успешно!\n\n" +
                    $"qBittorrent API v{version}\n" +
                    $"Адрес: {url}", "Успех!");
            }
            catch (Exception ex)
            {
                string errorMsg = ex.Message.Contains("403") || ex.Message.Contains("Unauthorized")
                    ? "❌ Ошибка авторизации. Проверь логин и пароль."
                    : $"❌ Ошибка: {ex.Message}";

                API.Instance.Dialogs.ShowMessage(errorMsg, "Ошибка подключения");
            }
        }
    }
}