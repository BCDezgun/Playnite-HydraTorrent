using DiscordRPC;
using DiscordRPC.Logging;
using HydraTorrent.Models;
using Playnite.SDK;
using System;

namespace HydraTorrent.Services.Discord
{
    public class DiscordRichPresenceService : IDisposable
    {
        private static readonly Playnite.SDK.ILogger Logger = LogManager.GetLogger();
        private DiscordRpcClient _client;
        private DiscordAssetResolver _assetResolver;
        private IPlayniteAPI _playniteApi;
        private bool _isInitialized = false;
        private bool _isDisposed = false;

        // Сохраняем Timestamps объект для переиспользования
        private Timestamps _currentTimestamps = null;

        public DiscordRichPresenceService(DiscordAssetResolver assetResolver, IPlayniteAPI playniteApi)
        {
            _assetResolver = assetResolver;
            _playniteApi = playniteApi;
        }

        public bool Initialize()
        {
            if (_isInitialized || _isDisposed)
                return _isInitialized;

            try
            {
                var appId = _assetResolver.GetApplicationId();

                if (string.IsNullOrEmpty(appId) || appId == "YOUR_DISCORD_APP_ID")
                {
                    Logger.Warn("[Discord] Application ID not configured. Discord Rich Presence disabled.");
                    return false;
                }

                _client = new DiscordRpcClient(appId)
                {
                    Logger = new ConsoleLogger() { Level = LogLevel.Warning },
                    //SkipIdenticalPresence = false // Отключаем пропуск идентичных presence
                };

                _client.OnReady += (sender, e) =>
                {
                    Logger.Info($"[Discord] Connected to Discord as {e.User.Username}");
                };

                _client.OnError += (sender, e) =>
                {
                    Logger.Error($"[Discord] Error: {e.Message}");
                };

                _client.Initialize();
                _isInitialized = true;

                Logger.Info("[Discord] Rich Presence initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[Discord] Failed to initialize Rich Presence");
                return false;
            }
        }

        public void UpdateDownloading(TorrentResult item, HydraTorrent.TorrentStatusInfo status)
        {
            if (!_isInitialized || _client == null || item == null || status == null)
                return;

            try
            {
                var gameName = item.GameName ?? item.Name;
                var progress = (int)(status.Progress * 100);
                
                Logger.Info($"[Discord] Processing game: {gameName}, GameId: {item.GameId}");
                
                // Получаем Steam ID из item
                string assetKey = null;
                if (item.GameId.HasValue)
                {
                    Logger.Info($"[Discord] GameId has value: {item.GameId.Value}");
                    
                    // Пытаемся получить Steam ID из Playnite
                    var game = _playniteApi?.Database?.Games?.Get(item.GameId.Value);
                    if (game != null)
                    {
                        Logger.Info($"[Discord] Found game in database: {game.Name}");
                        Logger.Info($"[Discord] PluginId: {game.PluginId}, GameId: {game.GameId}");
                        
                        int? steamId = null;
                        
                        // 1. Проверяем Steam metadata (PluginId для Steam)
                        if (game.PluginId == Guid.Parse("cb91dfc9-b977-43bf-8e70-55f46e410fab"))
                        {
                            if (!string.IsNullOrEmpty(game.GameId) && int.TryParse(game.GameId, out var id))
                            {
                                steamId = id;
                                Logger.Info($"[Discord] Found Steam ID from Steam PluginId: {steamId}");
                            }
                        }
                        
                        // 2. Fallback: Проверяем Links на Steam URL
                        if (!steamId.HasValue && game.Links != null)
                        {
                            foreach (var link in game.Links)
                            {
                                if (link.Url != null && link.Url.Contains("store.steampowered.com/app/"))
                                {
                                    var match = System.Text.RegularExpressions.Regex.Match(link.Url, @"/app/(\d+)");
                                    if (match.Success && int.TryParse(match.Groups[1].Value, out var id))
                                    {
                                        steamId = id;
                                        Logger.Info($"[Discord] Found Steam ID from Links: {steamId}");
                                        break;
                                    }
                                }
                            }
                        }
                        
                        // 3. Fallback: Если игра от HydraTorrent, используем GameId напрямую (это Steam ID)
                        if (!steamId.HasValue && game.PluginId == Guid.Parse("c2177dc7-8179-4098-8b6c-d683ce415279"))
                        {
                            if (!string.IsNullOrEmpty(game.GameId) && int.TryParse(game.GameId, out var id))
                            {
                                steamId = id;
                                Logger.Info($"[Discord] Found Steam ID from HydraTorrent GameId: {steamId}");
                            }
                        }
                        
                        if (steamId.HasValue)
                        {
                            assetKey = _assetResolver.GetAssetKey(steamId.Value);
                            Logger.Info($"[Discord] Game: {gameName}, Steam ID: {steamId.Value}, Asset: {assetKey}");
                        }
                        else
                        {
                            Logger.Warn($"[Discord] Game {gameName} has no Steam metadata (PluginId: {game.PluginId}, GameId: {game.GameId})");
                        }
                    }
                    else
                    {
                        Logger.Warn($"[Discord] Game with ID {item.GameId.Value} not found in Playnite database");
                    }
                }
                else
                {
                    Logger.Warn($"[Discord] item.GameId is null for game: {gameName}");
                }
                
                // Fallback: пробуем по имени
                if (string.IsNullOrEmpty(assetKey))
                {
                    assetKey = _assetResolver.GetAssetKeyByName(gameName);
                    Logger.Info($"[Discord] Using fallback for {gameName}, Asset: {assetKey}");
                }

                Logger.Info($"[Discord] Updating: {gameName} - {progress}% (Asset: {assetKey})");

                // Создаем Timestamps только если его еще нет
                if (_currentTimestamps == null)
                {
                    _currentTimestamps = new Timestamps()
                    {
                        Start = DateTime.UtcNow
                    };
                    Logger.Info($"[Discord] Created new Timestamps object");
                }

                var downloadText = Playnite.SDK.ResourceProvider.GetString("LOC_HydraTorrent_Download");

                var presence = new RichPresence()
                {
                    Details = TruncateString($"📥 {gameName}", 128),
                    State = $"{downloadText} • {progress}% • {status.ETA}",
                    Timestamps = _currentTimestamps // Используем сохраненный объект
                };

                if (!string.IsNullOrEmpty(assetKey))
                {
                    presence.Assets = new Assets()
                    {
                        LargeImageKey = assetKey,
                        LargeImageText = TruncateString(gameName, 128),
                        SmallImageKey = "hydra_logo",
                        SmallImageText = "HydraTorrent Plugin"
                    };
                }
                else
                {
                    // Fallback: используем дефолтный ассет если игра не найдена
                    presence.Assets = new Assets()
                    {
                        LargeImageKey = "downloading",
                        LargeImageText = TruncateString(gameName, 128),
                        SmallImageKey = "hydra_logo",
                        SmallImageText = "HydraTorrent Plugin"
                    };
                }

                _client.SetPresence(presence);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[Discord] Error updating downloading status");
            }
        }

        public void UpdatePaused(TorrentResult item, HydraTorrent.TorrentStatusInfo status)
        {
            if (!_isInitialized || _client == null || item == null || status == null)
                return;

            try
            {
                var gameName = item.GameName ?? item.Name;
                var progress = (int)(status.Progress * 100);
                
                Logger.Info($"[Discord] Processing paused game: {gameName}, GameId: {item.GameId}");
                
                // Получаем Steam ID из item (та же логика что и для downloading)
                string assetKey = null;
                if (item.GameId.HasValue)
                {
                    var game = _playniteApi?.Database?.Games?.Get(item.GameId.Value);
                    if (game != null)
                    {
                        int? steamId = null;
                        
                        // 1. Проверяем Steam metadata
                        if (game.PluginId == Guid.Parse("cb91dfc9-b977-43bf-8e70-55f46e410fab"))
                        {
                            if (!string.IsNullOrEmpty(game.GameId) && int.TryParse(game.GameId, out var id))
                            {
                                steamId = id;
                                Logger.Info($"[Discord] Found Steam ID from Steam PluginId: {steamId}");
                            }
                        }
                        
                        // 2. Fallback: Проверяем Links на Steam URL
                        if (!steamId.HasValue && game.Links != null)
                        {
                            foreach (var link in game.Links)
                            {
                                if (link.Url != null && link.Url.Contains("store.steampowered.com/app/"))
                                {
                                    var match = System.Text.RegularExpressions.Regex.Match(link.Url, @"/app/(\d+)");
                                    if (match.Success && int.TryParse(match.Groups[1].Value, out var id))
                                    {
                                        steamId = id;
                                        Logger.Info($"[Discord] Found Steam ID from Links: {steamId}");
                                        break;
                                    }
                                }
                            }
                        }
                        
                        // 3. Fallback: HydraTorrent GameId
                        if (!steamId.HasValue && game.PluginId == Guid.Parse("c2177dc7-8179-4098-8b6c-d683ce415279"))
                        {
                            if (!string.IsNullOrEmpty(game.GameId) && int.TryParse(game.GameId, out var id))
                            {
                                steamId = id;
                                Logger.Info($"[Discord] Found Steam ID from HydraTorrent GameId: {steamId}");
                            }
                        }
                        
                        if (steamId.HasValue)
                        {
                            assetKey = _assetResolver.GetAssetKey(steamId.Value);
                            Logger.Info($"[Discord] Paused - Game: {gameName}, Steam ID: {steamId.Value}, Asset: {assetKey}");
                        }
                    }
                }
                
                // Fallback: пробуем по имени
                if (string.IsNullOrEmpty(assetKey))
                {
                    assetKey = _assetResolver.GetAssetKeyByName(gameName);
                    Logger.Info($"[Discord] Using fallback for paused {gameName}, Asset: {assetKey}");
                }

                // Получаем локализованное слово "Пауза"
                var pauseText = Playnite.SDK.ResourceProvider.GetString("LOC_HydraTorrent_Pause");

                var presence = new RichPresence()
                {
                    Details = TruncateString(gameName, 128),
                    State = $"{pauseText}: {progress}%",
                    Timestamps = _currentTimestamps // Используем тот же Timestamps объект
                };

                if (!string.IsNullOrEmpty(assetKey))
                {
                    presence.Assets = new Assets()
                    {
                        LargeImageKey = assetKey,
                        LargeImageText = TruncateString(gameName, 128),
                        SmallImageKey = "hydra_logo",
                        SmallImageText = $"HydraTorrent Plugin"
                    };
                }
                else
                {
                    // Fallback: используем дефолтный ассет если игра не найдена
                    presence.Assets = new Assets()
                    {
                        LargeImageKey = "downloading",
                        LargeImageText = TruncateString(gameName, 128),
                        SmallImageKey = "hydra_logo",
                        SmallImageText = $"HydraTorrent Plugin"
                    };
                }

                // При паузе НЕ передаем Timestamps - таймер продолжит идти

                _client.SetPresence(presence);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[Discord] Error updating paused status");
            }
        }

        public void Clear()
        {
            if (!_isInitialized || _client == null)
                return;

            try
            {
                _client.ClearPresence();
                
                // Сбрасываем Timestamps при очистке
                _currentTimestamps = null;
                Logger.Info("[Discord] Presence cleared, Timestamps reset");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[Discord] Error clearing presence");
            }
        }

        private string FormatSpeed(long bytesPerSecond)
        {
            if (bytesPerSecond == 0) return "0 B/s";

            string[] sizes = { "B/s", "KB/s", "MB/s", "GB/s" };
            int order = 0;
            double size = bytesPerSecond;

            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }

            return $"{size:0.#} {sizes[order]}";
        }

        private string TruncateString(string str, int maxLength)
        {
            if (string.IsNullOrEmpty(str)) return str;
            return str.Length <= maxLength ? str : str.Substring(0, maxLength - 3) + "...";
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                if (_client != null)
                {
                    _client.ClearPresence();
                    _client.Dispose();
                    _client = null;
                }
                _isInitialized = false;
                Logger.Info("[Discord] Rich Presence disposed");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[Discord] Error disposing Rich Presence");
            }
        }
    }
}
