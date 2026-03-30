using System.Threading;
using Cysharp.Threading.Tasks;

namespace WebRtcV2.Application.Room
{
    /// <summary>
    /// Public API of the room and auto-lobby flow.
    /// UI code consumes bootstrap decisions and simple room operations.
    /// </summary>
    public interface IRoomFlow
    {
        string LocalClientId { get; }
        string LocalDisplayName { get; }

        UniTask<LobbyBootstrapResult> BootstrapLobbyAsync(CancellationToken ct = default);
        UniTask<RoomModel[]> GetRoomsAsync(CancellationToken ct = default);
        UniTask<RoomModel> GetRoomAsync(string sessionId, CancellationToken ct = default);
        UniTask<RoomModel> CreateOwnRoomAsync(CancellationToken ct = default);
        UniTask<JoinRoomResult> JoinRoomAsync(RoomModel room, CancellationToken ct = default);
        UniTask<bool> HeartbeatRoomAsync(string sessionId, CancellationToken ct = default);
        UniTask DeleteRoomAsync(string sessionId, CancellationToken ct = default);
    }

    public enum LobbyBootstrapMode
    {
        JoinExisting,
        WaitingForPeer,
    }

    public sealed class LobbyBootstrapResult
    {
        public LobbyBootstrapMode Mode { get; }
        public RoomModel OwnedRoom { get; }
        public RoomModel[] ForeignRooms { get; }

        private LobbyBootstrapResult(LobbyBootstrapMode mode, RoomModel ownedRoom, RoomModel[] foreignRooms)
        {
            Mode = mode;
            OwnedRoom = ownedRoom;
            ForeignRooms = foreignRooms;
        }

        public static LobbyBootstrapResult JoinExisting(RoomModel[] foreignRooms) =>
            new LobbyBootstrapResult(LobbyBootstrapMode.JoinExisting, null, foreignRooms);

        public static LobbyBootstrapResult Waiting(RoomModel ownedRoom) =>
            new LobbyBootstrapResult(LobbyBootstrapMode.WaitingForPeer, ownedRoom, null);
    }

    public sealed class JoinRoomResult
    {
        public bool Success { get; }
        public string SessionId { get; }
        public string CallerPeerId { get; }
        public string Error { get; }

        public static JoinRoomResult Ok(string sessionId, string callerPeerId) =>
            new JoinRoomResult(true, sessionId, callerPeerId, null);

        public static JoinRoomResult Fail(string error) =>
            new JoinRoomResult(false, null, null, error);

        private JoinRoomResult(bool success, string sessionId, string callerPeerId, string error)
        {
            Success = success;
            SessionId = sessionId;
            CallerPeerId = callerPeerId;
            Error = error;
        }
    }
}
