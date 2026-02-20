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
        private readonly List<IScraper> _scrapers;

        static ScraperService()
        {
            // 1. Позволяем системе использовать свои настройки
            AppContext.SetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", false);

            // 2. ВАЖНО: Отключаем AuxRecord (это решает проблему "Unexpected error during transmission")
            AppContext.SetSwitch("Switch.System.Net.DontEnableSchSendAuxRecord", true);

            // 3. Явно разрешаем TLS 1.2
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            // 4. Оптимизация соединений
            ServicePointManager.Expect100Continue = true;
            ServicePointManager.DefaultConnectionLimit = 100;
        }

        public ScraperService()
        {
            // Настраиваем обработчик с автоматической распаковкой
            var handler = new HttpClientHandler()
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            // Чистим и добавляем заголовки заново
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

            _scrapers = new List<IScraper>
            {
                new JsonSourceScraper("FitGirl", "https://hydralinks.pages.dev/sources/fitgirl.json"),
                new JsonSourceScraper("Xatab", "https://hydralinks.pages.dev/sources/xatab.json"),                
                new JsonSourceScraper("Rutracker", "https://raw.githubusercontent.com/KekitU/rutracker-hydra-links/main/all_categories.json")
            };
        }

        public async Task<List<TorrentResult>> SearchAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query) || _scrapers.Count == 0)
                return new List<TorrentResult>();

            var tasks = _scrapers.Select(s => s.SearchAsync(query, _httpClient));
            var resultsArrays = await Task.WhenAll(tasks);

            return resultsArrays.SelectMany(r => r).ToList();
        }
    }

    public interface IScraper
    {
        Task<List<TorrentResult>> SearchAsync(string query, HttpClient client);
    }
}