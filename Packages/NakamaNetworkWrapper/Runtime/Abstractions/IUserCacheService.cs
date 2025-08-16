using Nakama;

namespace OlegGrizzly.NakamaNetworkWrapper.Abstractions
{
    public interface IUserCacheService
    {
        void AddUser(IApiUser user);
        
        void RemoveUser(string userId);
        
        IApiUser GetUser(string userId);
        
        bool ContainsUser(string userId);
    }
}
