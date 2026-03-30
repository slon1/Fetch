using System;
using System.IO;
using UnityEngine;

namespace WebRtcV2.CrashHandling
{
    public sealed class LocalCrashReportSink : ICrashReportSink
    {
        private const string ReportFileName = "crash-report-latest.json";

        public CrashReport Current { get; private set; }

        public void Store(CrashReport report)
        {
            Current = report;
            Persist(report);
        }

        private static void Persist(CrashReport report)
        {
            try
            {
                string path = Path.Combine(UnityEngine.Application.persistentDataPath, ReportFileName);
                string json = JsonUtility.ToJson(report, true);
                File.WriteAllText(path, json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Crash] Failed to persist crash report: {e.Message}");
            }
        }
    }
}
