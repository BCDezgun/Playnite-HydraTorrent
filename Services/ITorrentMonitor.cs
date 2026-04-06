using HydraTorrent.Models;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace HydraTorrent.Services
{
    public interface ITorrentMonitor
    {
        void Start();
        void Stop();
        Task<bool> RemoveTorrentFromClientAsync(string hash);
        Task<int> RemoveTorrentsFromClientAsync(IEnumerable<string> hashes);
        CompletedManager GetCompletedManager();
    }
}
