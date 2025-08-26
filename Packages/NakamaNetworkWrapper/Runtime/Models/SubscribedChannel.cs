using Nakama;

namespace OlegGrizzly.NakamaNetworkWrapper.Models
{
	public sealed class SubscribedChannel
	{
		public SubscribedChannel(ChannelType type, string key, bool persistence, bool hidden)
		{
			Type = type;
			Key = key;
			Persistence = persistence;
			Hidden = hidden;
		}

		public ChannelType Type { get; }
		public string Key { get; }
		public bool Persistence { get; }
		public bool Hidden { get; }
	}
}


