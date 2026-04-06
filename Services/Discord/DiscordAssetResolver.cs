using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HydraTorrent.Services.Discord
{
    public class DiscordAssetsData
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

    public class DiscordAssetResolver
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private Dictionary<int, string> _steamIdToAssetKey;  // Изменено: Steam ID вместо IGDB ID
        private string _fallbackAsset = "downloading";
        private string _applicationId;
        private DateTime _lastUpdated;
        private readonly Dictionary<string, int> _gameNameToSteamId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public DiscordAssetResolver()
        {
            _steamIdToAssetKey = new Dictionary<int, string>();
        }

        /// <summary>
        /// Загружает маппинг из discord_assets.json (ищет рядом с DLL плагина)
        /// </summary>
        public bool LoadAssets()
        {
            try
            {
                // Получаем путь к DLL плагина (работает и в Debug, и в Extensions)
                var assemblyPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var assetsFilePath = Path.Combine(assemblyPath, "discord_assets.json");

                if (!File.Exists(assetsFilePath))
                {
                    Logger.Warn($"[Discord] discord_assets.json not found at: {assetsFilePath}");
                    return false;
                }

                var json = File.ReadAllText(assetsFilePath);
                var data = JsonConvert.DeserializeObject<DiscordAssetsData>(json);

                if (data == null || data.Assets == null)
                {
                    Logger.Error("[Discord] Failed to parse discord_assets.json");
                    return false;
                }

                _steamIdToAssetKey = data.Assets.ToDictionary(
                    kvp => int.Parse(kvp.Key),
                    kvp => kvp.Value
                );

                _fallbackAsset = data.FallbackAsset ?? "downloading";
                _applicationId = data.ApplicationId;
                _lastUpdated = data.LastUpdated;

                Logger.Info($"[Discord] Loaded {_steamIdToAssetKey.Count} game assets (Steam ID based)");
                Logger.Info($"[Discord] Application ID: {_applicationId}");

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[Discord] Error loading discord_assets.json");
                return false;
            }
        }

        public string GetAssetKey(int steamId)
        {
            return _steamIdToAssetKey.TryGetValue(steamId, out var assetKey) 
                ? assetKey 
                : _fallbackAsset;
        }

        public string GetAssetKeyByName(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName))
                return _fallbackAsset;

            if (_gameNameToSteamId.TryGetValue(gameName, out var cachedId))
            {
                return GetAssetKey(cachedId);
            }

            return _fallbackAsset;
        }

        public void CacheSteamId(string gameName, int steamId)
        {
            if (string.IsNullOrWhiteSpace(gameName))
                return;

            if (!_gameNameToSteamId.ContainsKey(gameName))
            {
                _gameNameToSteamId[gameName] = steamId;
            }
        }

        public bool HasAsset(int steamId)
        {
            return _steamIdToAssetKey.ContainsKey(steamId);
        }

        public int GetAssetsCount() => _steamIdToAssetKey.Count;
        public string GetApplicationId() => _applicationId;
        public DateTime GetLastUpdated() => _lastUpdated;
        public string GetFallbackAsset() => _fallbackAsset;
        public bool NeedsUpdate() => DateTime.UtcNow - _lastUpdated > TimeSpan.FromDays(90);
    }
}
