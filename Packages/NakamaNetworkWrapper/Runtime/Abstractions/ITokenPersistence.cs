namespace OlegGrizzly.NakamaNetworkWrapper.Abstractions
{
    public interface ITokenPersistence
    {
        bool HasToken { get; }
        
        void Save(string authToken, string refreshToken = null);
        
        bool TryLoad(out string authToken, out string refreshToken);
        
        void Clear();
    }
}
