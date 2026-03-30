namespace WebRtcV2.Shared
{
    /// <summary>
    /// Centralised logging for FSM transitions and network events.
    /// All layer boundaries log through this class so the history is consistent and searchable.
    /// </summary>
    public class ConnectionDiagnostics
    {
        private readonly string _sessionId;
        private readonly string _localPeerId;

        public ConnectionDiagnostics(string sessionId = null, string localPeerId = null)
        {
            _sessionId = sessionId ?? "-";
            _localPeerId = localPeerId ?? "-";
        }

        public void UpdateSession(string sessionId)
        {
            // Called by coordinator when a new session starts so log lines carry the correct id.
            // Field is immutable in this simple version; override in subclass if needed.
        }

        public void LogTransition(string from, string to, string reason = null)
        {
            string msg = $"FSM {from} -> {to}";
            if (!string.IsNullOrEmpty(reason)) msg += $"  reason={reason}";
            WLog.Info("FSM", Tag(msg));
        }

        public void LogSignaling(string direction, string type, string detail = null)
        {
            string msg = $"{direction} [{type}]";
            if (!string.IsNullOrEmpty(detail)) msg += $"  {detail}";
            WLog.Info("Signal", Tag(msg));
        }

        public void LogIce(string eventType, string detail = null)
        {
            string msg = string.IsNullOrEmpty(detail) ? eventType : $"{eventType}: {detail}";
            WLog.Info("ICE", Tag(msg));
        }

        public void LogRoom(string action, string detail = null)
        {
            string msg = string.IsNullOrEmpty(detail) ? action : $"{action}: {detail}";
            WLog.Info("Room", Tag(msg));
        }

        public void LogInfo(string context, string message) =>
            WLog.Info(context, Tag(message));

        public void LogError(string context, string error) =>
            WLog.Error(context, Tag(error));

        public void LogWarning(string context, string message) =>
            WLog.Warn(context, Tag(message));

        private string Tag(string msg) => $"[s={_sessionId}] {msg}";
    }
}
