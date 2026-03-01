using System;
using HydraTorrent.Models;

namespace HydraTorrent.Models
{
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
    }
}