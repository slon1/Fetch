using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WebRtcV2.Application.Connection;

namespace WebRtcV2.Presentation
{
    /// <summary>
    /// Overlay that shows current connection state, recovery feedback and a simple
    /// connection-quality indicator.
    /// </summary>
    public class ConnectionStatusView : MonoBehaviour
    {
        [Header("Texts")]
        [SerializeField] private TMP_Text stateText;
        [SerializeField] private TMP_Text errorText;
        [SerializeField] private TMP_Text qualityText;

        [Header("Quality Indicator")]
        [SerializeField] private Image qualityIndicator;
        [SerializeField] private Color qualityGoodColor = new(0.20f, 0.85f, 0.35f, 1f);
        [SerializeField] private Color qualityLimitedColor = new(0.95f, 0.78f, 0.16f, 1f);
        [SerializeField] private Color qualityLostColor = new(0.92f, 0.28f, 0.22f, 1f);

        private string _externalError;
        private string _connectionMessage;
        private string _trackedSessionId;
        private bool _hasConnectedInCurrentSession;
        private ConnectionLifecycleState _previousLifecycleState = ConnectionLifecycleState.Idle;

        public void SetSnapshot(ConnectionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                if (stateText != null) stateText.text = string.Empty;
                if (qualityText != null) qualityText.text = string.Empty;
                if (qualityIndicator != null) qualityIndicator.enabled = false;

                _connectionMessage = null;
                RenderErrorText();
                _trackedSessionId = null;
                _hasConnectedInCurrentSession = false;
                _previousLifecycleState = ConnectionLifecycleState.Idle;
                return;
            }

            if (_trackedSessionId != snapshot.SessionId)
            {
                _trackedSessionId = snapshot.SessionId;
                _hasConnectedInCurrentSession = false;
            }

            if (snapshot.LifecycleState == ConnectionLifecycleState.Connected)
                _hasConnectedInCurrentSession = true;

            if (stateText != null)
                stateText.text = BuildStatusText(snapshot);

            UpdateQualityIndicator(snapshot);
            UpdateConnectionMessage(snapshot);
            RenderErrorText();

            _previousLifecycleState = snapshot.LifecycleState;
        }

        public void ShowError(string message)
        {
            _externalError = message;
            RenderErrorText();
        }

        public void ClearError()
        {
            _externalError = null;
            RenderErrorText();
        }

        private string BuildStatusText(ConnectionSnapshot snapshot)
        {
            switch (snapshot.LifecycleState)
            {
                case ConnectionLifecycleState.Idle:
                case ConnectionLifecycleState.Closed:
                    return string.Empty;

                case ConnectionLifecycleState.Connected:
                    string prefix = _previousLifecycleState == ConnectionLifecycleState.Recovering
                        ? "Reconnected"
                        : "Connected";
                    return $"{prefix} | {snapshot.MediaMode} | {snapshot.RouteMode} | {snapshot.SignalingMode}";

                case ConnectionLifecycleState.Recovering:
                    return _hasConnectedInCurrentSession
                        ? $"Recovering | {snapshot.MediaMode} | {snapshot.RouteMode} | {snapshot.SignalingMode}"
                        : "Connecting...";

                default:
                    return LifecycleLabel(snapshot.LifecycleState);
            }
        }

        private static string LifecycleLabel(ConnectionLifecycleState state) => state switch
        {
            ConnectionLifecycleState.Preparing => "Preparing...",
            ConnectionLifecycleState.Signaling => "Signaling...",
            ConnectionLifecycleState.Connecting => "Connecting...",
            ConnectionLifecycleState.Failed => "Connection failed",
            _ => state.ToString(),
        };

        private void UpdateQualityIndicator(ConnectionSnapshot snapshot)
        {
            var visual = ResolveQualityVisual(snapshot);

            if (qualityIndicator != null)
            {
                qualityIndicator.enabled = true;
                qualityIndicator.color = visual.Color;
            }

            if (qualityText != null)
                qualityText.text = visual.Label;
        }

        private void UpdateConnectionMessage(ConnectionSnapshot snapshot)
        {
            _connectionMessage = snapshot.LifecycleState switch
            {
                ConnectionLifecycleState.Recovering when _hasConnectedInCurrentSession
                    => "Connection lost. Trying to restore...",
                ConnectionLifecycleState.Failed when _hasConnectedInCurrentSession
                    => "Connection lost.",
                ConnectionLifecycleState.Closed when _previousLifecycleState == ConnectionLifecycleState.Recovering
                    => "Connection lost.",
                ConnectionLifecycleState.Connected when _previousLifecycleState == ConnectionLifecycleState.Recovering
                    => "Reconnected.",
                _ => null
            };
        }

        private void RenderErrorText()
        {
            if (errorText == null) return;

            string text = !string.IsNullOrWhiteSpace(_externalError)
                ? _externalError
                : (_connectionMessage ?? string.Empty);

            errorText.text = text;
            errorText.gameObject.SetActive(!string.IsNullOrEmpty(text));
        }

        private QualityVisual ResolveQualityVisual(ConnectionSnapshot snapshot)
        {
            if (_hasConnectedInCurrentSession &&
                (snapshot.LifecycleState == ConnectionLifecycleState.Recovering ||
                snapshot.LifecycleState == ConnectionLifecycleState.Failed ||
                snapshot.LifecycleState == ConnectionLifecycleState.Closed))
            {
                return new QualityVisual(qualityLostColor, "No Link");
            }

            if (snapshot.LifecycleState != ConnectionLifecycleState.Connected)
                return new QualityVisual(qualityLimitedColor, "Connecting");

            return snapshot.MediaMode switch
            {
                MediaMode.DataOnly => new QualityVisual(qualityLimitedColor, "Limited"),
                MediaMode.Full => new QualityVisual(qualityGoodColor, "Video"),
                _ => new QualityVisual(qualityGoodColor, "Stable")
            };
        }

        private readonly struct QualityVisual
        {
            public QualityVisual(Color color, string label)
            {
                Color = color;
                Label = label;
            }

            public Color Color { get; }
            public string Label { get; }
        }
    }
}
