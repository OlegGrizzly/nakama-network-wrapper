using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nakama;

namespace OlegGrizzly.NakamaNetworkWrapper.Abstractions
{
    public interface IUserCacheService : IDisposable
    {
        Task<IApiUser> GetUserAsync(string userId);
        
        Task CollectUserIdsAsync(HashSet<string> ids);
    }
}
