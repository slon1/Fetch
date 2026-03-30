using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.WebRTC;
using WebRtcV2.Config;
using WebRtcV2.Shared;

namespace WebRtcV2.Transport
{
    /// <summary>
    /// Periodically polls WebRtcPeerAdapter.GetStatsAsync and emits QualitySnapshot events.
    /// Does not make policy decisions.
    /// </summary>
    public class WebRtcStatsSampler : IDisposable
    {
        private readonly WebRtcPeerAdapter _peer;
        private readonly AppConfig _config;
        private readonly ConnectionDiagnostics _diagnostics;

        private bool _disposed;
        private string _lastLoggedRouteSummary;

        public event Action<QualitySnapshot> OnSnapshot;

        public WebRtcStatsSampler(
            WebRtcPeerAdapter peer,
            AppConfig config,
            ConnectionDiagnostics diagnostics)
        {
            _peer = peer ?? throw new ArgumentNullException(nameof(peer));
            _config = config;
            _diagnostics = diagnostics;
        }

        public void Start(CancellationToken ct)
        {
            _diagnostics.LogIce("StatsSampler", "Started - polling every " +
                $"{_config.policy.statsPollingIntervalMs}ms | " +
                "Collecting: RTT, Jitter, PacketLoss, AvailableBitrate");
            SampleLoopAsync(ct).Forget();
        }

        private async UniTaskVoid SampleLoopAsync(CancellationToken ct)
        {
            var interval = TimeSpan.FromMilliseconds(_config.policy.statsPollingIntervalMs);

            while (!ct.IsCancellationRequested && !_disposed)
            {
                await UniTask.Delay(interval, cancellationToken: ct).SuppressCancellationThrow();
                if (ct.IsCancellationRequested || _disposed) break;

                var snapshot = await CollectAsync(ct);
                if (snapshot != null && !ct.IsCancellationRequested && !_disposed)
                    OnSnapshot?.Invoke(snapshot);
            }

            _diagnostics.LogIce("StatsSampler", "Stopped");
        }

        private async UniTask<QualitySnapshot> CollectAsync(CancellationToken ct)
        {
            var iceState = _peer.IceConnectionState;
            RTCStatsReport report = null;
            try
            {
                report = await _peer.GetStatsAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception e)
            {
                _diagnostics.LogWarning("StatsSampler", $"GetStats error: {e.Message}");
            }

            if (ct.IsCancellationRequested) return null;

            try
            {
                return ParseReport(report, iceState);
            }
            finally
            {
                report?.Dispose();
            }
        }

        private QualitySnapshot ParseReport(RTCStatsReport report, RTCIceConnectionState iceState)
        {
            double? rttMs = null;
            double? jitterMs = null;
            float? packetLossPercent = null;
            double? availableBitrate = null;
            bool rttFromIcePair = false;

            string selectedRouteSummary = null;
            string selectedLocalCandidateId = null;
            string selectedRemoteCandidateId = null;

            if (report != null)
            {
                try
                {
                    var candidateTypes = new Dictionary<string, string>();
                    var relayProtocols = new Dictionary<string, string>();

                    foreach (var entry in report.Stats.Values)
                    {
                        switch (entry)
                        {
                            case RTCIceCandidateStats candidate:
                                if (!string.IsNullOrEmpty(candidate.Id))
                                {
                                    candidateTypes[candidate.Id] = candidate.candidateType;
                                    relayProtocols[candidate.Id] = candidate.relayProtocol;
                                }
                                break;

                            case RTCIceCandidatePairStats pair when pair.nominated:
                                if (pair.currentRoundTripTime > 0)
                                {
                                    rttMs = pair.currentRoundTripTime * 1000.0;
                                    rttFromIcePair = true;
                                }

                                if (pair.availableOutgoingBitrate > 0)
                                    availableBitrate = pair.availableOutgoingBitrate;

                                selectedLocalCandidateId = pair.localCandidateId;
                                selectedRemoteCandidateId = pair.remoteCandidateId;
                                break;

                            case RTCRemoteInboundRtpStreamStats remote:
                                if (remote.jitter > 0)
                                    jitterMs = remote.jitter * 1000.0;

                                if (remote.fractionLost > 0)
                                    packetLossPercent = (float)(remote.fractionLost * 100.0);

                                if (!rttFromIcePair && remote.roundTripTime > 0)
                                    rttMs = remote.roundTripTime * 1000.0;
                                break;
                        }
                    }

                    if (!string.IsNullOrEmpty(selectedLocalCandidateId) &&
                        !string.IsNullOrEmpty(selectedRemoteCandidateId))
                    {
                        candidateTypes.TryGetValue(selectedLocalCandidateId, out var localType);
                        candidateTypes.TryGetValue(selectedRemoteCandidateId, out var remoteType);
                        relayProtocols.TryGetValue(selectedLocalCandidateId, out var localRelayProtocol);
                        relayProtocols.TryGetValue(selectedRemoteCandidateId, out var remoteRelayProtocol);

                        string relayProtocol = !string.IsNullOrEmpty(localRelayProtocol)
                            ? localRelayProtocol
                            : remoteRelayProtocol;

                        if (!string.IsNullOrEmpty(localType) || !string.IsNullOrEmpty(remoteType))
                        {
                            selectedRouteSummary = $"{NullToUnknown(localType)}/{NullToUnknown(remoteType)}";
                            if (!string.IsNullOrEmpty(relayProtocol))
                                selectedRouteSummary += $" {relayProtocol}";
                        }
                    }
                }
                catch (Exception e)
                {
                    _diagnostics.LogWarning("StatsSampler", $"Stats parse error: {e.Message}");
                }
            }

            var snapshot = new QualitySnapshot(
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                iceState: iceState,
                rttMs: rttMs,
                jitterMs: jitterMs,
                packetLossPercent: packetLossPercent,
                availableOutgoingBitrate: availableBitrate,
                selectedRouteSummary: selectedRouteSummary);

            if (snapshot.HasAnyMetric)
            {
                if (!string.IsNullOrEmpty(snapshot.SelectedRouteSummary) &&
                    snapshot.SelectedRouteSummary != _lastLoggedRouteSummary)
                {
                    _lastLoggedRouteSummary = snapshot.SelectedRouteSummary;
                    _diagnostics.LogIce("Route", $"Selected pair {snapshot.SelectedRouteSummary}");
                }

                //_diagnostics.LogIce("Stats", snapshot.ToString());
            }
            else
            {
                _diagnostics.LogWarning("StatsSampler",
                    "No quality metrics in stats report - stats may not be flowing yet");
            }

            return snapshot;
        }

        private static string NullToUnknown(string value) =>
            string.IsNullOrEmpty(value) ? "unknown" : value;

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
