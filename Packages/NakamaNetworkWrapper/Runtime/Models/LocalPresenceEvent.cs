using System.Collections.Generic;
using Nakama;

namespace OlegGrizzly.NakamaNetworkWrapper.Models
{
    public class LocalPresenceEvent
    {
        public string ChannelId { get; }
        public IReadOnlyList<IUserPresence> Joins { get; }
        public IReadOnlyList<IUserPresence> Leaves { get; }

        public LocalPresenceEvent(string channelId, IReadOnlyList<IUserPresence> joins, IReadOnlyList<IUserPresence> leaves)
        {
            ChannelId = channelId;
            Joins = joins;
            Leaves = leaves;
        }
    }
}