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

            [Tooltip("How often to poll Worker for signaling slot messages, seconds")]
            public float pollingIntervalSec = 1.5f;

            [Tooltip("TTL for a posted signaling message on the Worker, seconds")]
            public int signalingMessageTtlSec = 60;

            [Tooltip("Legacy room TTL kept only for migration compatibility")]
            public int roomTtlSec = 180;

            [Tooltip("Legacy waiting-room heartbeat interval kept only for migration compatibility")]
            public float roomHeartbeatIntervalSec = 20f;

            [Tooltip("Legacy waiting-room heartbeat timeout kept only for migration compatibility")]
            public int roomHeartbeatTimeoutSec = 30;

            [Tooltip("Reconnect delay for the booth control WebSocket, seconds")]
            public float roomControlSocketReconnectDelaySec = 2f;
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
            [Tooltip("Legacy serialized section name. Currently used only for display-name length limits.")]
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

            [Tooltip("If enabled, the next connection attempt after a direct-path failure uses relayOnly")]
            public bool enableRelayFallback = true;
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
