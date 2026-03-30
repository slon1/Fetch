using System;
using UnityEngine;

namespace WebRtcV2.CrashHandling
{
    [Serializable]
    public sealed class CrashReport
    {
        public string errorCode;
        public string exceptionType;
        public string message;
        public string stackTrace;
        public string unityLogTail;
        public string deviceModel;
        public string platform;
        public string apiLevel;
        public string appVersion;
        public string buildGuid;
        public string timestampUtc;
        public string startupStage;
    }
}
