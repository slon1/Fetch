namespace WebRtcV2.CrashHandling
{
    public readonly struct StartupCheckResult
    {
        public static StartupCheckResult Passed => new StartupCheckResult(true, null, null, null);

        public bool Success { get; }
        public string ErrorCode { get; }
        public string Message { get; }
        public string Detail { get; }

        private StartupCheckResult(bool success, string errorCode, string message, string detail)
        {
            Success = success;
            ErrorCode = errorCode;
            Message = message;
            Detail = detail;
        }

        public static StartupCheckResult Fail(string errorCode, string message, string detail = null)
            => new StartupCheckResult(false, errorCode, message, detail);
    }
}
