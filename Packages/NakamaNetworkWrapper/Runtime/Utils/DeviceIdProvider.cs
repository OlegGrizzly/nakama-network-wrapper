using System;
using UnityEngine;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;

namespace OlegGrizzly.NakamaNetworkWrapper.Utils
{
    public sealed class DeviceIdProvider : IDeviceIdProvider
    {
        private const string DeviceIdKey = "Device.UniqueId";
        
        public string GetDeviceId()
        {
            var id = PlayerPrefs.GetString(DeviceIdKey, string.Empty);
            if (!string.IsNullOrEmpty(id))
            {
                return id;
            }
            
            if (string.IsNullOrEmpty(id) || id == SystemInfo.unsupportedIdentifier)
            {
                id = Guid.NewGuid().ToString();
            }
            
            PlayerPrefs.SetString(DeviceIdKey, id);
            PlayerPrefs.Save();
            
            return id;
        }
    }
}
