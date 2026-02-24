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

        public TorrentMonitor(IPlayniteAPI api, HydraTorrent plugin)
        {
            _api = api;
            _plugin = plugin;
            _timer = new Timer(3000);
            _timer.Elapsed += Timer_Elapsed;

            var qb = _plugin.GetSettings().Settings;
            var url = new Uri($"http://{qb.QBittorrentHost}:{qb.QBittorrentPort}");
            _client = new QBittorrentClient(url);
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
                var queue = _plugin.DownloadQueue;
                var activeItems = queue.Where(q => q.QueueStatus == "Downloading").ToList();
                var allTorrents = await _client.GetTorrentListAsync();

                foreach (var item in activeItems)
                {
                    if (string.IsNullOrEmpty(item.TorrentHash)) continue;

                    var torrent = allTorrents.FirstOrDefault(t =>
                        t.Hash.Equals(item.TorrentHash, StringComparison.OrdinalIgnoreCase));

                    if (torrent == null) continue;

                    if (torrent.Progress >= 1.0 && torrent.State.ToString().Contains("Complete"))
                    {
                        HydraTorrent.logger.Info($"Загрузка завершена: {item.Name}");

                        item.QueueStatus = "Completed";
                        _plugin.SaveQueue();

                        if (item.GameId.HasValue)
                        {
                            var game = _api.Database.Games.Get(item.GameId.Value);
                            if (game != null)
                            {
                                game.IsInstalled = true;
                                game.IsInstalling = false;
                                _api.Database.Games.Update(game);
                            }
                        }

                        _api.Notifications.Add(new NotificationMessage(
                            "HydraTorrent",
                            $"✅ Загрузка завершена: {item.Name}",
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
                TotalSize = torrent.TotalSize ?? 0,
                DownloadedSize = torrent.Downloaded ?? 0,
                ETA = torrent.EstimatedTime
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