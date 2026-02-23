using Playnite.SDK;
using Playnite.SDK.Data;
using System.Collections.Generic;

namespace HydraTorrent
{
    /// <summary>
    /// Один источник (JSON-файл репаков)
    /// </summary>
    public class SourceEntry
    {
        public string Name { get; set; } = string.Empty;   // будет заполняться автоматически из JSON
        public string Url { get; set; } = string.Empty;
    }

    public class HydraTorrentSettings : ObservableObject
    {
        // ==================== qBittorrent ====================
        private string qbHost = "127.0.0.1";
        private int qbPort = 8080;
        private string qbUsername = "admin";
        private string qbPassword = "";
        private bool useQbittorrent = true;

        public string QBittorrentHost { get => qbHost; set => SetValue(ref qbHost, value); }
        public int QBittorrentPort { get => qbPort; set => SetValue(ref qbPort, value); }
        public string QBittorrentUsername { get => qbUsername; set => SetValue(ref qbUsername, value); }
        public string QBittorrentPassword { get => qbPassword; set => SetValue(ref qbPassword, value); }
        public bool UseQbittorrent { get => useQbittorrent; set => SetValue(ref useQbittorrent, value); }

        // ==================== Пути загрузки ====================
        private bool useDefaultDownloadPath = true;
        private string defaultDownloadPath = "";

        public bool UseDefaultDownloadPath { get => useDefaultDownloadPath; set => SetValue(ref useDefaultDownloadPath, value); }
        public string DefaultDownloadPath { get => defaultDownloadPath; set => SetValue(ref defaultDownloadPath, value); }

        // ==================== ИСТОЧНИКИ ====================
        // Пустой список по умолчанию — пользователь сам добавляет всё, что хочет
        public List<SourceEntry> Sources { get; set; } = new List<SourceEntry>();

        // =================== ХЭШ ПОИСКА ====================
        public List<string> SearchHistory { get; set; } = new List<string>();
    }

    // ViewModel (без изменений)
    public class HydraTorrentSettingsViewModel : ObservableObject, ISettings
    {
        private readonly HydraTorrent plugin;
        private HydraTorrentSettings editingClone;
        private HydraTorrentSettings settings;

        public HydraTorrentSettings Settings
        {
            get => settings;
            set { settings = value; OnPropertyChanged(); }
        }

        public HydraTorrentSettingsView SettingsView { get; set; }

        public HydraTorrentSettingsViewModel(HydraTorrent plugin)
        {
            this.plugin = plugin;
            var saved = plugin.LoadPluginSettings<HydraTorrentSettings>();
            Settings = saved ?? new HydraTorrentSettings();
        }

        public void BeginEdit() => editingClone = Serialization.GetClone(Settings);
        public void CancelEdit() => Settings = editingClone;
        public void EndEdit()
        {
            // === ДОБАВЬ ЭТОТ БЛОК ===
            // Перед сохранением принудительно забираем данные из UI
            SettingsView?.SaveSources();

            plugin.SavePluginSettings(Settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            return true;
        }
    }
}