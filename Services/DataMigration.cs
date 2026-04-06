using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;

namespace HydraTorrent.Services
{
    public static class DataMigration
    {
        private const int CurrentSchemaVersion = 2;
        private const string VersionFileName = "schema_version.json";

        public static readonly ILogger logger = LogManager.GetLogger();

        public static int Migrate(string dataFolder)
        {
            var versionFile = Path.Combine(dataFolder, VersionFileName);
            int currentVersion = 0;

            if (File.Exists(versionFile))
            {
                try
                {
                    var json = File.ReadAllText(versionFile);
                    var versionData = JsonConvert.DeserializeObject<Dictionary<string, int>>(json);
                    if (versionData != null && versionData.ContainsKey("version"))
                    {
                        currentVersion = versionData["version"];
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn($"DataMigration: Failed to read version file: {ex.Message}");
                }
            }

            if (currentVersion >= CurrentSchemaVersion)
                return 0;

            int migrated = 0;

            if (currentVersion < 1)
            {
                migrated += MigrateV0ToV1(dataFolder);
            }

            if (currentVersion < 2)
            {
                migrated += MigrateV1ToV2(dataFolder);
            }

            try
            {
                var versionData = new Dictionary<string, int> { { "version", CurrentSchemaVersion } };
                var json = JsonConvert.SerializeObject(versionData, Formatting.Indented);
                File.WriteAllText(versionFile, json);
                logger.Info($"DataMigration: Schema updated to v{CurrentSchemaVersion}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "DataMigration: Failed to update version file");
            }

            return migrated;
        }

        private static int MigrateV0ToV1(string dataFolder)
        {
            int migrated = 0;
            var torrentFolder = Path.Combine(dataFolder, "HydraTorrents");

            if (!Directory.Exists(torrentFolder))
                return 0;

            foreach (var file in Directory.GetFiles(torrentFolder, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);

                    bool needsSave = false;

                    if (!data.ContainsKey("DetectedType"))
                    {
                        data["DetectedType"] = 0;
                        needsSave = true;
                    }

                    if (!data.ContainsKey("IsConfigured"))
                    {
                        data["IsConfigured"] = false;
                        needsSave = true;
                    }

                    if (!data.ContainsKey("DownloadPath"))
                    {
                        data["DownloadPath"] = null;
                        needsSave = true;
                    }

                    if (!data.ContainsKey("ExecutablePath"))
                    {
                        data["ExecutablePath"] = null;
                        needsSave = true;
                    }

                    if (needsSave)
                    {
                        var newJson = JsonConvert.SerializeObject(data, Formatting.Indented);
                        File.WriteAllText(file, newJson);
                        migrated++;
                        logger.Info($"DataMigration: Migrated {Path.GetFileName(file)} v0→v1");
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn($"DataMigration: Failed to migrate {file}: {ex.Message}");
                }
            }

            return migrated;
        }

        private static int MigrateV1ToV2(string dataFolder)
        {
            int migrated = 0;
            var torrentFolder = Path.Combine(dataFolder, "HydraTorrents");

            if (!Directory.Exists(torrentFolder))
                return 0;

            foreach (var file in Directory.GetFiles(torrentFolder, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);

                    bool needsSave = false;

                    if (!data.ContainsKey("TotalDownloadedBytes"))
                    {
                        data["TotalDownloadedBytes"] = 0;
                        needsSave = true;
                    }

                    if (!data.ContainsKey("TotalUploadedBytes"))
                    {
                        data["TotalUploadedBytes"] = 0;
                        needsSave = true;
                    }

                    if (!data.ContainsKey("SeedRatio"))
                    {
                        data["SeedRatio"] = 0.0;
                        needsSave = true;
                    }

                    if (!data.ContainsKey("IsRemovedFromClient"))
                    {
                        data["IsRemovedFromClient"] = false;
                        needsSave = true;
                    }

                    if (!data.ContainsKey("AverageDownloadSpeed"))
                    {
                        data["AverageDownloadSpeed"] = 0;
                        needsSave = true;
                    }

                    if (!data.ContainsKey("DownloadDuration"))
                    {
                        data["DownloadDuration"] = null;
                        needsSave = true;
                    }

                    if (!data.ContainsKey("CompletedAt"))
                    {
                        data["CompletedAt"] = null;
                        needsSave = true;
                    }

                    if (needsSave)
                    {
                        var newJson = JsonConvert.SerializeObject(data, Formatting.Indented);
                        File.WriteAllText(file, newJson);
                        migrated++;
                        logger.Info($"DataMigration: Migrated {Path.GetFileName(file)} v1→v2");
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn($"DataMigration: Failed to migrate {file}: {ex.Message}");
                }
            }

            return migrated;
        }
    }
}
