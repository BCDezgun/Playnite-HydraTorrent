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
    /// Запись о топ-игре для статистики (сохраняется даже после удаления из Completed)
    /// </summary>
    public class TopGameRecord
    {
        public Guid GameId { get; set; }
        public string GameName { get; set; }
        public string TorrentName { get; set; }
        public long SizeBytes { get; set; }
        public DateTime? CompletedAt { get; set; }
        public GameType Type { get; set; }

        public TopGameRecord() { }
    }

    /// <summary>
    /// Модель для хранения агрегированной статистики
    /// </summary>
    public class DownloadStatistics
    {
        // ────────────────────────────────────────────────────────────────
        // НАКОПИТЕЛЬНАЯ статистика (суммируется, НЕ пересчитывается)
        // ────────────────────────────────────────────────────────────────

        /// <summary>Общее количество завершённых загрузок за всё время</summary>
        public int TotalDownloads { get; set; }

        /// <summary>Общий размер загруженных данных за всё время (байты)</summary>
        public long TotalBytesDownloaded { get; set; }

        /// <summary>Общий размер отданных данных за всё время (байты)</summary>
        public long TotalBytesUploaded { get; set; }

        /// <summary>Общее время загрузок за всё время (секунды)</summary>
        public long TotalDurationSeconds { get; set; }

        // ────────────────────────────────────────────────────────────────
        // Текущее состояние (пересчитывается из Completed)
        // ────────────────────────────────────────────────────────────────

        public int PortableCount { get; set; }
        public int RepackCount { get; set; }
        public int UnknownCount { get; set; }
        public DateTime? FirstDownloadAt { get; set; }
        public DateTime? LastDownloadAt { get; set; }
        public DateTime LastUpdated { get; set; }

        /// <summary>
        /// Топ игр по размеру (уникальные по имени игры)
        /// </summary>
        public List<TopGameRecord> TopGames { get; set; } = new List<TopGameRecord>();

        public DownloadStatistics()
        {
            TotalDownloads = 0;
            TotalBytesDownloaded = 0;
            TotalBytesUploaded = 0;
            TotalDurationSeconds = 0;
            PortableCount = 0;
            RepackCount = 0;
            UnknownCount = 0;
            LastUpdated = DateTime.Now;
            TopGames = new List<TopGameRecord>();
        }
    }

    /// <summary>
    /// Менеджер для управления статистикой загрузок
    /// </summary>
    public class StatisticsManager
    {
        private readonly CompletedManager _completedManager;
        private const string StatisticsFileName = "statistics.json";

        private DownloadStatistics _statistics;
        public DownloadStatistics Statistics => _statistics;

        public static readonly ILogger logger = LogManager.GetLogger();

        private readonly string _dataPath;

        public StatisticsManager(string pluginDataPath, CompletedManager completedManager)
        {
            _dataPath = Path.Combine(pluginDataPath, "HydraTorrents");
            Directory.CreateDirectory(_dataPath);
            _completedManager = completedManager;
            _statistics = new DownloadStatistics();
        }

        // ────────────────────────────────────────────────────────────────
        // Загрузка и сохранение
        // ────────────────────────────────────────────────────────────────

        public void Load()
        {
            var filePath = Path.Combine(_dataPath, StatisticsFileName);
            if (!File.Exists(filePath))
            {
                _statistics = new DownloadStatistics();
                return;
            }

            try
            {
                var json = File.ReadAllText(filePath);
                _statistics = JsonConvert.DeserializeObject<DownloadStatistics>(json) ?? new DownloadStatistics();

                // ✅ Инициализируем TopGames если null (для старых файлов)
                if (_statistics.TopGames == null)
                {
                    _statistics.TopGames = new List<TopGameRecord>();
                }

                logger.Info($"Статистика загружена: {_statistics.TotalDownloads} загрузок, " +
                    $"{FormatBytes(_statistics.TotalBytesDownloaded)}, " +
                    $"{FormatDuration(TimeSpan.FromSeconds(_statistics.TotalDurationSeconds))}, " +
                    $"{_statistics.TopGames.Count} в топе");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Ошибка загрузки статистики");
                _statistics = new DownloadStatistics();
            }
        }

        public void Save()
        {
            var filePath = Path.Combine(_dataPath, StatisticsFileName);
            try
            {
                _statistics.LastUpdated = DateTime.Now;
                var json = JsonConvert.SerializeObject(_statistics, Formatting.Indented);
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Ошибка сохранения статистики");
            }
        }

        // ────────────────────────────────────────────────────────────────
        // ГЛАВНЫЙ МЕТОД: Добавление данных новой загрузки
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Добавляет данные о завершённой загрузке к накопительной статистике.
        /// ВЫЗЫВАТЬ ЭТОТ МЕТОД при каждом завершении загрузки!
        /// </summary>
        public void AddCompletedDownload(TorrentResult item)
        {
            if (item == null) return;

            try
            {
                // ✅ СУММИРУЕМ данные (а не пересчитываем)
                _statistics.TotalDownloads++;

                // Размер
                long size = item.TotalDownloadedBytes > 0 ? item.TotalDownloadedBytes : item.SizeBytes;
                _statistics.TotalBytesDownloaded += size;
                _statistics.TotalBytesUploaded += item.TotalUploadedBytes;

                // Время
                if (item.DownloadDuration.HasValue)
                {
                    _statistics.TotalDurationSeconds += (long)item.DownloadDuration.Value.TotalSeconds;
                }

                // Обновляем даты
                if (item.CompletedAt.HasValue)
                {
                    if (!_statistics.FirstDownloadAt.HasValue || item.CompletedAt < _statistics.FirstDownloadAt)
                    {
                        _statistics.FirstDownloadAt = item.CompletedAt;
                    }
                    if (!_statistics.LastDownloadAt.HasValue || item.CompletedAt > _statistics.LastDownloadAt)
                    {
                        _statistics.LastDownloadAt = item.CompletedAt;
                    }
                }

                // Типы игр
                switch (item.DetectedType)
                {
                    case GameType.Portable:
                        _statistics.PortableCount++;
                        break;
                    case GameType.Repack:
                        _statistics.RepackCount++;
                        break;
                    default:
                        _statistics.UnknownCount++;
                        break;
                }

                // ✅ Добавляем в топ (с проверкой на дубликаты по имени игры)
                AddToTopGames(item);

                Save();

                logger.Info($"Статистика обновлена: +{FormatBytes(size)}, " +
                    $"итого: {_statistics.TotalDownloads} загрузок, " +
                    $"{FormatBytes(_statistics.TotalBytesDownloaded)}, " +
                    $"{FormatDuration(TimeSpan.FromSeconds(_statistics.TotalDurationSeconds))}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Ошибка добавления статистики");
            }
        }

        /// <summary>
        /// Добавляет игру в топ с проверкой на дубликаты по имени игры
        /// </summary>
        private void AddToTopGames(TorrentResult item)
        {
            if (_statistics.TopGames == null)
            {
                _statistics.TopGames = new List<TopGameRecord>();
            }

            if (!item.GameId.HasValue) return;

            string gameName = item.GameName ?? item.Name;
            if (string.IsNullOrEmpty(gameName)) return;

            // ✅ Проверяем, есть ли уже игра с таким именем (без учёта регистра)
            var existingByName = _statistics.TopGames.FirstOrDefault(t =>
                string.Equals(t.GameName, gameName, StringComparison.OrdinalIgnoreCase));

            if (existingByName != null)
            {
                // Обновляем существующую запись если новая больше
                long newSize = item.TotalDownloadedBytes > 0 ? item.TotalDownloadedBytes : item.SizeBytes;
                if (newSize > existingByName.SizeBytes)
                {
                    existingByName.SizeBytes = newSize;
                    existingByName.TorrentName = item.Name;
                    existingByName.CompletedAt = item.CompletedAt;
                    existingByName.Type = item.DetectedType;
                    logger.Debug($"Обновлён размер для: {gameName} → {FormatBytes(newSize)}");
                }
                return;
            }

            // Проверяем по GameId
            var existingById = _statistics.TopGames.FirstOrDefault(t => t.GameId == item.GameId.Value);
            if (existingById != null)
            {
                // Обновляем
                long newSize = item.TotalDownloadedBytes > 0 ? item.TotalDownloadedBytes : item.SizeBytes;
                if (newSize > existingById.SizeBytes)
                {
                    existingById.SizeBytes = newSize;
                    existingById.GameName = gameName;
                    existingById.TorrentName = item.Name;
                    existingById.CompletedAt = item.CompletedAt;
                    existingById.Type = item.DetectedType;
                }
                return;
            }

            // Добавляем новую запись
            _statistics.TopGames.Add(new TopGameRecord
            {
                GameId = item.GameId.Value,
                GameName = gameName,
                TorrentName = item.Name,
                SizeBytes = item.TotalDownloadedBytes > 0 ? item.TotalDownloadedBytes : item.SizeBytes,
                CompletedAt = item.CompletedAt,
                Type = item.DetectedType
            });

            logger.Debug($"Добавлена игра в топ: {gameName}");
        }

        // ────────────────────────────────────────────────────────────────
        // Пересчёт из CompletedManager (только при первом запуске/миграции)
        // ────────────────────────────────────────────────────────────────

        public void RecalculateFromCompleted()
        {
            try
            {
                var items = _completedManager.CompletedItems;

                // ✅ НЕ перезаписываем накопительную статистику!
                // Только обновляем счётчики типов и топ

                _statistics.PortableCount = items.Count(i => i.DetectedType == GameType.Portable);
                _statistics.RepackCount = items.Count(i => i.DetectedType == GameType.Repack);
                _statistics.UnknownCount = items.Count(i => i.DetectedType == GameType.Unknown);

                var completedDates = items
                    .Where(i => i.CompletedAt.HasValue)
                    .Select(i => i.CompletedAt.Value)
                    .ToList();

                if (completedDates.Any())
                {
                    if (!_statistics.FirstDownloadAt.HasValue || completedDates.Min() < _statistics.FirstDownloadAt)
                    {
                        _statistics.FirstDownloadAt = completedDates.Min();
                    }
                    if (!_statistics.LastDownloadAt.HasValue || completedDates.Max() > _statistics.LastDownloadAt)
                    {
                        _statistics.LastDownloadAt = completedDates.Max();
                    }
                }

                // Добавляем игры в топ (с проверкой дубликатов)
                foreach (var item in items)
                {
                    AddToTopGames(item);
                }

                Save();
                logger.Info("Статистика синхронизирована с CompletedManager");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Ошибка пересчёта статистики");
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Публичные методы для получения данных
        // ────────────────────────────────────────────────────────────────

        public string GetFormattedTotalSize()
        {
            return FormatBytes(_statistics.TotalBytesDownloaded);
        }

        public string GetFormattedTotalUploaded()
        {
            return FormatBytes(_statistics.TotalBytesUploaded);
        }

        public string GetFormattedDuration()
        {
            var totalSeconds = _statistics.TotalDurationSeconds;
            if (totalSeconds == 0) return "-";

            var ts = TimeSpan.FromSeconds(totalSeconds);
            return FormatDuration(ts);
        }

        public double GetAverageDownloadSpeed()
        {
            if (_statistics.TotalDurationSeconds == 0) return 0;
            return _statistics.TotalBytesDownloaded / _statistics.TotalDurationSeconds;
        }

        public string GetFormattedAverageSpeed()
        {
            var speed = GetAverageDownloadSpeed();
            return speed > 0 ? $"{FormatBytes((long)speed)}/s" : "-";
        }

        public double GetOverallRatio()
        {
            if (_statistics.TotalBytesDownloaded == 0) return 0;
            return (double)_statistics.TotalBytesUploaded / _statistics.TotalBytesDownloaded;
        }

        /// <summary>
        /// Возвращает топ N игр по размеру из сохранённой статистики
        /// </summary>
        public List<TopGameRecord> GetTopGamesBySize(int count = 5)
        {
            if (_statistics.TopGames == null || !_statistics.TopGames.Any())
            {
                return new List<TopGameRecord>();
            }

            return _statistics.TopGames
                .OrderByDescending(g => g.SizeBytes)
                .Take(count)
                .ToList();
        }

        // ────────────────────────────────────────────────────────────────
        // Сброс статистики
        // ────────────────────────────────────────────────────────────────

        public void Reset()
        {
            _statistics = new DownloadStatistics();
            Save();
            logger.Info("Статистика сброшена");
        }

        // ────────────────────────────────────────────────────────────────
        // Вспомогательные методы
        // ────────────────────────────────────────────────────────────────

        public static string FormatBytes(long bytes)
        {
            if (bytes == 0) return "0 B";

            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.##} {sizes[order]}";
        }

        public static string FormatDuration(TimeSpan ts)
        {
            if (ts.TotalSeconds < 1) return "-";

            if (ts.TotalDays >= 1)
            {
                return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
            }
            else if (ts.TotalHours >= 1)
            {
                return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
            }
            else if (ts.TotalMinutes >= 1)
            {
                return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
            }
            else
            {
                return $"{ts.Seconds}s";
            }
        }
    }
}