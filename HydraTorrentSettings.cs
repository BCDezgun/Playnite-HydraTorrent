using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.IO;

namespace HydraTorrent
{
    public class HydraTorrentSettings : ObservableObject
    {
        // === Настройки qBittorrent ===
        private string qbHost = "127.0.0.1";
        private int qbPort = 8080;
        private string qbUsername = "admin";
        private string qbPassword = "";
        private bool useQbittorrent = true;
        
        public string QBittorrentHost
        {
            get => qbHost;
            set => SetValue(ref qbHost, value);
        }

        public int QBittorrentPort
        {
            get => qbPort;
            set => SetValue(ref qbPort, value);
        }

        public string QBittorrentUsername
        {
            get => qbUsername;
            set => SetValue(ref qbUsername, value);
        }

        public string QBittorrentPassword
        {
            get => qbPassword;
            set => SetValue(ref qbPassword, value);
        }

        public bool UseQbittorrent
        {
            get => useQbittorrent;
            set => SetValue(ref useQbittorrent, value);
        }

        //public string DefaultInstallPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games");
        // Можно добавить позже: AutoStartQbittorrent, SaveMagnetToNotes и т.д.
        public string DefaultDownloadPath { get; set; } = "";
        public bool UseDefaultDownloadPath { get; set; } = false;
    }

    // ViewModel (остаётся почти без изменений)
    public class HydraTorrentSettingsViewModel : ObservableObject, ISettings
    {
        private readonly HydraTorrent plugin;
        private HydraTorrentSettings editingClone;
        private HydraTorrentSettings settings;        

        public HydraTorrentSettings Settings
        {
            get => settings;
            set
            {
                settings = value;
                OnPropertyChanged();
            }
        }

        public HydraTorrentSettingsViewModel(HydraTorrent plugin)
        {
            this.plugin = plugin;
            var saved = plugin.LoadPluginSettings<HydraTorrentSettings>();
            Settings = saved ?? new HydraTorrentSettings();
        }

        public void BeginEdit() => editingClone = Serialization.GetClone(Settings);
        public void CancelEdit() => Settings = editingClone;
        public void EndEdit() => plugin.SavePluginSettings(Settings);

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            return true;
        }        
    }
}