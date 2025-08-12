using UnityEngine;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;

namespace OlegGrizzly.NakamaNetworkWrapper.Persistence
{
    public sealed class PlayerPrefsTokenPersistence : ITokenPersistence
    {
        private const string AuthTokenKey = "Nakama.AuthToken";
        private const string RefreshTokenKey = "Nakama.RefreshToken";

        public bool HasToken => PlayerPrefs.HasKey(AuthTokenKey) && !string.IsNullOrEmpty(PlayerPrefs.GetString(AuthTokenKey));

        public void Save(string authToken, string refreshToken = null)
        {
            if (!string.IsNullOrEmpty(authToken))
            {
                PlayerPrefs.SetString(AuthTokenKey, authToken);
            }

            if (refreshToken != null)
            {
                if (string.IsNullOrEmpty(refreshToken))
                {
                    PlayerPrefs.DeleteKey(RefreshTokenKey);
                }
                else
                {
                    PlayerPrefs.SetString(RefreshTokenKey, refreshToken);
                }
            }

            PlayerPrefs.Save();
        }

        public bool TryLoad(out string authToken, out string refreshToken)
        {
            authToken = PlayerPrefs.GetString(AuthTokenKey, null);
            refreshToken = PlayerPrefs.GetString(RefreshTokenKey, null);

            return !string.IsNullOrEmpty(authToken);
        }

        public void Clear()
        {
            PlayerPrefs.DeleteKey(AuthTokenKey);
            PlayerPrefs.DeleteKey(RefreshTokenKey);
            PlayerPrefs.Save();
        }
    }
}
