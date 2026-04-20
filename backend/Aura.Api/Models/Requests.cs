using System.Text.Json.Serialization;

namespace Aura.Api.Models;

internal sealed record LoginReq(string UserName, string Password);
internal sealed record DeviceRegisterReq(string Name, string Ip, int Port, string Brand, string Protocol);

/// <summary>海康 ISAPI 调用：DeviceId 来自 nvr_device；账号密码可空，空则使用配置 Hikvision:Isapi。</summary>
internal sealed record HikvisionIsapiDeviceOpReq(long DeviceId, string? UserName, string? Password);

/// <summary>海康抓图：ChannelIndex 为摄像机序号（与 Demo 一致拼接 01/02/03）；StreamType 0 主码流 1 子码流 2 第三路。</summary>
internal sealed record HikvisionIsapiSnapshotReq(long DeviceId, int ChannelIndex, int StreamType, string? UserName, string? Password);

/// <summary>海康 ISAPI 通用网关：PathAndQuery 须以 /ISAPI/ 开头且落在白名单模块下。</summary>
internal sealed record HikvisionIsapiGatewayReq(
    long DeviceId,
    string Method,
    string PathAndQuery,
    string? UserName,
    string? Password,
    string? Body,
    string? BodyBase64,
    string? ContentType,
    bool PreferBinaryResponse);

/// <summary>请求 I 帧，StreamingChannelId 如 101（与 Streaming 通道号一致）。</summary>
internal sealed record HikvisionIsapiKeyFrameReq(long DeviceId, string StreamingChannelId, string? UserName, string? Password);

/// <summary>SDT 图片上传（与 Demo <c>CommonMethod.UploadPic</c> 字段 <c>imageFile</c> 一致），用于人脸库等流程前置上传。</summary>
internal sealed record HikvisionIsapiSdtPictureUploadReq(
    long DeviceId,
    string? UserName,
    string? Password,
    string FileName,
    string ImageBase64,
    string? PartContentType);

/// <summary>解析设备返回的 ResponseStatus XML/JSON 片段。</summary>
internal sealed record HikvisionIsapiAnalyzeReq(string? Raw);

/// <summary>媒体取流提示：按抓图相同规则拼接 <c>StreamingChannelId</c>，供 RTSP 客户端自行拉流（网关不代理码流）。</summary>
internal sealed record MediaStreamHintReq(long DeviceId, int ChannelIndex, int StreamType);
internal sealed record CaptureReq(long DeviceId, int ChannelNo, DateTimeOffset CaptureTime, string ImageBase64, string MetadataJson);
internal sealed record CaptureMockReq(long DeviceId, int ChannelNo, string MetadataJson);
internal sealed record RoiReq(long CameraId, long RoomNodeId, string VerticesJson);
internal sealed record CreateAlertReq(string AlertType, string Detail);
internal sealed record RoleCreateReq(string RoleName, string? PermissionJson);
internal sealed record UserCreateReq(string UserName, string DisplayName, string Password, long RoleId);
internal sealed record UserStatusReq(int Status);
/// <summary>超级管理员更新用户资料（登录用户名、显示名称、角色、状态）。</summary>
internal sealed record UserUpdateReq(string UserName, string DisplayName, long RoleId, int Status);
/// <summary>超级管理员重置指定用户密码。</summary>
internal sealed record UserPasswordResetReq(string? NewPassword = null);
internal sealed record ChangePasswordReq(string CurrentPassword, string NewPassword);
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
internal sealed record PageViewAuditReq(string? PagePath, string? PageTitle, string? EventType, long? StayMs, string? SessionId);
