namespace WebRtcV2.Application.Connection
{
    /// <summary>
    /// The action <see cref="ConnectionPolicy"/> recommends for the current session.
    /// The coordinator (not the policy) is responsible for executing the decision.
    /// </summary>
    public enum ConnectionPolicyDecision
    {
        /// <summary>No action required; quality is acceptable or data is insufficient.</summary>
        None,

        /// <summary>
        /// Quality has been sustained below threshold for enough consecutive samples.
        /// The coordinator should switch <see cref="MediaMode"/> to DataOnly and
        /// mute the local audio send path.
        /// </summary>
        DowngradeToDataOnly,
    }
}
