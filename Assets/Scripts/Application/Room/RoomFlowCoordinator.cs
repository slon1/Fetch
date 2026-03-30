using System;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using WebRtcV2.Config;
using WebRtcV2.Shared;
using WebRtcV2.Transport;

namespace WebRtcV2.Application.Room
{
    /// <summary>
    /// Orchestrates room and auto-lobby scenarios: bootstrap, create-own, list, join, delete, heartbeat.
    /// Fully independent from the WebRTC connection lifecycle.
    /// </summary>
    public sealed class RoomFlowCoordinator : IRoomFlow
    {
        private readonly WorkerClient _worker;
        private readonly AppConfig _config;
        private readonly ConnectionDiagnostics _diagnostics;

        public string LocalClientId { get; }
        public string LocalDisplayName { get; }

        public RoomFlowCoordinator(
            WorkerClient worker,
            AppConfig config,
            ConnectionDiagnostics diagnostics,
            string localClientId,
            string localDisplayName)
        {
            _worker = worker;
            _config = config;
            _diagnostics = diagnostics;
            LocalClientId = localClientId;
            LocalDisplayName = localDisplayName;
        }

        public async UniTask<LobbyBootstrapResult> BootstrapLobbyAsync(CancellationToken ct = default)
        {
            var rooms = await GetRoomsAsync(ct);
            var ownRooms = rooms.Where(r => r.IsOwnedBy(LocalClientId)).ToArray();

            if (ownRooms.Length > 0)
            {
                foreach (var ownRoom in ownRooms)
                {
                    _diagnostics.LogRoom("DeleteOwnStaleRoom", ownRoom.SessionId);
                    await DeleteRoomAsync(ownRoom.SessionId, ct);
                }

                rooms = rooms.Where(r => !r.IsOwnedBy(LocalClientId)).ToArray();
            }

            if (rooms.Length > 0)
                return LobbyBootstrapResult.JoinExisting(rooms);

            var ownWaitingRoom = await CreateOwnRoomAsync(ct);
            return LobbyBootstrapResult.Waiting(ownWaitingRoom);
        }

        public async UniTask<RoomModel[]> GetRoomsAsync(CancellationToken ct = default)
        {
            _diagnostics.LogRoom("ListRooms");
            var dtos = await _worker.GetRoomsAsync(ct);
            var rooms = dtos.Select(MapRoom).Where(r => r != null).ToArray();

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (rooms.Length == 0)
            {
                _diagnostics.LogRoom("ListRoomsResult", "count=0");
            }
            else
            {
                long newestAgeMs = rooms.Min(r => Math.Max(0, nowMs - r.CreatedAt));
                long oldestAgeMs = rooms.Max(r => Math.Max(0, nowMs - r.CreatedAt));
                _diagnostics.LogRoom("ListRoomsResult", $"count={rooms.Length} newestAgeMs={newestAgeMs} oldestAgeMs={oldestAgeMs}");
            }

            return rooms;
        }

        public async UniTask<RoomModel> GetRoomAsync(string sessionId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return null;
            var dto = await _worker.GetRoomAsync(sessionId, ct);
            return MapRoom(dto);
        }

        public async UniTask<RoomModel> CreateOwnRoomAsync(CancellationToken ct = default)
        {
            string sessionId = Guid.NewGuid().ToString("N");
            string displayName = LocalDisplayName;
            if (displayName.Length > _config.room.maxDisplayNameLength)
                displayName = displayName.Substring(0, _config.room.maxDisplayNameLength);

            _diagnostics.LogRoom("CreateRoom", $"name={displayName} session={sessionId}");

            var createdRoom = await _worker.CreateRoomAsync(displayName, sessionId, LocalClientId, ct);
            if (createdRoom == null)
            {
                _diagnostics.LogError("RoomFlow", "CreateRoom failed - Worker POST rejected");
                return null;
            }

            return MapRoom(createdRoom);
        }

        public async UniTask<JoinRoomResult> JoinRoomAsync(RoomModel room, CancellationToken ct = default)
        {
            if (room == null) return JoinRoomResult.Fail("room is null");

            long roomAgeMs = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - room.CreatedAt);
            _diagnostics.LogRoom("JoinRoom", $"{room} ageMs={roomAgeMs}");

            var response = await _worker.JoinRoomAsync(room.SessionId, ct);
            if (response == null || !response.ok)
            {
                _diagnostics.LogError("RoomFlow", $"JoinRoom failed for session={room.SessionId}");
                return JoinRoomResult.Fail("join request rejected by server");
            }

            return JoinRoomResult.Ok(response.sessionId, response.callerPeerId);
        }

        public async UniTask<bool> HeartbeatRoomAsync(string sessionId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return false;
            return await _worker.HeartbeatRoomAsync(sessionId, LocalClientId, ct);
        }

        public async UniTask DeleteRoomAsync(string sessionId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            _diagnostics.LogRoom("DeleteRoom", sessionId);
            await _worker.DeleteRoomAsync(sessionId, ct);
        }

        private static RoomModel MapRoom(WorkerClient.RoomEntryDto dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.sessionId)) return null;

            return new RoomModel(
                id: dto.id,
                sessionId: dto.sessionId,
                displayName: dto.displayName,
                creatorPeerId: dto.creatorPeerId,
                status: dto.status,
                createdAt: dto.createdAt,
                expiresAt: dto.expiresAt,
                joinedAt: dto.joinedAt,
                closedAt: dto.closedAt,
                lastHeartbeatAt: dto.lastHeartbeatAt,
                heartbeatExpiresAt: dto.heartbeatExpiresAt);
        }
    }
}
