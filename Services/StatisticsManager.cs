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
        public int TotalDownloads { get; set; }
        public long TotalBytesDownloaded { get; set; }
        public long TotalBytesUploaded { get; set; }
        public long TotalDurationSeconds { get; set; }
        public int PortableCount { get; set; }
        public int RepackCount { get; set; }
        public int UnknownCount { get; set; }
        public DateTime? FirstDownloadAt { get; set; }
        public DateTime? LastDownloadAt { get; set; }
        public DateTime LastUpdated { get; set; }

        /// <summary>
        /// Топ игр по размеру (сохраняется даже после удаления из Completed)
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
                RecalculateFromCompleted();
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

                logger.Info($"Статистика загружена: {_statistics.TotalDownloads} загрузок, {_statistics.TopGames.Count} в топе");
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
        // Пересчёт из CompletedManager
        // ────────────────────────────────────────────────────────────────

        public void RecalculateFromCompleted()
        {
            try
            {
                var items = _completedManager.CompletedItems;

                _statistics.TotalDownloads = items.Count;
                _statistics.TotalBytesDownloaded = items.Sum(i => i.TotalDownloadedBytes > 0 ? i.TotalDownloadedBytes : i.SizeBytes);
                _statistics.TotalBytesUploaded = items.Sum(i => i.TotalUploadedBytes);
                _statistics.TotalDurationSeconds = (long)items
                    .Where(i => i.DownloadDuration.HasValue)
                    .Sum(i => i.DownloadDuration.Value.TotalSeconds);

                _statistics.PortableCount = items.Count(i => i.DetectedType == GameType.Portable);
                _statistics.RepackCount = items.Count(i => i.DetectedType == GameType.Repack);
                _statistics.UnknownCount = items.Count(i => i.DetectedType == GameType.Unknown);

                var completedDates = items
                    .Where(i => i.CompletedAt.HasValue)
                    .Select(i => i.CompletedAt.Value)
                    .ToList();

                _statistics.FirstDownloadAt = completedDates.Any() ? completedDates.Min() : null;
                _statistics.LastDownloadAt = completedDates.Any() ? completedDates.Max() : null;

                // ✅ Обновляем топ игр - добавляем новые, не удаляя старые
                UpdateTopGames(items);

                Save();
                logger.Info("Статистика пересчитана из CompletedManager");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Ошибка пересчёта статистики");
            }
        }

        /// <summary>
        /// Обновляет топ игр - добавляет новые записи, сохраняя старые
        /// </summary>
        private void UpdateTopGames(List<TorrentResult> items)
        {
            // Убедимся, что список инициализирован
            if (_statistics.TopGames == null)
            {
                _statistics.TopGames = new List<TopGameRecord>();
            }

            foreach (var item in items)
            {
                if (!item.GameId.HasValue) continue;

                var existingRecord = _statistics.TopGames.FirstOrDefault(t => t.GameId == item.GameId.Value);

                if (existingRecord == null)
                {
                    // Добавляем новую запись
                    _statistics.TopGames.Add(new TopGameRecord
                    {
                        GameId = item.GameId.Value,
                        GameName = item.GameName ?? item.Name,
                        TorrentName = item.Name,
                        SizeBytes = item.TotalDownloadedBytes > 0 ? item.TotalDownloadedBytes : item.SizeBytes,
                        CompletedAt = item.CompletedAt,
                        Type = item.DetectedType
                    });
                    logger.Debug($"Добавлена игра в топ: {item.GameName ?? item.Name}");
                }
                else
                {
                    // Обновляем существующую запись
                    existingRecord.SizeBytes = item.TotalDownloadedBytes > 0 ? item.TotalDownloadedBytes : item.SizeBytes;
                    existingRecord.GameName = item.GameName ?? item.Name;
                    existingRecord.TorrentName = item.Name;
                    existingRecord.Type = item.DetectedType;
                    if (item.CompletedAt.HasValue)
                    {
                        existingRecord.CompletedAt = item.CompletedAt;
                    }
                }
            }

            // Логируем количество
            logger.Info($"Топ игр обновлён: {_statistics.TopGames.Count} записей");
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
            if (ts.TotalHours >= 24)
            {
                return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
            }
            else if (ts.TotalHours >= 1)
            {
                return $"{(int)ts.TotalHours}h {ts.Minutes}m";
            }
            else
            {
                return $"{ts.Minutes}m {ts.Seconds}s";
            }
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

        public static string FormatDuration(TimeSpan? duration)
        {
            if (!duration.HasValue) return "-";

            var ts = duration.Value;
            if (ts.TotalHours >= 24)
            {
                return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
            }
            else if (ts.TotalHours >= 1)
            {
                return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
            }
            else if (ts.TotalMinutes >= 1)
            {
                return $"{ts.Minutes}m {ts.Seconds}s";
            }
            else
            {
                return $"{ts.Seconds}s";
            }
        }
    }
}