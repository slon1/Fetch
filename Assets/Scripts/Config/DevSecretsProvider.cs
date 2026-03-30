using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace WebRtcV2.Config
{
    /// <summary>
    /// Reads TURN credentials from StreamingAssets/dev-secrets.json.
    /// This file must NOT be committed to version control.
    /// Falls back to STUN-only if the file is absent or malformed.
    /// </summary>
    public class DevSecretsProvider : ISecretsProvider
    {
        private const string FileName = "dev-secrets.json";

        private TurnCredentials? _cached;

        public async UniTask<TurnCredentials> GetTurnCredentialsAsync(CancellationToken ct = default)
        {
            if (_cached.HasValue)
                return _cached.Value;

            string path = System.IO.Path.Combine(UnityEngine.Application.streamingAssetsPath, FileName);

            using var req = UnityWebRequest.Get(path);
            req.downloadHandler = new DownloadHandlerBuffer();
            _ = req.SendWebRequest();

            try
            {
                await UniTask.WaitUntil(() => req.isDone, cancellationToken: ct);
            }
            catch (OperationCanceledException)
            {
                req.Abort();
                throw;
            }

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[DevSecrets] Could not load {FileName}: {req.error}. Running STUN-only.");
                _cached = TurnCredentials.Empty;
                return _cached.Value;
            }

            _cached = ParseJson(req.downloadHandler.text);
            if (_cached.Value.IsEmpty)
            {
                Debug.LogWarning($"[DevSecrets] {FileName} loaded but TURN credentials are empty. Running STUN-only.");
            }
            else
            {
                int urlCount = _cached.Value.TurnUrls?.Length ?? 0;
                Debug.Log($"[DevSecrets] Loaded TURN credentials from {FileName}. URLs={urlCount}");
            }
            return _cached.Value;
        }

        private static TurnCredentials ParseJson(string json)
        {
            try
            {
                var raw = JsonUtility.FromJson<RawSecrets>(json);
                if (raw == null || string.IsNullOrEmpty(raw.turnUsername))
                    return TurnCredentials.Empty;

                return new TurnCredentials
                {
                    Username = raw.turnUsername,
                    Credential = raw.turnCredential,
                    TurnUrls = raw.turnUrls
                };
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DevSecrets] Parse error: {e.Message}");
                return TurnCredentials.Empty;
            }
        }

        [Serializable]
        private class RawSecrets
        {
            public string turnUsername;
            public string turnCredential;
            public string[] turnUrls;
        }
    }
}

