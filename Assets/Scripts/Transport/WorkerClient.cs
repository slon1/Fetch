using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using WebRtcV2.Config;
using WebRtcV2.Shared;

namespace WebRtcV2.Transport
{
    /// <summary>
    /// HTTP client for all Cloudflare Worker endpoints: room management and signaling slots.
    /// This class owns the HTTP transport only. Business logic lives in coordinators.
    /// </summary>
    public class WorkerClient
    {
        private readonly AppConfig _config;
        private string Base => _config.workerEndpoint.baseUrl.TrimEnd('/');

        public WorkerClient(AppConfig config) => _config = config;

        public async UniTask<RoomEntryDto[]> GetRoomsAsync(CancellationToken ct = default)
        {
            string json = await GetAsync($"{Base}/api/rooms", ct);
            if (json == null) return Array.Empty<RoomEntryDto>();

            try
            {
                var wrapper = JsonUtility.FromJson<RoomListResponse>(json);
                return wrapper?.rooms ?? Array.Empty<RoomEntryDto>();
            }
            catch (Exception e)
            {
                WLog.Warn("WorkerClient", $"GetRooms parse error: {e.Message}");
                return Array.Empty<RoomEntryDto>();
            }
        }

        public async UniTask<RoomEntryDto> GetRoomAsync(string sessionId, CancellationToken ct = default)
        {
            string json = await GetAsync($"{Base}/api/rooms/{sessionId}", ct);
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                return JsonUtility.FromJson<RoomEntryDto>(json);
            }
            catch (Exception e)
            {
                WLog.Warn("WorkerClient", $"GetRoom parse error: {e.Message}");
                return null;
            }
        }

        public async UniTask<RoomEntryDto> CreateRoomAsync(
            string displayName, string sessionId, string creatorPeerId, CancellationToken ct = default)
        {
            var body = JsonUtility.ToJson(new CreateRoomRequest
            {
                displayName = displayName,
                sessionId = sessionId,
                creatorPeerId = creatorPeerId,
                roomTtlSec = _config.workerEndpoint.roomTtlSec,
                heartbeatTtlSec = _config.workerEndpoint.roomHeartbeatTimeoutSec,
            });
            var (ok, responseBody) = await PostAsync($"{Base}/api/rooms", body, ct);
            if (!ok || string.IsNullOrWhiteSpace(responseBody))
                return null;

            try
            {
                var response = JsonUtility.FromJson<CreateRoomResponse>(responseBody);
                return response?.room;
            }
            catch (Exception e)
            {
                WLog.Warn("WorkerClient", $"CreateRoom parse error: {e.Message}");
                return null;
            }
        }

        public async UniTask<bool> HeartbeatRoomAsync(
            string sessionId, string creatorPeerId, CancellationToken ct = default)
        {
            var body = JsonUtility.ToJson(new HeartbeatRoomRequest
            {
                creatorPeerId = creatorPeerId,
            });
            var (ok, _) = await PostAsync($"{Base}/api/rooms/{sessionId}/heartbeat", body, ct);
            return ok;
        }

        /// <summary>
        /// Marks a waiting room as joined. Returns session info on success, null on failure.
        /// The room is no longer returned by <see cref="GetRoomsAsync"/> after this call.
        /// </summary>
        public async UniTask<JoinRoomResponseDto> JoinRoomAsync(
            string sessionId, CancellationToken ct = default)
        {
            var (ok, body) = await PostAsync($"{Base}/api/rooms/{sessionId}/join", "{}", ct);
            if (!ok || body == null) return null;

            try
            {
                return JsonUtility.FromJson<JoinRoomResponseDto>(body);
            }
            catch (Exception e)
            {
                WLog.Warn("WorkerClient", $"JoinRoom parse error: {e.Message}");
                return null;
            }
        }

        public async UniTask<bool> DeleteRoomAsync(string sessionId, CancellationToken ct = default)
        {
            return await DeleteAsync($"{Base}/api/rooms/{sessionId}", ct);
        }

        public async UniTask<bool> PostSignalAsync(SignalingEnvelope envelope, CancellationToken ct = default)
        {
            string body = JsonUtility.ToJson(envelope);
            var (ok, _) = await PostAsync($"{Base}/api/signal/{envelope.sessionId}", body, ct);
            return ok;
        }

        /// <summary>Returns null when no message is available (404) or on error.</summary>
        public async UniTask<SignalingEnvelope> GetSignalAsync(
            string sessionId, string type, CancellationToken ct = default)
        {
            string url = $"{Base}/api/signal/{sessionId}?type={Uri.EscapeDataString(type)}";
            string json;
            try
            {
                json = await GetAsync(url, ct);
            }
            catch (Exception e) when (Is404Like(e))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(json)) return null;
            if (string.Equals(json.Trim(), "null", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                return JsonUtility.FromJson<SignalingEnvelope>(json);
            }
            catch (Exception e)
            {
                WLog.Warn("WorkerClient", $"GetSignal parse error: {e.Message}");
                return null;
            }
        }

        public async UniTask<bool> DeleteSignalAsync(
            string sessionId, string type, CancellationToken ct = default)
        {
            string url = $"{Base}/api/signal/{sessionId}?type={Uri.EscapeDataString(type)}";
            return await DeleteAsync(url, ct);
        }

        private async UniTask<string> GetAsync(string url, CancellationToken ct)
        {
            using var req = UnityWebRequest.Get(url);
            req.downloadHandler = new DownloadHandlerBuffer();
            _ = req.SendWebRequest();
            try { await UniTask.WaitUntil(() => req.isDone, cancellationToken: ct); }
            catch (OperationCanceledException) { req.Abort(); throw; }

            if (req.responseCode == 404)
                return null;

            if (req.result != UnityWebRequest.Result.Success)
            {
                WLog.Warn("WorkerClient", $"GET {url} failed: {req.error} ({req.responseCode})");
                return null;
            }

            string text = req.downloadHandler?.text;
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (string.Equals(text.Trim(), "null", StringComparison.OrdinalIgnoreCase)) return null;
            return text;
        }

        private static bool Is404Like(Exception e)
        {
            if (e == null) return false;

            string message = e.Message ?? string.Empty;
            return message.Contains("404") ||
                   message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async UniTask<(bool ok, string body)> PostAsync(
            string url, string jsonBody, CancellationToken ct)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(jsonBody);
            using var req = new UnityWebRequest(url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(bytes),
                downloadHandler = new DownloadHandlerBuffer()
            };
            req.SetRequestHeader("Content-Type", "application/json");
            _ = req.SendWebRequest();
            try { await UniTask.WaitUntil(() => req.isDone, cancellationToken: ct); }
            catch (OperationCanceledException) { req.Abort(); throw; }

            if (req.result != UnityWebRequest.Result.Success)
            {
                string responseText = req.downloadHandler?.text;
                WLog.Warn("WorkerClient",
                    $"POST {url} failed: {req.error} ({req.responseCode}) body={responseText}");
                return (false, null);
            }
            return (true, req.downloadHandler.text);
        }

        private async UniTask<bool> DeleteAsync(string url, CancellationToken ct)
        {
            using var req = UnityWebRequest.Delete(url);
            req.downloadHandler = new DownloadHandlerBuffer();
            _ = req.SendWebRequest();
            try { await UniTask.WaitUntil(() => req.isDone, cancellationToken: ct); }
            catch (OperationCanceledException) { req.Abort(); throw; }

            if (req.result != UnityWebRequest.Result.Success)
            {
                WLog.Warn("WorkerClient", $"DELETE {url} failed: {req.error}");
                return false;
            }
            return req.responseCode == 200 || req.responseCode == 204;
        }

        [Serializable]
        public class RoomEntryDto
        {
            public string id;
            public string sessionId;
            public string displayName;
            public string creatorPeerId;
            public string status;
            public long createdAt;
            public long expiresAt;
            public long joinedAt;
            public long closedAt;
            public long lastHeartbeatAt;
            public long heartbeatExpiresAt;
        }

        [Serializable]
        public class JoinRoomResponseDto
        {
            public bool ok;
            public string sessionId;
            public string callerPeerId;
        }

        [Serializable]
        private class RoomListResponse
        {
            public RoomEntryDto[] rooms;
        }

        [Serializable]
        private class CreateRoomResponse
        {
            public bool ok;
            public RoomEntryDto room;
        }

        [Serializable]
        private class CreateRoomRequest
        {
            public string displayName;
            public string sessionId;
            public string creatorPeerId;
            public int roomTtlSec;
            public int heartbeatTtlSec;
        }

        [Serializable]
        private class HeartbeatRoomRequest
        {
            public string creatorPeerId;
        }
    }
}

