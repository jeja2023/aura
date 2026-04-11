/* 文件：应用数据存储（AppStore.cs） | File: Application Data Store */
using Aura.Api.Models;

namespace Aura.Api.Data;

internal sealed class AppStore
{
    public long DeviceSeed = 1;
    public long CaptureSeed;
    public long RoiSeed;
    public long AlertSeed;
    public long OperationSeed;
    public long RoleSeed = 2;
    public long UserSeed = 1;
    public long CampusSeed = 4;
    public long FloorSeed;
    public long CameraSeed;
    public long TrackEventSeed;
    public long JudgeSeed;
    public List<DeviceEntity> Devices { get; } =
    [
        new(1, "1号NVR", "127.0.0.1", 8000, "hikvision", "isapi", "online", DateTimeOffset.Now)
    ];
    public List<CaptureEntity> Captures { get; } = [];
    public List<RoiEntity> Rois { get; } = [];
    public List<AlertEntity> Alerts { get; } = [];
    public List<OperationEntity> Operations { get; } = [];
    public List<RoleEntity> Roles { get; } =
    [
        new(1, "super_admin", "[\"all\"]"),
        new(2, "building_admin", "[\"device\",\"roi\",\"track\",\"alert\",\"stats\"]")
    ];
    public List<UserEntity> Users { get; } =
    [
        new(1, "admin", "super_admin", 1, DateTimeOffset.Now)
    ];
    public List<CampusNodeEntity> CampusNodes { get; } =
    [
        new(1, null, "campus", "一号园区"),
        new(2, 1, "building", "A栋"),
        new(3, 2, "floor", "1层"),
        new(4, 3, "room", "101室")
    ];
    public List<FloorEntity> Floors { get; } = [];
    public List<CameraEntity> Cameras { get; } = [];
    public List<TrackEventEntity> TrackEvents { get; } = [];
    public List<JudgeResultEntity> JudgeResults { get; } = [];
    public List<VirtualPersonEntity> VirtualPersons { get; } = [];
}
