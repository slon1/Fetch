using System;
using UnityEngine;

namespace WebRtcV2.Shared
{
    public sealed class AndroidLocalNotificationService : IDisposable
    {
        private const string PostNotificationsPermission = "android.permission.POST_NOTIFICATIONS";
        private const int NotificationImportanceHigh = 4;
        private const int PendingIntentFlagUpdateCurrent = 134217728;
        private const int PendingIntentFlagImmutable = 67108864;

        private readonly AppVisibilityTracker _visibility;
        private readonly ConnectionDiagnostics _diagnostics;
        private bool _notificationsAvailable = true;

#if UNITY_ANDROID
        private const string ChannelId = "booth-calls";
        private const string ChannelName = "Booth Calls";
        private const string ChannelDescription = "Incoming call notifications";
#endif

        public AndroidLocalNotificationService(AppVisibilityTracker visibility, ConnectionDiagnostics diagnostics)
        {
            _visibility = visibility;
            _diagnostics = diagnostics;
        }

        public void Warmup()
        {
#if UNITY_ANDROID
            if (!_notificationsAvailable)
                return;

            try
            {
                EnsureNotificationChannel();
                EnsurePermissionRequested();
                Debug.Log("[Notify] Android notification helper warmup complete.");
            }
            catch (Exception e)
            {
                DisableNotifications($"Warmup failed: {e.Message}", e);
            }
#endif
        }

        public void NotifyIncomingCall(string callId, string callerNumber)
        {
            Send(callId,
                title: "Incoming Call",
                text: $"Booth {SafePeer(callerNumber)} is calling you.");
        }


        public void CancelSessionNotification(string sessionId)
        {
#if UNITY_ANDROID
            if (!_notificationsAvailable || string.IsNullOrWhiteSpace(sessionId))
                return;

            try
            {
                using AndroidJavaObject context = GetApplicationContext();
                using AndroidJavaObject manager = GetNotificationManager(context);
                manager?.Call("cancel", MakeNotificationId(sessionId));
            }
            catch (Exception e)
            {
                DisableNotifications($"Cancel failed: {e.Message}", e);
            }
#endif
        }

        public void Dispose()
        {
        }

        private static string SafePeer(string value) =>
            string.IsNullOrWhiteSpace(value) ? "peer" : value.Trim();

        private void Send(string sessionId, string title, string text)
        {
#if UNITY_ANDROID
            if (!_notificationsAvailable) return;
            if (!_visibility.ShouldShowBackgroundNotification) return;
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            if (!HasNotificationPermission()) return;

            try
            {
                using AndroidJavaObject context = GetApplicationContext();
                EnsureNotificationChannel();

                int notificationId = MakeNotificationId(sessionId);
                using AndroidJavaObject pendingIntent = CreateLaunchPendingIntent(context, notificationId);
                using AndroidJavaObject builder = CreateNotificationBuilder(context, title, text, pendingIntent);
                using AndroidJavaObject notification = builder.Call<AndroidJavaObject>("build");
                using AndroidJavaObject manager = GetNotificationManager(context);
                manager?.Call("notify", notificationId, notification);
                _diagnostics.LogInfo("Notify", $"Local notification posted: {title}");
            }
            catch (Exception e)
            {
                DisableNotifications($"Send failed: {e.Message}", e);
            }
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

        private void DisableNotifications(string reason, Exception exception)
        {
            _notificationsAvailable = false;
            Debug.LogWarning($"[Notify] Android notifications disabled. {reason}");
            if (exception != null)
                Debug.LogWarning($"[Notify] {exception}");
            _diagnostics?.LogWarning("Notify", reason);
        }

        private static AndroidJavaObject GetCurrentActivity()
        {
            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            return unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
        }

        private static AndroidJavaObject GetApplicationContext()
        {
            using AndroidJavaObject activity = GetCurrentActivity();
            return activity.Call<AndroidJavaObject>("getApplicationContext");
        }

        private static AndroidJavaObject GetNotificationManager(AndroidJavaObject context)
        {
            return context.Call<AndroidJavaObject>("getSystemService", "notification");
        }

        private void EnsureNotificationChannel()
        {
            using AndroidJavaClass versionClass = new AndroidJavaClass("android.os.Build$VERSION");
            int sdk = versionClass.GetStatic<int>("SDK_INT");
            if (sdk < 26)
                return;

            using AndroidJavaObject context = GetApplicationContext();
            using AndroidJavaObject manager = GetNotificationManager(context);
            using AndroidJavaObject existing = manager?.Call<AndroidJavaObject>("getNotificationChannel", ChannelId);
            if (existing != null)
                return;

            using var channel = new AndroidJavaObject(
                "android.app.NotificationChannel",
                ChannelId,
                ChannelName,
                NotificationImportanceHigh);
            channel.Call("setDescription", ChannelDescription);
            manager?.Call("createNotificationChannel", channel);
        }

        private void EnsurePermissionRequested()
        {
            using AndroidJavaClass versionClass = new AndroidJavaClass("android.os.Build$VERSION");
            int sdk = versionClass.GetStatic<int>("SDK_INT");
            if (sdk < 33)
                return;

            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(PostNotificationsPermission))
                UnityEngine.Android.Permission.RequestUserPermission(PostNotificationsPermission);
        }

        private static bool HasNotificationPermission()
        {
            using AndroidJavaClass versionClass = new AndroidJavaClass("android.os.Build$VERSION");
            int sdk = versionClass.GetStatic<int>("SDK_INT");
            if (sdk < 33)
                return true;

            return UnityEngine.Android.Permission.HasUserAuthorizedPermission(PostNotificationsPermission);
        }

        private static AndroidJavaObject CreateLaunchPendingIntent(AndroidJavaObject context, int notificationId)
        {
            using AndroidJavaObject packageManager = context.Call<AndroidJavaObject>("getPackageManager");
            string packageName = context.Call<string>("getPackageName");
            AndroidJavaObject launchIntent = packageManager.Call<AndroidJavaObject>("getLaunchIntentForPackage", packageName);
            if (launchIntent == null)
                throw new InvalidOperationException("Could not resolve launch intent for notification tap.");

            launchIntent.Call<AndroidJavaObject>("addFlags", 536870912);
            launchIntent.Call<AndroidJavaObject>("addFlags", 268435456);

            using var pendingIntentClass = new AndroidJavaClass("android.app.PendingIntent");
            int flags = PendingIntentFlagUpdateCurrent | PendingIntentFlagImmutable;
            return pendingIntentClass.CallStatic<AndroidJavaObject>(
                "getActivity",
                context,
                notificationId,
                launchIntent,
                flags);
        }

        private static AndroidJavaObject CreateNotificationBuilder(
            AndroidJavaObject context,
            string title,
            string text,
            AndroidJavaObject pendingIntent)
        {
            using AndroidJavaObject appInfo = context.Call<AndroidJavaObject>("getApplicationInfo");
            int iconResId = appInfo.Get<int>("icon");

            var builder = new AndroidJavaObject("android.app.Notification$Builder", context, ChannelId);
            builder.Call<AndroidJavaObject>("setSmallIcon", iconResId);
            builder.Call<AndroidJavaObject>("setContentTitle", title);
            builder.Call<AndroidJavaObject>("setContentText", text);
            builder.Call<AndroidJavaObject>("setAutoCancel", true);
            builder.Call<AndroidJavaObject>("setContentIntent", pendingIntent);
            builder.Call<AndroidJavaObject>("setVisibility", 1);
            return builder;
        }
#endif
    }
}
