using UnityEngine;

namespace WebRtcV2.Config
{
    [CreateAssetMenu(fileName = "AppConfig", menuName = "WebRtcV2/AppConfig")]
    public class AppConfig : ScriptableObject
    {
        [SerializeField] public WorkerEndpointSection workerEndpoint = new WorkerEndpointSection();
        [SerializeField] public IceSection ice = new IceSection();
        [SerializeField] public RoomSection room = new RoomSection();
        [SerializeField] public ConnectionSection connection = new ConnectionSection();
        [SerializeField] public PolicySection policy = new PolicySection();

        [System.Serializable]
        public class WorkerEndpointSection
        {
            [Tooltip("Base URL of the Cloudflare Worker, without trailing slash")]
            public string baseUrl = "https://webrtc-signaler.slon-ru-tmp.workers.dev";

            [Tooltip("How often to poll Worker for signaling messages and room status, seconds")]
            public float pollingIntervalSec = 1.5f;

            [Tooltip("TTL for a posted signal message on the Worker, seconds")]
            public int signalingMessageTtlSec = 60;

            [Tooltip("TTL for a room entry in the lobby index, seconds")]
            public int roomTtlSec = 180;

            [Tooltip("How often the waiting room owner refreshes presence via heartbeat, seconds")]
            public float roomHeartbeatIntervalSec = 20f;

            [Tooltip("How long a waiting room stays visible without heartbeat, seconds")]
            public int roomHeartbeatTimeoutSec = 30;
        }

        [System.Serializable]
        public class IceSection
        {
            [Tooltip("Force TURN relay only. Hides local IP but adds latency.")]
            public bool relayOnly = false;

            [Tooltip("STUN server URLs. TURN credentials come from ISecretsProvider, not here.")]
            public string[] stunUrls = new[]
            {
                "stun:stun.l.google.com:19302",
                "stun:stun.relay.metered.ca:80"
            };
        }

        [System.Serializable]
        public class RoomSection
        {
            [Tooltip("Max characters in a display name")]
            public int maxDisplayNameLength = 24;
        }

        [System.Serializable]
        public class ConnectionSection
        {
            [Tooltip("Max automatic reconnect attempts before transitioning to Failed")]
            public int reconnectMaxAttempts = 3;

            [Tooltip("Base delay between reconnect attempts, seconds (exponential backoff)")]
            public float reconnectDelayBaseSec = 2f;

            [Tooltip("How long to wait after IceDisconnected before escalating to recovery, milliseconds")]
            public int iceDisconnectedGraceMs = 3000;

            [Tooltip("Timeout for ICE gathering phase, milliseconds")]
            public int iceGatheringTimeoutMs = 3000;

            [Tooltip("Timeout for ICE restart attempt, milliseconds")]
            public int iceRestartTimeoutMs = 8000;

            [Tooltip("Timeout for initial ICE connect after offer/answer exchange, milliseconds")]
            public int initialIceConnectTimeoutMs = 20000;

            [Tooltip("Total timeout for waiting a signaling response (offer/answer), milliseconds")]
            public int signalingPollTimeoutMs = 30000;
        }

        [System.Serializable]
        public class PolicySection
        {
            [Tooltip("How often WebRtcStatsSampler polls RTCPeerConnection.GetStats(), in milliseconds")]
            public int statsPollingIntervalMs = 1000;

            [Tooltip("RTT threshold in ms. Samples above this value are considered bad quality.")]
            public int degradeRttThresholdMs = 400;

            [Tooltip("Jitter threshold in ms. Samples above this value are considered bad quality.")]
            public float degradeJitterThresholdMs = 100f;

            [Tooltip("Packet loss threshold in percent (0-100). Samples above this are considered bad quality.")]
            public float degradePacketLossPercent = 10f;

            [Tooltip("Number of consecutive bad-quality samples required before triggering a downgrade decision.")]
            public int degradeConsecutiveBadSamples = 3;
        }
    }
}

