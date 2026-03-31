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
    public class WorkerClient
    {
        private readonly AppConfig _config;
        private string Base => _config.workerEndpoint.baseUrl.TrimEnd('/');

        public WorkerClient(AppConfig config) => _config = config;

        // ===== New booth/call APIs =====

        public static class RegisterBoothErrors
        {
            public const string NumberConflict = "number_conflict";
        }

        public static class DialOutcomes
        {
            public const string Ringing = "ringing";
            public const string NotRegistered = "not_registered";
            public const string Offline = "offline";
            public const string Busy = "busy";
        }

        public async UniTask<RegisterBoothResponseDto> RegisterBoothAsync(
            string boothNumber,
            string ownerClientId,
            CancellationToken ct = default)
        {
            var body = JsonUtility.ToJson(new RegisterBoothRequest
            {
                boothNumber = boothNumber,
                ownerClientId = ownerClientId,
            });
            var (ok, responseBody) = await PostAsync($"{Base}/api/booths/register", body, ct);
            if (string.IsNullOrWhiteSpace(responseBody))
                return ok ? null : new RegisterBoothResponseDto { ok = false, error = "register_failed" };

            try
            {
                var response = JsonUtility.FromJson<RegisterBoothResponseDto>(responseBody);
                response.ok = ok && response.ok;
                return response;
            }
            catch (Exception e)
            {
                WLog.Warn("WorkerClient", $"RegisterBooth parse error: {e.Message}");
                return ok ? null : new RegisterBoothResponseDto { ok = false, error = "register_parse_error" };
            }
        }

        public async UniTask<DialResponseDto> DialAsync(
            string callerNumber,
            string callerClientId,
            string targetNumber,
            CancellationToken ct = default)
        {
            var body = JsonUtility.ToJson(new DialRequest
            {
                callerNumber = callerNumber,
                callerClientId = callerClientId,
                targetNumber = targetNumber,
            });
            var (ok, responseBody) = await PostAsync($"{Base}/api/dial", body, ct);
            if (string.IsNullOrWhiteSpace(responseBody))
                return ok ? null : new DialResponseDto { ok = false, outcome = null, error = "dial_failed" };

            try
            {
                var response = JsonUtility.FromJson<DialResponseDto>(responseBody);
                response.ok = ok && response.ok;
                return response;
            }
            catch (Exception e)
            {
                WLog.Warn("WorkerClient", $"Dial parse error: {e.Message}");
                return ok ? null : new DialResponseDto { ok = false, error = "dial_parse_error" };
            }
        }

        public async UniTask<CallActionResponseDto> AcceptCallAsync(
            string callId,
            string boothNumber,
            string clientId,
            CancellationToken ct = default)
        {
            return await PostCallActionAsync($"{Base}/api/calls/{callId}/accept", boothNumber, clientId, ct);
        }

        public async UniTask<bool> RejectCallAsync(
            string callId,
            string boothNumber,
            string clientId,
            CancellationToken ct = default)
        {
            var response = await PostCallActionAsync($"{Base}/api/calls/{callId}/reject", boothNumber, clientId, ct);
            return response != null && response.ok;
        }

        public async UniTask<bool> HangupCallAsync(
            string callId,
            string boothNumber,
            string clientId,
            CancellationToken ct = default)
        {
            var response = await PostCallActionAsync($"{Base}/api/calls/{callId}/hangup", boothNumber, clientId, ct);
            return response != null && response.ok;
        }

        public async UniTask<bool> MarkCallConnectedAsync(
            string callId,
            string boothNumber,
            string clientId,
            CancellationToken ct = default)
        {
            var response = await PostCallActionAsync($"{Base}/api/calls/{callId}/connected", boothNumber, clientId, ct);
            return response != null && response.ok;
        }

        private async UniTask<CallActionResponseDto> PostCallActionAsync(
            string url,
            string boothNumber,
            string clientId,
            CancellationToken ct)
        {
            var body = JsonUtility.ToJson(new CallActionRequest
            {
                boothNumber = boothNumber,
                clientId = clientId,
            });
            var (ok, responseBody) = await PostAsync(url, body, ct);
            if (string.IsNullOrWhiteSpace(responseBody))
                return ok ? null : new CallActionResponseDto { ok = false, error = "call_action_failed" };

            try
            {
                var response = JsonUtility.FromJson<CallActionResponseDto>(responseBody);
                response.ok = ok && response.ok;
                return response;
            }
            catch (Exception e)
            {
                WLog.Warn("WorkerClient", $"Call action parse error: {e.Message}");
                return ok ? null : new CallActionResponseDto { ok = false, error = "call_action_parse_error" };
            }
        }

        // ===== Signaling =====

        public async UniTask<bool> PostSignalAsync(SignalingEnvelope envelope, CancellationToken ct = default)
        {
            string body = JsonUtility.ToJson(envelope);
            var (ok, _) = await PostAsync($"{Base}/api/signal/{envelope.sessionId}", body, ct);
            return ok;
        }

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

            string responseText = req.downloadHandler?.text;
            if (req.result != UnityWebRequest.Result.Success)
            {
                WLog.Warn("WorkerClient", $"POST {url} failed: {req.error} ({req.responseCode}) body={responseText}");
                return (false, responseText);
            }
            return (true, responseText);
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
                WLog.Warn("WorkerClient", $"DELETE {url} failed: {req.error} ({req.responseCode})");
                return false;
            }
            return req.responseCode == 200 || req.responseCode == 204;
        }

        [Serializable]
        public class RegisterBoothResponseDto
        {
            public bool ok;
            public string boothNumber;
            public string ownerClientId;
            public string error;
        }

        [Serializable]
        public class DialResponseDto
        {
            public bool ok;
            public string outcome;
            public string callId;
            public string callerNumber;
            public string calleeNumber;
            public string callerClientId;
            public string error;
        }

        [Serializable]
        public class CallActionResponseDto
        {
            public bool ok;
            public string callId;
            public string callerNumber;
            public string calleeNumber;
            public string callerClientId;
            public string error;
        }

        [Serializable]
        private class RegisterBoothRequest
        {
            public string boothNumber;
            public string ownerClientId;
        }

        [Serializable]
        private class DialRequest
        {
            public string callerNumber;
            public string callerClientId;
            public string targetNumber;
        }

        [Serializable]
        private class CallActionRequest
        {
            public string boothNumber;
            public string clientId;
        }
    }
}

