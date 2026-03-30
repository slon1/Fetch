using System;
using UnityEngine;

namespace WebRtcV2.Shared
{
    /// <summary>
    /// Minimal JSON envelope for messages sent over the WebRTC DataChannel.
    /// All messages use this wrapper so the receiver can dispatch by type without raw string parsing.
    /// </summary>
    [Serializable]
    public class DataChannelEnvelope
    {
        public string type;
        public string payload;
        public long timestamp;

        public static class Types
        {
            public const string Chat = "chat";
            public const string Ping = "ping";
            public const string Pong = "pong";
            public const string System = "system";
            public const string VoiceState = "voice_state";
        }

        public static class VoiceStates
        {
            public const string Speaking = "speaking";
            public const string Silent = "silent";
        }

        public static DataChannelEnvelope Chat(string text) => new DataChannelEnvelope
        {
            type = Types.Chat,
            payload = text,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        public static DataChannelEnvelope Ping() => new DataChannelEnvelope
        {
            type = Types.Ping,
            payload = string.Empty,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        public static DataChannelEnvelope Pong() => new DataChannelEnvelope
        {
            type = Types.Pong,
            payload = string.Empty,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        public static DataChannelEnvelope VoiceState(bool speaking) => new DataChannelEnvelope
        {
            type = Types.VoiceState,
            payload = speaking ? VoiceStates.Speaking : VoiceStates.Silent,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        public string Serialize() => JsonUtility.ToJson(this);

        public static DataChannelEnvelope Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<DataChannelEnvelope>(json); }
            catch { return null; }
        }
    }
}
