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
    public class UserCacheService : IUserCacheService
    {
        private readonly IClientService _clientService;
        private readonly IAuthService _authService;
        private readonly HashSet<string> _usersIds = new();
        private readonly Dictionary<string, IApiUser> _userCache = new();
        private readonly SemaphoreSlim _semaphore = new(MaxConcurrentRequests);
        private readonly SemaphoreSlim _gate = new(1, 1);

        private Task _inflightLoadTask;
        private readonly object _loadStartLock = new();

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
        
        private Task EnsureLoadRunningAsync()
        {
            lock (_loadStartLock)
            {
                if (_inflightLoadTask == null || _inflightLoadTask.IsCompleted)
                {
                    _inflightLoadTask = LoadUsersInternalAsync();
                }
                return _inflightLoadTask;
            }
        }
        
        public async Task<IApiUser> GetUserAsync(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return null;
            Debug.Log($"[Chat][UserCache] GET START userId={userId}");
            if (_userCache.TryGetValue(userId, out var cached))
            {
                Debug.Log($"[Chat][UserCache] GET HIT userId={userId}");
                return cached;
            }

            await _gate.WaitAsync();
            try
            {
                if (!_userCache.ContainsKey(userId))
                {
                    _usersIds.Add(userId);
                    Debug.Log($"[Chat][UserCache] GET MISS userId={userId} → queued, pending={_usersIds.Count}");
                }
            }
            finally
            {
                _gate.Release();
            }

            await EnsureLoadRunningAsync();

            _userCache.TryGetValue(userId, out var result);
            if (result == null)
            {
                Debug.LogWarning($"[Chat][UserCache] GET AFTER-LOAD NULL userId={userId}; inQueue={_usersIds.Contains(userId)}; cacheCount={_userCache.Count}; pendingCount={_usersIds.Count}");
            }
            else
            {
                Debug.Log($"[Chat][UserCache] GET FOUND AFTER-LOAD userId={userId}");
            }
            return result;
        }

        public async Task CollectUserIdsAsync(HashSet<string> ids)
        {
            if (ids == null) return;

            await _gate.WaitAsync();
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

            await EnsureLoadRunningAsync();
        }

        private async Task LoadUsersInternalAsync()
        {
            var session = _authService.Session ?? throw new InvalidOperationException("Not authenticated");

            List<string> userIdsToFetch;
            await _gate.WaitAsync();
            try
            {
                userIdsToFetch = new List<string>(_usersIds.Where(id => !_userCache.ContainsKey(id)));
            }
            finally
            {
                _gate.Release();
            }

            Debug.Log($"[Chat][UserCache] LOAD BUILD pendingUnique={userIdsToFetch.Count}, cache={_userCache.Count}");

            if (userIdsToFetch.Count == 0) return;

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var tasks = new List<Task>();
            
            foreach (var chunk in ChunkBy(userIdsToFetch, MaxUsersPerRequest))
            {
                await _semaphore.WaitAsync(timeoutCts.Token);
                tasks.Add(LoadUserChunkAsync(chunk, session, timeoutCts.Token));
            }

            await Task.WhenAll(tasks);
            
            await _gate.WaitAsync(timeoutCts.Token);
            try
            {
                foreach (var id in userIdsToFetch)
                {
                    if (_userCache.ContainsKey(id))
                    {
                        _usersIds.Remove(id);
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task LoadUserChunkAsync(List<string> userIds, ISession session, CancellationToken cancellationToken = default)
        {
            try
            {
                var users = await _clientService.Client.GetUsersAsync(session, userIds.ToArray(), canceller: cancellationToken);
                Debug.Log($"[Chat][UserCache] CHUNK FETCH OK requested={userIds.Count}, received={users.Users?.Count() ?? 0}");

                await _gate.WaitAsync(cancellationToken);
                try
                {
                    foreach (var user in users.Users)
                    {
                        _userCache[user.Id] = user;
                    }
                    var receivedIds = new HashSet<string>(users.Users.Select(u => u.Id));
                    var missing = userIds.Where(id => !receivedIds.Contains(id)).ToList();
                    if (missing.Count > 0)
                    {
                        Debug.LogWarning($"[Chat][UserCache] CHUNK MISSING ids={missing.Count} example=[{string.Join(",", missing.Take(5))}] ");
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Chat][UserCache] CHUNK ERROR requested={userIds.Count} → {ex.GetType().Name}: {ex.Message}");
                Debug.LogWarning($"[Chat][UserCache] CHUNK ERROR IDS example=[{string.Join(",", userIds.Take(5))}] ");
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
            if (_gate.Wait(TimeSpan.FromSeconds(1)))
            {
                try
                {
                    _usersIds?.Clear();
                    _userCache?.Clear();
                    _inflightLoadTask = null;
                }
                finally
                {
                    _gate.Release();
                }
            }

            _semaphore?.Dispose();
            _gate.Dispose();
        }
    }
}