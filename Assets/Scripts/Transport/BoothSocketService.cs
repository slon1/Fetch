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
    public sealed class BoothSocketService : IDisposable
    {
        private readonly AppConfig _config;
        private readonly ConnectionDiagnostics _diagnostics;

        private CancellationTokenSource _socketCts;
        private WebSocket _socket;
        private string _boothNumber;
        private string _clientId;
        private bool _disposed;

        public string ActiveBoothNumber => _boothNumber;
        public bool IsConnected => _socket != null && _socket.State == WebSocketState.Open;

        public event Action<BoothSocketEvent> OnEvent;

        public BoothSocketService(AppConfig config, ConnectionDiagnostics diagnostics)
        {
            _config = config;
            _diagnostics = diagnostics;
        }

        public void Connect(string boothNumber, string clientId, CancellationToken parentToken)
        {
            if (_disposed || string.IsNullOrWhiteSpace(boothNumber) || string.IsNullOrWhiteSpace(clientId))
                return;

            if (string.Equals(_boothNumber, boothNumber, StringComparison.Ordinal) &&
                string.Equals(_clientId, clientId, StringComparison.Ordinal) &&
                _socketCts != null)
            {
                return;
            }

            Disconnect();
            _boothNumber = boothNumber;
            _clientId = clientId;
            _socketCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            RunConnectLoopAsync(boothNumber, clientId, _socketCts.Token).Forget();
        }

        public void Disconnect()
        {
            _socketCts?.Cancel();
            _socketCts?.Dispose();
            _socketCts = null;

            WebSocket socket = _socket;
            _socket = null;
            _boothNumber = null;
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

        private async UniTaskVoid RunConnectLoopAsync(string boothNumber, string clientId, CancellationToken ct)
        {
            TimeSpan reconnectDelay = TimeSpan.FromSeconds(Math.Max(1f, _config.workerEndpoint.roomControlSocketReconnectDelaySec));

            while (!ct.IsCancellationRequested && Matches(boothNumber, clientId))
            {
                WebSocket socket = null;
                bool closed = false;

                try
                {
                    socket = new WebSocket(BuildBoothEventsUrl(boothNumber, clientId));
                    socket.OnOpen += () => _diagnostics.LogInfo("BoothSocket", $"Connected booth={boothNumber}");
                    socket.OnError += error => _diagnostics.LogWarning("BoothSocket", $"Error booth={boothNumber}: {error}");
                    socket.OnClose += code =>
                    {
                        closed = true;
                        _diagnostics.LogInfo("BoothSocket", $"Closed booth={boothNumber} code={code}");
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
                        _diagnostics.LogWarning("BoothSocket", $"Connect loop error booth={boothNumber}: {e.Message}");
                }
                finally
                {
                    if (ReferenceEquals(_socket, socket))
                        _socket = null;

                    if (socket != null)
                        await CloseSocketSafeAsync(socket);
                }

                if (ct.IsCancellationRequested || !Matches(boothNumber, clientId))
                    return;

                await UniTask.Delay(reconnectDelay, cancellationToken: ct).SuppressCancellationThrow();
            }
        }

        private void HandleSocketMessage(byte[] bytes)
        {
            try
            {
                string json = Encoding.UTF8.GetString(bytes);
                var controlEvent = JsonUtility.FromJson<BoothSocketEvent>(json);
                if (controlEvent == null || string.IsNullOrWhiteSpace(controlEvent.type))
                    return;

                OnEvent?.Invoke(controlEvent);
            }
            catch (Exception e)
            {
                _diagnostics.LogWarning("BoothSocket", $"Message parse error: {e.Message}");
            }
        }

        private string BuildBoothEventsUrl(string boothNumber, string clientId)
        {
            string baseUrl = _config.workerEndpoint.baseUrl.TrimEnd('/');
            if (baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                baseUrl = "wss://" + baseUrl.Substring("https://".Length);
            else if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                baseUrl = "ws://" + baseUrl.Substring("http://".Length);

            return $"{baseUrl}/api/booths/{Uri.EscapeDataString(boothNumber)}/events?clientId={Uri.EscapeDataString(clientId)}";
        }

        private bool Matches(string boothNumber, string clientId) =>
            string.Equals(_boothNumber, boothNumber, StringComparison.Ordinal) &&
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
