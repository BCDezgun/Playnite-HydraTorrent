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

        private async void Timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (!_isRunning) return;

            try
            {
                var torrents = await _client.GetTorrentListAsync();
                // Берем список наших игр один раз, чтобы не дергать базу в тройном цикле
                var hydraGames = _api.Database.Games.Where(g => g.PluginId == _plugin.Id).ToList();

                foreach (var torrent in torrents)
                {
                    var targetGame = hydraGames.FirstOrDefault(g => _plugin.GetHydraData(g)?.TorrentHash == torrent.Hash);

                    if (targetGame != null)
                    {
                        UpdateGameProgress(targetGame, torrent);
                    }
                }
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Error(ex, "Error during torrent monitoring tick.");
            }
        }

        private void UpdateGameProgress(Game game, TorrentInfo torrent)
        {
            var progressPercent = torrent.Progress * 100;
            string dynamicName;

            // 1. Формируем текст статуса
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

            // 2. Обновляем LiveStatus (ВАЖНО для контроллера установки!)
            HydraTorrent.LiveStatus[game.Id] = new HydraTorrent.TorrentStatusInfo
            {
                Progress = progressPercent,
                Status = dynamicName,
                DownloadSpeed = torrent.DownloadSpeed
            };

            // 3. Обновляем визуальный статус в Playnite
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

            // Привязываем статус к игре
            if (game.CompletionStatusId != status.Id)
            {
                game.CompletionStatusId = status.Id;
                _api.Database.Games.Update(game);
            }
        }

        public void Stop()
        {
            _timer.Stop();
            _isRunning = false;
        }

        public void Dispose() => Stop();
    }
}