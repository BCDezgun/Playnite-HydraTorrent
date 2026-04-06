using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace HydraTorrent.Tools.ImageGenerator
{
    class Program
    {
        // IGDB API
        private const string IgdbClientId = "aeecdtvo277ybl2rt6iimeaulgit3u";
        private const string IgdbClientSecret = "dw6m8t2btc58j1v8ojz1lthu05lqib";
        
        // Настройки
        private const int MaxPopularGames = 200;
        private const int MaxNewGames = 97;
        private static readonly string OutputFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DiscordAssets");
        
        private static HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static HttpClient _steamGridDbClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static string _igdbAccessToken;
        
        // SteamGridDB API
        private const string SteamGridDbApiKey = "bd73eef990adc05ab997504b73f79be2"; // Замените на ваш API ключ
        private const string SteamGridDbBaseUrl = "https://www.steamgriddb.com/api/v2";

        static async Task Main(string[] args)
        {
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.WriteLine("  HydraTorrent - Discord Assets List Generator");
            Console.WriteLine("  Автоматическое скачивание через SteamGridDB API");
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.WriteLine();

            try
            {
                // Устанавливаем заголовок Authorization для SteamGridDB сразу
                if (!string.IsNullOrEmpty(SteamGridDbApiKey) && SteamGridDbApiKey != "YOUR_STEAMGRIDDB_API_KEY")
                {
                    _steamGridDbClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {SteamGridDbApiKey}");
                }

                // 1. Получаем токен IGDB
                Console.WriteLine("[1/4] Получение токена IGDB...");
                _igdbAccessToken = await GetIgdbTokenAsync();
                if (string.IsNullOrEmpty(_igdbAccessToken))
                {
                    Console.WriteLine("❌ Не удалось получить токен IGDB!");
                    return;
                }
                Console.WriteLine("✅ Токен IGDB получен");
                Console.WriteLine();

                // 2. Создаём папку вывода
                Console.WriteLine("[2/4] Подготовка папки вывода...");
                if (Directory.Exists(OutputFolder))
                {
                    Console.WriteLine($"🗑️  Очистка старой папки: {OutputFolder}");
                    Directory.Delete(OutputFolder, true);
                }
                Directory.CreateDirectory(OutputFolder);
                Console.WriteLine($"✅ Папка создана: {OutputFolder}");
                Console.WriteLine();

                // 3. Получаем игры из IGDB
                Console.WriteLine("[3/4] Получение игр из IGDB...");
                var games = new List<GameInfo>();
                
                // 3a. Топ популярных игр
                Console.WriteLine($"  Запрос топ-{MaxPopularGames} популярных игр...");
                var popularGames = await GetTopGamesFromIgdbAsync(MaxPopularGames, "rating_count");
                games.AddRange(popularGames);
                Console.WriteLine($"  ✅ Получено {popularGames.Count} популярных игр");

                // 3b. Топ новых качественных игр (за последние 2 года, рейтинг 70+)
                Console.WriteLine($"  Запрос топ-{MaxNewGames} качественных новых игр...");
                var newGames = await GetRecentQualityGamesFromIgdbAsync(MaxNewGames);
                
                // Фильтруем дубликаты (если игра уже есть в популярных)
                var existingIds = new HashSet<int>(games.Select(g => g.Id));
                var uniqueNewGames = newGames.Where(g => !existingIds.Contains(g.Id)).ToList();
                games.AddRange(uniqueNewGames);
                
                Console.WriteLine($"  ✅ Получено {uniqueNewGames.Count} уникальных новых игр");
                Console.WriteLine($"  Итого уникальных игр: {games.Count}");
                Console.WriteLine();

                // 4. Генерируем файлы
                Console.WriteLine("[4/5] Генерация файлов...");
                await GenerateAssetsJsonAsync(games);
                await GenerateMappingJsonAsync(games);
                await GenerateReadmeAsync(games.Count);
                Console.WriteLine("✅ Все файлы созданы");
                Console.WriteLine();

                // 5. Скачиваем изображения через SteamGridDB
                Console.WriteLine("[5/5] Скачивание изображений через SteamGridDB...");
                if (string.IsNullOrEmpty(SteamGridDbApiKey) || SteamGridDbApiKey == "YOUR_STEAMGRIDDB_API_KEY")
                {
                    Console.WriteLine("⚠️  API ключ SteamGridDB не настроен!");
                    Console.WriteLine("   Получите ключ на https://www.steamgriddb.com/profile/preferences/api");
                    Console.WriteLine("   и замените YOUR_STEAMGRIDDB_API_KEY в коде (строка 27)");
                }
                else
                {
                    // Проверяем валидность API ключа
                    Console.WriteLine("  🔑 Проверка API ключа...");
                    if (await TestSteamGridDBApiKeyAsync())
                    {
                        Console.WriteLine("  ✅ API ключ валиден");
                        Console.WriteLine($"  ⏱️  Примерное время: ~{(games.Count * 4) / 60} минут");
                        Console.WriteLine();
                        await DownloadImagesFromSteamGridDBAsync(games);
                    }
                    else
                    {
                        Console.WriteLine("  ❌ API ключ невалиден!");
                        Console.WriteLine("     Проверьте правильность ключа в коде (строка 27)");
                        Console.WriteLine("     Получить ключ: https://www.steamgriddb.com/profile/preferences/api");
                    }
                }
                Console.WriteLine();

                // Итог
                Console.WriteLine("═══════════════════════════════════════════════════════");
                Console.WriteLine($"  ✅ Готово! Создан список из {games.Count} игр");
                Console.WriteLine($"  📁 Папка: {OutputFolder}");
                Console.WriteLine($"  📄 discord_assets.json - файл для плагина");
                Console.WriteLine($"  📄 games_mapping.json - список игр");
                Console.WriteLine($"  📄 README.md - инструкция");
                Console.WriteLine();
                
                if (!string.IsNullOrEmpty(SteamGridDbApiKey) && SteamGridDbApiKey != "YOUR_STEAMGRIDDB_API_KEY")
                {
                    Console.WriteLine("  📋 Следующие шаги:");
                    Console.WriteLine("  1. Проверьте скачанные изображения в папке");
                    Console.WriteLine("  2. Загрузите их в Discord Developer Portal");
                    Console.WriteLine("  3. Замените YOUR_DISCORD_APP_ID в discord_assets.json");
                    Console.WriteLine("  4. Скопируйте discord_assets.json в папку плагина");
                }
                else
                {
                    Console.WriteLine("  📋 Следующие шаги:");
                    Console.WriteLine("  1. Настройте SteamGridDB API ключ для автоскачивания");
                    Console.WriteLine("  2. Или скачайте обложки вручную из games_mapping.json");
                    Console.WriteLine("  3. Загрузите в Discord Developer Portal");
                    Console.WriteLine("  4. Замените YOUR_DISCORD_APP_ID в discord_assets.json");
                    Console.WriteLine("  5. Скопируйте discord_assets.json в папку плагина");
                }
                Console.WriteLine("═══════════════════════════════════════════════════════");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }

            Console.WriteLine();
            Console.WriteLine("Нажмите любую клавишу для выхода...");
            Console.ReadKey();
        }

        // ═══════════════════════════════════════════════════════
        // IGDB API
        // ═══════════════════════════════════════════════════════

        private static async Task<string> GetIgdbTokenAsync()
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", IgdbClientId),
                new KeyValuePair<string, string>("client_secret", IgdbClientSecret),
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            });

            var response = await _httpClient.PostAsync("https://id.twitch.tv/oauth2/token", content);
            var json = await response.Content.ReadAsStringAsync();
            var data = JObject.Parse(json);
            
            return data["access_token"]?.ToString();
        }

        private static async Task<List<GameInfo>> GetTopGamesFromIgdbAsync(int limit, string sortBy)
        {
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Client-ID", IgdbClientId);
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_igdbAccessToken}");

            // Запрос с фильтром по Steam (только игры со Steam URL)
            var body = $@"
                fields name, id, rating_count, first_release_date, websites.*;
                where cover != null 
                    & websites.url ~ *""store.steampowered.com""*;
                sort {sortBy} desc;
                limit {limit};
            ";

            var httpContent = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync("https://api.igdb.com/v4/games", httpContent);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new Exception($"IGDB API error: {response.StatusCode} - {error}");
            }

            var json = JArray.Parse(await response.Content.ReadAsStringAsync());
            var games = new List<GameInfo>();

            Console.WriteLine($"  [DEBUG] Получено {json.Count} игр от IGDB");

            int debugCount = 0;
            foreach (var game in json)
            {
                var name = game["name"]?.ToString();
                if (string.IsNullOrEmpty(name)) continue;

                // Извлекаем Steam ID из websites
                int? steamId = null;
                var websites = game["websites"] as JArray;
                
                // Показываем отладку только для первых 3 игр
                if (debugCount < 3)
                {
                    Console.WriteLine($"  [DEBUG] Игра: {name}");
                    Console.WriteLine($"  [DEBUG] websites: {(websites != null ? websites.Count.ToString() : "null")}");
                }
                
                if (websites != null)
                {
                    foreach (var site in websites)
                    {
                        var url = site["url"]?.ToString();
                        
                        if (debugCount < 3)
                        {
                            Console.WriteLine($"  [DEBUG]   - url: {url}");
                        }
                        
                        // Ищем Steam URL по паттерну
                        if (!string.IsNullOrEmpty(url) && url.Contains("store.steampowered.com/app/"))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(url, @"/app/(\d+)");
                            if (match.Success && int.TryParse(match.Groups[1].Value, out var id))
                            {
                                steamId = id;
                                if (debugCount < 3)
                                {
                                    Console.WriteLine($"  [DEBUG]   ✅ Найден Steam ID: {steamId}");
                                }
                                break;
                            }
                        }
                    }
                }

                games.Add(new GameInfo
                {
                    Id = game["id"]?.Value<int>() ?? 0,
                    Name = name,
                    RatingCount = game["rating_count"]?.Value<int>() ?? 0,
                    ReleaseDate = game["first_release_date"]?.Value<long>() ?? 0,
                    SteamId = steamId
                });
                
                debugCount++;
            }

            return games;
        }

        /// <summary>
        /// Получает недавно вышедшие игры с высоким рейтингом и популярностью
        /// </summary>
        private static async Task<List<GameInfo>> GetRecentQualityGamesFromIgdbAsync(int limit)
        {
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Client-ID", IgdbClientId);
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_igdbAccessToken}");

            // Получаем timestamp для последних 2 лет
            var twoYearsAgo = DateTimeOffset.UtcNow.AddYears(-2).ToUnixTimeSeconds();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Запрос: игры за последние 2 года со Steam URL
            var body = $@"
                fields name, id, rating_count, first_release_date, rating, websites.*;
                where cover != null 
                    & first_release_date >= {twoYearsAgo} 
                    & first_release_date <= {now}
                    & rating_count >= 10
                    & rating >= 70
                    & websites.url ~ *""store.steampowered.com""*;
                sort rating_count desc;
                limit {limit * 2};
            ";

            var httpContent = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync("https://api.igdb.com/v4/games", httpContent);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new Exception($"IGDB API error: {response.StatusCode} - {error}");
            }

            var json = JArray.Parse(await response.Content.ReadAsStringAsync());
            var games = new List<GameInfo>();

            foreach (var game in json)
            {
                var name = game["name"]?.ToString();
                if (string.IsNullOrEmpty(name)) continue;

                // Извлекаем Steam ID из websites
                int? steamId = null;
                var websites = game["websites"] as JArray;
                if (websites != null)
                {
                    foreach (var site in websites)
                    {
                        var url = site["url"]?.ToString();
                        
                        // Ищем Steam URL по паттерну
                        if (!string.IsNullOrEmpty(url) && url.Contains("store.steampowered.com/app/"))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(url, @"/app/(\d+)");
                            if (match.Success && int.TryParse(match.Groups[1].Value, out var id))
                            {
                                steamId = id;
                                break;
                            }
                        }
                    }
                }

                games.Add(new GameInfo
                {
                    Id = game["id"]?.Value<int>() ?? 0,
                    Name = name,
                    RatingCount = game["rating_count"]?.Value<int>() ?? 0,
                    ReleaseDate = game["first_release_date"]?.Value<long>() ?? 0,
                    SteamId = steamId
                });
            }

            // Возвращаем только нужное количество
            return games.Take(limit).ToList();
        }

        // ═══════════════════════════════════════════════════════
        // Генерация файлов
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Генерирует discord_assets.json для плагина
        /// </summary>
        private static async Task GenerateAssetsJsonAsync(List<GameInfo> games)
        {
            var assets = new Dictionary<string, string>();
            int gamesWithSteamId = 0;
            int gamesWithoutSteamId = 0;
            
            foreach (var game in games)
            {
                if (game.SteamId.HasValue)
                {
                    // Используем Steam ID как ключ
                    assets[game.SteamId.Value.ToString()] = $"game_{game.SteamId.Value}";
                    gamesWithSteamId++;
                }
                else
                {
                    gamesWithoutSteamId++;
                }
            }

            var data = new DiscordAssetsData
            {
                Version = "1.0",
                LastUpdated = DateTime.UtcNow,
                ApplicationId = "YOUR_DISCORD_APP_ID",
                Assets = assets,
                FallbackAsset = "downloading"
            };

            var json = JsonConvert.SerializeObject(data, Formatting.Indented);
            var filePath = Path.Combine(OutputFolder, "discord_assets.json");
            
            File.WriteAllText(filePath, json);
            Console.WriteLine($"  ✅ discord_assets.json создан ({gamesWithSteamId} игр со Steam ID)");
            if (gamesWithoutSteamId > 0)
            {
                Console.WriteLine($"  ⚠️  {gamesWithoutSteamId} игр без Steam ID (пропущены)");
            }
        }

        /// <summary>
        /// Генерирует games_mapping.json - список игр с именами файлов для ручного скачивания
        /// </summary>
        private static async Task GenerateMappingJsonAsync(List<GameInfo> games)
        {
            var mapping = new List<GameMapping>();
            
            foreach (var game in games)
            {
                // Пропускаем игры без Steam ID
                if (!game.SteamId.HasValue)
                    continue;
                
                mapping.Add(new GameMapping
                {
                    GameName = game.Name,
                    IgdbId = game.Id,
                    SteamId = game.SteamId.Value,
                    AssetKey = $"game_{game.SteamId.Value}",
                    FileName = $"game_{game.SteamId.Value}.png",
                    RatingCount = game.RatingCount,
                    ReleaseDate = game.ReleaseDate > 0 ? DateTimeOffset.FromUnixTimeSeconds(game.ReleaseDate).DateTime.ToString("yyyy-MM-dd") : "N/A"
                });
            }

            var json = JsonConvert.SerializeObject(mapping, Formatting.Indented);
            var filePath = Path.Combine(OutputFolder, "games_mapping.json");
            
            File.WriteAllText(filePath, json);
            Console.WriteLine($"  ✅ games_mapping.json создан");
        }

        /// <summary>
        /// Генерирует README.md с инструкцией
        /// </summary>
        private static async Task GenerateReadmeAsync(int gameCount)
        {
            var readme = $@"# HydraTorrent - Discord Rich Presence Assets

## 📊 Статистика
- Всего игр в списке: {gameCount}
- Дата генерации: {DateTime.UtcNow:yyyy-MM-dd}

## 📁 Файлы

### discord_assets.json
Файл для плагина HydraTorrent. Содержит маппинг IGDB ID → Discord Asset Key.

### games_mapping.json
Список всех игр с именами файлов и метаданными.

### game_*.png
Автоматически скачанные обложки игр (если настроен SteamGridDB API).

## 🤖 Автоматическое скачивание через SteamGridDB

### Настройка API ключа
1. Зарегистрируйтесь на https://www.steamgriddb.com
2. Получите API ключ: https://www.steamgriddb.com/profile/preferences/api
3. Откройте `Program.cs` в редакторе
4. Замените `YOUR_STEAMGRIDDB_API_KEY` на ваш ключ
5. Запустите ImageGenerator.exe

Программа автоматически:
- Найдет каждую игру в SteamGridDB
- Скачает квадратную иконку 512x512
- Сохранит как `game_{{IGDB_ID}}.png`

## 🚀 Инструкция по загрузке в Discord

### Шаг 1: Создайте Discord Application
1. Откройте https://discord.com/developers/applications
2. Нажмите **New Application**
3. Назовите его **HydraTorrent**
4. Скопируйте **Application ID** (раздел OAuth2)

### Шаг 2: Загрузите системные иконки
1. Откройте https://discord.com/developers/applications/{{ВАШ_APP_ID}}/rich-presence/assets
2. Загрузите 3 системные иконки:
   - **hydra_logo** — логотип плагина (512x512 PNG)
   - **downloading** — иконка загрузки (512x512 PNG)
   - **queued** — иконка очереди (512x512 PNG)

### Шаг 3: Загрузите обложки игр
1. В той же странице Rich Presence Assets загрузите все файлы `game_*.png` из папки DiscordAssets
2. Имя ассета должно соответствовать имени файла (без расширения):
   - `game_1020.png` → имя ассета: `game_1020`
   - `game_3328.png` → имя ассета: `game_3328`

💡 **Совет:** Discord позволяет загружать до 300 ассетов. Если у вас больше игр, загрузите самые популярные.

### Шаг 4: Обновите discord_assets.json
1. Откройте файл `discord_assets.json`
2. Замените `YOUR_DISCORD_APP_ID` на ваш Application ID
3. Сохраните файл

### Шаг 5: Скопируйте в папку плагина
Скопируйте `discord_assets.json` в папку плагина Playnite:
```
C:\Users\Admin\AppData\Roaming\Playnite\Extensions\Playnite-HydraTorrent\discord_assets.json
```

## 🎮 Использование
1. Включите **Discord Rich Presence** в настройках плагина HydraTorrent
2. Запустите загрузку игры
3. Статус появится в вашем Discord профиле!

## 🔄 Обновление списка
Запустите ImageGenerator.exe повторно для обновления списка игр.

## ❓ Ручное скачивание (если не используете SteamGridDB API)
1. Откройте файл `games_mapping.json`
2. Для каждой игры скачайте квадратную обложку (минимум 512x512)
   - SteamGridDB: https://www.steamgriddb.com
   - IGDB: https://www.igdb.com
3. Переименуйте файл согласно полю `FileName` (например: `game_1020.png`)
4. Сохраните в папку DiscordAssets
";
            var filePath = Path.Combine(OutputFolder, "README.md");
            File.WriteAllText(filePath, readme);
            Console.WriteLine($"  ✅ README.md создан");
        }

        // ═══════════════════════════════════════════════════════
        // SteamGridDB API
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Проверяет валидность API ключа SteamGridDB
        /// </summary>
        private static async Task<bool> TestSteamGridDBApiKeyAsync()
        {
            try
            {
                // Пробуем простой запрос поиска (заголовок уже установлен в Main)
                var url = $"{SteamGridDbBaseUrl}/search/autocomplete/test";
                var response = await _steamGridDbClient.GetAsync(url);

                // Если 401 - ключ невалиден
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return false;
                }

                // Если 200 или другой успешный код - ключ валиден
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Скачивает изображения для всех игр через SteamGridDB
        /// </summary>
        private static async Task DownloadImagesFromSteamGridDBAsync(List<GameInfo> games)
        {
            int successCount = 0;
            int failCount = 0;
            int skipCount = 0;

            for (int i = 0; i < games.Count; i++)
            {
                var game = games[i];
                
                // Пропускаем игры без Steam ID
                if (!game.SteamId.HasValue)
                {
                    Console.WriteLine($"  [{i + 1}/{games.Count}] ⏭️  {game.Name} - нет Steam ID");
                    skipCount++;
                    continue;
                }
                
                var fileName = $"game_{game.SteamId.Value}.png";
                var filePath = Path.Combine(OutputFolder, fileName);

                // Пропускаем если файл уже существует
                if (File.Exists(filePath))
                {
                    Console.WriteLine($"  [{i + 1}/{games.Count}] ⏭️  {game.Name} - уже скачан");
                    skipCount++;
                    continue;
                }

                Console.Write($"  [{i + 1}/{games.Count}] 🔍 {game.Name}...");

                try
                {
                    // 1. Ищем игру в SteamGridDB
                    var searchResults = await SearchGameInSteamGridDBAsync(game.Name);
                    if (searchResults == null || searchResults.Count == 0)
                    {
                        Console.WriteLine(" ❌ не найдена в базе");
                        failCount++;
                        await Task.Delay(1000);
                        continue;
                    }

                    // Берем первый результат (наиболее релевантный)
                    var sgdbGameId = searchResults[0].Id;
                    var foundName = searchResults[0].Name;

                    // 2. Получаем обложку для игры (пробуем разные типы)
                    var imageUrl = await GetBestImageFromSteamGridDBAsync(sgdbGameId);
                    if (string.IsNullOrEmpty(imageUrl))
                    {
                        Console.WriteLine($" ❌ нет изображений (найдено: {foundName})");
                        failCount++;
                        await Task.Delay(1000);
                        continue;
                    }

                    // 3. Скачиваем изображение (используем обычный HTTP клиент без авторизации)
                    using (var imageClient = new HttpClient())
                    {
                        var imageBytes = await imageClient.GetByteArrayAsync(imageUrl);
                        
                        // Проверяем что изображение квадратное
                        using (var ms = new MemoryStream(imageBytes))
                        {
                            using (var img = System.Drawing.Image.FromStream(ms))
                            {
                                // Проверяем что изображение квадратное (допускаем разницу до 10px)
                                if (Math.Abs(img.Width - img.Height) > 10)
                                {
                                    Console.WriteLine($" ❌ не квадратное ({img.Width}x{img.Height}, найдено: {foundName})");
                                    failCount++;
                                    await Task.Delay(1000);
                                    continue;
                                }
                                
                                // Проверяем минимальный размер 512x512
                                if (img.Width < 512 || img.Height < 512)
                                {
                                    Console.WriteLine($" ❌ слишком маленькое ({img.Width}x{img.Height}, найдено: {foundName})");
                                    failCount++;
                                    await Task.Delay(1000);
                                    continue;
                                }
                            }
                        }
                        
                        File.WriteAllBytes(filePath, imageBytes);
                        
                        Console.WriteLine($" ✅ {FormatBytes(imageBytes.Length)} (как: {foundName})");
                        successCount++;
                    }

                    // Задержка 4 секунды чтобы не упереться в лимит API (~15 запросов/минуту)
                    await Task.Delay(4000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($" ❌ ошибка: {ex.Message}");
                    failCount++;
                    await Task.Delay(2000);
                }
            }

            Console.WriteLine();
            Console.WriteLine($"  📊 Статистика скачивания:");
            Console.WriteLine($"     ✅ Успешно: {successCount}");
            Console.WriteLine($"     ⏭️  Пропущено: {skipCount}");
            Console.WriteLine($"     ❌ Ошибок: {failCount}");
        }

        /// <summary>
        /// Ищет игру в SteamGridDB по названию
        /// </summary>
        private static async Task<List<SteamGridDBGame>> SearchGameInSteamGridDBAsync(string gameName)
        {
            try
            {
                var url = $"{SteamGridDbBaseUrl}/search/autocomplete/{Uri.EscapeDataString(gameName)}";
                var response = await _steamGridDbClient.GetAsync(url);
                
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"\n      [DEBUG] Поиск вернул {response.StatusCode}");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var result = JObject.Parse(json);

                if (result["success"]?.Value<bool>() != true)
                {
                    return null;
                }

                var games = new List<SteamGridDBGame>();
                var dataArray = result["data"] as JArray;
                
                if (dataArray != null)
                {
                    foreach (var item in dataArray)
                    {
                        games.Add(new SteamGridDBGame
                        {
                            Id = item["id"]?.Value<int>() ?? 0,
                            Name = item["name"]?.ToString()
                        });
                    }
                }

                return games;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n      [DEBUG] Ошибка поиска: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Получает лучшее изображение для игры (grids 512x512 или 1024x1024)
        /// </summary>
        private static async Task<string> GetBestImageFromSteamGridDBAsync(int gameId)
        {
            try
            {
                // 1. Пробуем grids 512x512
                var gridUrl = $"{SteamGridDbBaseUrl}/grids/game/{gameId}?dimensions=512x512";
                var response = await _steamGridDbClient.GetAsync(gridUrl);
                
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"\n      [DEBUG] Grids 512x512 вернул {response.StatusCode}");
                }
                else
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var result = JObject.Parse(json);
                    
                    if (result["success"]?.Value<bool>() == true)
                    {
                        var dataArray = result["data"] as JArray;
                        if (dataArray != null && dataArray.Count > 0)
                        {
                            return dataArray[0]["url"]?.ToString();
                        }
                    }
                }

                // 2. Пробуем grids 1024x1024
                gridUrl = $"{SteamGridDbBaseUrl}/grids/game/{gameId}?dimensions=1024x1024";
                response = await _steamGridDbClient.GetAsync(gridUrl);
                
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"\n      [DEBUG] Grids 1024x1024 вернул {response.StatusCode}");
                }
                else
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var result = JObject.Parse(json);
                    
                    if (result["success"]?.Value<bool>() == true)
                    {
                        var dataArray = result["data"] as JArray;
                        if (dataArray != null && dataArray.Count > 0)
                        {
                            return dataArray[0]["url"]?.ToString();
                        }
                    }
                }

                // 3. Если квадратных нет, берем любой grid и ищем квадратный
                gridUrl = $"{SteamGridDbBaseUrl}/grids/game/{gameId}";
                response = await _steamGridDbClient.GetAsync(gridUrl);
                
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"\n      [DEBUG] Grids любые вернул {response.StatusCode}");
                    return null;
                }
                
                var json2 = await response.Content.ReadAsStringAsync();
                var result2 = JObject.Parse(json2);
                
                if (result2["success"]?.Value<bool>() == true)
                {
                    var dataArray = result2["data"] as JArray;
                    if (dataArray != null && dataArray.Count > 0)
                    {
                        // Ищем квадратное изображение (512x512 или 1024x1024)
                        foreach (var item in dataArray)
                        {
                            var width = item["width"]?.Value<int>() ?? 0;
                            var height = item["height"]?.Value<int>() ?? 0;
                            
                            // Ищем точно квадратные 512x512 или 1024x1024
                            if ((width == 512 && height == 512) || (width == 1024 && height == 1024))
                            {
                                return item["url"]?.ToString();
                            }
                        }
                        
                        // Если точных нет, ищем близкие к квадрату
                        foreach (var item in dataArray)
                        {
                            var width = item["width"]?.Value<int>() ?? 0;
                            var height = item["height"]?.Value<int>() ?? 0;
                            
                            if (width > 0 && height > 0 && Math.Abs(width - height) < 100)
                            {
                                return item["url"]?.ToString();
                            }
                        }
                        
                        // В крайнем случае берем первое
                        return dataArray[0]["url"]?.ToString();
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n      [DEBUG] Ошибка получения изображения: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Форматирует размер в байтах в читаемый вид
        /// </summary>
        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }

    class SteamGridDBGame
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    class GameInfo
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int RatingCount { get; set; }
        public long ReleaseDate { get; set; }
        public int? SteamId { get; set; }  // Steam ID (может быть null)
    }

    class GameMapping
    {
        [JsonProperty("game_name")]
        public string GameName { get; set; }

        [JsonProperty("igdb_id")]
        public int IgdbId { get; set; }

        [JsonProperty("steam_id")]
        public int SteamId { get; set; }

        [JsonProperty("asset_key")]
        public string AssetKey { get; set; }

        [JsonProperty("file_name")]
        public string FileName { get; set; }

        [JsonProperty("rating_count")]
        public int RatingCount { get; set; }

        [JsonProperty("release_date")]
        public string ReleaseDate { get; set; }
    }

    class DiscordAssetsData
    {
        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("last_updated")]
        public DateTime LastUpdated { get; set; }

        [JsonProperty("application_id")]
        public string ApplicationId { get; set; }

        [JsonProperty("assets")]
        public Dictionary<string, string> Assets { get; set; }

        [JsonProperty("fallback_asset")]
        public string FallbackAsset { get; set; }
    }
}
