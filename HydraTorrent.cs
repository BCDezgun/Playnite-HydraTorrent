using HydraTorrent.Models;
using HydraTorrent.Scrapers;
using HydraTorrent.Services;
using HydraTorrent.Views;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using QBittorrent.Client;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HydraTorrent
{
    public class HydraTorrent : LibraryPlugin
    {
        private ScraperService _scraperService;
        public static readonly ILogger logger = LogManager.GetLogger(); // Сделали PUBLIC
        private HydraTorrentSettingsViewModel settings { get; set; }
        public override Guid Id { get; } = Guid.Parse("c2177dc7-8179-4098-8b6c-d683ce415279");
        public override string Name => "HydraTorrent";
        public override LibraryClient Client { get; } = new HydraTorrentClient();
        private const string TorrentDataFolder = "HydraTorrents";
        private TorrentMonitor _monitor;

        // Хранилище для обмена данными между монитором и контроллером
        public static Dictionary<Guid, TorrentStatusInfo> LiveStatus = new Dictionary<Guid, TorrentStatusInfo>();

        public class TorrentStatusInfo
        {
            public string Status { get; set; }
            public double Progress { get; set; }
            public long DownloadSpeed { get; set; }
        }

        public HydraTorrent(IPlayniteAPI api) : base(api)
        {
            // 1. Сначала создаем настройки
            settings = new HydraTorrentSettingsViewModel(this);

            // 2. Теперь создаем сервис скрейпера, передавая ему объект настроек внутри вью-модели
            _scraperService = new ScraperService(settings.Settings);

            Properties = new LibraryPluginProperties { HasSettings = true };
            _monitor = new TorrentMonitor(api, this);
        }

        // ====================== ХРАНЕНИЕ ДАННЫХ ======================
        private string GetTorrentDataPath(Guid gameId)
        {
            var dataDir = Path.Combine(GetPluginUserDataPath(), TorrentDataFolder);
            Directory.CreateDirectory(dataDir);
            return Path.Combine(dataDir, $"{gameId}.json");
        }

        public TorrentResult GetHydraData(Game game)
        {
            if (game == null) return null;
            var filePath = GetTorrentDataPath(game.Id);
            if (!File.Exists(filePath)) return null;
            try
            {
                var json = File.ReadAllText(filePath);
                return JsonConvert.DeserializeObject<TorrentResult>(json);
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Ошибка загрузки данных торрента для {game.Name}");
                return null;
            }
        }

        public void SaveHydraData(Game game, TorrentResult torrent)
        {
            if (game == null || torrent == null) return;
            var filePath = GetTorrentDataPath(game.Id);
            try
            {
                var json = JsonConvert.SerializeObject(torrent, Formatting.Indented);
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex) { logger.Error(ex, "Ошибка сохранения данных торрента"); }
        }

        // ====================== УПРАВЛЕНИЕ УСТАНОВКОЙ ======================
        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId != Id) yield break;
            var torrentData = GetHydraData(args.Game);
            if (torrentData != null)
            {
                yield return new HydraInstallController(args.Game, this, torrentData);
            }
        }

        public async void InstallGame(Game game, TorrentResult torrentData)
        {
            if (game == null || torrentData == null || string.IsNullOrEmpty(torrentData.Magnet)) return;

            var qb = settings.Settings;
            string finalPath = (qb.UseDefaultDownloadPath == false || string.IsNullOrEmpty(qb.DefaultDownloadPath))
                ? ShowCustomInstallPathDialog(game.Name)
                : qb.DefaultDownloadPath;

            if (string.IsNullOrEmpty(finalPath)) return;

            try
            {
                var url = new Uri($"http://{qb.QBittorrentHost}:{qb.QBittorrentPort}");
                using (var client = new QBittorrentClient(url))
                {
                    await client.LoginAsync(qb.QBittorrentUsername, qb.QBittorrentPassword ?? "");
                    var request = new AddTorrentsRequest { Paused = false, DownloadFolder = finalPath };
                    request.TorrentUrls.Add(new Uri(torrentData.Magnet));

                    await client.AddTorrentsAsync(request);
                    await Task.Delay(2000);

                    var hash = ExtractHashFromMagnet(torrentData.Magnet);
                    if (!string.IsNullOrEmpty(hash))
                    {
                        torrentData.TorrentHash = hash;
                        SaveHydraData(game, torrentData);
                    }

                    game.IsInstalling = true;
                    PlayniteApi.Database.Games.Update(game);
                }
            }
            catch (Exception ex) { logger.Error(ex, "Ошибка qBittorrent"); }
        }

        private string ShowCustomInstallPathDialog(string gameName)
        {
            var windowView = new DownloadPathWindow();
            var window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions { ShowMaximizeButton = false, ShowMinimizeButton = false });
            window.Title = $"Установка {gameName}";
            window.Content = windowView;
            window.SizeToContent = SizeToContent.WidthAndHeight;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            return (window.ShowDialog() == true) ? windowView.SelectedPath : null;
        }

        private string ExtractHashFromMagnet(string magnet)
        {
            if (string.IsNullOrEmpty(magnet)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(magnet, @"urn:btih:([a-fA-F0-9]{40})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }

        // ====================== ОЧИЩЕННЫЙ КОНТРОЛЛЕР ======================
        private class HydraInstallController : InstallController
        {
            private readonly HydraTorrent _plugin;
            private readonly TorrentResult _torrentData;
            private System.Timers.Timer _watcherTimer;

            public HydraInstallController(Game game, HydraTorrent plugin, TorrentResult torrentData) : base(game)
            {
                _plugin = plugin;
                _torrentData = torrentData;
            }

            public override void Install(InstallActionArgs args)
            {
                _plugin.InstallGame(Game, _torrentData);

                // Таймер только для проверки завершения (раз в 5 сек вполне достаточно)
                _watcherTimer = new System.Timers.Timer(5000);
                _watcherTimer.Elapsed += (s, e) => CheckCompletion();
                _watcherTimer.Start();
            }

            private void CheckCompletion()
            {
                if (HydraTorrent.LiveStatus.TryGetValue(Game.Id, out var status))
                {
                    if (status.Progress >= 100)
                    {
                        _watcherTimer.Stop();
                        InvokeOnInstalled(new GameInstalledEventArgs());
                    }
                }
            }

            public override void Dispose()
            {
                _watcherTimer?.Stop();
                _watcherTimer?.Dispose();
                base.Dispose();
            }
        }

        // ====================== ИНТЕРФЕЙС И СОБЫТИЯ ======================

        public override IEnumerable<SidebarItem> GetSidebarItems()
        {
            yield return new SidebarItem
            {
                Title = "Hydra Hub",
                Type = SiderbarItemType.View,   // ← вот так, с опечаткой SiderbarItemType

                Icon = new TextBlock
                {
                    Text = "🐙",
                    FontSize = 22,
                    FontFamily = ResourceProvider.GetResource("FontIcoFont") as FontFamily
                                 ?? new FontFamily("Segoe UI Emoji")
                },

                Opened = () => new HydraHubView(PlayniteApi, this)
            };
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args) { _monitor.Start(); }
        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args) => new List<GameMetadata>();
        public override ISettings GetSettings(bool firstRunSettings) => settings;
        public override UserControl GetSettingsView(bool firstRunSettings) => new HydraTorrentSettingsView(settings);
        public override void Dispose() { _monitor?.Dispose(); base.Dispose(); }
        public HydraTorrentSettingsViewModel GetSettings() => settings;
        public ScraperService GetScraperService()
        {
            return _scraperService;
        }
    }
}