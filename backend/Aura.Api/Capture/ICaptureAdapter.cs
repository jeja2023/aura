using System.Text.Json;

namespace Aura.Api.Capture;

public interface ICaptureAdapter
{
    string Name { get; }

    CapturePayload Normalize(JsonElement rawPayload);
}
