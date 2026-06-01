using System.Text.Json.Serialization;

namespace OpenClaw.Gateway.Bootstrap;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(LocalStartupState))]
[JsonSerializable(typeof(StartupDiagnosticsReport))]
[JsonSerializable(typeof(ExceptionDetail))]
[JsonSerializable(typeof(InnerExceptionDetail))]
[JsonSerializable(typeof(ConfigurationSummary))]
[JsonSerializable(typeof(EnvironmentSummary))]
[JsonSerializable(typeof(FailureDiagnosis))]
[JsonSerializable(typeof(List<InnerExceptionDetail>))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class StartupJsonContext : JsonSerializerContext
{
}
