using System;

namespace HydraTorrent.Models
{
    public class TorrentResult
    {
        public string Name { get; set; }
        public string Size { get; set; }
        public string Magnet { get; set; }      // ссылка magnet:?xt=...
        public string Source { get; set; }      // "FitGirl", "1337x", "Xatab"
        public string Year { get; set; }
        //public string CoverUrl { get; set; }    // для будущей обложки
        //public string PageUrl { get; set; }     // ссылка на страницу торрента
        public string TorrentHash { get; set; }   // InfoHash торрента из qBittorrent
        
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