using UnityEngine;

namespace WebRtcV2.Shared
{
    /// <summary>
    /// Thin tagged wrapper around Debug.Log. Replace with a structured logger later without touching call sites.
    /// </summary>
    public static class WLog
    {
        public static void Info(string tag, string message) =>
            Debug.Log($"[{tag}] {message}");

        public static void Warn(string tag, string message) =>
            Debug.LogWarning($"[{tag}] {message}");

        public static void Error(string tag, string message) =>
            Debug.LogError($"[{tag}] {message}");

        public static void Info(string tag, string message, Object context) =>
            Debug.Log($"[{tag}] {message}", context);
    }
}
