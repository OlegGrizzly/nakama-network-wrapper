using System;
using System.Threading;
using System.Threading.Tasks;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Config;

namespace OlegGrizzly.NakamaNetworkWrapper.Abstractions
{
    public interface IClientService : IDisposable
    {
        event Action OnConnecting;
        
        event Action OnConnected;
        
        event Action OnDisconnected;

        /// <summary>Fired only for unexpected connection drops (not manual disconnects).</summary>
        event Action OnConnectionLost;

        event Action OnRetrying;
        
        event Action<Exception> OnReceivedError;
        
        Task ConnectAsync(ISession session);
        
        Task DisconnectAsync();
        
        Task<IApiRpc> RpcAsync(string rpcName, string payload = null);
        
        IClient Client { get; }
        
        ISocket Socket { get; }
        
        ConnectionConfig Config { get; }
        
        bool IsConnected { get; }
        
        CancellationToken ShutdownToken { get; }
    }
}