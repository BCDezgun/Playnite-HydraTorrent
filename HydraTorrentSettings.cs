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
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }

    /// <summary>
    /// Пороги ratio для автоудаления торрента
    /// </summary>
    public enum SeedRatioThreshold
    {
        Ratio_0_5 = 0,   // 0.5
        Ratio_1_0 = 1,   // 1.0
        Ratio_1_5 = 2,   // 1.5
        Ratio_2_0 = 3,   // 2.0
        Ratio_3_0 = 4    // 3.0
    }

    public class HydraTorrentSettings : ObservableObject
    {
        // ────────────────────────────────────────────────────────────────
        // qBittorrent
        // ────────────────────────────────────────────────────────────────
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

        // ────────────────────────────────────────────────────────────────
        // Пути загрузки
        // ────────────────────────────────────────────────────────────────
        private bool useDefaultDownloadPath = true;
        private string defaultDownloadPath = "";

        public bool UseDefaultDownloadPath { get => useDefaultDownloadPath; set => SetValue(ref useDefaultDownloadPath, value); }
        public string DefaultDownloadPath { get => defaultDownloadPath; set => SetValue(ref defaultDownloadPath, value); }

        // ────────────────────────────────────────────────────────────────
        // Источники и история поиска
        // ────────────────────────────────────────────────────────────────
        public List<SourceEntry> Sources { get; set; } = new List<SourceEntry>();
        public List<string> SearchHistory { get; set; } = new List<string>();

        // ────────────────────────────────────────────────────────────────
        // Настройки раздачи (Seeding)
        // ────────────────────────────────────────────────────────────────

        private bool keepSeedingAfterDownload = true;
        private int seedRatioThresholdIndex = (int)SeedRatioThreshold.Ratio_1_0;
        private bool autoRemoveAfterSeedRatio = true;

        /// <summary>
        /// Оставаться на раздаче после завершения загрузки.
        /// Если false - торрент сразу удаляется из qBittorrent (файлы сохраняются).
        /// </summary>
        public bool KeepSeedingAfterDownload
        {
            get => keepSeedingAfterDownload;
            set
            {
                SetValue(ref keepSeedingAfterDownload, value);
                // Если отключаем раздачу, то и автоудаление недоступно
                if (!value)
                {
                    autoRemoveAfterSeedRatio = false;
                    OnPropertyChanged(nameof(AutoRemoveAfterSeedRatio));
                }
            }
        }

        /// <summary>
        /// Индекс выбранного порога ratio (см. SeedRatioThreshold enum)
        /// </summary>
        public int SeedRatioThresholdIndex
        {
            get => seedRatioThresholdIndex;
            set => SetValue(ref seedRatioThresholdIndex, value);
        }

        /// <summary>
        /// Автоматически удалять торрент из qBittorrent при достижении порога ratio.
        /// Файлы игры при этом сохраняются.
        /// </summary>
        public bool AutoRemoveAfterSeedRatio
        {
            get => autoRemoveAfterSeedRatio;
            set
            {
                // Можно включить только если включена раздача
                if (keepSeedingAfterDownload)
                {
                    SetValue(ref autoRemoveAfterSeedRatio, value);
                }
            }
        }

        /// <summary>
        /// Возвращает числовое значение порога ratio
        /// </summary>
        public double GetSeedRatioValue()
        {
            return SeedRatioThresholdIndex switch
            {
                0 => 0.5,
                1 => 1.0,
                2 => 1.5,
                3 => 2.0,
                4 => 3.0,
                _ => 1.0
            };
        }
    }

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

        public void BeginEdit()
        {
            editingClone = Serialization.GetClone(Settings);
        }

        public void CancelEdit()
        {
            Settings = editingClone;
        }

        public void EndEdit()
        {
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