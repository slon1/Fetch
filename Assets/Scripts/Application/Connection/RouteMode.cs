namespace WebRtcV2.Application.Connection
{
    /// <summary>
    /// Describes the active ICE routing strategy.
    /// Updated by the policy engine (Stage 5) when a relay fallback or manual
    /// bootstrap is selected. MVP always stays on <see cref="Direct"/>.
    /// </summary>
    public enum RouteMode
    {
        /// <summary>STUN-assisted P2P; no TURN relay (MVP default).</summary>
        Direct,

        /// <summary>Forced TURN relay. Set when policy decides to force relay.</summary>
        Relay,

        /// <summary>Credentials/SDP exchanged out-of-band (manual / QR bootstrap).</summary>
        ManualBootstrap,
    }
}
