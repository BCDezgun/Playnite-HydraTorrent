using HydraTorrent.Models;
using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HydraTorrent.Services
{
    /// <summary>
    /// Менеджер для управления списком завершённых загрузок
    /// </summary>
    public class CompletedManager
    {
        private readonly HydraTorrent _plugin;
        private const string CompletedFileName = "completed.json";

        private List<TorrentResult> _completedItems;
        public List<TorrentResult> CompletedItems
        {
            get => _completedItems;
            private set => _completedItems = value;
        }

        public static readonly ILogger logger = LogManager.GetLogger();

        public CompletedManager(HydraTorrent plugin)
        {
            _plugin = plugin;
            _completedItems = new List<TorrentResult>();
        }

        // ────────────────────────────────────────────────────────────────
        // Загрузка и сохранение
        // ────────────────────────────────────────────────────────────────

        private string GetCompletedFilePath()
        {
            var dataDir = Path.Combine(_plugin.GetPluginUserDataPath(), "HydraTorrents");
            Directory.CreateDirectory(dataDir);
            return Path.Combine(dataDir, CompletedFileName);
        }

        public void LoadCompletedItems()
        {
            var filePath = GetCompletedFilePath();
            if (!File.Exists(filePath))
            {
                _completedItems = new List<TorrentResult>();
                logger.Info("Список завершённых: файл не найден, создаём новый");
                return;
            }

            try
            {
                var json = File.ReadAllText(filePath);
                _completedItems = JsonConvert.DeserializeObject<List<TorrentResult>>(json) ?? new List<TorrentResult>();
                logger.Info($"Загружено {_completedItems.Count} завершённых элементов");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Ошибка загрузки списка завершённых");
                _completedItems = new List<TorrentResult>();
            }
        }

        public void SaveCompletedItems()
        {
            var filePath = GetCompletedFilePath();
            try
            {
                var json = JsonConvert.SerializeObject(_completedItems, Formatting.Indented);
                File.WriteAllText(filePath, json);
                logger.Debug($"Сохранено {_completedItems.Count} завершённых элементов");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Ошибка сохранения списка завершённых");
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Управление списком
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Перемещает элемент из очереди в список завершённых
        /// </summary>
        public void MoveToCompleted(TorrentResult item)
        {
            if (item == null) return;

            // Заполняем данные завершения
            item.QueueStatus = "Completed";
            item.CompletedAt = DateTime.Now;

            // Удаляем из очереди если там есть
            var queueItem = _plugin.DownloadQueue.FirstOrDefault(q => q.GameId == item.GameId);
            if (queueItem != null)
            {
                _plugin.DownloadQueue.Remove(queueItem);
                _plugin.SaveQueue();
            }

            // Проверяем, нет ли уже в завершённых
            var existingItem = _completedItems.FirstOrDefault(c => c.GameId == item.GameId);
            if (existingItem != null)
            {
                // Обновляем существующий
                var index = _completedItems.IndexOf(existingItem);
                _completedItems[index] = item;
            }
            else
            {
                // Добавляем новый
                _completedItems.Insert(0, item); // Новые в начале списка
            }

            SaveCompletedItems();
            logger.Info($"Перемещено в завершённые: {item.Name}");
        }

        /// <summary>
        /// Добавляет завершённый элемент (вызывается после пост-обработки)
        /// </summary>
        public void AddCompletedItem(TorrentResult item)
        {
            if (item == null || !item.GameId.HasValue) return;

            // Заполняем дату завершения если ещё не заполнена
            if (!item.CompletedAt.HasValue)
            {
                item.CompletedAt = DateTime.Now;
            }

            // Проверяем, нет ли уже в списке
            var existingItem = _completedItems.FirstOrDefault(c => c.GameId == item.GameId);
            if (existingItem != null)
            {
                // Обновляем существующий
                var index = _completedItems.IndexOf(existingItem);
                _completedItems[index] = item;
                logger.Info($"Обновлён элемент в завершённых: {item.Name}");
            }
            else
            {
                // Добавляем новый в начало
                _completedItems.Insert(0, item);
                logger.Info($"Добавлено в завершённые: {item.Name}");
            }

            SaveCompletedItems();
        }

        /// <summary>
        /// Удаляет элемент из списка завершённых
        /// </summary>
        public void RemoveFromCompleted(Guid gameId)
        {
            var item = _completedItems.FirstOrDefault(c => c.GameId == gameId);
            if (item != null)
            {
                _completedItems.Remove(item);
                SaveCompletedItems();
                logger.Info($"Удалено из завершённых: {item.Name}");
            }
        }

        /// <summary>
        /// Очищает весь список завершённых
        /// </summary>
        public void ClearAll()
        {
            _completedItems.Clear();
            SaveCompletedItems();
            logger.Info("Список завершённых очищен");
        }

        /// <summary>
        /// Получает элемент по GameId
        /// </summary>
        public TorrentResult GetByGameId(Guid gameId)
        {
            return _completedItems.FirstOrDefault(c => c.GameId == gameId);
        }

        /// <summary>
        /// Обновляет данные элемента (например, после настройки игры)
        /// </summary>
        public void UpdateItem(TorrentResult item)
        {
            if (item == null || !item.GameId.HasValue) return;

            var existingItem = _completedItems.FirstOrDefault(c => c.GameId == item.GameId);
            if (existingItem != null)
            {
                var index = _completedItems.IndexOf(existingItem);
                _completedItems[index] = item;
                SaveCompletedItems();
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Статистика
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Возвращает общее количество завершённых загрузок
        /// </summary>
        public int GetTotalCount()
        {
            return _completedItems.Count;
        }

        /// <summary>
        /// Возвращает общий размер загруженных данных
        /// </summary>
        public long GetTotalSizeBytes()
        {
            return _completedItems.Sum(c => c.TotalDownloadedBytes > 0 ? c.TotalDownloadedBytes : c.SizeBytes);
        }

        /// <summary>
        /// Возвращает количество по типам игр
        /// </summary>
        public Dictionary<GameType, int> GetCountByType()
        {
            return _completedItems
                .GroupBy(c => c.DetectedType)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        /// <summary>
        /// Возвращает количество настроенных игр
        /// </summary>
        public int GetConfiguredCount()
        {
            return _completedItems.Count(c => c.IsConfigured);
        }

        /// <summary>
        /// Возвращает топ N самых больших игр
        /// </summary>
        public List<TorrentResult> GetTopBySize(int count = 5)
        {
            return _completedItems
                .OrderByDescending(c => c.TotalDownloadedBytes > 0 ? c.TotalDownloadedBytes : c.SizeBytes)
                .Take(count)
                .ToList();
        }

        /// <summary>
        /// Возвращает элементы за указанный период
        /// </summary>
        public List<TorrentResult> GetByDateRange(DateTime from, DateTime to)
        {
            return _completedItems
                .Where(c => c.CompletedAt.HasValue && c.CompletedAt >= from && c.CompletedAt <= to)
                .OrderByDescending(c => c.CompletedAt)
                .ToList();
        }
    }
}