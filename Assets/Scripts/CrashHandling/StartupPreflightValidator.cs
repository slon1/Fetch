using UnityEngine;
using WebRtcV2.Config;
using WebRtcV2.Presentation;

namespace WebRtcV2.CrashHandling
{
    public static class StartupPreflightValidator
    {
        public static StartupCheckResult Validate(
            AppConfig config,
            AudioSource remoteAudioSource,
            CallerScr callerScreen,
            VideoScr videoScreen,
            ChatScr chatScreen,
            InfoScr infoScreen)
        {
            if (config == null)
                return StartupCheckResult.Fail(
                    "BOOT-CONFIG",
                    "Application config is missing.",
                    "Assign AppConfig on AppBootstrap.");

            if (remoteAudioSource == null)
                return StartupCheckResult.Fail(
                    "BOOT-AUDIO",
                    "Remote audio output is not configured.",
                    "Assign AudioSource on AppBootstrap.");

            if (callerScreen == null || videoScreen == null || chatScreen == null || infoScreen == null)
                return StartupCheckResult.Fail(
                    "BOOT-SCENE-REF",
                    "Scene UI references are incomplete.",
                    "Check CallerScr, VideoScr, ChatScr and InfoScr on AppBootstrap.");

            if (!IsSupportedPlatform(UnityEngine.Application.platform))
                return StartupCheckResult.Fail(
                    "BOOT-PLATFORM",
                    $"Unsupported platform: {UnityEngine.Application.platform}.",
                    "This build currently supports Android and desktop targets.");

            string apiLevel = CrashEnvironmentInfo.GetAndroidApiLevel();
            if (!string.IsNullOrWhiteSpace(apiLevel) && apiLevel != CrashEnvironmentInfo.NotAvailable)
                Debug.Log($"[Bootstrap] Android API level: {apiLevel}");

            return StartupCheckResult.Passed;
        }

        private static bool IsSupportedPlatform(RuntimePlatform platform)
        {
            return platform switch
            {
                RuntimePlatform.Android => true,
                RuntimePlatform.WindowsPlayer => true,
                RuntimePlatform.WindowsEditor => true,
                RuntimePlatform.OSXPlayer => true,
                RuntimePlatform.OSXEditor => true,
                RuntimePlatform.LinuxPlayer => true,
                RuntimePlatform.LinuxEditor => true,
                RuntimePlatform.IPhonePlayer => true,
                _ => false
            };
        }
    }
}
