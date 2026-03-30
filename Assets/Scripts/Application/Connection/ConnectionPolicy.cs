using WebRtcV2.Config;
using WebRtcV2.Shared;
using WebRtcV2.Transport;

namespace WebRtcV2.Application.Connection
{
    /// <summary>
    /// Stateless-except-for-counter quality policy.
    ///
    /// Evaluates a pair of (ConnectionSnapshot, QualitySnapshot) and returns a
    /// <see cref="ConnectionPolicyDecision"/>. The coordinator executes the decision;
    /// the policy only recommends.
    ///
    /// MVP rule: downgrade AudioOnly → DataOnly when quality is bad for
    /// <see cref="AppConfig.PolicySection.degradeConsecutiveBadSamples"/> samples in a row.
    ///
    /// "Bad quality" means at least one of the following (if the metric is available):
    ///   RTT    > config.degradeRttThresholdMs
    ///   Jitter > config.degradeJitterThresholdMs
    ///   Loss   > config.degradePacketLossPercent
    ///
    /// If a metric is null (stat was absent in the report), it does NOT count as bad.
    /// The policy never triggers on missing data alone.
    ///
    /// Not implemented in this release:
    ///   Upgrade DataOnly → AudioOnly (Stage 5+)
    ///   Relay fallback    (Stage 5)
    ///   ICE restart       (Stage 5)
    /// </summary>
    public class ConnectionPolicy
    {
        private readonly AppConfig _config;
        private readonly ConnectionDiagnostics _diagnostics;

        private int _consecutiveBadSamples;

        public ConnectionPolicy(AppConfig config, ConnectionDiagnostics diagnostics)
        {
            _config = config;
            _diagnostics = diagnostics;
        }

        /// <summary>
        /// Evaluates current session and quality state and returns a policy decision.
        /// Must be called on every quality snapshot while in Connected state.
        /// </summary>
        public ConnectionPolicyDecision Evaluate(
            ConnectionSnapshot connection, QualitySnapshot quality)
        {
            // Policy only applies to Connected sessions in AudioOnly mode.
            // Any other lifecycle state resets the counter and returns None.
            if (connection.LifecycleState != ConnectionLifecycleState.Connected)
            {
                _consecutiveBadSamples = 0;
                return ConnectionPolicyDecision.None;
            }

            // Downgrade is only meaningful from AudioOnly.
            if (connection.MediaMode != MediaMode.AudioOnly)
                return ConnectionPolicyDecision.None;

            bool bad = IsBadQuality(quality);

            if (bad)
            {
                _consecutiveBadSamples++;
                _diagnostics.LogWarning("Policy",
                    $"Bad quality sample {_consecutiveBadSamples}/{_config.policy.degradeConsecutiveBadSamples}: {quality}");
            }
            else
            {
                if (_consecutiveBadSamples > 0)
                    _diagnostics.LogIce("Policy", "Quality recovered — resetting bad sample counter");
                _consecutiveBadSamples = 0;
            }

            if (_consecutiveBadSamples >= _config.policy.degradeConsecutiveBadSamples)
            {
                // Reset counter so we do not fire the decision repeatedly.
                _consecutiveBadSamples = 0;
                return ConnectionPolicyDecision.DowngradeToDataOnly;
            }

            return ConnectionPolicyDecision.None;
        }

        /// <summary>Resets the bad-sample counter. Call when a session ends.</summary>
        public void Reset() => _consecutiveBadSamples = 0;

        // ── Private ───────────────────────────────────────────────────────

        private bool IsBadQuality(QualitySnapshot q)
        {
            if (q.RttMs.HasValue && q.RttMs.Value > _config.policy.degradeRttThresholdMs)
                return true;
            if (q.JitterMs.HasValue && q.JitterMs.Value > _config.policy.degradeJitterThresholdMs)
                return true;
            if (q.PacketLossPercent.HasValue && q.PacketLossPercent.Value > _config.policy.degradePacketLossPercent)
                return true;
            return false;
        }
    }
}
