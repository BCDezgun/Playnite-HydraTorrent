using HydraTorrent.Models;
using HydraTorrent.Services;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace HydraTorrent.Scrapers
{
    public class JsonSourceScraper : IScraper
    {
        private readonly string _sourceUrl;
        private readonly string _sourceName;
        private List<HydraRepack> _repackList = new List<HydraRepack>();
        private bool _isLoaded = false;
        private DateTime _lastLoadTime = DateTime.MinValue;
        private readonly TimeSpan _cacheDuration = TimeSpan.FromHours(1);

        public JsonSourceScraper(string sourceName, string sourceUrl)
        {
            _sourceName = sourceName;
            _sourceUrl = sourceUrl;
            System.Diagnostics.Debug.WriteLine($"[{_sourceName}] Scraper initialized");
        }

        private async Task LoadDataAsync(HttpClient client)
        {
            // Проверяем кэш
            if (_isLoaded && DateTime.Now - _lastLoadTime < _cacheDuration)
            {
                return;
            }

            try
            {
                System.Diagnostics.Debug.WriteLine($"[{_sourceName}] Loading database...");

                // Сначала пробуем загрузить напрямую
                try
                {
                    var json = await client.GetStringAsync(_sourceUrl);
                    var root = JsonConvert.DeserializeObject<FitGirlRoot>(json);

                    if (root?.Downloads != null)
                    {
                        _repackList = root.Downloads;
                        _isLoaded = true;
                        _lastLoadTime = DateTime.Now;
                        System.Diagnostics.Debug.WriteLine($"[{_sourceName}] Loaded directly: {_repackList.Count} repacks");
                        HydraTorrent.logger.Info($"[{_sourceName}] Loaded directly: {_repackList.Count} repacks");
                        return;
                    }
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("403"))
                {
                    // Cloudflare блокирует - используем WebView2
                    System.Diagnostics.Debug.WriteLine($"[{_sourceName}] HTTP 403, trying WebView2...");
                    HydraTorrent.logger.Info($"[{_sourceName}] HTTP 403, trying WebView2...");
                }
                catch (HttpRequestException)
                {
                    // Другие HTTP ошибки - пробуем WebView2
                    System.Diagnostics.Debug.WriteLine($"[{_sourceName}] HTTP error, trying WebView2...");
                }

                // Fallback на WebView2
                if (CloudflareBypassService.IsWebView2Available())
                {
                    var cfService = CloudflareBypassService.Instance;
                    var rootCf = await cfService.FetchJsonAsync<FitGirlRoot>(_sourceUrl);

                    if (rootCf?.Downloads != null)
                    {
                        _repackList = rootCf.Downloads;
                        _isLoaded = true;
                        _lastLoadTime = DateTime.Now;
                        System.Diagnostics.Debug.WriteLine($"[{_sourceName}] Loaded via WebView2: {_repackList.Count} repacks");
                        HydraTorrent.logger.Info($"[{_sourceName}] Loaded via WebView2: {_repackList.Count} repacks");
                        return;
                    }
                }
                else
                {
                    HydraTorrent.logger.Warn($"[{_sourceName}] WebView2 not available, cannot bypass Cloudflare");
                }

                System.Diagnostics.Debug.WriteLine($"[{_sourceName}] Failed to load");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[{_sourceName}] Error: {ex.Message}");
                HydraTorrent.logger.Error(ex, $"[{_sourceName}] Error loading data");
            }
        }

        private long ParseSizeToBytes(string sizeString)
        {
            if (string.IsNullOrEmpty(sizeString)) return 0;

            var match = System.Text.RegularExpressions.Regex.Match(
                sizeString,
                @"([\d\.]+)\s*(GB|MB|KB|TB)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (!match.Success) return 0;

            if (!double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double value))
            {
                return 0;
            }

            string unit = match.Groups[2].Value.ToUpper();
            return unit switch
            {
                "TB" => (long)(value * 1024 * 1024 * 1024 * 1024),
                "GB" => (long)(value * 1024 * 1024 * 1024),
                "MB" => (long)(value * 1024 * 1024),
                "KB" => (long)(value * 1024),
                _ => 0
            };
        }

        public async Task<List<TorrentResult>> SearchAsync(string query, HttpClient client)
        {
            await LoadDataAsync(client);

            if (string.IsNullOrWhiteSpace(query) || !_isLoaded)
                return new List<TorrentResult>();

            var lowerQuery = query.ToLowerInvariant();

            return _repackList
                .Where(r => !string.IsNullOrEmpty(r.Title) && r.Title.ToLowerInvariant().Contains(lowerQuery))
                .Select(r => new TorrentResult
                {
                    Name = r.Title,
                    Size = r.FileSize ?? "N/A",
                    Magnet = r.Uris?.FirstOrDefault(u => u.StartsWith("magnet:")) ?? "",
                    Source = _sourceName,
                    Year = (r.UploadDate != null && r.UploadDate.Length >= 4) ? r.UploadDate.Substring(0, 4) : "",
                    UploadDate = !string.IsNullOrEmpty(r.UploadDate) && DateTime.TryParse(r.UploadDate, out var date)
                        ? date
                        : null,
                    SizeBytes = ParseSizeToBytes(r.FileSize)
                })
                .ToList();
        }
    }
}