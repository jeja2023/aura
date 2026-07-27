namespace Aura.Api.Data;

internal sealed record DbUser(string UserName, string PasswordHash, string? RoleName, string PermissionJson, bool MustChangePassword);
internal sealed record DbDevice(long DeviceId, string Name, string Ip, int Port, string Brand, string Protocol, string Status, DateTime CreatedAt, DateTime? LastSeenAt = null);
internal sealed record DbCapture(long CaptureId, long DeviceId, int ChannelNo, DateTime CaptureTime, string MetadataJson, string? ImagePath = null);
internal sealed record DbAlert(long AlertId, string AlertType, string Detail, DateTime CreatedAt);
internal sealed record DbOperation(long OperationId, string OperatorName, string Action, string Detail, DateTime CreatedAt);
internal sealed record DbSystemLog(long SystemLogId, string Level, string Source, string Message, DateTime CreatedAt);
internal sealed record DbSystemConfig(string ConfigKey, string ConfigValue, string? UpdatedBy, DateTimeOffset UpdatedAt);
internal sealed record DbRole(long RoleId, string RoleName, string PermissionJson);
internal sealed record DbUserListItem(long UserId, string UserName, long Status, string DisplayName, string? RoleName, long RoleId, DateTime CreatedAt, DateTime? LastLoginAt, bool MustChangePassword);
internal sealed record DbCampusNode(long NodeId, long? ParentId, string LevelType, string NodeName);
internal sealed record DbFloor(long FloorId, long NodeId, string FilePath, decimal ScaleRatio);
internal sealed record DbCamera(long CameraId, long FloorId, long DeviceId, int ChannelNo, decimal PosX, decimal PosY);
internal sealed record DbRoi(long RoiId, long CameraId, long RoomNodeId, string VerticesJson, DateTime CreatedAt);
internal sealed record DbTrackEvent
{
    public long EventId { get; }
    public string Vid { get; }
    public long CameraId { get; }
    public long RoiId { get; }
    public DateTimeOffset EventTime { get; }

    public DbTrackEvent(long eventId, string vid, long cameraId, long roiId, DateTime eventTime)
    {
        EventId = eventId;
        Vid = vid;
        CameraId = cameraId;
        RoiId = roiId;
        var normalized = eventTime.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(eventTime, DateTimeKind.Local)
            : eventTime;
        EventTime = new DateTimeOffset(normalized);
    }

    public DbTrackEvent(long eventId, string vid, long cameraId, long roiId, DateTimeOffset eventTime)
    {
        EventId = eventId;
        Vid = vid;
        CameraId = cameraId;
        RoiId = roiId;
        EventTime = eventTime;
    }
}

internal sealed record DbJudgeResult
{
    public long JudgeId { get; }
    public string Vid { get; }
    public long RoomId { get; }
    public string JudgeType { get; }
    public DateTime JudgeDate { get; }
    public string DetailJson { get; }
    public DateTimeOffset CreatedAt { get; }

    public DbJudgeResult(long judgeId, string vid, long roomId, string judgeType, DateTime judgeDate, string detailJson, DateTime createdAt)
    {
        JudgeId = judgeId;
        Vid = vid;
        RoomId = roomId;
        JudgeType = judgeType;
        JudgeDate = judgeDate;
        DetailJson = detailJson;
        var normalized = createdAt.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(createdAt, DateTimeKind.Local)
            : createdAt;
        CreatedAt = new DateTimeOffset(normalized);
    }

    public DbJudgeResult(long judgeId, string vid, long roomId, string judgeType, DateTime judgeDate, string detailJson, DateTimeOffset createdAt)
    {
        JudgeId = judgeId;
        Vid = vid;
        RoomId = roomId;
        JudgeType = judgeType;
        JudgeDate = judgeDate;
        DetailJson = detailJson;
        CreatedAt = createdAt;
    }
}

internal sealed record DbVirtualPerson(string Vid, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, long DeviceId, int CaptureCount);
internal sealed record DbVidImage(string Vid, string? ImagePath);
internal sealed record DbAlertWorkflow(long WorkflowId, long AlertId, string Status, string? Assignee, string Priority, int EscalationLevel, string? HandoverTo, string? Note, DateTime? ClosedAt, string? UpdatedBy, DateTime UpdatedAt, DateTime CreatedAt);
internal sealed record DbSpaceTopologyEdge(long EdgeId, long FromCameraId, long ToCameraId, string RelationType, decimal Weight, DateTime CreatedAt);
internal sealed record DbSpaceHeatmapSnapshot(long SnapshotId, long FloorId, DateTime BucketStart, int BucketMinutes, string HeatJson, DateTime CreatedAt);
internal sealed record DbReportSchedule(long ScheduleId, string ReportType, string CronExpr, string RoleName, string DeliveryChannel, bool Enabled, string? CreatedBy, DateTime UpdatedAt, DateTime CreatedAt);
internal sealed record DbReportRun(long RunId, long? ScheduleId, string ReportType, DateTime RangeStart, DateTime RangeEnd, string Status, string SummaryJson, string? CreatedBy, DateTime GeneratedAt);
internal sealed record DbReportDelivery(long DeliveryId, long RunId, string RoleName, string DeliveryChannel, string Status, DateTime DeliveredAt);
internal sealed record DbTenantProject(long TenantId, string TenantCode, string TenantName, string ConfigJson, bool Enabled, DateTime CreatedAt);
internal sealed record DbTenantRoleScope(long ScopeId, long TenantId, string TenantCode, string TenantName, string RoleName, string PermissionJson, DateTime CreatedAt);
internal sealed record DbAiProviderConfig(long ProviderId, string ProviderName, string ProviderType, string EndpointUrl, string ModelName, string ModelVersion, int TrafficWeight, bool Enabled, DateTime CreatedAt);
internal sealed record DbAiAbExperiment(long ExperimentId, string ExperimentName, long ProviderAId, long ProviderBId, int TrafficSplit, string MetricName, bool Enabled, DateTime CreatedAt);
