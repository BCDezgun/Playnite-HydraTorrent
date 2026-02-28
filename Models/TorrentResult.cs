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

        // Конструктор по умолчанию для JSON сериализации
        public TorrentResult()
        {
            QueuePosition = -1; // -1 означает "не в очереди"
            QueueStatus = null;
            SizeBytes = 0;
            DetectedType = GameType.Unknown;
            IsConfigured = false;
        }
    }
}
