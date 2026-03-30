using Unity.WebRTC;

namespace WebRtcV2.Transport
{
    /// <summary>
    /// Immutable point-in-time quality snapshot collected by WebRtcStatsSampler.
    /// Nullable fields are populated only when the corresponding stats are available.
    /// </summary>
    public sealed class QualitySnapshot
    {
        public long TimestampUtcMs { get; }
        public RTCIceConnectionState IceState { get; }
        public double? RttMs { get; }
        public double? JitterMs { get; }
        public float? PacketLossPercent { get; }
        public double? AvailableOutgoingBitrate { get; }
        public string SelectedRouteSummary { get; }

        public QualitySnapshot(
            long timestampUtcMs,
            RTCIceConnectionState iceState,
            double? rttMs,
            double? jitterMs,
            float? packetLossPercent,
            double? availableOutgoingBitrate,
            string selectedRouteSummary)
        {
            TimestampUtcMs = timestampUtcMs;
            IceState = iceState;
            RttMs = rttMs;
            JitterMs = jitterMs;
            PacketLossPercent = packetLossPercent;
            AvailableOutgoingBitrate = availableOutgoingBitrate;
            SelectedRouteSummary = selectedRouteSummary;
        }

        public bool HasAnyMetric =>
            RttMs.HasValue ||
            JitterMs.HasValue ||
            PacketLossPercent.HasValue ||
            AvailableOutgoingBitrate.HasValue ||
            !string.IsNullOrEmpty(SelectedRouteSummary);

        public override string ToString() =>
            $"RTT={FormatMs(RttMs)} jitter={FormatMs(JitterMs)} " +
            $"loss={FormatPct(PacketLossPercent)} bitrate={FormatBps(AvailableOutgoingBitrate)}" +
            $"{FormatRoute(SelectedRouteSummary)}";

        private static string FormatMs(double? value) => value.HasValue ? $"{value.Value:F0}ms" : "?";
        private static string FormatPct(float? value) => value.HasValue ? $"{value.Value:F1}%" : "?";
        private static string FormatBps(double? value) => value.HasValue ? $"{value.Value / 1000.0:F0}kbps" : "?";
        private static string FormatRoute(string value) => string.IsNullOrEmpty(value) ? string.Empty : $" route={value}";
    }
}
