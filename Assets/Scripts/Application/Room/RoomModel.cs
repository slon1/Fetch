using System;

namespace WebRtcV2.Application.Room
{
    /// <summary>
    /// Immutable domain model for lobby and waiting-room state.
    /// Created by RoomFlowCoordinator, consumed by the UI layer.
    /// </summary>
    public sealed class RoomModel
    {
        public string Id { get; }
        public string SessionId { get; }
        public string DisplayName { get; }
        public string CreatorPeerId { get; }
        public string Status { get; }
        public long CreatedAt { get; }
        public long ExpiresAt { get; }
        public long JoinedAt { get; }
        public long ClosedAt { get; }
        public long LastHeartbeatAt { get; }
        public long HeartbeatExpiresAt { get; }

        public bool IsWaiting => string.Equals(Status, "waiting", StringComparison.OrdinalIgnoreCase);
        public bool IsJoined => string.Equals(Status, "joined", StringComparison.OrdinalIgnoreCase);
        public bool IsClosed => string.Equals(Status, "closed", StringComparison.OrdinalIgnoreCase);

        public RoomModel(
            string id,
            string sessionId,
            string displayName,
            string creatorPeerId,
            string status,
            long createdAt,
            long expiresAt,
            long joinedAt,
            long closedAt,
            long lastHeartbeatAt,
            long heartbeatExpiresAt)
        {
            Id = id;
            SessionId = sessionId;
            DisplayName = displayName;
            CreatorPeerId = creatorPeerId;
            Status = status;
            CreatedAt = createdAt;
            ExpiresAt = expiresAt;
            JoinedAt = joinedAt;
            ClosedAt = closedAt;
            LastHeartbeatAt = lastHeartbeatAt;
            HeartbeatExpiresAt = heartbeatExpiresAt;
        }

        public bool IsOwnedBy(string clientId) =>
            !string.IsNullOrEmpty(clientId) &&
            string.Equals(CreatorPeerId, clientId, StringComparison.Ordinal);

        public override string ToString() =>
            $"Room({DisplayName}, session={SessionId}, status={Status})";
    }
}
