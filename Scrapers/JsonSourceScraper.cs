using HydraTorrent.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace HydraTorrent.Scrapers
{
    public class JsonSourceScraper : IScraper
    {
        private readonly string _sourceUrl;
        private readonly string _sourceName;
        private List<HydraRepack> _repackList = new List<HydraRepack>();
        private bool _isLoaded = false;

        public JsonSourceScraper(string sourceName, string sourceUrl)
        {
            _sourceName = sourceName;
            _sourceUrl = sourceUrl;
            System.Diagnostics.Debug.WriteLine($"[{_sourceName}] Скрейпер инициализирован");
        }

        private async Task LoadDataAsync(HttpClient client)
        {
            if (_isLoaded) return;

            try
            {
                System.Diagnostics.Debug.WriteLine($"[{_sourceName}] Загрузка базы данных...");
                var json = await client.GetStringAsync(_sourceUrl);

                // Используем общую модель FitGirlRoot для всех JSON источников
                var root = JsonConvert.DeserializeObject<FitGirlRoot>(json);

                if (root?.Downloads != null)
                {
                    _repackList = root.Downloads;
                    _isLoaded = true;
                    System.Diagnostics.Debug.WriteLine($"[{_sourceName}] Готово. Загружено репаков: {_repackList.Count}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[{_sourceName}] Ошибка сети при загрузке JSON: {ex.Message}");
            }
        }

        public async Task<List<TorrentResult>> SearchAsync(string query, HttpClient client)
        {
            await LoadDataAsync(client);

            if (string.IsNullOrWhiteSpace(query) || !_isLoaded)
                return new List<TorrentResult>();

            var lowerQuery = query.ToLowerInvariant();

            // УБРАНО: .Take(20). Теперь возвращаем полный список совпадений для пагинации
            return _repackList
                .Where(r => !string.IsNullOrEmpty(r.Title) && r.Title.ToLowerInvariant().Contains(lowerQuery))
                .Select(r => new TorrentResult
                {
                    Name = r.Title,
                    Size = r.FileSize ?? "N/A",
                    Magnet = r.Uris?.FirstOrDefault(u => u.StartsWith("magnet:")) ?? "",
                    Source = _sourceName,
                    Year = (r.UploadDate != null && r.UploadDate.Length >= 4) ? r.UploadDate.Substring(0, 4) : ""
                })
                .ToList();
        }
    }
}