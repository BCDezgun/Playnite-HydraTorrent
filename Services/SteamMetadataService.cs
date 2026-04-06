using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace HydraTorrent.Services
{
    public class SteamMetadataService
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly HttpClient _httpClient;
        private readonly IPlayniteAPI _api;
        private static readonly string[] UserAgents = {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:123.0) Gecko/20100101 Firefox/123.0"
        };

        static SteamMetadataService()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        public SteamMetadataService(IPlayniteAPI api)
        {
            _api = api;
            var handler = new HttpClientHandler()
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgents[0]);
        }

        public async Task<(int appId, string name, string englishName, int? year, List<string> genres, List<string> developers, string description)> SearchGameAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return (0, null, null, null, null, null, null);
            }

            try
            {
                var appId = await FindSteamAppIdAsync(query);
                if (appId == 0)
                {
                    return (0, null, null, null, null, null, null);
                }

                var lang = GetSteamLanguageCode();
                var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l={lang}";
                var response = await _httpClient.GetStringAsync(url);
                var json = JObject.Parse(response);
                var appData = json[appId.ToString()];

                if (appData == null || appData["success"]?.Value<bool>() != true)
                {
                    if (lang != "english")
                    {
                        url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english";
                        response = await _httpClient.GetStringAsync(url);
                        json = JObject.Parse(response);
                        appData = json[appId.ToString()];
                    }

                    if (appData == null || appData["success"]?.Value<bool>() != true)
                    {
                        return (appId, null, null, null, null, null, null);
                    }
                }

                var data = appData["data"];
                if (data == null)
                {
                    return (appId, null, null, null, null, null, null);
                }

                var name = data["name"]?.ToString();
                
                // Всегда получаем английское название для SteamGridDB
                string englishName = name;
                if (lang != "english")
                {
                    try
                    {
                        var englishUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english";
                        var englishResponse = await _httpClient.GetStringAsync(englishUrl);
                        var englishJson = JObject.Parse(englishResponse);
                        var englishAppData = englishJson[appId.ToString()];
                        
                        if (englishAppData != null && englishAppData["success"]?.Value<bool>() == true)
                        {
                            var englishData = englishAppData["data"];
                            if (englishData != null)
                            {
                                englishName = englishData["name"]?.ToString() ?? name;
                                logger.Info($"[SteamMetadata] Localized name: '{name}', English name: '{englishName}'");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Warn($"[SteamMetadata] Failed to fetch English name: {ex.Message}");
                        // Fallback: используем локализованное название
                    }
                }

                int? year = null;
                var releaseDate = data["release_date"];
                if (releaseDate != null)
                {
                    var dateStr = releaseDate["date"]?.ToString();
                    if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var parsedDate))
                    {
                        year = parsedDate.Year;
                    }
                }

                var genres = data["genres"] as JArray;
                var genreNames = genres?.Select(g => g["description"]?.ToString())
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();

                var developers = data["developers"] as JArray;
                var devNames = developers?.Select(d => d.ToString())
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();

                var description = data["short_description"]?.ToString();
                if (!string.IsNullOrEmpty(description))
                {
                    description = StripHtml(description);
                }

                return (appId, name, englishName, year, genreNames, devNames, description);
            }
            catch (Exception ex)
            {
                logger.Warn($"[SteamMetadata] SearchGameAsync failed: {ex.Message}");
                return (0, null, null, null, null, null, null);
            }
        }

        private string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return html;
            var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", string.Empty);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
            text = text.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&nbsp;", " ");
            return text;
        }

        public async Task<bool> DownloadAndApplyMetadataAsync(Game game, string searchTerm = null, Action<int> onAppIdFound = null)
        {
            if (game == null)
            {
                logger.Info("[SteamMetadata] Game is null");
                return false;
            }

            var query = searchTerm ?? game.Name;
            if (string.IsNullOrWhiteSpace(query))
            {
                logger.Info("[SteamMetadata] Query is empty");
                return false;
            }

            try
            {
                logger.Info($"[SteamMetadata] Searching Steam for: '{query}'");
                var appId = await FindSteamAppIdAsync(query);
                if (appId == 0)
                {
                    logger.Info($"[SteamMetadata] No Steam app found for: '{query}'");
                    return false;
                }

                logger.Info($"[SteamMetadata] Found Steam AppId {appId} for: '{query}'");
                onAppIdFound?.Invoke(appId);
                return await ApplySteamMetadataAsync(game, appId);
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"[SteamMetadata] Failed to download metadata for: '{query}'");
                return false;
            }
        }

        private async Task<int> FindSteamAppIdAsync(string query)
        {
            try
            {
                var encodedQuery = Uri.EscapeDataString(query);
                var lang = GetSteamLanguageCode();
                var url = $"https://store.steampowered.com/api/storesearch?term={encodedQuery}&cc=us&l={lang}";

                var response = await _httpClient.GetStringAsync(url);
                var json = JObject.Parse(response);

                var items = json["items"] as JArray;
                if (items == null || !items.Any())
                {
                    if (lang != "english")
                    {
                        url = $"https://store.steampowered.com/api/storesearch?term={encodedQuery}&cc=us&l=english";
                        response = await _httpClient.GetStringAsync(url);
                        json = JObject.Parse(response);
                        items = json["items"] as JArray;
                    }

                    if (items == null || !items.Any())
                    {
                        url = $"https://store.steampowered.com/api/storesearch?term={encodedQuery}&l=english";
                        response = await _httpClient.GetStringAsync(url);
                        json = JObject.Parse(response);
                        items = json["items"] as JArray;
                    }

                    if (items == null || !items.Any())
                    {
                        logger.Info($"[SteamMetadata] Search returned 0 results for: '{query}'");
                        return 0;
                    }
                }

                var firstItem = items[0];
                var type = firstItem["type"]?.ToString();
                var name = firstItem["name"]?.ToString();

                if (type == "Game" || type == "app")
                {
                    var id = firstItem["id"]?.Value<int>() ?? 0;
                    logger.Info($"[SteamMetadata] Found game '{name}' (AppId: {id})");
                    return id;
                }

                foreach (var item in items)
                {
                    var itemType = item["type"]?.ToString();
                    if (itemType == "Game" || itemType == "app")
                    {
                        var id = item["id"]?.Value<int>() ?? 0;
                        var gName = item["name"]?.ToString();
                        logger.Info($"[SteamMetadata] Found game '{gName}' (AppId: {id})");
                        return id;
                    }
                }

                logger.Info($"[SteamMetadata] No game-type results found for: '{query}'");
                return 0;
            }
            catch (Exception ex)
            {
                logger.Warn($"[SteamMetadata] Search failed: {ex.Message}");
                return 0;
            }
        }

        private async Task<bool> ApplySteamMetadataAsync(Game game, int appId)
        {
            try
            {
                var lang = GetSteamLanguageCode();
                var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l={lang}";

                var response = await _httpClient.GetStringAsync(url);
                var json = JObject.Parse(response);

                var appData = json[appId.ToString()];
                if (appData == null || appData["success"]?.Value<bool>() != true)
                {
                    if (lang != "english")
                    {
                        url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english";
                        response = await _httpClient.GetStringAsync(url);
                        json = JObject.Parse(response);
                        appData = json[appId.ToString()];
                    }

                    if (appData == null || appData["success"]?.Value<bool>() != true)
                    {
                        logger.Info($"[SteamMetadata] AppId {appId} returned unsuccessful response");
                        return false;
                    }
                }

                var data = appData["data"];
                if (data == null) return false;

                var steamName = data["name"]?.ToString();
                
                // Всегда получаем английское название для game.Name
                string englishName = steamName;
                if (lang != "english")
                {
                    try
                    {
                        var englishUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english";
                        var englishResponse = await _httpClient.GetStringAsync(englishUrl);
                        var englishJson = JObject.Parse(englishResponse);
                        var englishAppData = englishJson[appId.ToString()];
                        
                        if (englishAppData != null && englishAppData["success"]?.Value<bool>() == true)
                        {
                            var englishData = englishAppData["data"];
                            if (englishData != null)
                            {
                                englishName = englishData["name"]?.ToString() ?? steamName;
                                logger.Info($"[SteamMetadata] Using English name: '{englishName}' (localized was: '{steamName}')");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Warn($"[SteamMetadata] Failed to fetch English name for metadata: {ex.Message}");
                        // Fallback: используем локализованное название
                    }
                }

                bool changed = false;

                if (game.GameId != appId.ToString())
                {
                    game.GameId = appId.ToString();
                    changed = true;
                }

                // Используем английское название для game.Name
                if (!string.IsNullOrWhiteSpace(englishName))
                {
                    var currentName = game.Name?.Trim() ?? "";
                    var isRepackName = currentName.Contains("(") || currentName.Contains("[") ||
                                       currentName.Contains("FitGirl") || currentName.Contains("DODI") ||
                                       currentName.Contains("ElAmigos") || currentName.Contains("KaOs") ||
                                       currentName.Contains("RG") || currentName.Contains("Repack");

                    if (isRepackName || !currentName.Equals(englishName, StringComparison.OrdinalIgnoreCase))
                    {
                        game.Name = englishName;
                        changed = true;
                    }
                }

                var shortDesc = data["short_description"]?.ToString();
                var detailedDesc = data["detailed_description"]?.ToString();

                if (!string.IsNullOrEmpty(shortDesc) || !string.IsNullOrEmpty(detailedDesc))
                {
                    if (string.IsNullOrEmpty(game.Description))
                    {
                        game.Description = BuildDescription(shortDesc, detailedDesc);
                        changed = true;
                    }
                }

                var genres = data["genres"] as JArray;
                if (genres != null && genres.Any())
                {
                    var genreNames = genres.Select(g => g["description"]?.ToString())
                                           .Where(n => !string.IsNullOrEmpty(n))
                                           .ToList();

                    if (genreNames.Any())
                    {
                        game.GenreIds = AddGenresToDatabase(genreNames);
                        changed = true;
                    }
                }

                var developers = data["developers"] as JArray;
                if (developers != null && developers.Any())
                {
                    var devNames = developers.Select(d => d.ToString())
                                             .Where(n => !string.IsNullOrEmpty(n))
                                             .ToList();

                    if (devNames.Any())
                    {
                        game.DeveloperIds = AddCompaniesToDatabase(devNames);
                        changed = true;
                    }
                }

                var publishers = data["publishers"] as JArray;
                if (publishers != null && publishers.Any())
                {
                    var pubNames = publishers.Select(p => p.ToString())
                                             .Where(n => !string.IsNullOrEmpty(n))
                                             .ToList();

                    if (pubNames.Any())
                    {
                        game.PublisherIds = AddCompaniesToDatabase(pubNames);
                        changed = true;
                    }
                }

                var releaseDate = data["release_date"];
                if (releaseDate != null)
                {
                    var dateStr = releaseDate["date"]?.ToString();
                    if (!string.IsNullOrEmpty(dateStr) && game.ReleaseDate == null)
                    {
                        if (DateTime.TryParse(dateStr, out var parsedDate))
                        {
                            game.ReleaseDate = new ReleaseDate(parsedDate);
                            changed = true;
                        }
                    }
                }

                var categories = data["categories"] as JArray;
                if (categories != null && categories.Any())
                {
                    var catNames = categories.Select(c => c["description"]?.ToString())
                                             .Where(n => !string.IsNullOrEmpty(n))
                                             .Where(n => !n.StartsWith("Steam"))
                                             .ToList();

                    if (catNames.Any())
                    {
                        game.FeatureIds = AddFeaturesToDatabase(catNames);
                        changed = true;
                    }
                }

                try
                {
                    var recommendations = data["recommendations"];
                    if (recommendations != null && recommendations.Type == JTokenType.Object)
                    {
                        var total = recommendations["total"]?.Value<int>();
                        if (total.HasValue && game.CommunityScore == null)
                        {
                            var score = total > 0 ? Math.Min(100, (int)Math.Log10((double)total.Value) * 20) : 0;
                            game.CommunityScore = score;
                            changed = true;
                        }
                    }
                }
                catch { }

                try
                {
                    var metacritic = data["metacritic"];
                    if (metacritic != null && metacritic.Type == JTokenType.Object)
                    {
                        var score = metacritic["score"]?.Value<int>();
                        if (score.HasValue && game.CriticScore == null)
                        {
                            game.CriticScore = score.Value;
                            changed = true;
                        }
                    }
                }
                catch { }

                try
                {
                    var ratings = data["ratings"];
                    if (ratings != null && ratings.Type == JTokenType.Object && !(game.AgeRatingIds?.Any() ?? false))
                    {
                        string ratingName = null;

                        var pegi = ratings["pegi"] as JObject;
                        if (pegi != null)
                        {
                            var ratingCode = pegi["rating"]?.ToString();
                            if (!string.IsNullOrEmpty(ratingCode))
                            {
                                ratingName = $"PEGI {ratingCode}";
                            }
                        }

                        if (ratingName == null)
                        {
                            var esrb = ratings["esrb"] as JObject;
                            if (esrb != null)
                            {
                                var esrbCode = esrb["rating"]?.ToString()?.ToUpperInvariant();
                                ratingName = esrbCode switch
                                {
                                    "E" => "PEGI 3",
                                    "E10" => "PEGI 7",
                                    "T" => "PEGI 12",
                                    "M" => "PEGI 16",
                                    "AO" => "PEGI 18",
                                    _ => null
                                };
                            }
                        }

                        if (ratingName == null)
                        {
                            var usk = ratings["usk"] as JObject;
                            if (usk != null)
                            {
                                var uskCode = usk["rating"]?.ToString();
                                if (!string.IsNullOrEmpty(uskCode) && uskCode != "0")
                                {
                                    ratingName = $"PEGI {uskCode}";
                                }
                            }
                        }

                        if (ratingName == null)
                        {
                            var steamGermany = ratings["steam_germany"] as JObject;
                            if (steamGermany != null)
                            {
                                var germanRating = steamGermany["rating"]?.ToString();
                                if (!string.IsNullOrEmpty(germanRating))
                                {
                                    ratingName = $"PEGI {germanRating}";
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(ratingName))
                        {
                            var existing = _api.Database.AgeRatings.FirstOrDefault(a =>
                                string.Equals(a.Name, ratingName, StringComparison.OrdinalIgnoreCase));

                            if (existing == null)
                            {
                                var newRating = new AgeRating(ratingName);
                                _api.Database.AgeRatings.Add(newRating);
                                game.AgeRatingIds = new List<Guid> { newRating.Id };
                            }
                            else
                            {
                                game.AgeRatingIds = new List<Guid> { existing.Id };
                            }

                            changed = true;
                        }
                    }
                }
                catch { }

                var series = data["series"];
                if (series != null)
                {
                    var seriesName = series["description"]?.ToString();
                    if (!string.IsNullOrEmpty(seriesName) && !(game.SeriesIds?.Any() ?? false))
                    {
                        var existing = _api.Database.Series.FirstOrDefault(s =>
                            string.Equals(s.Name, seriesName, StringComparison.OrdinalIgnoreCase));
                        if (existing == null)
                        {
                            var newSeries = new Series(seriesName);
                            _api.Database.Series.Add(newSeries);
                            game.SeriesIds = new List<Guid> { newSeries.Id };
                        }
                        else
                        {
                            game.SeriesIds = new List<Guid> { existing.Id };
                        }
                        changed = true;
                    }
                }

                if (!(game.PlatformIds?.Any() ?? false))
                {
                    var pcPlatform = _api.Database.Platforms.FirstOrDefault(p =>
                        string.Equals(p.Name, "PC", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.Name, "Windows", StringComparison.OrdinalIgnoreCase));
                    if (pcPlatform == null)
                    {
                        pcPlatform = new Platform("PC");
                        _api.Database.Platforms.Add(pcPlatform);
                    }
                    game.PlatformIds = new List<Guid> { pcPlatform.Id };
                    changed = true;
                }

                var tagNames = await FetchSteamTagsAsync(appId);
                if (tagNames.Any())
                {
                    var existingTagIds = game.TagIds ?? new List<Guid>();
                    foreach (var tagName in tagNames)
                    {
                        var existing = _api.Database.Tags.FirstOrDefault(t =>
                            string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase));
                        if (existing == null)
                        {
                            var newTag = new Tag(tagName);
                            _api.Database.Tags.Add(newTag);
                            existingTagIds.Add(newTag.Id);
                        }
                        else if (!existingTagIds.Contains(existing.Id))
                        {
                            existingTagIds.Add(existing.Id);
                        }
                    }
                    if (existingTagIds.Count > (game.TagIds?.Count ?? 0))
                    {
                        game.TagIds = existingTagIds;
                        changed = true;
                    }
                }

                if (game.Links == null)
                    game.Links = new System.Collections.ObjectModel.ObservableCollection<Link>();

                var linkData = new[]
                {
                    ("Steam", $"https://store.steampowered.com/app/{appId}/"),
                    ("Community Hub", $"https://steamcommunity.com/app/{appId}/"),
                    ("Discussions", $"https://steamcommunity.com/app/{appId}/discussions/"),
                    ("Guides", $"https://steamcommunity.com/app/{appId}/guides/"),
                    ("News", $"https://store.steampowered.com/news/?appids={appId}")
                };

                foreach (var (linkName, linkUrl) in linkData)
                {
                    if (!game.Links.Any(l => l.Name == linkName))
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (!game.Links.Any(l => l.Name == linkName))
                                game.Links.Add(new Link(linkName, linkUrl));
                        });
                    }
                }

                if (changed)
                {
                    logger.Info($"[SteamMetadata] Successfully applied metadata for: {game.Name}");
                    return true;
                }

                logger.Info($"[SteamMetadata] No metadata changes needed for: {game.Name}");
                return false;
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"[SteamMetadata] Failed to apply metadata for AppId {appId}");
                return false;
            }
        }

        private async Task<string> DownloadImageAsync(string url, Guid gameId, string imageType)
        {
            try
            {
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    logger.Warn($"[SteamMetadata] HTTP {response.StatusCode} for {imageType}: {url}");
                    return null;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync();

                var ext = url.Contains(".png") ? ".png" : ".jpg";
                var tempFile = Path.Combine(Path.GetTempPath(), $"{imageType}_{Guid.NewGuid():N}{ext}");
                File.WriteAllBytes(tempFile, bytes);

                var dbFileId = _api.Database.AddFile(tempFile, gameId);
                File.Delete(tempFile);

                return dbFileId;
            }
            catch (Exception ex)
            {
                logger.Warn($"[SteamMetadata] Failed to download {imageType}: {url} - {ex.Message}");
                return null;
            }
        }

        private List<Guid> AddGenresToDatabase(List<string> genreNames)
        {
            var ids = new List<Guid>();
            var db = _api.Database;

            foreach (var name in genreNames)
            {
                var existing = db.Genres.FirstOrDefault(g =>
                    string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    ids.Add(existing.Id);
                }
                else
                {
                    var newGenre = new Genre(name);
                    db.Genres.Add(newGenre);
                    ids.Add(newGenre.Id);
                }
            }

            return ids;
        }

        private List<Guid> AddCompaniesToDatabase(List<string> companyNames)
        {
            var ids = new List<Guid>();
            var db = _api.Database;

            foreach (var name in companyNames)
            {
                var existing = db.Companies.FirstOrDefault(c =>
                    string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    ids.Add(existing.Id);
                }
                else
                {
                    var newCompany = new Company(name);
                    db.Companies.Add(newCompany);
                    ids.Add(newCompany.Id);
                }
            }

            return ids;
        }

        private List<Guid> AddFeaturesToDatabase(List<string> featureNames)
        {
            var ids = new List<Guid>();
            var db = _api.Database;

            foreach (var name in featureNames)
            {
                var existing = db.Features.FirstOrDefault(f =>
                    string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    ids.Add(existing.Id);
                }
                else
                {
                    var newFeature = new GameFeature(name);
                    db.Features.Add(newFeature);
                    ids.Add(newFeature.Id);
                }
            }

            return ids;
        }

        private async Task<List<string>> FetchSteamTagsAsync(int appId)
        {
            var tags = new List<string>();
            try
            {
                var lang = GetSteamLanguageCode();
                var url = $"https://store.steampowered.com/app/{appId}/?l={lang}";
                var response = await _httpClient.GetStringAsync(url);

                var match = System.Text.RegularExpressions.Regex.Match(response,
                    @"g_rgAppTagDetails\s*=\s*(\{.+?\})\s*;");

                if (match.Success)
                {
                    var tagObj = JObject.Parse(match.Groups[1].Value);
                    foreach (var prop in tagObj.Properties())
                    {
                        var tag = prop.Value["name"]?.ToString() ?? prop.Name;
                        if (!string.IsNullOrEmpty(tag))
                            tags.Add(tag);
                    }
                }

                if (tags.Count == 0)
                {
                    var tagPatterns = new[]
                    {
                        @"<a[^>]*class=""[^""]*app_tag[^""]*""[^>]*>([^<]+)<\/a>",
                        @"<a[^>]*class=""[^""]*tag[^""]*""[^>]*>([^<]+)<\/a>",
                        @"data-ds-tagid[^>]*>([^<]+)<"
                    };

                    foreach (var pattern in tagPatterns)
                    {
                        var tagMatches = System.Text.RegularExpressions.Regex.Matches(response, pattern,
                            System.Text.RegularExpressions.RegexOptions.Singleline);

                        foreach (System.Text.RegularExpressions.Match m in tagMatches)
                        {
                            var tag = m.Groups[1].Value.Trim();
                            if (!string.IsNullOrEmpty(tag) && !tags.Contains(tag))
                            {
                                tags.Add(tag);
                            }
                        }

                        if (tags.Count > 0) break;
                    }
                }

                if (tags.Count == 0)
                {
                    var metaMatch = System.Text.RegularExpressions.Regex.Match(response,
                        @"<meta[^>]*name=""keywords""[^>]*content=""([^""]+)""",
                        System.Text.RegularExpressions.RegexOptions.Singleline);

                    if (metaMatch.Success)
                    {
                        var keywordTags = metaMatch.Groups[1].Value.Split(',');
                        foreach (var t in keywordTags)
                        {
                            var tag = t.Trim();
                            if (!string.IsNullOrEmpty(tag) && !tags.Contains(tag))
                            {
                                tags.Add(tag);
                            }
                        }
                    }
                }

                if (tags.Count == 0)
                {
                    try
                    {
                        var tagApiUrl = $"https://store.steampowered.com/tagdetails/GetAppTags?appid={appId}";
                        var tagResponse = await _httpClient.GetStringAsync(tagApiUrl);
                        var tagJson = JObject.Parse(tagResponse);
                        var tagData = tagJson["tags"] as JObject;
                        if (tagData != null)
                        {
                            foreach (var prop in tagData.Properties())
                            {
                                tags.Add(prop.Name);
                            }
                        }
                        else
                        {
                            var tagArray = tagJson["tags"] as JArray;
                            if (tagArray != null)
                            {
                                foreach (var item in tagArray)
                                {
                                    var tag = item["tag"]?.ToString() ?? item["name"]?.ToString() ?? item.ToString();
                                    if (!string.IsNullOrEmpty(tag) && !tags.Contains(tag))
                                    {
                                        tags.Add(tag);
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                logger.Warn($"[SteamMetadata] FetchSteamTagsAsync failed: {ex.Message}");
            }
            return tags;
        }

        private string BuildDescription(string shortDesc, string detailedDesc)
        {
            var result = "";

            if (!string.IsNullOrEmpty(shortDesc))
            {
                result += "<p>" + shortDesc + "</p><br>";
            }

            if (!string.IsNullOrEmpty(detailedDesc))
            {
                result += CleanHtml(detailedDesc);
            }

            return result.Trim();
        }

        private string CleanHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return html;

            var result = html;

            result = System.Text.RegularExpressions.Regex.Replace(result,
                @"<video[^>]*?poster\s*=\s*[""']([^""']+)[""'][^>]*>.*?<\/video>",
                "<img src=\"$1\">",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            result = System.Text.RegularExpressions.Regex.Replace(result,
                @"<video[^>]*>.*?<\/video>",
                "",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            result = System.Text.RegularExpressions.Regex.Replace(result,
                @"<source[^>]*>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            result = System.Text.RegularExpressions.Regex.Replace(result,
                @"<iframe[^>]*>.*?<\/iframe>",
                "",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            result = System.Text.RegularExpressions.Regex.Replace(result,
                @"<script[^>]*>.*?<\/script>",
                "",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            result = System.Text.RegularExpressions.Regex.Replace(result,
                @"<style[^>]*>.*?<\/style>",
                "",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            result = System.Text.RegularExpressions.Regex.Replace(result,
                @"<object[^>]*>.*?<\/object>",
                "",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            result = System.Text.RegularExpressions.Regex.Replace(result,
                @"<embed[^>]*>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            result = System.Text.RegularExpressions.Regex.Replace(result,
                @"<h1([^>]*)>",
                "<h2$1>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            result = result.Replace("</h1>", "</h2>");

            result = System.Text.RegularExpressions.Regex.Replace(result,
                @"\s*width\s*=\s*[""']?[^""'\s>]+[""']?",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            result = System.Text.RegularExpressions.Regex.Replace(result,
                @"\s*height\s*=\s*[""']?[^""'\s>]+[""']?",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            result = result.Replace("&nbsp;", " ");
            result = result.Replace("&amp;", "&");
            result = result.Replace("&lt;", "<");
            result = result.Replace("&gt;", ">");
            result = result.Replace("&quot;", "\"");

            return result.Trim();
        }

        private string GetSteamLanguageCode()
        {
            var playniteLang = _api.ApplicationSettings.Language?.ToLowerInvariant() ?? "english";

            return playniteLang switch
            {
                var l when l.StartsWith("ru") => "russian",
                var l when l.StartsWith("de") => "german",
                var l when l.StartsWith("fr") => "french",
                var l when l.StartsWith("es") => "spanish",
                var l when l.StartsWith("pt") => "brazilian",
                var l when l.StartsWith("zh") => "schinese",
                var l when l.StartsWith("ja") => "japanese",
                var l when l.StartsWith("ko") => "koreana",
                var l when l.StartsWith("pl") => "polish",
                var l when l.StartsWith("it") => "italian",
                var l when l.StartsWith("tr") => "turkish",
                var l when l.StartsWith("uk") => "ukrainian",
                var l when l.StartsWith("th") => "thai",
                var l when l.StartsWith("vi") => "vietnamese",
                var l when l.StartsWith("cs") => "czech",
                var l when l.StartsWith("nl") => "dutch",
                var l when l.StartsWith("sv") => "swedish",
                var l when l.StartsWith("no") => "norwegian",
                var l when l.StartsWith("da") => "danish",
                var l when l.StartsWith("fi") => "finnish",
                var l when l.StartsWith("el") => "greek",
                var l when l.StartsWith("hu") => "hungarian",
                var l when l.StartsWith("ro") => "romanian",
                var l when l.StartsWith("bg") => "bulgarian",
                var l when l.StartsWith("ar") => "arabic",
                _ => "english"
            };
        }
    }
}
