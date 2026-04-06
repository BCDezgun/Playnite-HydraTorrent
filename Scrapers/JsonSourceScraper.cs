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
        
        // Индекс для быстрого поиска: слово -> список репаков
        private Dictionary<string, HashSet<HydraRepack>> _searchIndex = new Dictionary<string, HashSet<HydraRepack>>();
        private bool _isIndexBuilt = false;

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

                    var validation = JsonSourceValidator.Validate(root);
                    if (validation.IsValid)
                    {
                        _repackList = root.Downloads;
                        _isLoaded = true;
                        _lastLoadTime = DateTime.Now;
                        _isIndexBuilt = false; // Сбрасываем индекс при новой загрузке
                        System.Diagnostics.Debug.WriteLine($"[{_sourceName}] Loaded directly: {validation.ValidItemsCount} valid repacks");
                        HydraTorrent.logger.Info($"[{_sourceName}] Loaded directly: {validation.ValidItemsCount} valid repacks");

                        if (validation.InvalidItemsCount > 0)
                        {
                            HydraTorrent.logger.Warn($"[{_sourceName}] {validation.InvalidItemsCount} invalid items skipped");
                        }
                        return;
                    }
                    else
                    {
                        HydraTorrent.logger.Warn($"[{_sourceName}] Validation failed: {string.Join("; ", validation.Errors)}");
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

                    var validation = JsonSourceValidator.Validate(rootCf);
                    if (validation.IsValid)
                    {
                        _repackList = rootCf.Downloads;
                        _isLoaded = true;
                        _lastLoadTime = DateTime.Now;
                        _isIndexBuilt = false; // Сбрасываем индекс при новой загрузке
                        System.Diagnostics.Debug.WriteLine($"[{_sourceName}] Loaded via WebView2: {validation.ValidItemsCount} valid repacks");
                        HydraTorrent.logger.Info($"[{_sourceName}] Loaded via WebView2: {validation.ValidItemsCount} valid repacks");

                        if (validation.InvalidItemsCount > 0)
                        {
                            HydraTorrent.logger.Warn($"[{_sourceName}] {validation.InvalidItemsCount} invalid items skipped");
                        }
                        return;
                    }
                    else
                    {
                        HydraTorrent.logger.Warn($"[{_sourceName}] Validation failed (WebView2): {string.Join("; ", validation.Errors)}");
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

        /// <summary>
        /// Строит поисковый индекс для быстрого поиска по словам
        /// </summary>
        private void BuildSearchIndex()
        {
            if (_isIndexBuilt || !_isLoaded)
                return;

            _searchIndex.Clear();

            foreach (var repack in _repackList)
            {
                if (string.IsNullOrEmpty(repack.Title))
                    continue;

                // Разбиваем название на слова (добавлены знаки препинания: : , ; ! ? ' ")
                var title = repack.Title.ToLowerInvariant();
                var words = System.Text.RegularExpressions.Regex.Split(title, @"[\s\-_\.\(\)\[\]:,;!?'""\+]+")
                    .Where(w => w.Length >= 2) // Игнорируем слова короче 2 символов
                    .ToList();

                foreach (var word in words)
                {
                    if (!_searchIndex.ContainsKey(word))
                    {
                        _searchIndex[word] = new HashSet<HydraRepack>();
                    }
                    _searchIndex[word].Add(repack);
                }

                // Также добавляем полное название для точного поиска
                if (!_searchIndex.ContainsKey(title))
                {
                    _searchIndex[title] = new HashSet<HydraRepack>();
                }
                _searchIndex[title].Add(repack);
            }

            _isIndexBuilt = true;
            System.Diagnostics.Debug.WriteLine($"[{_sourceName}] Search index built: {_searchIndex.Count} unique words");
            HydraTorrent.logger.Info($"[{_sourceName}] Search index built: {_searchIndex.Count} unique words");
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

            // Строим индекс если ещё не построен
            if (!_isIndexBuilt)
            {
                BuildSearchIndex();
            }

            var lowerQuery = query.ToLowerInvariant();
            
            // Разбиваем запрос на слова (добавлены знаки препинания: : , ; ! ? ' ")
            var queryWords = System.Text.RegularExpressions.Regex.Split(lowerQuery, @"[\s\-_\.\(\)\[\]:,;!?'""\+]+")
                .Where(w => w.Length >= 2)
                .ToList();

            HashSet<HydraRepack> candidates = null;

            // Если запрос состоит из одного слова или фразы
            if (queryWords.Count == 0)
            {
                // Fallback на полный поиск
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
                        SizeBytes = ParseSizeToBytes(r.FileSize),
                        RepackType = TorrentResult.DetectRepackType(r.Title)
                    })
                    .ToList();
            }

            // Используем индекс для быстрого поиска
            foreach (var word in queryWords)
            {
                if (_searchIndex.TryGetValue(word, out var matches))
                {
                    if (candidates == null)
                    {
                        candidates = new HashSet<HydraRepack>(matches);
                    }
                    else
                    {
                        // Пересечение - оставляем только те, что содержат ВСЕ слова
                        candidates.IntersectWith(matches);
                    }
                }
                else
                {
                    // Если хотя бы одно слово не найдено, ищем частичные совпадения
                    var partialMatches = _searchIndex
                        .Where(kvp => kvp.Key.Contains(word))
                        .SelectMany(kvp => kvp.Value)
                        .ToHashSet();

                    if (partialMatches.Any())
                    {
                        if (candidates == null)
                        {
                            candidates = partialMatches;
                        }
                        else
                        {
                            candidates.IntersectWith(partialMatches);
                        }
                    }
                    else
                    {
                        // Слово вообще не найдено - возвращаем пустой результат
                        candidates = new HashSet<HydraRepack>();
                        break;
                    }
                }
            }

            // Если ничего не найдено через индекс, делаем fallback на полный поиск
            if (candidates == null || candidates.Count == 0)
            {
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
                        SizeBytes = ParseSizeToBytes(r.FileSize),
                        RepackType = TorrentResult.DetectRepackType(r.Title)
                    })
                    .ToList();
            }

            return candidates
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
                    SizeBytes = ParseSizeToBytes(r.FileSize),
                    RepackType = TorrentResult.DetectRepackType(r.Title)
                })
                .ToList();
        }
    }
}