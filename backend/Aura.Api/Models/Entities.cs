namespace Aura.Api.Models;

internal sealed record DeviceEntity(long DeviceId, string Name, string Ip, int Port, string Brand, string Protocol, string Status, DateTimeOffset CreatedAt);
internal sealed record CaptureEntity(long CaptureId, long DeviceId, int ChannelNo, DateTimeOffset CaptureTime, string MetadataJson, string? ImagePath = null);
internal sealed record RoiEntity(long RoiId, long CameraId, long RoomNodeId, string VerticesJson, DateTimeOffset CreatedAt);
internal sealed record AlertEntity(long AlertId, string AlertType, string Detail, DateTimeOffset CreatedAt);
internal sealed record OperationEntity(long OperationId, string OperatorName, string Action, string Detail, DateTimeOffset CreatedAt);
internal sealed record SystemLogEntity(long SystemLogId, string Level, string Source, string Message, DateTimeOffset CreatedAt);
internal sealed record RoleEntity(long RoleId, string RoleName, string PermissionJson);
internal sealed record UserEntity(long UserId, string UserName, string DisplayName, string RoleName, long RoleId, int Status, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt = null, bool MustChangePassword = false);
internal sealed record CampusNodeEntity(long NodeId, long? ParentId, string LevelType, string NodeName);
internal sealed record FloorEntity(long FloorId, long NodeId, string FilePath, decimal ScaleRatio);
internal sealed record CameraEntity(long CameraId, long FloorId, long DeviceId, int ChannelNo, decimal PosX, decimal PosY);
internal sealed record TrackEventEntity(long EventId, string Vid, long CameraId, long RoiId, DateTimeOffset EventTime);
internal sealed record JudgeResultEntity(long JudgeId, string Vid, long RoomId, string JudgeType, DateOnly JudgeDate, string DetailJson, DateTimeOffset CreatedAt);
internal sealed record VirtualPersonEntity(string Vid, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, long DeviceId, int CaptureCount, string ClusterAlgorithm = "unknown", double ClusterScore = 0d);
