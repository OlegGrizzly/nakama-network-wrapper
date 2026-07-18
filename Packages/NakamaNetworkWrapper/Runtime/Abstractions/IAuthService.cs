using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Common;

namespace OlegGrizzly.NakamaNetworkWrapper.Abstractions
{
    public interface IAuthService
    {
        event Action<ISession> OnAuthenticated;
        
        event Action<IApiAccount> OnAccountChanged;
        
        event Action<Exception> OnAuthenticationFailed;
        
        event Action OnLoggedOut;

        /// <summary>Fired when the stored session can no longer be refreshed and a full re-login is required.</summary>
        event Action OnSessionExpired;

        Task<ISession> LoginAsync(AuthType type, string id, string username = null, Dictionary<string, string> vars = null);

        Task<bool> TryRestoreSessionAsync();

        /// <summary>Refreshes the session if needed and re-opens the socket. Returns false if there is no usable session or the attempt failed.</summary>
        Task<bool> TryReconnectAsync(CancellationToken ct = default);
        
        Task LogoutAsync();
        
        bool IsAuthenticated { get; }
        
        ISession Session { get; }

        IApiAccount Account { get; }

        Task UpdateAccountAsync(string username = null, string displayName = null, string avatarUrl = null, string langTag = null, string location = null, string timezone = null, CancellationToken ct = default);
    }
}