using System;
using System.Collections.Generic;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;

namespace OlegGrizzly.NakamaNetworkWrapper.Services
{
    public class UserCacheService : IUserCacheService, IDisposable
    {
        private readonly Dictionary<string, IApiUser> _userCache = new();
        
        private const int MaxUsersPerRequest = 100;

        public void AddUser(IApiUser user)
        {
            if (user == null || string.IsNullOrEmpty(user.Id)) return;
            
            _userCache[user.Id] = user;
        }

        public void RemoveUser(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return;
            
            _userCache.Remove(userId);
        }

        public IApiUser GetUser(string userId)
        {
            return string.IsNullOrEmpty(userId) ? null : _userCache.GetValueOrDefault(userId);
        }

        public bool ContainsUser(string userId)
        {
            return !string.IsNullOrEmpty(userId) && _userCache.ContainsKey(userId);
        }

        public void Dispose()
        {
            
        }
    }
}
