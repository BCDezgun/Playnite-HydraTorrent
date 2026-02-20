using Playnite.SDK;
using Playnite.SDK.Models;
using QBittorrent.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using static HydraTorrent.HydraTorrent;

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

            _timer = new Timer(3000); // каждые 3 секунды
            _timer.Elapsed += Timer_Elapsed;

            // Клиент для qBittorrent (используем настройки плагина)
            var qb = _plugin.GetSettings().Settings; // добавим метод позже
            var url = new Uri($"http://{qb.QBittorrentHost}:{qb.QBittorrentPort}");
            _client = new QBittorrentClient(url);
        }

        public void Start()
        {
            if (_isRunning) return;

            try
            {
                _client.LoginAsync(
                    _plugin.GetSettings().Settings.QBittorrentUsername,
                    _plugin.GetSettings().Settings.QBittorrentPassword ?? "")
                    .Wait();

                _timer.Start();
                _isRunning = true;
                _api.Notifications.Add(new NotificationMessage("HydraTorrent", "Мониторинг торрентов запущен", NotificationType.Info));
            }
            catch { /* тихо, если qBittorrent не запущен */ }
        }

        private async void Timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (!_isRunning) return;

            try
            {
                var torrents = await _client.GetTorrentListAsync();

                foreach (var torrent in torrents)
                {
                    // Находим все игры Hydra, у которых совпадает Hash
                    var games = _api.Database.Games
                        .Where(g => g.PluginId == _plugin.Id)
                        .ToList();

                    foreach (var game in games)
                    {
                        var data = _plugin.GetHydraData(game);
                        if (data?.TorrentHash == torrent.Hash)
                        {
                            UpdateGameProgress(game, torrent);
                        }
                    }
                }
            }
            catch { /* не падаем, если qBittorrent недоступен */ }
        }

        private void UpdateGameProgress(Game game, TorrentInfo torrent)
        {
            var status = new TorrentStatusInfo
            {
                Status = torrent.State.ToString(),
                Progress = torrent.Progress * 100,
                DownloadSpeed = torrent.DownloadSpeed
            };

            // Обновляем словарь в памяти
            HydraTorrent.LiveStatus[game.Id] = status;

            // Playnite не узнает об обновлении, пока мы не заставим интерфейс перерисоваться
            // Но данные уже доступны для использования!
        }

        public void Stop()
        {
            _timer.Stop();
            _isRunning = false;
            _client?.Dispose();
        }

        public void Dispose() => Stop();
    }
}