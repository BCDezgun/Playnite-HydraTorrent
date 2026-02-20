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
        public string CoverUrl { get; set; }    // для будущей обложки
        public string PageUrl { get; set; }     // ссылка на страницу торрента

        public string TorrentHash { get; set; }   // InfoHash торрента из qBittorrent
    }
}