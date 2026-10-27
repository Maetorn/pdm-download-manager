using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PDM.Core.Engine;
using PDM.Core.Models;

namespace PDM.Core;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<DownloadItem>))]
[JsonSerializable(typeof(List<CookieData>))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(PDM.Core.Video.DirectState))]
[JsonSerializable(typeof(HistoryData))]
[JsonSerializable(typeof(List<ExportEntry>))]
public partial class PdmJson : JsonSerializerContext
{
}
