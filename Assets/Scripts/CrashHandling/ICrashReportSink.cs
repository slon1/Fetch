namespace WebRtcV2.CrashHandling
{
    public interface ICrashReportSink
    {
        CrashReport Current { get; }

        void Store(CrashReport report);
    }
}
