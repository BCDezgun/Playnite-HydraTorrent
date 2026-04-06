using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HydraTorrent.Services
{
    public class GameSearchSuggestionsService
    {
        private const string ClientId = "aeecdtvo277ybl2rt6iimeaulgit3u";
        private const string ClientSecret = "dw6m8t2btc58j1v8ojz1lthu05lqib";

        private readonly HttpClient _httpClient;
        private string _accessToken;
        private DateTime _tokenExpiry;
        
        // Многоуровневый кэш
        private readonly Dictionary<string, List<string>> _fullCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _prefixCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        
        // Debounce и отмена запросов
        private CancellationTokenSource _currentSearchCts;
        private const int DEBOUNCE_MS = 150;
        
        // Инкрементальная загрузка
        private const int INITIAL_LIMIT = 50;
        private const int MAX_LIMIT = 200;

        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "and", "or", "in", "on", "at", "to", "for", "is", "it", "with", "by", "from"
        };

        static GameSearchSuggestionsService()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        public GameSearchSuggestionsService()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        /// <summary>
        /// Предзагрузка токена IGDB (вызывается при открытии HydraHub)
        /// </summary>
        public async Task PreloadTokenAsync()
        {
            try
            {
                await GetAccessTokenAsync();
            }
            catch
            {
                // Игнорируем ошибки предзагрузки
            }
        }

        public async Task<List<string>> GetSuggestionsAsync(string query)
        {
            // 1. Отменяем предыдущий запрос СРАЗУ
            _currentSearchCts?.Cancel();
            _currentSearchCts = new CancellationTokenSource();
            var cancellationToken = _currentSearchCts.Token;

            // 2. Проверка на пустой запрос ДО debounce (с trim)
            var trimmedQuery = query?.Trim() ?? "";
            if (trimmedQuery.Length < 2)
                return new List<string>();

            try
            {
                // 3. Debounce 200ms
                await Task.Delay(DEBOUNCE_MS, cancellationToken);

                // 4. Повторная проверка ПОСЛЕ debounce
                trimmedQuery = query?.Trim() ?? "";
                if (trimmedQuery.Length < 2)
                    return new List<string>();

                var words = trimmedQuery.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (!words.Any()) return new List<string>();

                // 5. Проверка: если все слова - стоп-слова, не показываем результаты
                if (words.All(w => StopWords.Contains(w)))
                    return new List<string>();

                // 6. Выбираем умный якорь (первое значимое слово)
                string primaryAnchor = words.FirstOrDefault(w => !StopWords.Contains(w)) ?? words[0];
                string primaryLower = primaryAnchor.ToLowerInvariant();

                // Формируем массив слов для поиска (убираем стоп-слова)
                var searchWords = words.Where(w => !StopWords.Contains(w)).ToArray();
                if (!searchWords.Any())
                    searchWords = new[] { words[0] }; // Fallback если все стоп-слова

                // 7. Проверяем префиксный кэш (быстрый поиск)
                var prefixResults = SearchPrefixCache(primaryLower);
                if (prefixResults.Any())
                {
                    var filtered = FilterAndSortOptimized(prefixResults, trimmedQuery);
                    if (filtered.Count >= 7) return filtered;
                }

                // 8. Проверяем полный кэш
                if (_fullCache.TryGetValue(primaryLower, out var cachedList))
                {
                    var filtered = FilterAndSortOptimized(cachedList, trimmedQuery);
                    if (filtered.Any()) return filtered;
                }

                // 9. Запрос к IGDB (сразу загружаем максимум 200 игр)
                var results = await FetchGamesAsync(searchWords, MAX_LIMIT, cancellationToken);
                
                // 10. Обновляем кэши
                UpdateCaches(primaryLower, results);

                var primaryResults = FilterAndSortOptimized(results, trimmedQuery);
                if (primaryResults.Any()) return primaryResults;

                // 8. Fallback на второе слово
                if (words.Length > 1)
                {
                    string secondaryAnchor = words[1];
                    string secondaryLower = secondaryAnchor.ToLowerInvariant();

                    var secondaryPrefixResults = SearchPrefixCache(secondaryLower);
                    if (secondaryPrefixResults.Any())
                    {
                        var filtered = FilterAndSortOptimized(secondaryPrefixResults, trimmedQuery);
                        if (filtered.Count >= 7) return filtered;
                    }

                    if (_fullCache.TryGetValue(secondaryLower, out var secondaryCached))
                    {
                        var secondaryFiltered = FilterAndSortOptimized(secondaryCached, trimmedQuery);
                        if (secondaryFiltered.Any()) return secondaryFiltered;
                    }

                    // Формируем массив для второго слова
                    var secondarySearchWords = words.Skip(1).Where(w => !StopWords.Contains(w)).ToArray();
                    if (!secondarySearchWords.Any())
                        secondarySearchWords = new[] { words[1] };

                    var secondaryResults = await FetchGamesAsync(secondarySearchWords, MAX_LIMIT, cancellationToken);
                    UpdateCaches(secondaryLower, secondaryResults);

                    var secondaryFinal = FilterAndSortOptimized(secondaryResults, trimmedQuery);
                    if (secondaryFinal.Any()) return secondaryFinal;
                }

                return new List<string>();
            }
            catch (OperationCanceledException)
            {
                // Запрос отменён (новый ввод) - это нормально
                return new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Поиск в префиксном кэше (быстрый поиск по началу слова)
        /// </summary>
        private List<string> SearchPrefixCache(string query)
        {
            var queryLower = query.ToLowerInvariant();
            
            // Ищем точное совпадение префикса
            if (_prefixCache.TryGetValue(queryLower, out var exactMatch))
            {
                return exactMatch;
            }

            // Ищем самый длинный подходящий префикс
            var matchingPrefix = _prefixCache.Keys
                .Where(k => queryLower.StartsWith(k) || k.StartsWith(queryLower))
                .OrderByDescending(k => k.Length)
                .FirstOrDefault();

            if (matchingPrefix != null)
            {
                return _prefixCache[matchingPrefix];
            }

            return new List<string>();
        }

        /// <summary>
        /// Обновляет оба кэша (полный и префиксный)
        /// </summary>
        private void UpdateCaches(string anchor, List<string> games)
        {
            var anchorLower = anchor.ToLowerInvariant();
            
            // Обновляем полный кэш
            _fullCache[anchorLower] = games;

            // Обновляем префиксный кэш (для всех префиксов от 2 до длины слова)
            for (int len = 2; len <= anchorLower.Length; len++)
            {
                var prefix = anchorLower.Substring(0, len);
                if (!_prefixCache.ContainsKey(prefix))
                {
                    _prefixCache[prefix] = games;
                }
            }
        }

        /// <summary>
        /// Инкрементальная загрузка игр из IGDB (сначала 50, потом до 200)
        /// Использует гибридный подход: search для 1 слова, where для нескольких
        /// </summary>
        private async Task<List<string>> FetchGamesIncrementalAsync(string[] searchWords, CancellationToken cancellationToken)
        {
            // Первый запрос: 50 игр (быстро)
            var initialResults = await FetchGamesAsync(searchWords, INITIAL_LIMIT, cancellationToken);
            
            // Если нашли достаточно - возвращаем
            if (initialResults.Count >= 20)
            {
                return initialResults;
            }

            // Если мало результатов - догружаем до максимума
            var fullResults = await FetchGamesAsync(searchWords, MAX_LIMIT, cancellationToken);
            return fullResults;
        }

        /// <summary>
        /// Гибридный поиск: search для 1 слова, where для нескольких
        /// </summary>
        private async Task<List<string>> FetchGamesAsync(string[] words, int limit = MAX_LIMIT, CancellationToken cancellationToken = default)
        {
            var token = await GetAccessTokenAsync();
            if (string.IsNullOrEmpty(token)) return new List<string>();

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Client-ID", ClientId);
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            string body;
            
            if (words.Length == 1)
            {
                // Быстрый search для одного слова
                var escapedWord = words[0].Replace("\"", "\\\"");
                body = $"search \"{escapedWord}\"; fields name; limit {limit};";
            }
            else
            {
                // Точный where для нескольких слов
                var conditions = words
                    .Select(w => $"name ~ *\"{w.Replace("\"", "\\\"")}\"*")
                    .ToList();
                var whereClause = string.Join(" & ", conditions);
                body = $"fields name; where {whereClause}; limit {limit};";
            }
            
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync("https://api.igdb.com/v4/games", content, cancellationToken);

            if (!response.IsSuccessStatusCode) return new List<string>();

            var json = JArray.Parse(await response.Content.ReadAsStringAsync());
            return json
                .Select(g => g["name"]?.ToString())
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Оптимизированная фильтрация и сортировка с приоритетом StartsWith
        /// </summary>
        private List<string> FilterAndSortOptimized(List<string> allGames, string query)
        {
            var queryLower = query.ToLowerInvariant();

            // Разделяем на категории с приоритетом
            var exactMatch = new List<string>();
            var startsWith = new List<string>();
            var contains = new List<string>();
            var fuzzyMatch = new List<string>();

            foreach (var name in allGames)
            {
                var nameLower = name.ToLowerInvariant();

                // Точное совпадение (высший приоритет)
                if (nameLower == queryLower)
                {
                    exactMatch.Add(name);
                }
                // Начинается с запроса
                else if (nameLower.StartsWith(queryLower))
                {
                    startsWith.Add(name);
                }
                // Содержит запрос
                else if (nameLower.Contains(queryLower))
                {
                    contains.Add(name);
                }
                // Fuzzy match (для опечаток)
                else if (CalculateLevenshteinDistance(nameLower, queryLower) <= 2)
                {
                    fuzzyMatch.Add(name);
                }
            }

            // Сортируем каждую категорию по длине (короче = релевантнее)
            var sortedStartsWith = startsWith.OrderBy(n => n.Length).ToList();
            var sortedContains = contains.OrderBy(n => n.IndexOf(queryLower, StringComparison.OrdinalIgnoreCase)).ThenBy(n => n.Length).ToList();
            var sortedFuzzy = fuzzyMatch.OrderBy(n => n.Length).ToList();

            // Объединяем с приоритетом
            return exactMatch
                .Concat(sortedStartsWith)
                .Concat(sortedContains)
                .Concat(sortedFuzzy)
                .Take(7)
                .ToList();
        }

        /// <summary>
        /// Вычисляет расстояние Левенштейна для fuzzy matching
        /// </summary>
        private int CalculateLevenshteinDistance(string s1, string s2)
        {
            // Оптимизация: если разница в длине > 2, сразу возвращаем большое число
            if (Math.Abs(s1.Length - s2.Length) > 2)
                return 999;

            int len1 = s1.Length;
            int len2 = s2.Length;

            // Используем только 2 строки вместо полной матрицы (оптимизация памяти)
            int[] prev = new int[len2 + 1];
            int[] curr = new int[len2 + 1];

            for (int j = 0; j <= len2; j++)
                prev[j] = j;

            for (int i = 1; i <= len1; i++)
            {
                curr[0] = i;

                for (int j = 1; j <= len2; j++)
                {
                    int cost = (s1[i - 1] == s2[j - 1]) ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }

                // Swap
                var temp = prev;
                prev = curr;
                curr = temp;
            }

            return prev[len2];
        }

        /// <summary>
        /// Старый метод фильтрации (оставлен для совместимости)
        /// </summary>
        private List<string> FilterAndSort(List<string> allGames, string query)
        {
            // Используем новый оптимизированный метод
            return FilterAndSortOptimized(allGames, query);
        }

        private async Task<string> GetAccessTokenAsync()
        {
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry)
                return _accessToken;

            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client_id", ClientId),
                    new KeyValuePair<string, string>("client_secret", ClientSecret),
                    new KeyValuePair<string, string>("grant_type", "client_credentials")
                });

                var response = await _httpClient.PostAsync("https://id.twitch.tv/oauth2/token", content);
                if (!response.IsSuccessStatusCode) return null;

                var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                _accessToken = json["access_token"]?.ToString();
                var expiresIn = json["expires_in"]?.Value<int>() ?? 3600;
                _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);

                return _accessToken;
            }
            catch
            {
                return null;
            }
        }
    }
}
