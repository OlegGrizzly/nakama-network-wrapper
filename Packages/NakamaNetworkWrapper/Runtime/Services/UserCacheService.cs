using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;
using UnityEngine;
using System.Threading.Tasks;
using System.Threading;

namespace OlegGrizzly.NakamaNetworkWrapper.Services
{
    public class UserCacheService : IUserCacheService
    {
        private readonly IClientService _clientService;
        private readonly IAuthService _authService;
        private readonly HashSet<string> _usersIds = new();
        private readonly ConcurrentDictionary<string, IApiUser> _userCache = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _semaphore = new(MaxConcurrentRequests);
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly SemaphoreSlim _gate = new(1, 1);

        private readonly ConcurrentDictionary<string, TaskCompletionSource<IApiUser>> _waiters = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _retryCounts = new(StringComparer.Ordinal);

        private int _isLoading;
        
        private const int MaxUsersPerRequest = 50;
        private const int MaxConcurrentRequests = 5;
        private const int MaxRetryAttempts = 3;
        
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
            
            var tcs = _waiters.GetOrAdd(userId, _ => new TaskCompletionSource<IApiUser>(TaskCreationOptions.RunContinuationsAsynchronously));

            await _gate.WaitAsync(_cancellationTokenSource.Token);
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
            
            TryStartLoader(_cancellationTokenSource.Token);
            
            return await tcs.Task;
        }

        public async Task CollectUserIdsAsync(HashSet<string> ids)
        {
            if (ids == null || ids.Count == 0) return;

            await _gate.WaitAsync(_cancellationTokenSource.Token);
            try
            {
                foreach (var id in ids)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    if (!_userCache.ContainsKey(id))
                    {
                        _usersIds.Add(id);
                        _waiters.TryAdd(id, new TaskCompletionSource<IApiUser>(TaskCreationOptions.RunContinuationsAsynchronously));
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
            
            TryStartLoader(_cancellationTokenSource.Token);
        }

        private void TryStartLoader(CancellationToken token)
        {
            if (Interlocked.Exchange(ref _isLoading, 1) == 0)
            {
                _ = LoadUsersAsync(token).ContinueWith(_ => _isLoading = 0, token);
            }
        }

        private async Task LoadUsersAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var session = _authService.Session ?? throw new InvalidOperationException("Not authenticated");

                List<string> userIdsToFetch;
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    userIdsToFetch = new List<string>(_usersIds.Count);
                    foreach (var id in _usersIds)
                    {
                        if (!_userCache.ContainsKey(id))
                        {
                            userIdsToFetch.Add(id);
                        }
                    }
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

                await _gate.WaitAsync(cancellationToken);
                try
                {
                    foreach (var id in userIdsToFetch)
                    {
                        _usersIds.Remove(id);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UserCacheService] LoadUsersAsync failed: {ex}");
            }
        }

        private async Task LoadUserChunkAsync(List<string> userIds, ISession session, CancellationToken cancellationToken = default)
        {
            try
            {
                var users = await _clientService.Client.GetUsersAsync(session, userIds.ToArray(), canceller: cancellationToken);

                var foundIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var u in users.Users)
                {
                    foundIds.Add(u.Id);
                }

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

                foreach (var user in users.Users)
                {
                    if (_waiters.TryRemove(user.Id, out var w))
                    {
                        w.TrySetResult(user);
                    }

                    _retryCounts.TryRemove(user.Id, out _);
                }
                
                foreach (var id in userIds)
                {
                    if (!foundIds.Contains(id))
                    {
                        if (_waiters.TryRemove(id, out var w))
                        {
                            w.TrySetResult(null);
                        }
                        
                        var attempts = _retryCounts.AddOrUpdate(id, 1, (_, cur) => cur + 1);
                        if (attempts <= MaxRetryAttempts)
                        {
                            await _gate.WaitAsync(cancellationToken);
                            try
                            {
                                _usersIds.Add(id);
                            }
                            finally
                            {
                                _gate.Release();
                            }
                            
                            _ = LoadUsersAsync(cancellationToken);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UserCacheService] Error fetching users: {ex.Message}");
              
                foreach (var id in userIds)
                {
                    if (_waiters.TryRemove(id, out var w))
                    {
                        w.TrySetResult(null);
                    }

                    var attempts = _retryCounts.AddOrUpdate(id, 1, (_, cur) => cur + 1);
                    if (attempts <= MaxRetryAttempts)
                    {
                        try
                        {
                            await _gate.WaitAsync(cancellationToken);
                            try
                            {
                                _usersIds.Add(id);
                            }
                            finally
                            {
                                _gate.Release();
                            }

                            _ = LoadUsersAsync(cancellationToken);
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[UserCacheService] Retry re-enqueue failed for {id}: {e}");
                        }
                    }
                }
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
                foreach (var kv in _waiters)
                {
                    if (_waiters.TryRemove(kv.Key, out var w))
                    {
                        w.TrySetResult(null);
                    }
                }

                _usersIds?.Clear();
                _userCache?.Clear();
                _retryCounts.Clear();
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
