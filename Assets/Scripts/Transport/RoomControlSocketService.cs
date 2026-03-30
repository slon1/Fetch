using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using NativeWebSocket;
using UnityEngine;
using WebRtcV2.Config;
using WebRtcV2.Shared;

namespace WebRtcV2.Transport
{
    /// <summary>
    /// Maintains a single control WebSocket to the Durable Object room endpoint.
    /// Used for low-latency room events while leaving SDP payload transport on HTTP.
    /// </summary>
    public sealed class RoomControlSocketService : IDisposable
    {
        private readonly AppConfig _config;
        private readonly ConnectionDiagnostics _diagnostics;

        private CancellationTokenSource _socketCts;
        private WebSocket _socket;
        private string _sessionId;
        private string _clientId;
        private bool _disposed;

        public string ActiveSessionId => _sessionId;
        public bool IsConnected => _socket != null && _socket.State == WebSocketState.Open;

        public event Action<RoomControlEvent> OnEvent;

        public RoomControlSocketService(AppConfig config, ConnectionDiagnostics diagnostics)
        {
            _config = config;
            _diagnostics = diagnostics;
        }

        public void ConnectToRoom(string sessionId, string clientId, CancellationToken parentToken)
        {
            if (_disposed || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(clientId))
                return;

            if (string.Equals(_sessionId, sessionId, StringComparison.Ordinal) &&
                string.Equals(_clientId, clientId, StringComparison.Ordinal) &&
                _socketCts != null)
            {
                return;
            }

            Disconnect();

            _sessionId = sessionId;
            _clientId = clientId;
            _socketCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            RunConnectLoopAsync(sessionId, clientId, _socketCts.Token).Forget();
        }

        public void Disconnect()
        {
            _socketCts?.Cancel();
            _socketCts?.Dispose();
            _socketCts = null;

            WebSocket socket = _socket;
            _socket = null;
            _sessionId = null;
            _clientId = null;

            if (socket != null)
                CloseSocketSafeAsync(socket).Forget();
        }

        public void DispatchMessageQueue()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            _socket?.DispatchMessageQueue();
#endif
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }

        private async UniTaskVoid RunConnectLoopAsync(string sessionId, string clientId, CancellationToken ct)
        {
            TimeSpan reconnectDelay = TimeSpan.FromSeconds(
                Math.Max(1f, _config.workerEndpoint.roomControlSocketReconnectDelaySec));

            while (!ct.IsCancellationRequested && Matches(sessionId, clientId))
            {
                WebSocket socket = null;
                bool closed = false;

                try
                {
                    socket = new WebSocket(BuildRoomEventsUrl(sessionId, clientId));
                    socket.OnOpen += () => _diagnostics.LogInfo("RoomSocket", $"Connected session={sessionId}");
                    socket.OnError += error => _diagnostics.LogWarning("RoomSocket", $"Error session={sessionId}: {error}");
                    socket.OnClose += code =>
                    {
                        closed = true;
                        _diagnostics.LogInfo("RoomSocket", $"Closed session={sessionId} code={code}");
                    };
                    socket.OnMessage += bytes => HandleSocketMessage(bytes);

                    _socket = socket;
                    await socket.Connect();

                    await UniTask.WaitUntil(
                            () => ct.IsCancellationRequested || closed || socket.State != WebSocketState.Open,
                            cancellationToken: ct)
                        .SuppressCancellationThrow();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    if (!ct.IsCancellationRequested)
                        _diagnostics.LogWarning("RoomSocket", $"Connect loop error session={sessionId}: {e.Message}");
                }
                finally
                {
                    if (ReferenceEquals(_socket, socket))
                        _socket = null;

                    if (socket != null)
                        await CloseSocketSafeAsync(socket);
                }

                if (ct.IsCancellationRequested || !Matches(sessionId, clientId))
                    return;

                await UniTask.Delay(reconnectDelay, cancellationToken: ct).SuppressCancellationThrow();
            }
        }

        private void HandleSocketMessage(byte[] bytes)
        {
            try
            {
                string json = Encoding.UTF8.GetString(bytes);
                var controlEvent = JsonUtility.FromJson<RoomControlEvent>(json);
                if (controlEvent == null || string.IsNullOrWhiteSpace(controlEvent.type))
                    return;

                OnEvent?.Invoke(controlEvent);
            }
            catch (Exception e)
            {
                _diagnostics.LogWarning("RoomSocket", $"Message parse error: {e.Message}");
            }
        }

        private string BuildRoomEventsUrl(string sessionId, string clientId)
        {
            string baseUrl = _config.workerEndpoint.baseUrl.TrimEnd('/');
            if (baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                baseUrl = "wss://" + baseUrl.Substring("https://".Length);
            else if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                baseUrl = "ws://" + baseUrl.Substring("http://".Length);

            return $"{baseUrl}/api/rooms/{Uri.EscapeDataString(sessionId)}/events?clientId={Uri.EscapeDataString(clientId)}";
        }

        private bool Matches(string sessionId, string clientId) =>
            string.Equals(_sessionId, sessionId, StringComparison.Ordinal) &&
            string.Equals(_clientId, clientId, StringComparison.Ordinal);

        private static async UniTask CloseSocketSafeAsync(WebSocket socket)
        {
            try
            {
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.Connecting)
                    await socket.Close();
            }
            catch
            {
            }
        }
    }
}