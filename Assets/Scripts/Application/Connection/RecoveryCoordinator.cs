using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.WebRTC;
using WebRtcV2.Config;
using WebRtcV2.Shared;

namespace WebRtcV2.Application.Connection
{
    public enum RecoveryOutcome
    {
        Cancelled,
        Recovered,
        DisconnectedTimeout,
        RetryBudgetExceeded,
        StartIceRestart
    }

    /// <summary>
    /// Owns timing, backoff, and retry-budget mechanics for recovery attempts.
    ///
    /// It does not transition the connection FSM directly. The caller decides how to react to
    /// the returned outcome (e.g. Connected, Recovering, Failed).
    /// </summary>
    public sealed class RecoveryCoordinator : IDisposable
    {
        private readonly AppConfig _config;
        private readonly ConnectionDiagnostics _diagnostics;
        private CancellationTokenSource _pendingRecoveryCts;

        public RecoveryCoordinator(AppConfig config, ConnectionDiagnostics diagnostics)
        {
            _config = config;
            _diagnostics = diagnostics;
        }

        public void CancelPending()
        {
            _pendingRecoveryCts?.Cancel();
            _pendingRecoveryCts?.Dispose();
            _pendingRecoveryCts = null;
        }

        public async UniTask<RecoveryOutcome> BeginDisconnectedGracePeriodAsync(
            Func<RTCIceConnectionState> getCurrentIceState,
            CancellationToken sessionCt)
        {
            var localCts = ResetPending(sessionCt);
            int graceMs = _config.connection.iceDisconnectedGraceMs;
            _diagnostics.LogInfo("Recovery", $"ICE disconnected - grace period {graceMs}ms");

            try
            {
                await UniTask.Delay(graceMs, cancellationToken: localCts.Token)
                    .SuppressCancellationThrow();

                if (localCts.IsCancellationRequested)
                    return RecoveryOutcome.Cancelled;

                var state = getCurrentIceState?.Invoke() ?? RTCIceConnectionState.Closed;
                return state == RTCIceConnectionState.Connected || state == RTCIceConnectionState.Completed
                    ? RecoveryOutcome.Recovered
                    : RecoveryOutcome.DisconnectedTimeout;
            }
            finally
            {
                ClearPending(localCts);
            }
        }

        public async UniTask<RecoveryOutcome> BeginFailedRecoveryAsync(
            ConnectionSession session,
            CancellationToken sessionCt)
        {
            var localCts = ResetPending(sessionCt);
            session.IncrementReconnectAttempts();

            int attempt = session.ReconnectAttempts;
            int maxAttempts = _config.connection.reconnectMaxAttempts;
            if (attempt > maxAttempts)
            {
                _diagnostics.LogInfo("Recovery", $"Retry budget exhausted ({attempt - 1}/{maxAttempts})");
                ClearPending(localCts);
                return RecoveryOutcome.RetryBudgetExceeded;
            }

            int delayMs = (int)(_config.connection.reconnectDelayBaseSec * 1000 * attempt);
            _diagnostics.LogInfo("Recovery", $"ICE failed - attempt {attempt}/{maxAttempts}, backoff {delayMs}ms");

            try
            {
                await UniTask.Delay(delayMs, cancellationToken: localCts.Token)
                    .SuppressCancellationThrow();

                return localCts.IsCancellationRequested
                    ? RecoveryOutcome.Cancelled
                    : RecoveryOutcome.StartIceRestart;
            }
            finally
            {
                ClearPending(localCts);
            }
        }

        public void Dispose() => CancelPending();

        private CancellationTokenSource ResetPending(CancellationToken sessionCt)
        {
            CancelPending();
            _pendingRecoveryCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
            return _pendingRecoveryCts;
        }

        private void ClearPending(CancellationTokenSource localCts)
        {
            if (!ReferenceEquals(_pendingRecoveryCts, localCts))
                return;

            _pendingRecoveryCts?.Dispose();
            _pendingRecoveryCts = null;
        }
    }
}
