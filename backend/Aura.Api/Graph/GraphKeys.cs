namespace Aura.Api.Graph;

internal static class GraphKeys
{
    internal static string Node(long tenantId, long nodeId) => $"t{tenantId}_node_{nodeId}";
    internal static string Floor(long tenantId, long floorId) => $"t{tenantId}_floor_{floorId}";
    internal static string Camera(long tenantId, long cameraId) => $"t{tenantId}_camera_{cameraId}";
    internal static string Roi(long tenantId, long roiId) => $"t{tenantId}_roi_{roiId}";
    internal static string Person(long tenantId, string personId) => $"t{tenantId}_person_{ArangoGraphRepository.SafeKey(personId)}";
    internal static string Source(long tenantId, long sourceId) => $"t{tenantId}_source_{sourceId}";

    internal static string NodeRef(string collection, long tenantId, long nodeId) => $"{collection}/{Node(tenantId, nodeId)}";
    internal static string FloorRef(long tenantId, long floorId) => $"floors/{Floor(tenantId, floorId)}";
    internal static string CameraRef(long tenantId, long cameraId) => $"cameras/{Camera(tenantId, cameraId)}";
    internal static string RoiRef(long tenantId, long roiId) => $"rois/{Roi(tenantId, roiId)}";
    internal static string PersonRef(long tenantId, string personId) => $"persons/{Person(tenantId, personId)}";
    internal static string SourceRef(long tenantId, long sourceId) => $"analysis_sources/{Source(tenantId, sourceId)}";
}
