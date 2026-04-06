using System;
using System.Collections.Generic;
using System.Linq;

namespace HydraTorrent.Models
{
    public class JsonSourceValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public int ValidItemsCount { get; set; }
        public int InvalidItemsCount { get; set; }
    }

    public static class JsonSourceValidator
    {
        public static JsonSourceValidationResult Validate(FitGirlRoot root)
        {
            var result = new JsonSourceValidationResult();

            if (root == null)
            {
                result.Errors.Add("JSON root is null — неверный формат источника");
                return result;
            }

            if (root.Downloads == null)
            {
                result.Errors.Add("Отсутствует поле 'downloads' — неверный формат источника");
                return result;
            }

            if (root.Downloads.Count == 0)
            {
                result.Errors.Add("Список 'downloads' пуст");
                return result;
            }

            foreach (var item in root.Downloads)
            {
                if (ValidateItem(item, result.Errors))
                    result.ValidItemsCount++;
                else
                    result.InvalidItemsCount++;
            }

            result.IsValid = result.ValidItemsCount > 0;
            return result;
        }

        private static bool ValidateItem(HydraRepack item, List<string> errors)
        {
            var itemErrors = new List<string>();

            if (string.IsNullOrWhiteSpace(item.Title))
                itemErrors.Add("Отсутствует 'title'");

            if (item.Uris == null || !item.Uris.Any())
                itemErrors.Add("Отсутствует 'uris'");
            else if (!item.Uris.Any(u => u.StartsWith("magnet:")))
                itemErrors.Add("Нет magnet-ссылок в 'uris'");

            if (itemErrors.Count > 0)
            {
                errors.Add($"[{item.Title ?? "???"}]: {string.Join(", ", itemErrors)}");
                return false;
            }

            return true;
        }
    }
}
