using System;
using System.Collections.Generic;

namespace HydraTorrent.Models
{
    /// <summary>
    /// Кандидат на исполняемый файл игры
    /// </summary>
    public class ExecutableCandidate
    {
        /// <summary>
        /// Полный путь к файлу
        /// </summary>
        public string FilePath { get; set; }

        /// <summary>
        /// Имя файла (например, Cyberpunk2077.exe)
        /// </summary>
        public string FileName { get; set; }

        /// <summary>
        /// Размер файла в байтах
        /// </summary>
        public long FileSize { get; set; }

        /// <summary>
        /// Размер файла в читаемом формате (например, "45.2 MB")
        /// </summary>
        public string FileSizeFormatted
        {
            get
            {
                const long KB = 1024;
                const long MB = KB * 1024;
                const long GB = MB * 1024;

                if (FileSize >= GB)
                    return $"{FileSize / (double)GB:F1} GB";
                if (FileSize >= MB)
                    return $"{FileSize / (double)MB:F1} MB";
                if (FileSize >= KB)
                    return $"{FileSize / (double)KB:F1} KB";
                return $"{FileSize} B";
            }
        }

        /// <summary>
        /// ProductName из FileVersionInfo
        /// </summary>
        public string ProductName { get; set; }

        /// <summary>
        /// FileDescription из FileVersionInfo
        /// </summary>
        public string FileDescription { get; set; }

        /// <summary>
        /// CompanyName из FileVersionInfo
        /// </summary>
        public string CompanyName { get; set; }

        /// <summary>
        /// Очки уверенности (0-100)
        /// </summary>
        public int ConfidenceScore { get; set; }

        /// <summary>
        /// Причины начисления очков (для отображения в UI)
        /// </summary>
        public List<string> ScoreReasons { get; set; } = new List<string>();

        /// <summary>
        /// Флаг: файл называется Launcher.exe или похоже
        /// </summary>
        public bool IsLauncher { get; set; }

        /// <summary>
        /// Имя файла без расширения (для сравнения)
        /// </summary>
        public string FileNameWithoutExtension =>
            System.IO.Path.GetFileNameWithoutExtension(FileName);

        /// <summary>
        /// Директория, в которой находится файл
        /// </summary>
        public string Directory =>
            System.IO.Path.GetDirectoryName(FilePath);

        /// <summary>
        /// Возвращает строку с процентом уверенности
        /// </summary>
        public string ConfidencePercentage => $"{ConfidenceScore}%";
    }
}
