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
        
        event Action OnRetrying;
        
        event Action<Exception> OnReceivedError;
        
        Task ConnectAsync(ISession session);
        
        Task DisconnectAsync();
        
        IClient Client { get; }
        
        ISocket Socket { get; }
        
        ConnectionConfig Config { get; }
        
        bool IsConnected { get; }
        
        CancellationToken ShutdownToken { get; }
    }
}