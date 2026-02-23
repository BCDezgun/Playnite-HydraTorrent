using HydraTorrent.Models;
using HydraTorrent.Scrapers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Net;

namespace HydraTorrent.Scrapers
{
    public class ScraperService
    {
        private readonly HttpClient _httpClient;
        private readonly HydraTorrentSettings _settings; // Добавляем хранение настроек

        static ScraperService()
        {
            AppContext.SetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", false);
            AppContext.SetSwitch("Switch.System.Net.DontEnableSchSendAuxRecord", true);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.Expect100Continue = true;
            ServicePointManager.DefaultConnectionLimit = 100;
        }

        // В конструктор теперь передаем настройки плагина
        public ScraperService(HydraTorrentSettings settings)
        {
            _settings = settings;

            var handler = new HttpClientHandler()
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public async Task<List<TorrentResult>> SearchAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query) || _settings.Sources == null || _settings.Sources.Count == 0)
                return new List<TorrentResult>();

            var scrapers = new List<IScraper>();
            foreach (var source in _settings.Sources)
            {
                if (!string.IsNullOrWhiteSpace(source.Url))
                {
                    string displayName = string.IsNullOrWhiteSpace(source.Name) ? "Источник" : source.Name;
                    scrapers.Add(new JsonSourceScraper(displayName, source.Url));
                }
            }

            var tasks = scrapers.Select(async s =>
            {
                try
                {
                    return await s.SearchAsync(query, _httpClient);
                }
                catch (Exception ex)
                {
                    // Логируем ошибку, но не даем ей прервать поиск в других источниках
                    HydraTorrent.logger.Warn($"Ошибка поиска в источнике: {ex.Message}");
                    return new List<TorrentResult>();
                }
            });

            var resultsArrays = await Task.WhenAll(tasks);
            return resultsArrays.SelectMany(r => r).ToList();
        }
    }

    public interface IScraper
    {
        Task<List<TorrentResult>> SearchAsync(string query, HttpClient client);
    }
}