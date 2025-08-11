using System.Collections.Generic;
using System.Threading.Tasks;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Common;

namespace OlegGrizzly.NakamaNetworkWrapper.Abstractions
{
    public interface IAuthService
    {
        Task<ISession> LoginAsync(AuthType type, string id, string username = null, Dictionary<string, string> vars = null);
        
        Task LogoutAsync();
        
        bool IsAuthenticated { get; }
        
        ISession CurrentSession { get; }
    }
}