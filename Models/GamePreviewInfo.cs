using System.Collections.Generic;

namespace HydraTorrent.Models
{
    public class GamePreviewInfo
    {
        public string Name { get; set; }
        public string EnglishName { get; set; }
        public int? Year { get; set; }
        public string Genres { get; set; }
        public string Developers { get; set; }
        public string Description { get; set; }
        public int? SteamAppId { get; set; }
        public string CoverUrl { get; set; }
    }
}
