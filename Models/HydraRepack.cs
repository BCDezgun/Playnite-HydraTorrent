using Newtonsoft.Json;
using System.Collections.Generic;

namespace HydraTorrent.Models
{
    // Класс для одного конкретного репака
    public class HydraRepack
    {
        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("uris")]
        public List<string> Uris { get; set; }

        [JsonProperty("fileSize")]
        public string FileSize { get; set; }

        [JsonProperty("uploadDate")]
        public string UploadDate { get; set; }
    }

    // Класс для структуры всего JSON-файла
    public class FitGirlRoot
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("downloads")]
        public List<HydraRepack> Downloads { get; set; }
    }
}