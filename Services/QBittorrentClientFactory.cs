using Playnite.SDK;
using QBittorrent.Client;
using System;
using System.Threading.Tasks;

namespace HydraTorrent.Services
{
    public class QBittorrentClientFactory
    {
        private readonly HydraTorrentSettings _settings;
        private QBittorrentClient _cachedClient;
        private ITorrentClient _cachedAdapter;
        private bool _isConnected;
        private readonly object _lock = new object();

        public static readonly ILogger logger = LogManager.GetLogger();

        public QBittorrentClientFactory(HydraTorrentSettings settings)
        {
            _settings = settings;
        }

        public async Task<QBittorrentClient> CreateClientAsync()
        {
            lock (_lock)
            {
                if (_isConnected && _cachedClient != null)
                {
                    return _cachedClient;
                }
            }

            return await CreateNewClientAsync();
        }

        public async Task<ITorrentClient> CreateAdapterAsync()
        {
            lock (_lock)
            {
                if (_isConnected && _cachedAdapter != null)
                {
                    return _cachedAdapter;
                }
            }

            var url = new Uri($"http://{_settings.QBittorrentHost}:{_settings.QBittorrentPort}");
            var adapter = new QBittorrentClientAdapter(url);
            await adapter.LoginAsync(_settings.QBittorrentUsername, _settings.QBittorrentPassword ?? "");

            lock (_lock)
            {
                _cachedAdapter?.Dispose();
                _cachedAdapter = adapter;
                _isConnected = true;
            }

            logger.Info("qBittorrent: подключено (adapter)");
            return adapter;
        }

        public async Task<QBittorrentClient> CreateFreshClientAsync()
        {
            Invalidate();
            return await CreateNewClientAsync();
        }

        private async Task<QBittorrentClient> CreateNewClientAsync()
        {
            var url = new Uri($"http://{_settings.QBittorrentHost}:{_settings.QBittorrentPort}");
            var client = new QBittorrentClient(url);

            await client.LoginAsync(_settings.QBittorrentUsername, _settings.QBittorrentPassword ?? "");

            lock (_lock)
            {
                _cachedClient?.Dispose();
                _cachedClient = client;
                _isConnected = true;
            }

            logger.Info("qBittorrent: подключено");
            return client;
        }

        public void Invalidate()
        {
            lock (_lock)
            {
                _cachedClient = null;
                _cachedAdapter = null;
                _isConnected = false;
            }
        }
    }
}
