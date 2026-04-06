using System;
using System.Threading.Tasks;

namespace HydraTorrent.Services
{
    public interface IGameSetupService
    {
        Task ProcessDownloadedGameAsync(Guid gameId, string downloadPath, string torrentHash = null);
    }
}
