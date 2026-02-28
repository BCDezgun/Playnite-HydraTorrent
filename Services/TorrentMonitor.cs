using HydraTorrent.Views;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using QBittorrent.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using HydraTorrent.Services;

namespace HydraTorrent.Services
{
    public class TorrentMonitor : IDisposable
    {
        private readonly IPlayniteAPI _api;
        private readonly HydraTorrent _plugin;
        private readonly Timer _timer;
        private readonly QBittorrentClient _client;
        private bool _isRunning;
        private GameSetupService _gameSetupService;
        public static readonly ILogger logger = LogManager.GetLogger();

        public TorrentMonitor(IPlayniteAPI api, HydraTorrent plugin)
        {
            _api = api;
            _plugin = plugin;
            _timer = new Timer(3000);
            _timer.Elapsed += Timer_Elapsed;

            var qb = _plugin.GetSettings().Settings;
            var url = new Uri($"http://{qb.QBittorrentHost}:{qb.QBittorrentPort}");
            _client = new QBittorrentClient(url);
            _gameSetupService = new GameSetupService(_plugin);
        }

        // ────────────────────────────────────────────────────────────────
        // Запуск и остановка мониторинга
        // ────────────────────────────────────────────────────────────────

        public void Start()
        {
            if (_isRunning) return;

            Task.Run(async () =>
            {
                try
                {
                    var qb = _plugin.GetSettings().Settings;
                    await _client.LoginAsync(qb.QBittorrentUsername, qb.QBittorrentPassword ?? "");
                    _timer.Start();
                    _isRunning = true;
                    HydraTorrent.logger.Info("Hydra Monitor: qBittorrent connected.");
                }
                catch (Exception ex)
                {
                    HydraTorrent.logger.Warn($"Hydra Monitor: Could not connect to qBittorrent. {ex.Message}");
                }
            });
        }

        public void Stop()
        {
            _timer.Stop();
            _isRunning = false;
        }

        public void Dispose()
        {
            Stop();
        }

        // ────────────────────────────────────────────────────────────────
        // Основной цикл мониторинга
        // ────────────────────────────────────────────────────────────────

        private async void Timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (!_isRunning) return;

            try
            {
                HydraTorrent.logger.Debug("[DEBUG] Timer_Elapsed сработал");
                var torrents = await _client.GetTorrentListAsync();

                var hydraGames = _api.Database.Games
                    .Where(g => g.PluginId == _plugin.Id)
                    .ToList();

                foreach (var torrent in torrents)
                {
                    var targetGame = hydraGames.FirstOrDefault(g =>
                        _plugin.GetHydraData(g)?.TorrentHash == torrent.Hash);

                    if (targetGame != null)
                    {
                        UpdateGameProgress(targetGame, torrent);
                    }
                }

                // ✅ Управление очередью (каждые 3 секунды)
                await ManageQueueAsync();

                // ✅ Проверка завершённых
                await CheckCompletedDownloadsAsync();
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Error(ex, "Error during torrent monitoring tick.");
            }
        }

        private async Task ManageQueueAsync()
        {
            try
            {
                var queue = _plugin.DownloadQueue;
                if (queue == null || !queue.Any()) return;

                // ✅ Ищем игру с позицией 0 (приоритет по позиции, не по статусу!)
                var priorityDownload = queue
                    .Where(q => q.QueuePosition == 0 && q.QueueStatus == "Downloading")
                    .FirstOrDefault();

                // ✅ Если нет с позицией 0, ищем любую "Downloading"
                if (priorityDownload == null)
                {
                    priorityDownload = queue.FirstOrDefault(q => q.QueueStatus == "Downloading");
                }

                var allTorrents = await _client.GetTorrentListAsync();

                foreach (var item in queue)
                {
                    if (string.IsNullOrEmpty(item.TorrentHash)) continue;

                    var torrent = allTorrents.FirstOrDefault(t =>
                        t.Hash.Equals(item.TorrentHash, StringComparison.OrdinalIgnoreCase));

                    if (torrent == null) continue;

                    if (item == priorityDownload)
                    {
                        // Приоритетная загрузка — должна работать
                        if (torrent.State.ToString().Contains("Paused"))
                        {
                            await _client.ResumeAsync(item.TorrentHash);
                        }
                    }
                    else if (item.QueueStatus == "Queued" || item.QueueStatus == "Paused")
                    {
                        // Очередь ИЛИ ручная пауза — должна быть на паузе
                        if (!torrent.State.ToString().Contains("Paused") &&
                            !torrent.State.ToString().Contains("Complete"))
                        {
                            await _client.PauseAsync(item.TorrentHash);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Error(ex, "Ошибка управления очередью");
            }
        }

        private async Task CheckCompletedDownloadsAsync()
        {
            try
            {
                // ✅ DEBUG: Лог в самом начале
                HydraTorrent.logger.Info("[DEBUG] CheckCompletedDownloadsAsync: ЗАПУСК");

                var queue = _plugin.DownloadQueue;

                if (queue == null)
                {
                    HydraTorrent.logger.Warn("[DEBUG] Очередь = NULL!");
                    return;
                }

                HydraTorrent.logger.Info($"[DEBUG] Очередь имеет {queue.Count} элементов");

                var activeItems = queue.Where(q => q.QueueStatus == "Downloading").ToList();
                HydraTorrent.logger.Info($"[DEBUG] Активных (Downloading): {activeItems.Count}");

                foreach (var item in activeItems)
                {
                    HydraTorrent.logger.Info($"[DEBUG] Активный элемент: {item.Name}");
                    HydraTorrent.logger.Info($"[DEBUG]   - TorrentHash: {item.TorrentHash ?? "NULL"}");
                    HydraTorrent.logger.Info($"[DEBUG]   - DownloadPath: {item.DownloadPath ?? "NULL"}");
                    HydraTorrent.logger.Info($"[DEBUG]   - QueueStatus: {item.QueueStatus}");
                }

                var allTorrents = await _client.GetTorrentListAsync();
                HydraTorrent.logger.Info($"[DEBUG] Получено торрентов от qBittorrent: {allTorrents.Count()}");

                foreach (var item in activeItems)
                {
                    if (string.IsNullOrEmpty(item.TorrentHash))
                    {
                        HydraTorrent.logger.Warn($"[DEBUG] Пустой TorrentHash для: {item.Name}");
                        continue;
                    }

                    var torrent = allTorrents.FirstOrDefault(t =>
                        t.Hash.Equals(item.TorrentHash, StringComparison.OrdinalIgnoreCase));

                    if (torrent == null)
                    {
                        HydraTorrent.logger.Warn($"[DEBUG] Торрент НЕ НАЙДЕН в qBittorrent: {item.TorrentHash}");
                        continue;
                    }

                    HydraTorrent.logger.Info($"[DEBUG] Торрент найден: {item.Name}");
                    HydraTorrent.logger.Info($"[DEBUG]   - Progress: {torrent.Progress}");
                    HydraTorrent.logger.Info($"[DEBUG]   - State: {torrent.State}");

                    if (torrent.Progress >= 1.0)
                    {
                        HydraTorrent.logger.Info($"✅ Загрузка завершена: {item.Name}");

                        item.QueueStatus = "Completed";
                        _plugin.SaveQueue();

                        if (item.GameId.HasValue)
                        {
                            // ❌ НЕ обновляем IsInstalled/IsInstalling здесь!
                            // GameSetupService сам установит правильные значения в зависимости от типа игры:
                            // - Portable: IsInstalled=true, IsInstalling=false, создаёт Play Action
                            // - Repack: IsInstalled=false, IsInstalling=true, создаёт Install Action

                            // ✅ Запуск пост-обработки
                            if (!string.IsNullOrEmpty(item.DownloadPath))
                            {
                                try
                                {
                                    HydraTorrent.logger.Info($"[DEBUG] Запуск пост-обработки: {item.DownloadPath}");
                                    await _gameSetupService.ProcessDownloadedGameAsync(
                                        item.GameId.Value,
                                        item.DownloadPath,
                                        item.TorrentHash);
                                }
                                catch (Exception ex)
                                {
                                    HydraTorrent.logger.Error(ex, $"Ошибка пост-обработки: {item.Name}");
                                }
                            }
                            else
                            {
                                HydraTorrent.logger.Warn($"⚠️ Путь загрузки не сохранён для: {item.Name}");
                            }
                        }

                        _api.Notifications.Add(new NotificationMessage(
                            "HydraTorrent",
                            $"✅ {ResourceProvider.GetString("LOC_HydraTorrent_DownloadCompleted")}: {item.Name}",
                            NotificationType.Info));

                        // Запускаем следующую
                        await _plugin.StartNextInQueueAsync();
                    }
                    else
                    {
                        HydraTorrent.logger.Info($"[DEBUG] Торрент ещё не завершён: Progress={torrent.Progress}, State={torrent.State}");
                    }
                }
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Error(ex, "Ошибка проверки завершённых загрузок");
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Обновление прогресса одной игры
        // ────────────────────────────────────────────────────────────────

        private void UpdateGameProgress(Game game, TorrentInfo torrent)
        {
            var progressPercent = torrent.Progress;
            string dynamicName;

            if (torrent.State.ToString().Contains("Downloading") && !torrent.State.ToString().Contains("Paused"))
            {
                dynamicName = $"Загрузка: {progressPercent:F1}% ({torrent.DownloadSpeed / 1024 / 1024:F1} МБ/с)";
            }
            else if (progressPercent >= 100)
            {
                dynamicName = "Загрузка: Завершена";
            }
            else
            {
                dynamicName = $"Загрузка: Пауза ({progressPercent:F1}%)";
            }

            // Обновляем LiveStatus
            HydraTorrent.LiveStatus[game.Id] = new HydraTorrent.TorrentStatusInfo
            {
                Progress = progressPercent,
                Status = dynamicName,
                DownloadSpeed = torrent.DownloadSpeed,
                UploadSpeed = torrent.UploadSpeed,
                TotalSize = torrent.TotalSize ?? 0,
                DownloadedSize = torrent.Downloaded ?? 0,
                ETA = torrent.EstimatedTime,
                Seeds = torrent.TotalSeeds,
                Peers = torrent.TotalLeechers
            };

            // Обновляем UI, если окно открыто
            //if (HydraHubView.CurrentInstance != null)
            //{
            //    HydraHubView.CurrentInstance.UpdateDownloadUI(game, HydraTorrent.LiveStatus[game.Id]);
            //}

            // Обновляем статус в библиотеке Playnite
            var status = _api.Database.CompletionStatuses
                .FirstOrDefault(s => s.Name.StartsWith("Загрузка:", StringComparison.OrdinalIgnoreCase));

            if (status == null)
            {
                status = new CompletionStatus(dynamicName);
                _api.Database.CompletionStatuses.Add(status);
            }
            else
            {
                status.Name = dynamicName;
                _api.Database.CompletionStatuses.Update(status);
            }

            if (game.CompletionStatusId != status.Id)
            {
                game.CompletionStatusId = status.Id;
                _api.Database.Games.Update(game);
            }
        }
    }
}