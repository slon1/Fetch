using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using WebRtcV2.Config;
using WebRtcV2.Shared;

namespace WebRtcV2.Application.Room
{
    /// <summary>
    /// Lightweight waiting-room presence service.
    /// Sends periodic heartbeats while the local client owns a waiting room.
    /// Independent from active WebRTC quality and recovery logic.
    /// </summary>
    public sealed class RoomHeartbeatService : IDisposable
    {
        private readonly IRoomFlow _roomFlow;
        private readonly AppConfig _config;
        private readonly ConnectionDiagnostics _diagnostics;

        private CancellationTokenSource _heartbeatCts;
        private string _sessionId;

        public RoomHeartbeatService(IRoomFlow roomFlow, AppConfig config, ConnectionDiagnostics diagnostics)
        {
            _roomFlow = roomFlow;
            _config = config;
            _diagnostics = diagnostics;
        }

        public void Start(string sessionId, CancellationToken parentToken)
        {
            Stop();
            if (string.IsNullOrWhiteSpace(sessionId)) return;

            _sessionId = sessionId;
            _heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            RunHeartbeatLoopAsync(_heartbeatCts.Token).Forget();
        }

        public void Stop()
        {
            _heartbeatCts?.Cancel();
            _heartbeatCts?.Dispose();
            _heartbeatCts = null;
            _sessionId = null;
        }

        public void Dispose() => Stop();

        private async UniTaskVoid RunHeartbeatLoopAsync(CancellationToken ct)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1f, _config.workerEndpoint.roomHeartbeatIntervalSec));

            try
            {
                while (!ct.IsCancellationRequested && !string.IsNullOrWhiteSpace(_sessionId))
                {
                    bool ok = await _roomFlow.HeartbeatRoomAsync(_sessionId, ct);
                    if (!ok)
                        _diagnostics.LogWarning("Heartbeat", $"Heartbeat failed for session={_sessionId}");

                    await UniTask.Delay(interval, cancellationToken: ct).SuppressCancellationThrow();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _diagnostics.LogWarning("Heartbeat", $"Heartbeat loop stopped: {e.Message}");
            }
        }
    }
}
