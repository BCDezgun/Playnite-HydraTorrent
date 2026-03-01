using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HydraTorrent.Services
{
    /// <summary>
    /// Менеджер для хранения хешей торрентов, удалённых пользователем из вкладки "Завершённые".
    /// Используется для предотвращения повторного появления игр в окне загрузки.
    /// </summary>
    public class RemovedHashesManager
    {
        private readonly HydraTorrent _plugin;
        private const string RemovedHashesFileName = "removed_hashes.json";

        private HashSet<string> _removedHashes;
        public HashSet<string> RemovedHashes
        {
            get => _removedHashes;
        }

        public static readonly ILogger logger = LogManager.GetLogger();

        public RemovedHashesManager(HydraTorrent plugin)
        {
            _plugin = plugin;
            _removedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // ────────────────────────────────────────────────────────────────
        // Загрузка и сохранение
        // ────────────────────────────────────────────────────────────────

        private string GetRemovedHashesFilePath()
        {
            var dataDir = Path.Combine(_plugin.GetPluginUserDataPath(), "HydraTorrents");
            Directory.CreateDirectory(dataDir);
            return Path.Combine(dataDir, RemovedHashesFileName);
        }

        public void Load()
        {
            var filePath = GetRemovedHashesFilePath();
            if (!File.Exists(filePath))
            {
                _removedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                logger.Info("Список удалённых хешей: файл не найден, создаём новый");
                return;
            }

            try
            {
                var json = File.ReadAllText(filePath);
                var list = JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>();
                _removedHashes = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
                logger.Info($"Загружено {_removedHashes.Count} удалённых хешей");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Ошибка загрузки списка удалённых хешей");
                _removedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public void Save()
        {
            var filePath = GetRemovedHashesFilePath();
            try
            {
                var json = JsonConvert.SerializeObject(_removedHashes.ToList(), Formatting.Indented);
                File.WriteAllText(filePath, json);
                logger.Debug($"Сохранено {_removedHashes.Count} удалённых хешей");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Ошибка сохранения списка удалённых хешей");
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Управление хешами
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Добавляет хеш в список удалённых
        /// </summary>
        public void AddRemovedHash(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return;

            if (_removedHashes.Add(hash))
            {
                Save();
                logger.Info($"Хеш добавлен в список удалённых: {hash}");
            }
        }

        /// <summary>
        /// Добавляет несколько хешей в список удалённых
        /// </summary>
        public void AddRemovedHashes(IEnumerable<string> hashes)
        {
            bool added = false;
            foreach (var hash in hashes)
            {
                if (!string.IsNullOrEmpty(hash) && _removedHashes.Add(hash))
                {
                    added = true;
                }
            }

            if (added)
            {
                Save();
                logger.Info($"Добавлено хешей в список удалённых: {hashes.Count()}");
            }
        }

        /// <summary>
        /// Проверяет, был ли хеш удалён пользователем
        /// </summary>
        public bool IsRemoved(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return false;
            return _removedHashes.Contains(hash);
        }

        public void RemoveRemovedHash(string hash)
        {
            if (_removedHashes.Contains(hash))
            {
                _removedHashes.Remove(hash);
                Save();
            }
        }

        /// <summary>
        /// Очищает список удалённых хешей
        /// </summary>
        public void Clear()
        {
            _removedHashes.Clear();
            Save();
            logger.Info("Список удалённых хешей очищен");
        }
    }
}