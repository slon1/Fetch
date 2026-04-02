using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;
#if UNITY_ANDROID
using Firebase;
using Firebase.Messaging;
#endif

namespace WebRtcV2.Shared
{
    public sealed class FcmIncomingCallPush
    {
        public string CallId { get; }
        public string CallerNumber { get; }
        public string CalleeNumber { get; }
        public string BoothNumber { get; }
        public bool HasDisplayNotification { get; }

        public FcmIncomingCallPush(
            string callId,
            string callerNumber,
            string calleeNumber,
            string boothNumber,
            bool hasDisplayNotification)
        {
            CallId = callId;
            CallerNumber = callerNumber;
            CalleeNumber = calleeNumber;
            BoothNumber = boothNumber;
            HasDisplayNotification = hasDisplayNotification;
        }
    }

    public sealed class FcmPushService : IDisposable
    {
        private const string PushTypeIncomingCall = "incoming_call";
        private const string PlatformAndroid = "android";
        private const string RegisteredTokenPrefKey = "webrtcv2.fcm.registeredToken";
        private const string RegisteredBoothPrefKey = "webrtcv2.fcm.registeredBooth";
        private const string RegisteredClientPrefKey = "webrtcv2.fcm.registeredClient";

        private readonly ConnectionDiagnostics _diagnostics;
        private bool _initialized;
        private bool _disposed;
        private string _lastRegisteredToken;
        private string _lastRegisteredBooth;
        private string _lastRegisteredClientId;

        public string CurrentToken { get; private set; }
        public bool IsSupported => UnityEngine.Application.platform == RuntimePlatform.Android;

        public event Action<string> OnTokenReceived;
        public event Action<FcmIncomingCallPush> OnIncomingCallPushReceived;

        public FcmPushService(ConnectionDiagnostics diagnostics)
        {
            _diagnostics = diagnostics;
            _lastRegisteredToken = PlayerPrefs.GetString(RegisteredTokenPrefKey, string.Empty);
            _lastRegisteredBooth = PlayerPrefs.GetString(RegisteredBoothPrefKey, string.Empty);
            _lastRegisteredClientId = PlayerPrefs.GetString(RegisteredClientPrefKey, string.Empty);
        }

        public async UniTask InitializeAsync(CancellationToken ct = default)
        {
            if (_disposed || _initialized)
                return;

            _initialized = true;

#if UNITY_ANDROID
            try
            {
                Debug.Log("[FCM] InitializeAsync start.");
                DependencyStatus dependencyStatus = await FirebaseApp.CheckAndFixDependenciesAsync();
                Debug.Log($"[FCM] CheckAndFixDependenciesAsync -> {dependencyStatus}");
                if (ct.IsCancellationRequested || _disposed)
                    return;

                if (dependencyStatus != DependencyStatus.Available)
                {
                    Debug.LogWarning($"[FCM] Firebase dependencies unavailable: {dependencyStatus}");
                    _diagnostics.LogWarning("FCM", $"Firebase dependencies unavailable: {dependencyStatus}");
                    return;
                }

                Debug.Log("[FCM] Wiring FirebaseMessaging callbacks.");
                FirebaseMessaging.TokenReceived += HandleTokenReceived;
                FirebaseMessaging.MessageReceived += HandleMessageReceived;
                FirebaseMessaging.TokenRegistrationOnInitEnabled = true;

                await TryFetchCurrentTokenAsync();
                Debug.Log("[FCM] Firebase Messaging initialized.");
                _diagnostics.LogInfo("FCM", "Firebase Messaging initialized.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[FCM] Firebase Messaging init failed: {e}");
                _diagnostics.LogWarning("FCM", $"Firebase Messaging init failed: {e.Message}");
            }
#else
            _diagnostics.LogInfo("FCM", "Firebase Messaging is Android-only in this iteration.");
            await UniTask.CompletedTask;
#endif
        }

        public bool NeedsServerRegistration(string boothNumber, string clientId)
        {
            return !string.IsNullOrWhiteSpace(CurrentToken) &&
                   !string.IsNullOrWhiteSpace(boothNumber) &&
                   !string.IsNullOrWhiteSpace(clientId) &&
                   (!string.Equals(CurrentToken, _lastRegisteredToken, StringComparison.Ordinal) ||
                    !string.Equals(boothNumber, _lastRegisteredBooth, StringComparison.Ordinal) ||
                    !string.Equals(clientId, _lastRegisteredClientId, StringComparison.Ordinal));
        }

        public void MarkRegistered(string boothNumber, string clientId, string token)
        {
            _lastRegisteredBooth = boothNumber ?? string.Empty;
            _lastRegisteredClientId = clientId ?? string.Empty;
            _lastRegisteredToken = token ?? string.Empty;

            PlayerPrefs.SetString(RegisteredBoothPrefKey, _lastRegisteredBooth);
            PlayerPrefs.SetString(RegisteredClientPrefKey, _lastRegisteredClientId);
            PlayerPrefs.SetString(RegisteredTokenPrefKey, _lastRegisteredToken);
            PlayerPrefs.Save();
        }

        public void ClearRegisteredToken(string boothNumber = null)
        {
            if (!string.IsNullOrWhiteSpace(boothNumber) && !string.Equals(boothNumber, _lastRegisteredBooth, StringComparison.Ordinal))
                return;

            _lastRegisteredToken = string.Empty;
            PlayerPrefs.DeleteKey(RegisteredTokenPrefKey);
            PlayerPrefs.Save();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

#if UNITY_ANDROID
            FirebaseMessaging.TokenReceived -= HandleTokenReceived;
            FirebaseMessaging.MessageReceived -= HandleMessageReceived;
#endif
        }

#if UNITY_ANDROID
        private async UniTask TryFetchCurrentTokenAsync()
        {
            try
            {
                Debug.Log("[FCM] Trying to fetch initial token.");
                MethodInfo getTokenMethod = typeof(FirebaseMessaging).GetMethod("GetTokenAsync", BindingFlags.Public | BindingFlags.Static);
                if (getTokenMethod == null)
                {
                    Debug.LogWarning("[FCM] FirebaseMessaging.GetTokenAsync not found.");
                    return;
                }

                if (getTokenMethod.Invoke(null, null) is Task<string> tokenTask)
                {
                    string token = await tokenTask;
                    Debug.Log($"[FCM] Initial token fetched. Length={token?.Length ?? 0}");
                    PublishToken(token);
                    return;
                }

                Debug.LogWarning("[FCM] FirebaseMessaging.GetTokenAsync returned unexpected object.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[FCM] Could not fetch initial FCM token: {e}");
                _diagnostics.LogWarning("FCM", $"Could not fetch initial FCM token: {e.Message}");
            }
        }

        private void HandleTokenReceived(object sender, TokenReceivedEventArgs args)
        {
            Debug.Log($"[FCM] TokenReceived event. Length={args?.Token?.Length ?? 0}");
            PublishToken(args?.Token);
        }

        private void HandleMessageReceived(object sender, MessageReceivedEventArgs args)
        {
            if (!TryMapIncomingCall(args?.Message, out FcmIncomingCallPush push))
                return;

            Debug.Log($"[FCM] Incoming call push received: callId={push.CallId} caller={push.CallerNumber} hasNotification={push.HasDisplayNotification}");
            _diagnostics.LogInfo("FCM", $"Incoming call push received: callId={push.CallId} caller={push.CallerNumber}");
            OnIncomingCallPushReceived?.Invoke(push);
        }

        private void PublishToken(string token)
        {
            string normalized = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            CurrentToken = normalized;
            Debug.Log($"[FCM] Registration token available. Length={CurrentToken.Length}");
            _diagnostics.LogInfo("FCM", "Registration token available.");
            OnTokenReceived?.Invoke(CurrentToken);
        }

        private static bool TryMapIncomingCall(FirebaseMessage message, out FcmIncomingCallPush push)
        {
            push = null;
            IDictionary<string, string> data = message?.Data;
            if (data == null)
                return false;

            string type = GetDataValue(data, "type");
            if (!string.Equals(type, PushTypeIncomingCall, StringComparison.Ordinal))
                return false;

            string callId = GetDataValue(data, "callId");
            if (string.IsNullOrWhiteSpace(callId))
                return false;

            push = new FcmIncomingCallPush(
                callId,
                GetDataValue(data, "callerNumber"),
                GetDataValue(data, "calleeNumber"),
                GetDataValue(data, "boothNumber"),
                message.Notification != null);
            return true;
        }

        private static string GetDataValue(IDictionary<string, string> data, string key)
        {
            if (data == null || string.IsNullOrWhiteSpace(key))
                return null;

            return data.TryGetValue(key, out string value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;
        }
#endif
    }
}

