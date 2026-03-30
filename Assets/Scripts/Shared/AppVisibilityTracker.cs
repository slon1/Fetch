namespace WebRtcV2.Shared
{
    /// <summary>
    /// Tracks whether the Unity app is currently visible and focused.
    /// Used for best-effort local notifications while the app is still alive in background.
    /// </summary>
    public sealed class AppVisibilityTracker
    {
        public bool IsFocused { get; private set; } = true;
        public bool IsPaused { get; private set; }

        public bool ShouldShowBackgroundNotification => IsPaused || !IsFocused;

        public void SetFocused(bool focused) => IsFocused = focused;

        public void SetPaused(bool paused) => IsPaused = paused;
    }
}
