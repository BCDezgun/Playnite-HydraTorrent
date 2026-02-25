using System;

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
        
        // ────────────────────────────────────────────────────────────────
        // Свойства для системы очереди загрузок
        // ────────────────────────────────────────────────────────────────

        public Guid? GameId { get; set; }              // ID игры в базе Playnite (null до импорта)
        public int QueuePosition { get; set; }         // Позиция в очереди (0 = активная/первая)
        public string QueueStatus { get; set; }        // "Queued", "Downloading", "Paused", "Completed"
        public DateTime? AddedToQueueAt { get; set; }  // Время добавления в очередь

        // Конструктор по умолчанию для JSON сериализации
        public TorrentResult()
        {
            QueuePosition = -1; // -1 означает "не в очереди"
            QueueStatus = null;
        }
    }
}