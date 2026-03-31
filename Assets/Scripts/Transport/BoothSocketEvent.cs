using System;

namespace WebRtcV2.Transport
{
    [Serializable]
    public sealed class BoothSocketEvent
    {
        public string type;
        public string boothNumber;
        public string lineState;
        public string callId;
        public string peerNumber;
        public string callerNumber;
        public string calleeNumber;
        public string callerClientId;
        public string reason;

        public static class Types
        {
            public const string LineSnapshot = "line_snapshot";
            public const string IncomingCall = "incoming_call";
            public const string OutgoingRinging = "outgoing_ringing";
            public const string CallAccepted = "call_accepted";
            public const string CallRejected = "call_rejected";
            public const string RemoteHangup = "remote_hangup";
            public const string Busy = "busy";
            public const string Offline = "offline";
            public const string LineReset = "line_reset";
        }
    }
}
