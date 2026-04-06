using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace HydraTorrent.Services
{
    public class TorrentInfoDto
    {
        public string Hash { get; set; }
        public string Name { get; set; }
        public double Progress { get; set; }
        public long DownloadSpeed { get; set; }
        public long UploadSpeed { get; set; }
        public long? TotalSize { get; set; }
        public long? Downloaded { get; set; }
        public long? Uploaded { get; set; }
        public double Ratio { get; set; }
        public TimeSpan? EstimatedTime { get; set; }
        public int? TotalSeeds { get; set; }
        public int? TotalLeechers { get; set; }
        public TorrentStateDto State { get; set; }
    }

    public class TorrentFileDto
    {
        public string Name { get; set; }
        public long Size { get; set; }
    }

    public enum TorrentStateDto
    {
        Downloading,
        StalledDL,
        Uploading,
        StalledUP,
        PausedDL,
        PausedUP,
        QueuedDL,
        QueuedUP,
        CheckingDL,
        CheckingUP,
        Error,
        MissingFiles,
        Unknown
    }

    public interface ITorrentClient : IDisposable
    {
        Task LoginAsync(string username, string password);
        Task<IList<TorrentInfoDto>> GetTorrentListAsync();
        Task<IList<TorrentFileDto>> GetTorrentContentsAsync(string hash);
        Task PauseAsync(string hash);
        Task ResumeAsync(string hash);
        Task DeleteAsync(string hash, bool deleteFiles = false);
    }
}
