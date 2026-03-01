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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HydraTorrent
{
    public class HydraTorrent : LibraryPlugin
    {
        private ScraperService _scraperService;
        public static readonly ILogger logger = LogManager.GetLogger();
        private HydraTorrentSettingsViewModel settings { get; set; }

        public override Guid Id { get; } = Guid.Parse("c2177dc7-8179-4098-8b6c-d683ce415279");
        public override string Name => "HydraTorrent";
        public override LibraryClient Client { get; } = new HydraTorrentClient();

        private const string TorrentDataFolder = "HydraTorrents";
        private TorrentMonitor _monitor;

        private CompletedManager _completedManager;
        private StatisticsManager _statisticsManager;

        public static Dictionary<Guid, TorrentStatusInfo> LiveStatus = new Dictionary<Guid, TorrentStatusInfo>();

        public CompletedManager GetCompletedManager() => _completedManager;
        public StatisticsManager GetStatisticsManager() => _statisticsManager;

        // ────────────────────────────────────────────────────────────────
        // Очередь загрузок
        // ────────────────────────────────────────────────────────────────

        private const string QueueFileName = "queue.json";
        public List<TorrentResult> DownloadQueue { get; set; } = new List<TorrentResult>();

        public class TorrentStatusInfo
        {
            public string Status { get; set; }
            public double Progress { get; set; }
            public long DownloadSpeed { get; set; }
            public long UploadSpeed { get; set; }
            public long TotalSize { get; set; }
            public long DownloadedSize { get; set; }
            public TimeSpan? ETA { get; set; }
            public int? Seeds { get; set; }
            public int? Peers { get; set; }
        }

        public HydraTorrent(IPlayniteAPI api) : base(api)
        {
            settings = new HydraTorrentSettingsViewModel(this);
            _scraperService = new ScraperService(settings.Settings);
            Properties = new LibraryPluginProperties { HasSettings = true };
            _monitor = new TorrentMonitor(api, this);
        }

        // ────────────────────────────────────────────────────────────────
        // Хранение данных торрента
        // ────────────────────────────────────────────────────────────────

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
            catch (Exception ex)
            {
                logger.Error(ex, "Ошибка сохранения данных торрента");
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Хранение очереди загрузок
        // ────────────────────────────────────────────────────────────────       

        private string GetQueueFilePath()
        {
            var dataDir = Path.Combine(GetPluginUserDataPath(), TorrentDataFolder);
            Directory.CreateDirectory(dataDir);
            return Path.Combine(dataDir, QueueFileName);
        }

        public void LoadQueue()
        {
            var filePath = GetQueueFilePath();
            if (!File.Exists(filePath))
            {
                DownloadQueue = new List<TorrentResult>();
                return;
            }

            try
            {
                var json = File.ReadAllText(filePath);
                DownloadQueue = JsonConvert.DeserializeObject<List<TorrentResult>>(json) ?? new List<TorrentResult>();
                logger.Info($"Очередь: загружено {DownloadQueue.Count} элементов");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Ошибка загрузки очереди");
                DownloadQueue = new List<TorrentResult>();
            }
        }

        public void SaveQueue()
        {
            var filePath = GetQueueFilePath();
            try
            {
                var json = JsonConvert.SerializeObject(DownloadQueue, Formatting.Indented);
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Ошибка сохранения очереди");
            }
        }

        public TorrentResult GetActiveDownload()
        {
            return DownloadQueue.FirstOrDefault(t => t.QueueStatus == "Downloading");
        }

        public async Task StartNextInQueueAsync()
        {
            // ✅ СНАЧАЛА пересчитываем позиции по текущему порядку списка!
            RecalculateQueuePositions();

            // ────────────────────────────────────────────────────────────────
            // ✅ ПРИОРИТЕТ 1: Ищем игру со статусом "Paused" (была активна)
            // ────────────────────────────────────────────────────────────────
            var pausedGame = DownloadQueue
                .FirstOrDefault(q => q.QueueStatus == "Paused");

            if (pausedGame != null && pausedGame.GameId.HasValue)
            {
                logger.Info($"Найдена игра на паузе: {pausedGame.Name} — возобновляем");

                pausedGame.QueueStatus = "Downloading";
                SaveQueue();

                // Возобновляем торрент в qBittorrent
                var qb = settings.Settings;
                var url = new Uri($"http://{qb.QBittorrentHost}:{qb.QBittorrentPort}");
                using (var client = new QBittorrentClient(url))
                {
                    await client.LoginAsync(qb.QBittorrentUsername, qb.QBittorrentPassword ?? "");
                    await client.ResumeAsync(pausedGame.TorrentHash);
                }

                // Обновляем игру в БД
                var game = PlayniteApi.Database.Games.Get(pausedGame.GameId.Value);
                if (game != null)
                {
                    game.IsInstalling = true;

                    if (string.IsNullOrEmpty(pausedGame.GameName))
                    {
                        pausedGame.GameName = game.Name;
                        SaveQueue();
                    }

                    PlayniteApi.Database.Games.Update(game);
                }

                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "HydraTorrent",
                    string.Format(ResourceProvider.GetString("LOC_HydraTorrent_ResumedDownload"), pausedGame.Name),
                    NotificationType.Info));

                if (HydraHubView.CurrentInstance != null)
                {
                    HydraHubView.CurrentInstance.RefreshQueueUI();
                }

                return; // ✅ Возвращаем — нашли игру!
            }

            // ────────────────────────────────────────────────────────────────
            // ✅ ПРИОРИТЕТ 2: Если нет paused, ищем следующую "Queued"
            // ────────────────────────────────────────────────────────────────
            var nextQueued = DownloadQueue
                .Where(q => q.QueueStatus == "Queued")
                .OrderBy(q => q.QueuePosition)
                .FirstOrDefault();

            if (nextQueued == null)
            {
                logger.Info("Очередь пуста, нечего запускать");
                return;
            }

            if (!nextQueued.GameId.HasValue)
            {
                logger.Warn($"Игра в очереди без GameId: {nextQueued.Name}");
                return;
            }

            logger.Info($"Авто-старт из очереди: {nextQueued.Name} (позиция {nextQueued.QueuePosition})");

            nextQueued.QueueStatus = "Downloading";
            RecalculateQueuePositions();
            SaveQueue();

            // Возобновляем торрент в qBittorrent
            var qb2 = settings.Settings;
            var url2 = new Uri($"http://{qb2.QBittorrentHost}:{qb2.QBittorrentPort}");
            using (var client2 = new QBittorrentClient(url2))
            {
                await client2.LoginAsync(qb2.QBittorrentUsername, qb2.QBittorrentPassword ?? "");
                await client2.ResumeAsync(nextQueued.TorrentHash);
            }

            // Обновляем игру в БД
            var game2 = PlayniteApi.Database.Games.Get(nextQueued.GameId.Value);
            if (game2 != null)
            {
                game2.IsInstalling = true;

                if (string.IsNullOrEmpty(nextQueued.GameName))
                {
                    nextQueued.GameName = game2.Name;
                    SaveQueue();
                }

                PlayniteApi.Database.Games.Update(game2);
            }

            PlayniteApi.Notifications.Add(new NotificationMessage(
                "HydraTorrent",
                string.Format(ResourceProvider.GetString("LOC_HydraTorrent_DownloadStarted"), nextQueued.Name),
                NotificationType.Info));

            if (HydraHubView.CurrentInstance != null)
            {
                HydraHubView.CurrentInstance.RefreshQueueUI();
            }
        }

        public void RecalculateQueuePositions()
        {
            int pos = 0;

            // ✅ ВАЖНО: Итерируемся по фактическому порядку в списке, НЕ сортируем!
            foreach (var item in DownloadQueue)
            {
                if (item.QueueStatus == "Downloading")
                {
                    item.QueuePosition = 0;  // Активная загрузка всегда первая
                }
                else if (item.QueueStatus == "Queued" || item.QueueStatus == "Paused")
                {
                    item.QueuePosition = ++pos;  // Остальные по порядку в списке
                }
            }

            SaveQueue();
            logger.Info($"Пересчитаны позиции очереди: {DownloadQueue.Count} элементов");
        }

        // ────────────────────────────────────────────────────────────────
        // Установка игры
        // ────────────────────────────────────────────────────────────────

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

            // ✅ ПРОВЕРКА НА ДУБЛИКАТЫ
            var existingInQueue = DownloadQueue.FirstOrDefault(q => q.GameId == game.Id);
            if (existingInQueue != null)
            {
                string statusText = existingInQueue.QueueStatus switch
                {
                    "Downloading" => ResourceProvider.GetString("LOC_HydraTorrent_DuplicateWarning_Downloading"),
                    "Queued" => string.Format(ResourceProvider.GetString("LOC_HydraTorrent_DuplicateWarning_Queued"), existingInQueue.QueuePosition),
                    "Completed" => ResourceProvider.GetString("LOC_HydraTorrent_DuplicateWarning_Completed"),
                    _ => ResourceProvider.GetString("LOC_HydraTorrent_DuplicateWarning_Generic")
                };

                PlayniteApi.Dialogs.ShowMessage(statusText, ResourceProvider.GetString("LOC_HydraTorrent_Attention"));
                logger.Warn($"Попытка добавить дубликат: {game.Name}");
                return;
            }

            var activeDownload = GetActiveDownload();
            var hash = ExtractHashFromMagnet(torrentData.Magnet);

            if (string.IsNullOrEmpty(hash))
            {
                PlayniteApi.Dialogs.ShowErrorMessage(
                    ResourceProvider.GetString("LOC_HydraTorrent_FailedExtractHash"),
                    ResourceProvider.GetString("LOC_HydraTorrent_Error"));
                return;
            }

            torrentData.TorrentHash = hash;
            var removedHashesMgr = _completedManager?.GetRemovedHashesManager();
            if (removedHashesMgr != null && removedHashesMgr.IsRemoved(hash))
            {
                removedHashesMgr.RemoveRemovedHash(hash);
                logger.Info($"Хеш очищен из RemovedHashesManager для повторной загрузки: {hash}");
            }
            torrentData.GameId = game.Id;
            torrentData.AddedToQueueAt = DateTime.Now;
            torrentData.GameName = game.Name;

            var qb = settings.Settings;
            string finalPath = (qb.UseDefaultDownloadPath == false || string.IsNullOrEmpty(qb.DefaultDownloadPath))
                ? ShowCustomInstallPathDialog(game.Name)
                : qb.DefaultDownloadPath;

            if (string.IsNullOrEmpty(finalPath))
            {
                logger.Warn($"Путь установки не выбран для: {game.Name}");
                return;
            }

            // ✅ Сохраняем путь загрузки
            torrentData.DownloadPath = finalPath;
            logger.Info($"[DEBUG] DownloadPath сохранён: {finalPath}");

            try
            {
                var url = new Uri($"http://{qb.QBittorrentHost}:{qb.QBittorrentPort}");
                using (var client = new QBittorrentClient(url))
                {
                    await client.LoginAsync(qb.QBittorrentUsername, qb.QBittorrentPassword ?? "");

                    // ✅ ОТПРАВЛЯЕМ В QBITTORRENT ВСЕГДА
                    // Если есть активная загрузка — добавляем на паузе
                    bool shouldBePaused = activeDownload != null;

                    var request = new AddTorrentsRequest
                    {
                        Paused = shouldBePaused,
                        DownloadFolder = finalPath
                    };
                    request.TorrentUrls.Add(new Uri(torrentData.Magnet));
                    await client.AddTorrentsAsync(request);

                    await Task.Delay(2000);

                    if (shouldBePaused)
                    {
                        // ✅ ДОБАВЛЯЕМ В ОЧЕРЕДЬ
                        torrentData.QueueStatus = "Queued";
                        DownloadQueue.Add(torrentData);
                        RecalculateQueuePositions();
                        SaveQueue();
                        SaveHydraData(game, torrentData);
                        logger.Info($"[DEBUG] Очередь и данные сохранены");

                        int position = torrentData.QueuePosition;

                        PlayniteApi.Notifications.Add(new NotificationMessage(
                            "HydraTorrent",
                            string.Format(ResourceProvider.GetString("LOC_HydraTorrent_AddedToQueue"), game.Name, position),
                            NotificationType.Info));

                        logger.Info($"Добавлено в очередь: {game.Name} (позиция {position})");
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(500); // ✅ Даём время на сохранение и обновление UI
                            if (HydraHubView.CurrentInstance != null)
                            {
                                HydraHubView.CurrentInstance.RefreshQueueUI();
                            }
                        });
                    }
                    else
                    {
                        // ✅ ЗАПУСКАЕМ СРАЗУ
                        torrentData.QueueStatus = "Downloading";
                        torrentData.QueuePosition = 0;
                        DownloadQueue.Add(torrentData);
                        SaveQueue();
                        SaveHydraData(game, torrentData);

                        PlayniteApi.Notifications.Add(new NotificationMessage(
                            "HydraTorrent",
                            string.Format(ResourceProvider.GetString("LOC_HydraTorrent_DownloadStarted"), game.Name),
                            NotificationType.Info));

                        logger.Info($"Начата загрузка: {game.Name}");
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(500);
                            if (HydraHubView.CurrentInstance != null)
                            {
                                HydraHubView.CurrentInstance.RefreshQueueUI();
                            }
                        });
                    }

                    game.IsInstalling = true;
                    PlayniteApi.Database.Games.Update(game);
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "qBittorrent error");  // ← Логи можно на английском
                PlayniteApi.Dialogs.ShowErrorMessage(
                    string.Format(ResourceProvider.GetString("LOC_HydraTorrent_DownloadError"), ex.Message),
                    ResourceProvider.GetString("LOC_HydraTorrent_Error"));
            }
        }

        private string ShowCustomInstallPathDialog(string gameName)
        {
            var windowView = new DownloadPathWindow();
            var window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions { ShowMaximizeButton = false, ShowMinimizeButton = false });

            string titleTemplate = ResourceProvider.GetString("LOC_HydraTorrent_InstallTitle");
            window.Title = string.Format(titleTemplate, gameName);

            window.Content = windowView;
            window.SizeToContent = SizeToContent.WidthAndHeight;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            return (window.ShowDialog() == true) ? windowView.SelectedPath : null;
        }

        private string ExtractHashFromMagnet(string magnet)
        {
            if (string.IsNullOrEmpty(magnet)) return null;

            var match = Regex.Match(magnet, @"urn:btih:([a-fA-F0-9]{40})", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }

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
                // ✅ НОВОЕ: Проверяем, загружен ли уже торрент
                if (!string.IsNullOrEmpty(_torrentData.DownloadPath) &&
                    Directory.Exists(_torrentData.DownloadPath))
                {
                    // Торрент уже загружен - ищем setup.exe
                    var setupPath = FindSetupExe(_torrentData.DownloadPath);
                    if (setupPath != null)
                    {
                        // Запускаем setup.exe
                        StartSetupExe(setupPath);
                        return;
                    }
                }

                // Торрент не загружен - начинаем загрузку
                _plugin.InstallGame(Game, _torrentData);

                _watcherTimer = new System.Timers.Timer(5000);
                _watcherTimer.Elapsed += WatcherTimer_Elapsed;
                _watcherTimer.Start();
            }

            private string FindSetupExe(string downloadPath)
            {
                try
                {
                    // Ищем setup.exe в корне
                    var rootSetup = Path.Combine(downloadPath, "setup.exe");
                    if (File.Exists(rootSetup))
                        return rootSetup;

                    // Ищем во вложенных папках (1 уровень)
                    foreach (var dir in Directory.GetDirectories(downloadPath))
                    {
                        var nestedSetup = Path.Combine(dir, "setup.exe");
                        if (File.Exists(nestedSetup))
                            return nestedSetup;
                    }
                }
                catch { }
                return null;
            }

            private void StartSetupExe(string setupPath)
            {
                try
                {
                    logger.Info($"Запуск setup.exe: {setupPath}");

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = setupPath,
                        WorkingDirectory = Path.GetDirectoryName(setupPath),
                        UseShellExecute = true
                    });

                    // Игра начинает устанавливаться
                    Game.IsInstalling = true;
                    _plugin.PlayniteApi.Database.Games.Update(Game);

                    // Запускаем мониторинг завершения установки
                    StartInstallationWatcher();
                }
                catch (Exception ex)
                {
                    logger.Error(ex, $"Ошибка запуска setup.exe: {setupPath}");
                }
            }

            private void StartInstallationWatcher()
            {
                // Следим за процессом установки
                // Когда пользователь закончит установку, нужно обновить игру
                _watcherTimer = new System.Timers.Timer(10000);
                _watcherTimer.Elapsed += (s, e) =>
                {
                    // Здесь можно проверить, установлена ли игра
                    // Например, поискать .exe в InstallDirectory
                    CheckInstallationComplete();
                };
                _watcherTimer.Start();
            }

            private void CheckInstallationComplete()
            {
                // TODO: Определить, что установка завершена
                // Например, если игра имеет InstallDirectory и там есть исполняемый файл
                if (!string.IsNullOrEmpty(Game.InstallDirectory) &&
                    Directory.Exists(Game.InstallDirectory))
                {
                    var executables = Directory.GetFiles(Game.InstallDirectory, "*.exe", SearchOption.TopDirectoryOnly);
                    if (executables.Length > 0)
                    {
                        _watcherTimer?.Stop();
                        Game.IsInstalling = false;
                        Game.IsInstalled = true;
                        _plugin.PlayniteApi.Database.Games.Update(Game);
                        InvokeOnInstalled(new GameInstalledEventArgs());
                    }
                }
            }

            private void WatcherTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
            {
                CheckCompletion();
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

        // ────────────────────────────────────────────────────────────────
        // Sidebar и жизненный цикл
        // ────────────────────────────────────────────────────────────────

        public override IEnumerable<SidebarItem> GetSidebarItems()
        {
            yield return new SidebarItem
            {
                Title = ResourceProvider.GetString("LOC_HydraTorrent_HubTitle"),
                Type = SiderbarItemType.View,
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

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            _monitor.Start();
            LoadQueue(); // ✅ Загружаем очередь при старте

            _completedManager = new CompletedManager(this);
            _completedManager.LoadCompletedItems();

            _statisticsManager = new StatisticsManager(GetPluginUserDataPath(), _completedManager);
            _statisticsManager.Load();

            // ✅ Восстанавливаем состояния после перезапуска
            _ = RestoreQueueStateAsync();
        }

        public async Task RestoreQueueStateAsync()
        {
            await Task.Delay(3000); // Ждём подключения к qBittorrent

            try
            {
                var qb = settings.Settings;
                var url = new Uri($"http://{qb.QBittorrentHost}:{qb.QBittorrentPort}");
                using (var client = new QBittorrentClient(url))
                {
                    await client.LoginAsync(qb.QBittorrentUsername, qb.QBittorrentPassword ?? "");

                    var allTorrents = await client.GetTorrentListAsync();

                    // Проверяем каждый элемент очереди
                    foreach (var item in DownloadQueue)
                    {
                        if (string.IsNullOrEmpty(item.TorrentHash)) continue;

                        var torrent = allTorrents.FirstOrDefault(t =>
                            t.Hash.Equals(item.TorrentHash, StringComparison.OrdinalIgnoreCase));

                        if (torrent == null) continue;

                        // Синхронизируем статус с тем, что в qBittorrent
                        bool isPaused = torrent.State.ToString().Contains("Paused");

                        if (item.QueueStatus == "Downloading" && isPaused)
                        {
                            // Должна качаться, но на паузе — возможно ручной пауза
                            logger.Debug($"Торрент {item.Name} на паузе в qBittorrent");
                        }
                        else if (item.QueueStatus == "Queued" && !isPaused)
                        {
                            // Должна быть на паузе, но качается — ставим на паузу
                            await client.PauseAsync(item.TorrentHash);
                            logger.Info($"Поставлен на паузу (из очереди): {item.Name}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Ошибка восстановления состояния очереди");
            }
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            return new List<GameMetadata>();
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new HydraTorrentSettingsView(settings);
        }

        public override void Dispose()
        {
            _monitor?.Dispose();
            base.Dispose();
        }

        public HydraTorrentSettingsViewModel GetSettings()
        {
            return settings;
        }

        public ScraperService GetScraperService()
        {
            return _scraperService;
        }
    }
}