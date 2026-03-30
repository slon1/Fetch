using System;

namespace WebRtcV2.Transport
{
    [Serializable]
    public sealed class RoomControlEvent
    {
        public string type;
        public string sessionId;
        public string roomStatus;
        public string signalType;

        public static class Types
        {
            public const string RoomState = "room_state";
            public const string PeerJoined = "peer_joined";
            public const string RoomClosed = "room_closed";
            public const string SignalAvailable = "signal_available";
        }
    }
}