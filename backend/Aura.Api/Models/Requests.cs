using System.Text.Json.Serialization;

namespace Aura.Api.Models;

internal sealed record LoginReq(string UserName, string Password);
internal sealed record DeviceRegisterReq(string Name, string Ip, int Port, string Brand, string Protocol);
internal sealed record CaptureReq(long DeviceId, int ChannelNo, DateTimeOffset CaptureTime, string ImageBase64, string MetadataJson);
internal sealed record CaptureMockReq(long DeviceId, int ChannelNo, string MetadataJson);
internal sealed record RoiReq(long CameraId, long RoomNodeId, string VerticesJson);
internal sealed record CreateAlertReq(string AlertType, string Detail);
internal sealed record RoleCreateReq(string RoleName, string? PermissionJson);
internal sealed record UserCreateReq(string UserName, string Password, long RoleId);
internal sealed record UserStatusReq(int Status);
internal sealed record CampusCreateReq(long? ParentId, string LevelType, string NodeName);
internal sealed record CampusUpdateReq(string NodeName);
internal sealed record FloorCreateReq(long NodeId, string FilePath, decimal ScaleRatio);
internal sealed record CameraCreateReq(long FloorId, long DeviceId, int ChannelNo, decimal PosX, decimal PosY);
internal sealed record RetryProcessReq(int Take);
internal sealed record ClusterRunReq(int GapMinutes, double SimilarityThreshold = 0.82, int MinPoints = 2, int MaxCaptures = 500);
internal sealed record VectorExtractReq(string ImageBase64, string? MetadataJson);
internal sealed record VectorSearchReq(List<float> Feature, int TopK);
internal sealed record SpaceCollisionReq(string? Vid, long CameraId, decimal PosX, decimal PosY, DateTimeOffset? EventTime);
internal sealed record JudgeRunReq(string? Date);
internal sealed record JudgeAbnormalReq(string? Date, int GroupThreshold, int StayMinutes);
internal sealed record JudgeNightReq(string? Date, int CutoffHour);
internal sealed record JudgeRunResult(DateOnly JudgeDate, string JudgeType, int SourceCount, int ResultCount);
internal sealed record OpsAlertNotifyTestReq(string? AlertType, string? Detail);
