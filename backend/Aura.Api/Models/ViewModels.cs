namespace Aura.Api.Models;

internal sealed record PointVm(double X, double Y);
internal sealed record CampusNodeVm(long NodeId, long? ParentId, string LevelType, string NodeName, List<CampusNodeVm> Children);
