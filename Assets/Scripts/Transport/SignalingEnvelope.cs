using System;
using UnityEngine;

namespace WebRtcV2.Transport
{
    /// <summary>
    /// Transport-agnostic signaling message. The same struct is used for HTTP polling and will
    /// be reused for WebSocket transport when that is added.
    /// </summary>
    [Serializable]
    public class SignalingEnvelope
    {
        public string sessionId;
        public string fromPeerId;
        public string toPeerId;

        /// <summary>
        /// Unique identifier for this message instance.
        /// Reserved for future deduplication — the Worker does not deduplicate in MVP.
        /// For non-trickle ICE each message type is written at most once per session,
        /// so deduplication is not required until trickle ICE or retries are introduced.
        /// </summary>
        public string messageId;

        public string type;
        public int ttlMs;
        public long sentAt;

        /// <summary>Payload serialized as a JSON string. Deserialize based on <see cref="type"/>.</summary>
        public string payloadJson;

        public static class Types
        {
            public const string Offer = "offer";
            public const string Answer = "answer";
            public const string IceCandidate = "ice-candidate";
            public const string EndOfCandidates = "end-of-candidates";
            public const string Ping = "ping";
            public const string Pong = "pong";
            public const string Hangup = "hangup";
            public const string Error = "error";
        }
    }

    [Serializable]
    public class SdpPayload
    {
        public string sdp;
        public string sdpType;
    }

    [Serializable]
    public class IceCandidatePayload
    {
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex;
    }

    [Serializable]
    public class SignalingErrorPayload
    {
        public string code;
        public string message;
    }
}
