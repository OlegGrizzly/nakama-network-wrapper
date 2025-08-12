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
            var id = SystemInfo.deviceUniqueIdentifier;

            if (string.IsNullOrEmpty(id) || id == SystemInfo.unsupportedIdentifier)
            {
                id = PlayerPrefs.GetString(DeviceIdKey, null);
                if (string.IsNullOrEmpty(id))
                {
                    id = Guid.NewGuid().ToString();
                    PlayerPrefs.SetString(DeviceIdKey, id);
                    PlayerPrefs.Save();
                }
            }

            return id;
        }
    }
}
