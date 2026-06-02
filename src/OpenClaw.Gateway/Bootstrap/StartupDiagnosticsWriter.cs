using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Bootstrap;

// -- Model types --------------------------------------------------------

internal sealed record StartupDiagnosticsReport
{
    public DateTimeOffset GeneratedAt { get; init; }
    public string? GatewayVersion { get; init; }
    public required ExceptionDetail Exception { get; init; }
    public required ConfigurationSummary Configuration { get; init; }
    public required EnvironmentSummary Environment { get; init; }
    public required FailureDiagnosis CategorizedDiagnosis { get; init; }
}

internal sealed record ExceptionDetail
{
    public required string Type { get; init; }
    public required string Message { get; init; }
    public string? StackTrace { get; init; }
    public int HResult { get; init; }
    public List<InnerExceptionDetail>? InnerException { get; init; }
}

internal sealed record InnerExceptionDetail
{
    public required string Type { get; init; }
    public required string Message { get; init; }
    public string? StackTrace { get; init; }
    public int Depth { get; init; }
}

internal sealed record ConfigurationSummary
{
    public string? Environment { get; init; }
    public string? BindAddress { get; init; }
    public int? Port { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Endpoint { get; init; }
    public bool ApiKeyConfigured { get; init; }
    public bool AuthTokenConfigured { get; init; }
    public string? MemoryProvider { get; init; }
    public string? MemoryStoragePath { get; init; }
    public string? WorkspacePath { get; init; }
    public bool? IsNonLoopbackBind { get; init; }
    public string? RuntimeMode { get; init; }
    public bool? DynamicCodeSupported { get; init; }
}

internal sealed record EnvironmentSummary
{
    public required string OSDescription { get; init; }
    public required string OSArchitecture { get; init; }
    public required string RuntimeVersion { get; init; }
    public required string ProcessArchitecture { get; init; }
    public int ProcessorCount { get; init; }
    public required string CurrentDirectory { get; init; }
    public double WorkingSetMB { get; init; }
    public Dictionary<string, string?>? EnvironmentVariables { get; init; }
    public string? CommandLine { get; init; }
}

internal sealed record FailureDiagnosis
{
    public required string Category { get; init; }
    public required string Summary { get; init; }
    public string? RootCause { get; init; }
    public List<string>? SuggestedFixes { get; init; }
}

/// <summary>
/// Writes a detailed, machine-readable JSON diagnostic report when the gateway
/// fails to start. The report is saved alongside the gateway binary so operators
/// can send it to support or inspect it offline.
/// </summary>
internal static class StartupDiagnosticsWriter
{

    /// <summary>
    /// Writes <c>startup-diagnostics-{timestamp}.json</c> to
    /// <paramref name="outputDirectory"/> (defaults to the current directory).
    /// Returns the full path of the written file, or null on failure.
    /// </summary>
    public static string? Write(
        Exception ex,
        GatewayStartupContext? startup,
        string environmentName,
        string? outputDirectory = null)
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(outputDirectory)
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(outputDirectory);

            Directory.CreateDirectory(dir);

            var report = BuildReport(ex, startup, environmentName);
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
            var fileName = $"startup-diagnostics-{timestamp}.json";
            var filePath = Path.Combine(dir, fileName);

            File.WriteAllText(filePath, JsonSerializer.Serialize(report, typeof(StartupDiagnosticsReport), StartupJsonContext.Default));

            Console.Error.WriteLine();
            Console.Error.WriteLine($"Diagnostics written to: {filePath}");
            return filePath;
        }
        catch
        {
            // Never let the diagnostics writer itself crash the process.
            return null;
        }
    }

    internal static StartupDiagnosticsReport BuildReport(
        Exception ex,
        GatewayStartupContext? startup,
        string environmentName)
    {
        var config = startup?.Config;

        return new StartupDiagnosticsReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            GatewayVersion = GetGatewayVersion(),

            Exception = new ExceptionDetail
            {
                Type = ex.GetType().FullName!,
                Message = ex.Message,
                StackTrace = ex.StackTrace,
                HResult = ex.HResult,
                InnerException = WalkInnerExceptions(ex)
            },

            Configuration = new ConfigurationSummary
            {
                Environment = environmentName,
                BindAddress = config?.BindAddress,
                Port = config?.Port ?? (startup is not null ? 18789 : null),
                Provider = config?.Llm.Provider,
                Model = config?.Llm.Model,
                Endpoint = SanitizeEndpoint(config?.Llm.Endpoint),
                ApiKeyConfigured = !string.IsNullOrWhiteSpace(config?.Llm.ApiKey),
                AuthTokenConfigured = !string.IsNullOrWhiteSpace(config?.AuthToken),
                MemoryProvider = config?.Memory.Provider,
                MemoryStoragePath = config?.Memory.StoragePath,
                WorkspacePath = startup?.WorkspacePath,
                IsNonLoopbackBind = startup?.IsNonLoopbackBind,
                RuntimeMode = startup?.RuntimeState.EffectiveModeName,
                DynamicCodeSupported = startup?.RuntimeState.DynamicCodeSupported
            },

            Environment = new EnvironmentSummary
            {
                OSDescription = RuntimeInformation.OSDescription,
                OSArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                RuntimeVersion = RuntimeInformation.FrameworkDescription,
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessorCount = Environment.ProcessorCount,
                CurrentDirectory = Directory.GetCurrentDirectory(),
                WorkingSetMB = Math.Round(Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0), 2),
                EnvironmentVariables = CaptureRelevantEnvironmentVariables(),
                CommandLine = Environment.CommandLine
            },

            CategorizedDiagnosis = CategorizeFailure(ex, startup, environmentName)
        };
    }

    private static string? GetGatewayVersion()
    {
        try
        {
            var asm = typeof(StartupDiagnosticsWriter).Assembly;
            var attr = asm.GetCustomAttributes(false)
                .OfType<AssemblyInformationalVersionAttribute>()
                .FirstOrDefault();
            return attr?.InformationalVersion ?? asm.GetName().Version?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static List<InnerExceptionDetail>? WalkInnerExceptions(Exception ex)
    {
        List<InnerExceptionDetail>? list = null;
        var inner = ex.InnerException;
        var depth = 0;
        while (inner is not null && depth < 10)
        {
            list ??= new List<InnerExceptionDetail>();
            list.Add(new InnerExceptionDetail
            {
                Type = inner.GetType().FullName!,
                Message = inner.Message,
                StackTrace = inner.StackTrace,
                Depth = depth
            });
            inner = inner.InnerException;
            depth++;
        }
        return list;
    }

    private static Dictionary<string, string?> CaptureRelevantEnvironmentVariables()
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            ["MODEL_PROVIDER_KEY"] = MaskIfPresent(Environment.GetEnvironmentVariable("MODEL_PROVIDER_KEY")),
            ["MODEL_PROVIDER_MODEL"] = Environment.GetEnvironmentVariable("MODEL_PROVIDER_MODEL"),
            ["MODEL_PROVIDER_ENDPOINT"] = Environment.GetEnvironmentVariable("MODEL_PROVIDER_ENDPOINT"),
            ["OPENCLAW_AUTH_TOKEN"] = MaskIfPresent(Environment.GetEnvironmentVariable("OPENCLAW_AUTH_TOKEN")),
            ["OPENCLAW_WORKSPACE"] = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE"),
            ["OPENCLAW_CONFIG_PATH"] = Environment.GetEnvironmentVariable("OPENCLAW_CONFIG_PATH"),
            ["OpenClaw__BindAddress"] = Environment.GetEnvironmentVariable("OpenClaw__BindAddress"),
            ["OpenClaw__Port"] = Environment.GetEnvironmentVariable("OpenClaw__Port"),
            ["OpenClaw__Llm__Provider"] = Environment.GetEnvironmentVariable("OpenClaw__Llm__Provider"),
            ["OpenClaw__Llm__Model"] = Environment.GetEnvironmentVariable("OpenClaw__Llm__Model"),
            ["OpenClaw__Llm__Endpoint"] = Environment.GetEnvironmentVariable("OpenClaw__Llm__Endpoint"),
            ["OpenClaw__Memory__Provider"] = Environment.GetEnvironmentVariable("OpenClaw__Memory__Provider"),
            ["OpenClaw__Memory__StoragePath"] = Environment.GetEnvironmentVariable("OpenClaw__Memory__StoragePath"),
            ["DOTNET_ENVIRONMENT"] = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
            ["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT"),
        };

    private static string? MaskIfPresent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Length <= 8)
            return "***";
        return value[..4] + "..." + value[^4..];
    }

    private static string? SanitizeEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        // Redact query-string tokens that might leak credentials
        try
        {
            var uri = new Uri(endpoint);
            return $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath}";
        }
        catch
        {
            return endpoint;
        }
    }

    private static FailureDiagnosis CategorizeFailure(
        Exception ex,
        GatewayStartupContext? startup,
        string environmentName)
    {
        var message = ex.Message;
        var config = startup?.Config;

        // Pattern 1: Unsupported / unavailable provider
        if (Contains(message, "Configured provider '") && Contains(message, "is not available"))
            return new FailureDiagnosis
            {
                Category = "unsupported_provider",
                Summary = $"The configured LLM provider '{config?.Llm.Provider}' is not registered as a built-in provider and no dynamic plugin provides it.",
                RootCause = FirstExceptionMessage(ex) ?? message,
                SuggestedFixes = new List<string>
                {
                    $"Provider '{config?.Llm.Provider}' is not in the built-in list. " +
                    "If it offers an OpenAI-compatible API, set OpenClaw:Llm:Provider to 'openai-compatible' and OpenClaw:Llm:Endpoint to the API base URL.",
                    "Check that MODEL_PROVIDER_KEY is set and valid.",
                    "Run with --doctor to perform preflight checks.",
                    "Run with --quickstart for an interactive bootstrap."
                }
            };

        // Pattern 2: Auth token required
        if (Contains(message, "OPENCLAW_AUTH_TOKEN must be set"))
            return new FailureDiagnosis
            {
                Category = "missing_auth_token",
                Summary = "Non-loopback bind requires an authentication token.",
                RootCause = "BindAddress is set to a non-loopback address and OPENCLAW_AUTH_TOKEN is not configured.",
                SuggestedFixes = new List<string>
                {
                    "Set OPENCLAW_AUTH_TOKEN to a strong random value.",
                    "Or set OpenClaw:BindAddress to 127.0.0.1 for local-only use."
                }
            };

        // Pattern 3: Missing API key
        if (Contains(message, "MODEL_PROVIDER_KEY must be set"))
            return new FailureDiagnosis
            {
                Category = "missing_api_key",
                Summary = $"The configured provider '{config?.Llm.Provider}' requires an API key.",
                RootCause = "MODEL_PROVIDER_KEY environment variable is not set.",
                SuggestedFixes = new List<string>
                {
                    "Set MODEL_PROVIDER_KEY before launching the gateway.",
                    "For OpenAI, you can also set OPENAI_API_KEY and the gateway will pick it up during recovery."
                }
            };

        // Pattern 4: Missing endpoint
        if (Contains(message, "Endpoint must be set for provider") ||
            Contains(message, "MODEL_PROVIDER_ENDPOINT must be set"))
            return new FailureDiagnosis
            {
                Category = "missing_endpoint",
                Summary = $"The provider '{config?.Llm.Provider}' requires an explicit endpoint.",
                RootCause = "MODEL_PROVIDER_ENDPOINT or OpenClaw:Llm:Endpoint is not configured.",
                SuggestedFixes = new List<string>
                {
                    "Set MODEL_PROVIDER_ENDPOINT to the provider's API base URL.",
                    "Or set OpenClaw:Llm:Endpoint in your configuration file."
                }
            };

        // Pattern 5: Port in use
        if (IsPortInUse(ex))
            return new FailureDiagnosis
            {
                Category = "port_in_use",
                Summary = $"Port {config?.Port ?? 18789} is already in use by another process.",
                RootCause = WalkExceptionChain(ex).Select(e => e.Message).FirstOrDefault(m => m.Contains("address")),
                SuggestedFixes = new List<string>
                {
                    $"Stop the process using port {config?.Port} or change OpenClaw:Port."
                }
            };

        // Pattern 6: Storage / file system
        if (Contains(message, "Memory.StoragePath") || IsStorageAccessError(ex))
            return new FailureDiagnosis
            {
                Category = "storage_inaccessible",
                Summary = "The configured memory storage path is not writable or accessible.",
                RootCause = FirstExceptionMessage(ex) ?? message,
                SuggestedFixes = new List<string>
                {
                    "Set OpenClaw:Memory:StoragePath to a writable directory.",
                    "Ensure the directory exists or can be created by the current user."
                }
            };

        // Fallthrough: generic / unknown
        return new FailureDiagnosis
        {
            Category = "unexpected_startup_failure",
            Summary = "The gateway exited before it finished starting. See the exception details above.",
            RootCause = FirstExceptionMessage(ex) ?? message,
            SuggestedFixes = new List<string>
            {
                "Review the full exception chain in this diagnostics report.",
                "Run with --doctor to check for misconfigurations.",
                "Check that all required environment variables are set.",
                "Verify the configuration file is valid JSON with correct keys."
            }
        };
    }

    private static bool Contains(string value, string fragment)
        => value.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static bool IsPortInUse(Exception ex)
    {
        var baseException = ex.GetBaseException();
        return string.Equals(baseException.GetType().Name, "AddressInUseException", StringComparison.Ordinal) ||
               baseException.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
               baseException.Message.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStorageAccessError(Exception ex)
    {
        var baseMessage = ex.GetBaseException().Message;
        return baseMessage.Contains("Read-only file system", StringComparison.OrdinalIgnoreCase) ||
               baseMessage.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
               baseMessage.Contains("Access to the path", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstExceptionMessage(Exception ex)
    {
        var first = WalkExceptionChain(ex).FirstOrDefault();
        return first != default ? first.Message : null;
    }

    private static List<(string Type, string Message)> WalkExceptionChain(Exception ex)
    {
        var chain = new List<(string Type, string Message)>();
        var current = ex;
        while (current is not null && chain.Count < 20)
        {
            chain.Add((current.GetType().Name, current.Message));
            current = current.InnerException;
        }
        return chain;
    }
}
