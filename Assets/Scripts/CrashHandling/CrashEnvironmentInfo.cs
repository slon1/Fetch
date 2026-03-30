using System;
using UnityEngine;

namespace WebRtcV2.CrashHandling
{
    public static class CrashEnvironmentInfo
    {
        public const string NotAvailable = "n/a";

        public static string GetDeviceModel()
        {
            return string.IsNullOrWhiteSpace(SystemInfo.deviceModel)
                ? "Unknown"
                : SystemInfo.deviceModel;
        }

        public static string GetPlatform()
        {
            return UnityEngine.Application.platform.ToString();
        }

        public static string GetAndroidApiLevel()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var version = new AndroidJavaClass("android.os.Build$VERSION");
                int apiLevel = version.GetStatic<int>("SDK_INT");
                return apiLevel.ToString();
            }
            catch
            {
                return NotAvailable;
            }
#else
            return NotAvailable;
#endif
        }

        public static string GetAppVersion()
        {
            return string.IsNullOrWhiteSpace(UnityEngine.Application.version)
                ? "Unknown"
                : UnityEngine.Application.version;
        }

        public static string GetBuildGuid()
        {
            try
            {
                return string.IsNullOrWhiteSpace(UnityEngine.Application.buildGUID)
                    ? NotAvailable
                    : UnityEngine.Application.buildGUID;
            }
            catch
            {
                return NotAvailable;
            }
        }

        public static string GetTimestampUtc()
        {
            return DateTime.UtcNow.ToString("O");
        }
    }
}
