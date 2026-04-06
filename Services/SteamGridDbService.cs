using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace HydraTorrent.Services
{
    public class SteamGridDbService
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly HttpClient _httpClient;
        private readonly HttpClient _downloadClient; // Для скачивания изображений без авторизации
        private readonly string _apiKey;
        private readonly IPlayniteAPI _api;

        static SteamGridDbService()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        public SteamGridDbService(IPlayniteAPI api, string apiKey)
        {
            _api = api;
            _apiKey = apiKey;
            var handler = new HttpClientHandler()
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            
            // Отдельный клиент для скачивания изображений (без авторизации)
            _downloadClient = new HttpClient(new HttpClientHandler()
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            }) { Timeout = TimeSpan.FromSeconds(15) };
        }

        public async Task<string> GetCoverUrlAsync(string gameName, int? releaseYear = null)
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                return null;
            }

            try
            {
                var gridId = await GetGridIdFromSearchAsync(gameName, releaseYear);
                if (gridId == 0)
                {
                    return null;
                }

                // Запрашиваем только квадратные изображения 512x512 и 1024x1024
                var gridsUrl = $"https://www.steamgriddb.com/api/v2/grids/game/{gridId}?dimensions=512x512,1024x1024";
                var gridsResponse = await _httpClient.GetStringAsync(gridsUrl);
                var gridsJson = JObject.Parse(gridsResponse);
                var gridsData = gridsJson["data"] as JArray;

                if (gridsData == null || !gridsData.Any())
                {
                    return null;
                }

                // Приоритет: 1024x1024 > 512x512
                foreach (var item in gridsData)
                {
                    var width = item["width"]?.Value<int>() ?? 0;
                    var height = item["height"]?.Value<int>() ?? 0;

                    if (width == 1024 && height == 1024)
                    {
                        return item["url"]?.ToString() ?? item["thumb"]?.ToString();
                    }
                }

                // Если нет 1024x1024, берем первое доступное (512x512)
                var first = gridsData[0];
                return first["url"]?.ToString() ?? first["thumb"]?.ToString();
            }
            catch (Exception ex)
            {
                logger.Warn($"[SteamGridDB] GetCoverUrlAsync failed: {ex.Message}");
                return null;
            }
        }

        public async Task<string> GetCoverUrlForPreviewAsync(string gameName, int? releaseYear = null)
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                return null;
            }

            try
            {
                var gridId = await GetGridIdFromSearchAsync(gameName, releaseYear);
                if (gridId == 0)
                {
                    return null;
                }

                // Пробуем запросить с параметром types=static (вертикальные обложки обычно static)
                // Также можно попробовать styles=alternate,blurred,white_logo,material,no_logo
                var gridsUrl = $"https://www.steamgriddb.com/api/v2/grids/game/{gridId}?types=static";
                logger.Info($"[SteamGridDB] Requesting static grids for gridId {gridId}");
                
                var gridsResponse = await _httpClient.GetStringAsync(gridsUrl);
                var gridsJson = JObject.Parse(gridsResponse);
                var gridsData = gridsJson["data"] as JArray;

                if (gridsData == null || !gridsData.Any())
                {
                    logger.Info($"[SteamGridDB] No static grids found for gridId {gridId}, trying without filter");
                    // Пробуем без фильтра
                    gridsUrl = $"https://www.steamgriddb.com/api/v2/grids/game/{gridId}";
                    gridsResponse = await _httpClient.GetStringAsync(gridsUrl);
                    gridsJson = JObject.Parse(gridsResponse);
                    gridsData = gridsJson["data"] as JArray;
                    
                    if (gridsData == null || !gridsData.Any())
                    {
                        logger.Info($"[SteamGridDB] No grids found at all, falling back to square");
                        return await GetCoverUrlAsync(gameName, releaseYear);
                    }
                }

                logger.Info($"[SteamGridDB] Received {gridsData.Count} grids, filtering for 2:3 ratio");

                // Фильтруем изображения с соотношением 2:3 (допуск ±0.01)
                var verticalCovers = new List<(int width, int height, string url, int priority)>();
                
                foreach (var item in gridsData)
                {
                    var width = item["width"]?.Value<int>() ?? 0;
                    var height = item["height"]?.Value<int>() ?? 0;
                    var url = item["url"]?.ToString() ?? item["thumb"]?.ToString();

                    if (width == 0 || height == 0 || string.IsNullOrEmpty(url))
                        continue;

                    // Проверяем соотношение 2:3 (0.666...)
                    double ratio = (double)width / height;
                    logger.Debug($"[SteamGridDB] Grid: {width}x{height}, ratio: {ratio:F3}");
                    
                    if (Math.Abs(ratio - (2.0 / 3.0)) < 0.01)
                    {
                        // Определяем приоритет по размеру
                        int priority = 0;
                        if (width == 600 && height == 900) priority = 3;
                        else if (width == 460 && height == 690) priority = 2;
                        else if (width == 342 && height == 512) priority = 1;
                        else priority = 0; // Другие размеры 2:3

                        verticalCovers.Add((width, height, url, priority));
                        logger.Info($"[SteamGridDB] Found 2:3 cover: {width}x{height} (priority: {priority})");
                    }
                }

                if (verticalCovers.Any())
                {
                    // Сортируем по приоритету, затем по размеру (больше = лучше)
                    var best = verticalCovers.OrderByDescending(c => c.priority).ThenByDescending(c => c.width).First();
                    logger.Info($"[SteamGridDB] Selected {best.width}x{best.height} cover for preview");
                    return best.url;
                }

                logger.Info($"[SteamGridDB] No 2:3 covers found, falling back to square");
                // Fallback: если нет 2:3, используем квадратные
                return await GetCoverUrlAsync(gameName, releaseYear);
            }
            catch (Exception ex)
            {
                logger.Warn($"[SteamGridDB] GetCoverUrlForPreviewAsync failed: {ex.Message}");
                return null;
            }
        }

        private async Task<int> GetGridIdFromSearchAsync(string gameName, int? releaseYear = null)
        {
            try
            {
                var url = $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(gameName)}";
                var response = await _httpClient.GetStringAsync(url);
                var json = JObject.Parse(response);
                var data = json["data"] as JArray;

                if (data == null || !data.Any())
                {
                    return 0;
                }

                int gridId = 0;

                if (releaseYear.HasValue)
                {
                    foreach (var item in data)
                    {
                        var types = item["types"] as JArray;
                        if (types == null || !types.Any(t => t.ToString() == "game" || t.ToString() == "steam"))
                            continue;

                        var name = item["name"]?.ToString() ?? "";
                        var id = item["id"]?.Value<int>() ?? 0;
                        var sgdbRelease = item["release_date"]?.Value<long>() ?? 0;

                        if (sgdbRelease > 0)
                        {
                            var sgdbYear = DateTimeOffset.FromUnixTimeSeconds(sgdbRelease).UtcDateTime.Year;
                            if (sgdbYear == releaseYear.Value && name.Equals(gameName, StringComparison.OrdinalIgnoreCase))
                            {
                                gridId = id;
                                break;
                            }
                        }
                    }
                }

                if (gridId == 0)
                {
                    foreach (var item in data)
                    {
                        var types = item["types"] as JArray;
                        if (types != null && types.Any(t => t.ToString() == "game" || t.ToString() == "steam"))
                        {
                            var name = item["name"]?.ToString() ?? "";
                            if (name.Equals(gameName, StringComparison.OrdinalIgnoreCase))
                            {
                                gridId = item["id"]?.Value<int>() ?? 0;
                                break;
                            }
                        }
                    }
                }

                if (gridId == 0)
                {
                    foreach (var item in data)
                    {
                        var types = item["types"] as JArray;
                        if (types != null && types.Any(t => t.ToString() == "game" || t.ToString() == "steam"))
                        {
                            gridId = item["id"]?.Value<int>() ?? 0;
                            break;
                        }
                    }
                }

                if (gridId == 0 && data.Any())
                {
                    gridId = data[0]["id"]?.Value<int>() ?? 0;
                }

                return gridId;
            }
            catch (Exception ex)
            {
                logger.Warn($"[SteamGridDB] GetGridIdFromSearchAsync failed: {ex.Message}");
                return 0;
            }
        }

        public async Task<bool> DownloadImagesAsync(Game game, string steamAppId, string gameName, int? releaseYear = null)
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                logger.Info("[SteamGridDB] No API key configured");
                return false;
            }

            try
            {
                var gridId = await GetGridIdAsync(steamAppId, gameName, releaseYear);
                if (gridId == 0)
                {
                    logger.Info($"[SteamGridDB] No grid found for: {gameName} (Steam AppId: {steamAppId})");
                    return false;
                }

                logger.Info($"[SteamGridDB] Found grid {gridId} for: {gameName}");

                bool changed = false;

                if (string.IsNullOrEmpty(game.CoverImage))
                {
                    var coverUrl = await GetBestImageAsync(gridId, "grid");
                    if (!string.IsNullOrEmpty(coverUrl))
                    {
                        game.CoverImage = await DownloadImageAsync(coverUrl, game.Id, "cover");
                        if (!string.IsNullOrEmpty(game.CoverImage))
                        {
                            changed = true;
                        }
                    }
                }

                if (string.IsNullOrEmpty(game.BackgroundImage))
                {
                    var heroUrl = await GetBestImageAsync(gridId, "hero");
                    if (!string.IsNullOrEmpty(heroUrl))
                    {
                        game.BackgroundImage = await DownloadImageAsync(heroUrl, game.Id, "background");
                        if (!string.IsNullOrEmpty(game.BackgroundImage))
                        {
                            changed = true;
                        }
                    }
                }

                if (string.IsNullOrEmpty(game.Icon))
                {
                    var iconUrl = await GetBestImageAsync(gridId, "logo");
                    if (!string.IsNullOrEmpty(iconUrl))
                    {
                        game.Icon = await DownloadImageAsync(iconUrl, game.Id, "icon");
                        if (!string.IsNullOrEmpty(game.Icon))
                        {
                            changed = true;
                        }
                    }
                }

                return changed;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[SteamGridDB] Failed to download images");
                return false;
            }
        }

        private async Task<int> GetGridIdAsync(string steamAppId, string gameName, int? releaseYear = null)
        {
            try
            {
                var url = $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(gameName)}";
                var response = await _httpClient.GetStringAsync(url);
                var json = JObject.Parse(response);
                var data = json["data"] as JArray;

                if (data == null)
                {
                    return 0;
                }

                if (!data.Any())
                {
                    return 0;
                }

                int _gridId = 0;

                if (releaseYear.HasValue)
                {
                    foreach (var item in data)
                    {
                        var types = item["types"] as JArray;
                        if (types == null || !types.Any(t => t.ToString() == "game" || t.ToString() == "steam"))
                            continue;

                        var name = item["name"]?.ToString();
                        var id = item["id"]?.Value<int>() ?? 0;
                        var sgdbRelease = item["release_date"]?.Value<long>() ?? 0;

                        if (sgdbRelease > 0)
                        {
                            var sgdbYear = DateTimeOffset.FromUnixTimeSeconds(sgdbRelease).UtcDateTime.Year;

                            if (sgdbYear == releaseYear.Value && name.Equals(gameName, StringComparison.OrdinalIgnoreCase))
                            {
                                logger.Info($"[SteamGridDB] Found matching game: '{name}' (gridId: {id}, year: {sgdbYear})");
                                return id;
                            }
                        }
                    }
                }

                // Fallback: find by exact name match
                foreach (var item in data)
                {
                    var types = item["types"] as JArray;
                    var name = item["name"]?.ToString();
                    if (name != null && name.Equals(gameName, StringComparison.OrdinalIgnoreCase))
                    {
                        var id = item["id"]?.Value<int>() ?? 0;
                        logger.Info($"[SteamGridDB] Found matching game by name: '{name}' (gridId: {id})");
                        return id;
                    }
                }

                // Final fallback: return first game/steam type item
                foreach (var item in data)
                {
                    var types = item["types"] as JArray;
                    if (types != null && types.Any(t => t.ToString() == "game" || t.ToString() == "steam"))
                    {
                        var id = item["id"]?.Value<int>() ?? 0;
                        var name = item["name"]?.ToString();
                        return id;
                    }
                }

                if (data.Any())
                {
                    var id = data[0]["id"]?.Value<int>() ?? 0;
                    return id;
                }

                return 0;
            }
            catch (Exception ex)
            {
                logger.Warn($"[SteamGridDB] Search failed: {ex.Message}");
                return 0;
            }
        }

        private class ImageCandidate
        {
            public int Width { get; set; }
            public int Score { get; set; }
            public string Style { get; set; }
            public string Url { get; set; }
        }

        private async Task<string> GetBestImageAsync(int gridId, string imageType)
        {
            try
            {
                var url = imageType switch
                {
                    "grid" => $"https://www.steamgriddb.com/api/v2/grids/game/{gridId}?dimensions=512x512,1024x1024",
                    "hero" => $"https://www.steamgriddb.com/api/v2/heroes/game/{gridId}",
                    "logo" => $"https://www.steamgriddb.com/api/v2/icons/game/{gridId}",
                    _ => throw new ArgumentException($"Unknown image type: {imageType}")
                };

                var response = await _httpClient.GetStringAsync(url);
                var json = JObject.Parse(response);
                var data = json["data"] as JArray;

                if (data == null || !data.Any())
                {
                    return null;
                }

                if (imageType == "logo")
            {
                foreach (var item in data)
                {
                    var width = item["width"]?.Value<int>() ?? 0;
                    var height = item["height"]?.Value<int>() ?? 0;
                    var thumb = item["thumb"]?.ToString();

                    if (width > 0 && height > 0 && width == height && !string.IsNullOrEmpty(thumb))
                    {
                        return thumb;
                    }
                }

                var firstIcon = data[0];
                return firstIcon["thumb"]?.ToString() ?? firstIcon["url"]?.ToString();
            }

            if (imageType == "hero")
                {
                    ImageCandidate best = null;
                    foreach (var item in data)
                    {
                        var width = item["width"]?.Value<int>() ?? 0;
                        if (width < 1920) continue;

                        var style = item["style"]?.ToString() ?? "";
                        var thumb = item["thumb"]?.ToString();
                        var imgUrl = item["url"]?.ToString() ?? thumb;
                        if (string.IsNullOrEmpty(imgUrl)) continue;

                        var score = 0;
                        if (style.Contains("alternate")) score += 10;
                        if (width >= 3840) score += 5;

                        var candidate = new ImageCandidate { Width = width, Score = score, Style = style, Url = imgUrl };
                        if (best == null || candidate.Score > best.Score || (candidate.Score == best.Score && candidate.Width > best.Width))
                        {
                            best = candidate;
                        }
                    }

                    return best?.Url;
                }

                // Для grid (обложки) API уже вернул только квадратные изображения благодаря параметру dimensions
                logger.Info($"[SteamGridDB] Received {data.Count} square grid images from API");
                
                if (data.Count > 0)
                {
                    // Приоритет: 1024x1024 > 512x512
                    foreach (var item in data)
                    {
                        var width = item["width"]?.Value<int>() ?? 0;
                        var height = item["height"]?.Value<int>() ?? 0;
                        var thumb = item["thumb"]?.ToString();
                        var imgUrl = item["url"]?.ToString() ?? thumb;

                        logger.Info($"[SteamGridDB] Square grid option: {width}x{height}");

                        if (width == 1024 && height == 1024)
                        {
                            logger.Info($"[SteamGridDB] Selected 1024x1024 square grid, URL: {imgUrl}");
                            return imgUrl;
                        }
                    }

                    // Если нет 1024x1024, берем первое доступное (512x512 или другое квадратное)
                    var first = data[0];
                    var firstWidth = first["width"]?.Value<int>() ?? 0;
                    var firstHeight = first["height"]?.Value<int>() ?? 0;
                    var firstUrl = first["url"]?.ToString() ?? first["thumb"]?.ToString();
                    logger.Info($"[SteamGridDB] Selected {firstWidth}x{firstHeight} square grid, URL: {firstUrl}");
                    return firstUrl;
                }

                logger.Warn($"[SteamGridDB] No square grids available for this game");
                return null;
            }
            catch (Exception ex)
            {
                logger.Warn($"[SteamGridDB] GetBestImageAsync failed: {ex.Message}");
                return null;
            }
        }

        private async Task<string> DownloadImageAsync(string url, Guid gameId, string imageType)
        {
            try
            {
                // Используем _downloadClient без авторизации для CDN URL
                var response = await _downloadClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    logger.Warn($"[SteamGridDB] HTTP {response.StatusCode} for {imageType}: {url}");
                    return null;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync();

                string ext;
                if (url.Contains(".png") || imageType == "icon")
                    ext = ".png";
                else if (url.Contains(".ico"))
                    ext = ".png";
                else
                    ext = ".jpg";
                var tempFile = Path.Combine(Path.GetTempPath(), $"{imageType}_{Guid.NewGuid():N}{ext}");

                if (imageType == "background")
                {
                    tempFile = CropTo169(bytes, tempFile);
                }
                else
                {
                    File.WriteAllBytes(tempFile, bytes);
                }

                var dbFileId = _api.Database.AddFile(tempFile, gameId);
                File.Delete(tempFile);

                return dbFileId;
            }
            catch (Exception ex)
            {
                logger.Warn($"[SteamGridDB] Failed to download {imageType}: {ex.Message}");
                return null;
            }
        }

        private string CropTo169(byte[] imageBytes, string outputFile)
        {
            try
            {
                using (var ms = new MemoryStream(imageBytes))
                using (var img = Image.FromStream(ms))
                {
                    double targetRatio = 16.0 / 9.0;
                    double currentRatio = (double)img.Width / img.Height;

                    if (currentRatio > targetRatio)
                    {
                        int newWidth = (int)(img.Height * targetRatio);
                        int startX = (img.Width - newWidth) / 2;

                        var cropRect = new Rectangle(startX, 0, newWidth, img.Height);
                        using (var cropped = new Bitmap(newWidth, img.Height, img.PixelFormat))
                        using (var g = Graphics.FromImage(cropped))
                        {
                            g.DrawImage(img, new Rectangle(0, 0, newWidth, img.Height), cropRect, GraphicsUnit.Pixel);

                            var codec = GetImageCodec(outputFile);
                            cropped.Save(outputFile, codec, null);
                        }
                    }
                    else
                    {
                        File.WriteAllBytes(outputFile, imageBytes);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn($"[SteamGridDB] Failed to crop image: {ex.Message}");
                File.WriteAllBytes(outputFile, imageBytes);
            }
            return outputFile;
        }

        private ImageCodecInfo GetImageCodec(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".png")
                return ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Png.Guid);
            return ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid)
                   ?? ImageCodecInfo.GetImageEncoders().First();
        }
    }
}
