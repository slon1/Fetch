using System;
using UnityEngine;
#if UNITY_ANDROID
using Unity.Notifications.Android;
#endif

namespace WebRtcV2.Shared
{
    public sealed class AndroidLocalNotificationService : IDisposable
    {
        private readonly AppVisibilityTracker _visibility;
        private readonly ConnectionDiagnostics _diagnostics;

#if UNITY_ANDROID
        private const string ChannelId = "room-events";
        private const string ChannelName = "Room Events";
        private PermissionRequest _permissionRequest;
#endif

        public AndroidLocalNotificationService(AppVisibilityTracker visibility, ConnectionDiagnostics diagnostics)
        {
            _visibility = visibility;
            _diagnostics = diagnostics;
        }

        public void Warmup()
        {
#if UNITY_ANDROID
            RegisterChannel();
            EnsurePermissionRequested();
#endif
        }

        public void NotifyIncomingCall(string callId, string callerNumber)
        {
            Send(callId,
                title: "Входящий звонок",
                text: $"Номер {SafePeer(callerNumber)} звонит вам.");
        }

        public void NotifyConnected(string callId, string peerDisplay)
        {
            Send(callId,
                title: "Связь установлена",
                text: $"{SafePeer(peerDisplay)}. Вас ждут у телефона.");
        }

        public void CancelSessionNotification(string sessionId)
        {
#if UNITY_ANDROID
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            AndroidNotificationCenter.CancelNotification(MakeNotificationId(sessionId));
#endif
        }

        public void Dispose()
        {
#if UNITY_ANDROID
            _permissionRequest = null;
#endif
        }

        private static string SafePeer(string value) =>
            string.IsNullOrWhiteSpace(value) ? "собеседник" : value.Trim();

        private void Send(string sessionId, string title, string text)
        {
#if UNITY_ANDROID
            if (!_visibility.ShouldShowBackgroundNotification) return;
            if (string.IsNullOrWhiteSpace(sessionId)) return;

            RegisterChannel();
            EnsurePermissionRequested();

            if (AndroidNotificationCenter.UserPermissionToPost != PermissionStatus.Allowed)
                return;

            var notification = new AndroidNotification
            {
                Title = title,
                Text = text,
                FireTime = DateTime.Now,
            };

            int id = MakeNotificationId(sessionId);
            AndroidNotificationCenter.SendNotificationWithExplicitID(notification, ChannelId, id);
            _diagnostics.LogInfo("Notify", $"Local notification posted: {title}");
#endif
        }

#if UNITY_ANDROID
        private static int MakeNotificationId(string sessionId)
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in sessionId)
                    hash = hash * 31 + c;
                return Math.Abs(hash == int.MinValue ? int.MaxValue : hash);
            }
        }

        private static void RegisterChannel()
        {
            var channel = new AndroidNotificationChannel
            {
                Id = ChannelId,
                Name = ChannelName,
                Importance = Importance.High,
                Description = "Incoming call and connection events"
            };
            AndroidNotificationCenter.RegisterNotificationChannel(channel);
        }

        private void EnsurePermissionRequested()
        {
            switch (AndroidNotificationCenter.UserPermissionToPost)
            {
                case PermissionStatus.NotRequested:
                case PermissionStatus.Denied:
                    _permissionRequest ??= new PermissionRequest();
                    break;
            }
        }
#endif
    }
}
