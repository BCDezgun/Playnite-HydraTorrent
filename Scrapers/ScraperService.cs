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
        private readonly HydraTorrentSettings _settings;
        private readonly Dictionary<string, CachedResults> _searchCache = new Dictionary<string, CachedResults>();
        private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);

        static ScraperService()
        {
            AppContext.SetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", false);
            AppContext.SetSwitch("Switch.System.Net.DontEnableSchSendAuxRecord", true);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.Expect100Continue = true;
            ServicePointManager.DefaultConnectionLimit = 100;
        }

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

            var cacheKey = query.Trim().ToLowerInvariant();

            if (_searchCache.TryGetValue(cacheKey, out var cached) && DateTime.Now - cached.Timestamp < _cacheDuration)
            {
                HydraTorrent.logger.Debug($"[SearchCache] Hit for '{query}' ({cached.Results.Count} results)");
                return cached.Results;
            }

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
                    HydraTorrent.logger.Warn($"Ошибка поиска в источнике: {ex.Message}");
                    return new List<TorrentResult>();
                }
            });

            var resultsArrays = await Task.WhenAll(tasks);
            var results = resultsArrays.SelectMany(r => r).ToList();

            _searchCache[cacheKey] = new CachedResults { Results = results, Timestamp = DateTime.Now };

            var expiredKeys = _searchCache.Where(kvp => DateTime.Now - kvp.Value.Timestamp > _cacheDuration).ToList();
            foreach (var key in expiredKeys)
            {
                _searchCache.Remove(key.Key);
            }

            HydraTorrent.logger.Debug($"[SearchCache] Miss for '{query}' ({results.Count} results, cached)");
            return results;
        }

        public void ClearCache()
        {
            _searchCache.Clear();
        }

        private class CachedResults
        {
            public List<TorrentResult> Results { get; set; }
            public DateTime Timestamp { get; set; }
        }
    }

    public interface IScraper
    {
        Task<List<TorrentResult>> SearchAsync(string query, HttpClient client);
    }
}