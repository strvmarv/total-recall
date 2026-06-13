using System.Text.Json.Serialization;
using TotalRecall.Web.Api;

namespace TotalRecall.Web;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(HealthInfo))]
[JsonSerializable(typeof(ApiError))]
public partial class WebJsonContext : JsonSerializerContext;
