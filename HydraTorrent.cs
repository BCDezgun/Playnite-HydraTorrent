using HydraTorrent.Models;
using HydraTorrent.Scrapers;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Playnite.SDK.Events;
using QBittorrent.Client;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HydraTorrent.Services;

namespace HydraTorrent
{
    public class HydraTorrent : LibraryPlugin
    {
        private ScraperService _scraperService = new ScraperService();
        private SearchWindow _currentSearchWindow;
        private static readonly ILogger logger = LogManager.GetLogger();
        private HydraTorrentSettingsViewModel settings { get; set; }

        public override Guid Id { get; } = Guid.Parse("c2177dc7-8179-4098-8b6c-d683ce415279");
        public override string Name => "HydraTorrent";
        public override LibraryClient Client { get; } = new HydraTorrentClient();

        private const string TorrentDataFolder = "HydraTorrents";

        private TorrentMonitor _monitor;

        // Наше "быстрое" хранилище: ID игры -> Объект с данными
        public static Dictionary<Guid, TorrentStatusInfo> LiveStatus = new Dictionary<Guid, TorrentStatusInfo>();

        public class TorrentStatusInfo
        {
            public string Status { get; set; }
            public double Progress { get; set; }
            public long DownloadSpeed { get; set; } // Можно добавить и скорость!
        }

        public HydraTorrent(IPlayniteAPI api) : base(api)
        {
            settings = new HydraTorrentSettingsViewModel(this);
            Properties = new LibraryPluginProperties { HasSettings = true };
            _monitor = new TorrentMonitor(api, this);
        }

        // ====================== ХРАНЕНИЕ ДАННЫХ ТОРРЕНТА ======================
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
                logger.Error(ex, $"Не удалось загрузить данные торрента для {game.Name}");
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
            catch (Exception ex)
            {
                logger.Error(ex, "Не удалось сохранить данные торрента");
            }
        }

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            // 1. Проверяем, что игру добавил именно наш плагин
            if (args.Game.PluginId != Id)
                yield break;

            // 2. Достаем из файла сохраненную магнит-ссылку
            var torrentData = GetHydraData(args.Game);

            // 3. Если данные есть, говорим Playnite использовать наш контроллер установки
            if (torrentData != null)
            {
                yield return new HydraInstallController(args.Game, this, torrentData);
            }
        }

        // ====================== РЕАЛЬНАЯ УСТАНОВКА ЧЕРЕЗ qBittorrent ======================
        public async void InstallGame(Game game, TorrentResult torrentData)
        {
            if (game == null || torrentData == null || string.IsNullOrEmpty(torrentData.Magnet))
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Не удалось найти magnet-ссылку.", "Hydra Torrent");
                return;
            }

            var qb = settings.Settings;

            // 1. ОПРЕДЕЛЯЕМ ПУТЬ УСТАНОВКИ
            string finalPath = "";

            // Принудительно проверяем: если галка НЕ стоит ИЛИ путь пустой — показываем окно
            if (qb.UseDefaultDownloadPath == false || string.IsNullOrEmpty(qb.DefaultDownloadPath))
            {
                finalPath = ShowCustomInstallPathDialog(game.Name);
            }
            else
            {
                finalPath = qb.DefaultDownloadPath;
            }

            // Если после всего этого пути нет (отмена в окне) — выходим
            if (string.IsNullOrEmpty(finalPath))
            {
                logger.Info("Установка отменена пользователем: путь не выбран.");
                return;
            }

            if (!qb.UseQbittorrent)
            {
                try { System.Diagnostics.Process.Start(torrentData.Magnet); }
                catch { PlayniteApi.Dialogs.ShowErrorMessage("Не удалось открыть magnet-ссылку.", "Ошибка"); }
                return;
            }

            try
            {
                var url = new Uri($"http://{qb.QBittorrentHost}:{qb.QBittorrentPort}");

                using (var client = new QBittorrentClient(url))
                {
                    await client.LoginAsync(qb.QBittorrentUsername, qb.QBittorrentPassword ?? "");

                    var request = new AddTorrentsRequest();
                    request.TorrentUrls.Add(new Uri(torrentData.Magnet));
                    request.Paused = false;
                    request.DownloadFolder = finalPath;

                    await client.AddTorrentsAsync(request);

                    // === НАДЁЖНОЕ ПОЛУЧЕНИЕ HASH ===
                    await Task.Delay(1200); // даём qBittorrent время обработать магнит

                    var hash = ExtractHashFromMagnet(torrentData.Magnet);

                    if (!string.IsNullOrEmpty(hash))
                    {
                        torrentData.TorrentHash = hash;
                        SaveHydraData(game, torrentData);   // сохраняем с hash
                    }

                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        "Hydra Torrent",
                        $"✅ Игра **{game.Name}** добавлена в очередь!\nПуть: {finalPath}",
                        NotificationType.Info));

                    game.IsInstalling = true;
                    PlayniteApi.Database.Games.Update(game);
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Ошибка при отправке в qBittorrent");
                PlayniteApi.Dialogs.ShowErrorMessage($"Ошибка: {ex.Message}", "Hydra Torrent");
            }
        }

        // НОВЫЙ МЕТОД ВЫЗОВА ОКНА
        private string ShowCustomInstallPathDialog(string gameName)
        {
            var windowView = new DownloadPathWindow();

            // Создаем окно средствами Playnite, чтобы оно выглядело родным
            var window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
            {
                ShowMaximizeButton = false,
                ShowMinimizeButton = false
            });

            window.Title = $"Установка {gameName}";
            window.Content = windowView;
            window.SizeToContent = SizeToContent.WidthAndHeight;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // Если в окне нажали "Окей" (DialogResult = true)
            if (window.ShowDialog() == true)
            {
                return windowView.SelectedPath;
            }

            return null;
        }

        private string ExtractHashFromMagnet(string magnet)
        {
            if (string.IsNullOrEmpty(magnet)) return null;

            var match = System.Text.RegularExpressions.Regex.Match(magnet, @"urn:btih:([a-fA-F0-9]{40})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }

        private class HydraInstallController : InstallController
        {
            private readonly HydraTorrent _plugin;
            private readonly TorrentResult _torrentData;

            public HydraInstallController(Game game, HydraTorrent plugin, TorrentResult torrentData)
                : base(game)
            {
                Name = "Установить через Hydra Torrent";
                _plugin = plugin;
                _torrentData = torrentData;
            }

            public override void Install(InstallActionArgs args)
            {
                _plugin.InstallGame(Game, _torrentData);   // Game берётся из базового класса контроллера
            }
        }

        // ====================== ТВОЙ СТАРЫЙ КОД (без изменений) ======================
        public override IEnumerable<SidebarItem> GetSidebarItems()
        {
            yield return new SidebarItem
            {
                Title = "🔍 Поиск торрентов (Hydra)",
                Type = SiderbarItemType.Button,
                Icon = new TextBlock
                {
                    Text = "🔍",
                    FontSize = 22,
                    FontFamily = ResourceProvider.GetResource("FontIcoFont") as FontFamily
                                 ?? new FontFamily("Segoe UI Emoji")
                },
                Activated = () =>
                {
                    if (_currentSearchWindow == null || !_currentSearchWindow.IsLoaded)
                    {
                        _currentSearchWindow = new SearchWindow(PlayniteApi, this);
                        _currentSearchWindow.Closed += (s, e) => _currentSearchWindow = null;
                        _currentSearchWindow.Show();
                    }
                    else
                    {
                        _currentSearchWindow.Activate();
                        _currentSearchWindow.Focus();
                    }
                }
            };
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            return new List<GameMetadata>();
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public HydraTorrentSettingsViewModel GetSettings() => settings;

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new HydraTorrentSettingsView(settings);
        }
        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            base.OnApplicationStarted(args);
            _monitor.Start();
        }

        public override void Dispose()
        {
            _monitor?.Dispose();
            base.Dispose();
        }        
    }
}