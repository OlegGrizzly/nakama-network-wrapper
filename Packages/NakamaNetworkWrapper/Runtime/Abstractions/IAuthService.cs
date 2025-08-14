using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Common;

namespace OlegGrizzly.NakamaNetworkWrapper.Abstractions
{
    public interface IAuthService
    {
        event Action<ISession> OnAuthenticated;
        
        event Action<Exception> OnAuthenticationFailed;
        
        event Action OnLoggedOut;
        
        Task<ISession> LoginAsync(AuthType type, string id, string username = null, Dictionary<string, string> vars = null);
        
        Task<bool> TryRestoreSessionAsync();
        
        Task LogoutAsync();
        
        bool IsAuthenticated { get; }
        
        ISession CurrentSession { get; }

        IApiAccount Account { get; }
    }
}