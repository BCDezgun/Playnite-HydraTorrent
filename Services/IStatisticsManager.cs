using HydraTorrent.Models;
using System.Collections.Generic;

namespace HydraTorrent.Services
{
    public interface IStatisticsManager
    {
        DownloadStatistics Statistics { get; }
        void Load();
        void Save();
        void AddCompletedDownload(TorrentResult item);
        void RecalculateFromCompleted();
        string GetFormattedTotalSize();
        string GetFormattedTotalUploaded();
        string GetFormattedDuration();
        double GetAverageDownloadSpeed();
        string GetFormattedAverageSpeed();
        double GetOverallRatio();
        List<TopGameRecord> GetTopGamesBySize(int count = 5);
        void Reset();
    }
}
