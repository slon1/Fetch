namespace WebRtcV2.Application.Connection
{
    /// <summary>
    /// Describes how SDP/ICE exchange is being performed.
    /// MVP always uses <see cref="WorkerPolling"/>. Other modes are placeholders
    /// for Stage 6 (WebSocket) and Stage 7 (manual / offline bootstrap).
    /// </summary>
    public enum SignalingMode
    {
        /// <summary>HTTP polling against the Cloudflare Worker KV (MVP default).</summary>
        WorkerPolling,

        /// <summary>Reserved: WebSocket connection to the Worker (Stage 6).</summary>
        WorkerWebSocket,

        /// <summary>Reserved: out-of-band manual signaling (Stage 7).</summary>
        Manual,
    }
}
