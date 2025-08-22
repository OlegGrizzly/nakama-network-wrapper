using System;
using System.Collections.Generic;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;
using UnityEngine;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;

namespace OlegGrizzly.NakamaNetworkWrapper.Services
{
    public class UserCacheService : IUserCacheService, IDisposable
    {
        private readonly IClientService _clientService;
        private readonly IAuthService _authService;
        private readonly HashSet<string> _usersIds = new();
        private readonly Dictionary<string, IApiUser> _userCache = new();
        private readonly SemaphoreSlim _semaphore = new(MaxConcurrentRequests);
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly SemaphoreSlim _gate = new(1, 1);

        private const int MaxUsersPerRequest = 100;
        private const int MaxConcurrentRequests = 5;
        
        public UserCacheService(IClientService clientService, IAuthService authService)
        {
            _clientService = clientService ?? throw new ArgumentNullException(nameof(clientService));
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            
            if (_clientService.Client == null)
            {
                throw new ArgumentException("ClientService.Client is null", nameof(clientService));
            }
        }
        
        public async Task<IApiUser> GetUserAsync(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return null;
            if (_userCache.TryGetValue(userId, out var cached)) return cached;

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(5));
            
            await _gate.WaitAsync(linkedCts.Token);
            try
            {
                if (!_userCache.ContainsKey(userId))
                {
                    _usersIds.Add(userId);
                }
            }
            finally
            {
                _gate.Release();
            }

            await LoadUsersAsync(linkedCts.Token);

            _userCache.TryGetValue(userId, out var result);
            return result;
        }

        public async Task CollectUserIdsAsync(HashSet<string> ids)
        {
            if (ids == null) return;

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(5));

            await _gate.WaitAsync(linkedCts.Token);
            try
            {
                foreach (var id in ids)
                {
                    if (!string.IsNullOrEmpty(id))
                    {
                        _usersIds.Add(id);
                    }
                }
            }
            finally
            {
                _gate.Release();
            }

            await LoadUsersAsync(linkedCts.Token);
        }

        private async Task LoadUsersAsync(CancellationToken cancellationToken = default)
        {
            var session = _authService.Session ?? throw new InvalidOperationException("Not authenticated");

            List<string> userIdsToFetch;
            await _gate.WaitAsync(cancellationToken);
            try
            {
                userIdsToFetch = new List<string>(_usersIds.Where(id => !_userCache.ContainsKey(id)));
            }
            finally
            {
                _gate.Release();
            }

            if (userIdsToFetch.Count == 0) return;

            var tasks = new List<Task>();
            foreach (var chunk in ChunkBy(userIdsToFetch, MaxUsersPerRequest))
            {
                await _semaphore.WaitAsync(cancellationToken);
                tasks.Add(LoadUserChunkAsync(chunk, session, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }

        private async Task LoadUserChunkAsync(List<string> userIds, ISession session, CancellationToken cancellationToken = default)
        {
            try
            {
                var users = await _clientService.Client.GetUsersAsync(session, userIds.ToArray(), canceller: cancellationToken);

                await _gate.WaitAsync(cancellationToken);
                try
                {
                    foreach (var user in users.Users)
                    {
                        _userCache[user.Id] = user;
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UserCacheService] Error fetching users: {ex.Message}");
            }
            finally
            {
                _semaphore.Release();
            }
        }
        
        private static IEnumerable<List<T>> ChunkBy<T>(List<T> source, int chunkSize)
        {
            for (var i = 0; i < source.Count; i += chunkSize)
            {
                yield return source.GetRange(i, Math.Min(chunkSize, source.Count - i));
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();

            _gate.Wait();
            try
            {
                _usersIds?.Clear();
                _userCache?.Clear();
            }
            finally
            {
                _gate.Release();
            }

            _cancellationTokenSource.Dispose();
            _semaphore?.Dispose();
            _gate.Dispose();
        }
    }
}
