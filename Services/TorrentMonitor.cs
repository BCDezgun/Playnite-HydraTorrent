using HydraTorrent.Models;
using HydraTorrent.Services;
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
        private CompletedManager _completedManager;

        // Для отслеживания времени загрузки
        private Dictionary<Guid, DateTime> _downloadStartTimes = new Dictionary<Guid, DateTime>();

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
            _completedManager = new CompletedManager(_plugin);
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

                    // Загружаем список завершённых
                    _completedManager.LoadCompletedItems();

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

                // ✅ Проверка ratio для автоудаления
                await CheckSeedRatioAsync();
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

                            // Фиксируем время начала если ещё не зафиксировано
                            if (item.GameId.HasValue && !_downloadStartTimes.ContainsKey(item.GameId.Value))
                            {
                                _downloadStartTimes[item.GameId.Value] = DateTime.Now;
                            }
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
                var queue = _plugin.DownloadQueue;

                if (queue == null || !queue.Any())
                    return;

                var activeItems = queue.Where(q => q.QueueStatus == "Downloading").ToList();

                var allTorrents = await _client.GetTorrentListAsync();

                var settings = _plugin.GetSettings().Settings;

                foreach (var item in activeItems)
                {
                    if (string.IsNullOrEmpty(item.TorrentHash))
                        continue;

                    var torrent = allTorrents.FirstOrDefault(t =>
                        t.Hash.Equals(item.TorrentHash, StringComparison.OrdinalIgnoreCase));

                    if (torrent == null)
                        continue;

                    if (torrent.Progress >= 1.0)
                    {
                        HydraTorrent.logger.Info($"✅ Загрузка завершена: {item.Name}");

                        // ✅ Сохраняем статистику загрузки
                        SaveDownloadStatistics(item, torrent);

                        // ✅ Обновляем статус
                        item.QueueStatus = "Completed";

                        if (item.GameId.HasValue)
                        {
                            // ✅ Запуск пост-обработки
                            if (!string.IsNullOrEmpty(item.DownloadPath))
                            {
                                try
                                {
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

                            // ✅ Добавляем в список завершённых
                            _completedManager.AddCompletedItem(item);
                        }

                        // ✅ Проверяем настройки раздачи
                        if (!settings.KeepSeedingAfterDownload)
                        {
                            // Удаляем торрент из qBittorrent, файлы сохраняем
                            await RemoveTorrentFromClient(item.TorrentHash);
                            item.IsRemovedFromClient = true;
                        }

                        _plugin.SaveQueue();

                        _api.Notifications.Add(new NotificationMessage(
                            "HydraTorrent",
                            $"✅ {ResourceProvider.GetString("LOC_HydraTorrent_DownloadCompleted")}: {item.Name}",
                            NotificationType.Info));

                        // Запускаем следующую
                        await _plugin.StartNextInQueueAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Error(ex, "Ошибка проверки завершённых загрузок");
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Проверка ratio для автоудаления
        // ────────────────────────────────────────────────────────────────

        private async Task CheckSeedRatioAsync()
        {
            try
            {
                var settings = _plugin.GetSettings().Settings;

                // Если автоудаление выключено или раздача выключена — не проверяем
                if (!settings.KeepSeedingAfterDownload || !settings.AutoRemoveAfterSeedRatio)
                    return;

                var completedItems = _completedManager.CompletedItems
                    .Where(c => !c.IsRemovedFromClient && !string.IsNullOrEmpty(c.TorrentHash))
                    .ToList();

                if (!completedItems.Any())
                    return;

                var allTorrents = await _client.GetTorrentListAsync();
                var ratioThreshold = settings.GetSeedRatioValue();

                foreach (var item in completedItems)
                {
                    var torrent = allTorrents.FirstOrDefault(t =>
                        t.Hash.Equals(item.TorrentHash, StringComparison.OrdinalIgnoreCase));

                    if (torrent == null)
                    {
                        // Торрент уже не в клиенте — помечаем
                        item.IsRemovedFromClient = true;
                        continue;
                    }

                    // Вычисляем текущий ratio
                    var currentRatio = torrent.Ratio;
                    item.SeedRatio = currentRatio;
                    item.TotalUploadedBytes = torrent.Uploaded ?? 0;

                    // Проверяем порог
                    if (currentRatio >= ratioThreshold)
                    {
                        HydraTorrent.logger.Info($"🔄 Достигнут ratio {currentRatio:F2} для: {item.Name}");

                        // Удаляем торрент из клиента
                        await RemoveTorrentFromClient(item.TorrentHash);
                        item.IsRemovedFromClient = true;

                        _completedManager.UpdateItem(item);

                        _api.Notifications.Add(new NotificationMessage(
                            "HydraTorrent",
                            string.Format(ResourceProvider.GetString("LOC_HydraTorrent_TorrentRemovedRatio"),
                                item.Name, currentRatio.ToString("F2")),
                            NotificationType.Info));
                    }
                }
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Error(ex, "Ошибка проверки seed ratio");
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Удаление торрента из клиента (файлы сохраняются)
        // ────────────────────────────────────────────────────────────────

        private async Task RemoveTorrentFromClient(string hash)
        {
            try
            {
                // DeleteTorrentAsync с deleteFiles = false — удаляет торрент, но НЕ файлы
                await _client.DeleteAsync(hash, false);
                HydraTorrent.logger.Info($"Торрент удалён из qBittorrent: {hash}");
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Error(ex, $"Ошибка удаления торрента: {hash}");
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Сохранение статистики загрузки
        // ────────────────────────────────────────────────────────────────

        private void SaveDownloadStatistics(TorrentResult item, TorrentInfo torrent)
        {
            try
            {
                item.TotalDownloadedBytes = torrent.Downloaded ?? torrent.TotalSize ?? 0;
                item.TotalUploadedBytes = torrent.Uploaded ?? 0;
                item.SeedRatio = torrent.Ratio;

                // Вычисляем длительность загрузки
                if (item.GameId.HasValue && _downloadStartTimes.ContainsKey(item.GameId.Value))
                {
                    var startTime = _downloadStartTimes[item.GameId.Value];
                    item.DownloadDuration = DateTime.Now - startTime;
                    _downloadStartTimes.Remove(item.GameId.Value);

                    // Вычисляем среднюю скорость
                    if (item.DownloadDuration.HasValue && item.DownloadDuration.Value.TotalSeconds > 0)
                    {
                        item.AverageDownloadSpeed = (long)(item.TotalDownloadedBytes / item.DownloadDuration.Value.TotalSeconds);
                    }
                }

                HydraTorrent.logger.Info($"Статистика сохранена для {item.Name}: " +
                    $"Downloaded={FormatBytes(item.TotalDownloadedBytes)}, " +
                    $"Uploaded={FormatBytes(item.TotalUploadedBytes)}, " +
                    $"Ratio={item.SeedRatio:F2}");
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Error(ex, "Ошибка сохранения статистики");
            }
        }

        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.##} {sizes[order]}";
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

        // ────────────────────────────────────────────────────────────────
        // Публичный доступ к CompletedManager
        // ────────────────────────────────────────────────────────────────

        public CompletedManager GetCompletedManager()
        {
            return _completedManager;
        }
    }
}