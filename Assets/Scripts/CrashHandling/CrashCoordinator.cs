using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace WebRtcV2.CrashHandling
{
    public sealed class CrashCoordinator : IDisposable
    {
        private const int MaxLogEntries = 40;

        private readonly ICrashReportSink _sink;
        private readonly object _sync = new object();
        private readonly Queue<string> _logTail = new Queue<string>(MaxLogEntries);

        private bool _registered;
        private bool _hasFatal;
        private CrashReport _pendingFatal;

        public CrashCoordinator(ICrashReportSink sink)
        {
            _sink = sink;
        }

        public CrashReport CurrentReport => _sink.Current;

        public void RegisterGlobalHandlers()
        {
            if (_registered) return;
            _registered = true;

            UnityEngine.Application.logMessageReceivedThreaded += HandleLogMessageReceivedThreaded;
            AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
            TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;
        }

        public void Dispose()
        {
            if (!_registered) return;
            _registered = false;

            UnityEngine.Application.logMessageReceivedThreaded -= HandleLogMessageReceivedThreaded;
            AppDomain.CurrentDomain.UnhandledException -= HandleUnhandledException;
            TaskScheduler.UnobservedTaskException -= HandleUnobservedTaskException;
        }

        public CrashReport CreatePreflightFailureReport(StartupCheckResult result)
        {
            return CreateReport(
                result.ErrorCode,
                "preflight",
                result.Message,
                exception: null,
                stackTrace: result.Detail);
        }

        public CrashReport ReportFatal(string errorCode, string startupStage, string message, Exception exception)
        {
            return CreateReport(errorCode, startupStage, message, exception, exception?.StackTrace);
        }

        public bool TryConsumePendingFatal(out CrashReport report)
        {
            lock (_sync)
            {
                report = _pendingFatal;
                _pendingFatal = null;
                return report != null;
            }
        }

        private void HandleLogMessageReceivedThreaded(string condition, string stackTrace, LogType type)
        {
            // Unity logs are always useful for the crash tail, but they are not reliable
            // evidence of a fatal condition. Entering fatal mode is reserved for explicit
            // fatal reports and true unhandled managed exceptions.
            AppendLog(condition, stackTrace, type);
        }

        private void HandleUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception exception = e.ExceptionObject as Exception;
            string fallbackMessage = e.ExceptionObject?.ToString() ?? "Unknown unhandled exception.";
            string stackTrace = exception?.StackTrace ?? fallbackMessage;

            CreateReport(
                errorCode: "RUNTIME-UNHANDLED",
                startupStage: "runtime-unhandled",
                message: exception?.Message ?? fallbackMessage,
                exception: exception,
                stackTrace: stackTrace);
        }

        private void HandleUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved();

            CreateReport(
                errorCode: "TASK-UNOBSERVED",
                startupStage: "task-unobserved",
                message: e.Exception.GetBaseException().Message,
                exception: e.Exception,
                stackTrace: e.Exception.ToString());
        }

        private CrashReport CreateReport(
            string errorCode,
            string startupStage,
            string message,
            Exception exception,
            string stackTrace)
        {
            CrashReport report;
            bool shouldStore;

            lock (_sync)
            {
                if (_hasFatal && _sink.Current != null)
                    return _sink.Current;

                _hasFatal = true;
                report = new CrashReport
                {
                    errorCode = errorCode,
                    exceptionType = exception?.GetType().FullName ?? string.Empty,
                    message = string.IsNullOrWhiteSpace(message) ? "Unknown fatal error." : message,
                    stackTrace = stackTrace ?? string.Empty,
                    unityLogTail = BuildLogTailSnapshot(),
                    deviceModel = CrashEnvironmentInfo.GetDeviceModel(),
                    platform = CrashEnvironmentInfo.GetPlatform(),
                    apiLevel = CrashEnvironmentInfo.GetAndroidApiLevel(),
                    appVersion = CrashEnvironmentInfo.GetAppVersion(),
                    buildGuid = CrashEnvironmentInfo.GetBuildGuid(),
                    timestampUtc = CrashEnvironmentInfo.GetTimestampUtc(),
                    startupStage = startupStage ?? string.Empty
                };
                _pendingFatal = report;
                shouldStore = true;
            }

            if (shouldStore)
                _sink.Store(report);

            return report;
        }

        private void AppendLog(string condition, string stackTrace, LogType type)
        {
            string sanitizedCondition = string.IsNullOrWhiteSpace(condition)
                ? "(no message)"
                : condition.Replace('\r', ' ').Replace('\n', ' ');
            string line = $"[{DateTime.UtcNow:HH:mm:ss}] {type}: {sanitizedCondition}";
            if (type == LogType.Exception && !string.IsNullOrWhiteSpace(stackTrace))
                line = $"{line}\n{stackTrace.Trim()}";

            lock (_sync)
            {
                while (_logTail.Count >= MaxLogEntries)
                    _logTail.Dequeue();
                _logTail.Enqueue(line);
            }
        }

        private string BuildLogTailSnapshot()
        {
            var builder = new StringBuilder();
            foreach (string line in _logTail)
            {
                if (builder.Length > 0)
                    builder.AppendLine();
                builder.Append(line);
            }

            return builder.ToString();
        }
    }
}
