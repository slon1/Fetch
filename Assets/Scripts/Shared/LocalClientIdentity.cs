using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace WebRtcV2.Shared
{
    /// <summary>
    /// Stable per-install identity used for ownership and human-facing booth number assignment.
    /// ClientId is stable and internal; booth number is stable after first successful registration.
    /// </summary>
    public sealed class LocalClientIdentity
    {
        private const string ClientIdPrefsKey = "WebRtcV2.ClientId";
        private const string BoothNumberPrefsKey = "WebRtcV2.BoothNumber";
        private const string BoothAttemptPrefsKey = "WebRtcV2.BoothAttempt";

        public const int BoothNumberLength = 4;
        private const ulong BoothModulo = 10_000UL;

        public string ClientId { get; }
        public string DisplayName { get; }
        public string ShortSuffix { get; }
        public string BoothNumber { get; }
        public int BoothAttempt { get; }

        private LocalClientIdentity(string clientId, string displayName, string shortSuffix, string boothNumber, int boothAttempt)
        {
            ClientId = clientId;
            DisplayName = displayName;
            ShortSuffix = shortSuffix;
            BoothNumber = boothNumber;
            BoothAttempt = boothAttempt;
        }

        public static LocalClientIdentity Load(int maxDisplayNameLength)
        {
            string clientId = ResolveClientId();
            string shortSuffix = clientId.Length >= 6 ? clientId.Substring(0, 6) : clientId;
            string deviceModel = ResolveDisplayDeviceName();
            string displayName = $"{deviceModel}-{shortSuffix}";
            if (displayName.Length > maxDisplayNameLength)
                displayName = displayName.Substring(0, maxDisplayNameLength);

            string storedBoothNumber = SanitizeStoredBoothNumber(PlayerPrefs.GetString(BoothNumberPrefsKey, string.Empty));
            int boothAttempt = Mathf.Max(0, PlayerPrefs.GetInt(BoothAttemptPrefsKey, 0));
            return new LocalClientIdentity(clientId, displayName, shortSuffix, storedBoothNumber, boothAttempt);
        }

        public string GetBoothNumberCandidate(int attempt)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes($"{ClientId}:{attempt.ToString(CultureInfo.InvariantCulture)}"));
            ulong value = BitConverter.ToUInt64(hash, 0) % BoothModulo;
            return value.ToString($"D{BoothNumberLength}", CultureInfo.InvariantCulture);
        }

        public void PersistBoothNumber(string boothNumber, int attempt)
        {
            string sanitizedBoothNumber = SanitizeStoredBoothNumber(boothNumber);
            if (string.IsNullOrWhiteSpace(sanitizedBoothNumber))
                return;

            PlayerPrefs.SetString(BoothNumberPrefsKey, sanitizedBoothNumber);
            PlayerPrefs.SetInt(BoothAttemptPrefsKey, Mathf.Max(0, attempt));
            PlayerPrefs.Save();
        }

        private static string SanitizeStoredBoothNumber(string boothNumber)
        {
            if (string.IsNullOrWhiteSpace(boothNumber))
                return string.Empty;

            string trimmed = boothNumber.Trim();
            if (trimmed.Length != BoothNumberLength)
                return string.Empty;

            foreach (char c in trimmed)
            {
                if (!char.IsDigit(c))
                    return string.Empty;
            }

            return trimmed;
        }

        private static string ResolveClientId()
        {
            string source = SystemInfo.deviceUniqueIdentifier;
            if (!string.IsNullOrWhiteSpace(source) &&
                !string.Equals(source, SystemInfo.unsupportedIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                return ComputeSha256Hex(source);
            }

            string stored = PlayerPrefs.GetString(ClientIdPrefsKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(stored))
                return stored;

            stored = Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString(ClientIdPrefsKey, stored);
            PlayerPrefs.Save();
            return stored;
        }

        private static string ComputeSha256Hex(string value)
        {
            using var sha = SHA256.Create();
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            byte[] hash = sha.ComputeHash(bytes);

            var builder = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
                builder.Append(b.ToString("x2"));
            return builder.ToString();
        }

        private static string ResolveDisplayDeviceName()
        {
            string platformFallback = GetPlatformFallbackName();
            switch (UnityEngine.Application.platform)
            {
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor:
                    return platformFallback;
            }

            string sanitized = SanitizeModel(SystemInfo.deviceModel);
            if (IsUsefulModelName(sanitized))
                return sanitized;

            return platformFallback;
        }

        private static string GetPlatformFallbackName()
        {
            return UnityEngine.Application.platform switch
            {
                RuntimePlatform.Android => "Android",
                RuntimePlatform.IPhonePlayer => "iPhone",
                RuntimePlatform.WindowsPlayer => "Windows",
                RuntimePlatform.WindowsEditor => "Windows",
                RuntimePlatform.OSXPlayer => "Mac",
                RuntimePlatform.OSXEditor => "Mac",
                RuntimePlatform.LinuxPlayer => "Linux",
                RuntimePlatform.LinuxEditor => "Linux",
                _ => "Device"
            };
        }

        private static bool IsUsefulModelName(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return false;

            string normalized = model.Replace("-", " ").Trim().ToLowerInvariant();
            return normalized switch
            {
                "system product name" => false,
                "system product" => false,
                "default string" => false,
                "to be filled by o e m" => false,
                "to be filled by oem" => false,
                "unknown" => false,
                _ => true
            };
        }

        private static string SanitizeModel(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var builder = new StringBuilder(raw.Length);
            bool lastWasSeparator = false;
            foreach (char c in raw.Trim())
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(c);
                    lastWasSeparator = false;
                    continue;
                }

                if (!lastWasSeparator)
                {
                    builder.Append('-');
                    lastWasSeparator = true;
                }
            }

            return builder.ToString().Trim('-');
        }
    }
}
