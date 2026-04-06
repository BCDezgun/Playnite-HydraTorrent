using HydraTorrent.Models;
using System;
using System.Collections.Generic;

namespace HydraTorrent.Services
{
    public interface ICompletedManager
    {
        List<TorrentResult> CompletedItems { get; }
        void LoadCompletedItems();
        void SaveCompletedItems();
        void AddCompletedItem(TorrentResult item);
        TorrentResult GetByGameId(Guid gameId);
        void UpdateItem(TorrentResult item);
        bool IsTorrentRemoved(string hash);
        IRemovedHashesManager GetRemovedHashesManager();
    }

    public interface IRemovedHashesManager
    {
        void Load();
        void Save();
        void AddRemovedHash(string hash);
        void RemoveRemovedHash(string hash);
        bool IsRemoved(string hash);
    }
}
