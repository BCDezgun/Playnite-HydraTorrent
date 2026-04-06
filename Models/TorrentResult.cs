using System;
using HydraTorrent.Models;

namespace HydraTorrent.Models
{
    public enum RepackType
    {
        Repack,
        Portable,
        Other
    }

    public class TorrentResult
    {
        public string Name { get; set; }
        public string Size { get; set; }
        public string Magnet { get; set; }
        public string Source { get; set; }
        public string Year { get; set; }
        public string GameName { get; set; }
        public string TorrentHash { get; set; }
        public DateTime? UploadDate { get; set; }
        public long SizeBytes { get; set; }

        /// <summary>
        /// Тип раздачи (определяется по названию при поиске)
        /// </summary>
        public RepackType RepackType { get; set; } = RepackType.Other;

        // ────────────────────────────────────────────────────────────────
        // Свойства для системы очереди загрузок
        // ────────────────────────────────────────────────────────────────

        public Guid? GameId { get; set; }              // ID игры в базе Playnite (null до импорта)
        public int QueuePosition { get; set; }         // Позиция в очереди (0 = активная/первая)
        public string QueueStatus { get; set; }        // "Queued", "Downloading", "Paused", "Completed"
        public DateTime? AddedToQueueAt { get; set; }  // Время добавления в очередь

        // ────────────────────────────────────────────────────────────────
        // Свойства для пост-обработки после загрузки
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Путь к папке с загруженными файлами
        /// </summary>
        public string DownloadPath { get; set; }

        /// <summary>
        /// Тип загруженной игры (определяется после завершения загрузки)
        /// </summary>
        public GameType DetectedType { get; set; } = GameType.Unknown;

        /// <summary>
        /// Настроен ли запуск игры в Playnite
        /// </summary>
        public bool IsConfigured { get; set; } = false;

        /// <summary>
        /// Путь к исполняемому файлу (заполняется после анализа)
        /// </summary>
        public string ExecutablePath { get; set; }

        // ────────────────────────────────────────────────────────────────
        // Новые свойства для раздела "Завершённые" и статистики
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Дата и время завершения загрузки
        /// </summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>
        /// Общий размер загруженных данных в байтах
        /// </summary>
        public long TotalDownloadedBytes { get; set; }

        /// <summary>
        /// Общий размер отданных данных в байтах (для раздачи)
        /// </summary>
        public long TotalUploadedBytes { get; set; }

        /// <summary>
        /// Текущий ratio (соотношение upload/download)
        /// </summary>
        public double SeedRatio { get; set; }

        /// <summary>
        /// Удалён ли торрент из qBittorrent (файлы сохранены)
        /// </summary>
        public bool IsRemovedFromClient { get; set; } = false;

        /// <summary>
        /// Средняя скорость загрузки в байтах/сек
        /// </summary>
        public long AverageDownloadSpeed { get; set; }

        /// <summary>
        /// Время загрузки (длительность)
        /// </summary>
        public TimeSpan? DownloadDuration { get; set; }        

        // Конструктор по умолчанию для JSON сериализации
        public TorrentResult()
        {
            QueuePosition = -1; // -1 означает "не в очереди"
            QueueStatus = null;
            SizeBytes = 0;
            DetectedType = GameType.Unknown;
            IsConfigured = false;
            IsRemovedFromClient = false;
            SeedRatio = 0;
            TotalDownloadedBytes = 0;
            TotalUploadedBytes = 0;
            AverageDownloadSpeed = 0;
        }

        private static readonly string[] RepackIndicators = new[]
        {
            "repack", "fitgirl", "dodi", "xatab", "elamigos",
            "kaos", "kapitalsin", "cpym", "darkumbra", "skidrow",
            "codex", "plaza", "hoodlum", "razor1911", "flt",
            "gog-freedom", "reloaded", "prophet", "goldberg"
        };

        private static readonly string[] PortableIndicators = new[]
        {
            "portable", "standalone", "[gog]", "[gog] ",
            "[папка игры]", "[папка", "[folder]",
            "no-install", "no install", "pre-installed"
        };

        public static RepackType DetectRepackType(string title)
        {
            if (string.IsNullOrEmpty(title)) return RepackType.Other;

            var lower = title.ToLowerInvariant();

            foreach (var indicator in PortableIndicators)
            {
                if (lower.Contains(indicator))
                    return RepackType.Portable;
            }

            foreach (var indicator in RepackIndicators)
            {
                if (lower.Contains(indicator))
                    return RepackType.Repack;
            }

            return RepackType.Other;
        }
    }
}