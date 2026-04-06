using QBittorrent.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HydraTorrent.Services
{
    public class QBittorrentClientAdapter : ITorrentClient
    {
        private readonly QBittorrentClient _client;
        private bool _disposed;

        public QBittorrentClientAdapter(Uri url)
        {
            _client = new QBittorrentClient(url);
        }

        public async Task LoginAsync(string username, string password)
        {
            await _client.LoginAsync(username, password);
        }

        public async Task<IList<TorrentInfoDto>> GetTorrentListAsync()
        {
            var torrents = await _client.GetTorrentListAsync();
            return torrents.Select(Map).ToList();
        }

        public async Task<IList<TorrentFileDto>> GetTorrentContentsAsync(string hash)
        {
            var files = await _client.GetTorrentContentsAsync(hash);
            return files.Select(f => new TorrentFileDto
            {
                Name = f.Name,
                Size = f.Size
            }).ToList();
        }

        public async Task PauseAsync(string hash)
        {
            await _client.PauseAsync(hash);
        }

        public async Task ResumeAsync(string hash)
        {
            await _client.ResumeAsync(hash);
        }

        public async Task DeleteAsync(string hash, bool deleteFiles = false)
        {
            await _client.DeleteAsync(hash, deleteFiles);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _client?.Dispose();
                _disposed = true;
            }
        }

        private static TorrentInfoDto Map(TorrentInfo t)
        {
            return new TorrentInfoDto
            {
                Hash = t.Hash,
                Name = t.Name,
                Progress = t.Progress,
                DownloadSpeed = t.DownloadSpeed,
                UploadSpeed = t.UploadSpeed,
                TotalSize = t.TotalSize,
                Downloaded = t.Downloaded,
                Uploaded = t.Uploaded,
                Ratio = t.Ratio,
                EstimatedTime = t.EstimatedTime,
                TotalSeeds = t.TotalSeeds,
                TotalLeechers = t.TotalLeechers,
                State = MapState(t.State)
            };
        }

        private static TorrentStateDto MapState(TorrentState state)
        {
            var name = state.ToString();
            if (name.Contains("Downloading") && !name.Contains("Paused")) return TorrentStateDto.Downloading;
            if (name.Contains("Stalled") && !name.Contains("UP")) return TorrentStateDto.StalledDL;
            if (name.Contains("Uploading")) return TorrentStateDto.Uploading;
            if (name.Contains("Stalled") && name.Contains("UP")) return TorrentStateDto.StalledUP;
            if (name.Contains("Paused") && name.Contains("DL")) return TorrentStateDto.PausedDL;
            if (name.Contains("Paused") && name.Contains("UP")) return TorrentStateDto.PausedUP;
            if (name.Contains("Queued") && name.Contains("DL")) return TorrentStateDto.QueuedDL;
            if (name.Contains("Queued") && name.Contains("UP")) return TorrentStateDto.QueuedUP;
            if (name.Contains("Checking")) return TorrentStateDto.CheckingDL;
            if (name.Contains("Error")) return TorrentStateDto.Error;
            if (name.Contains("Missing")) return TorrentStateDto.MissingFiles;
            return TorrentStateDto.Unknown;
        }
    }
}
