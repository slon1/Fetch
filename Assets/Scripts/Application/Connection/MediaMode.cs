namespace WebRtcV2.Application.Connection
{
    /// <summary>
    /// Describes which media types are active in the current session.
    /// Tracked in <see cref="ConnectionSession"/> independently from lifecycle state
    /// so the FSM does not need separate ConnectedAudio / ConnectedDataOnly states.
    /// </summary>
    public enum MediaMode
    {
        /// <summary>Audio track only (MVP default).</summary>
        AudioOnly,

        /// <summary>DataChannel only — audio track removed or not available.</summary>
        DataOnly,

        /// <summary>Reserved for audio + video. Not used in MVP.</summary>
        Full,
    }
}
