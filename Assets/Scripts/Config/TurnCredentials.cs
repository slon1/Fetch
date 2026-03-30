namespace WebRtcV2.Config
{
    public struct TurnCredentials
    {
        public string Username;
        public string Credential;
        public string[] TurnUrls;

        public bool IsEmpty => string.IsNullOrEmpty(Username);

        public static TurnCredentials Empty => default;
    }
}
