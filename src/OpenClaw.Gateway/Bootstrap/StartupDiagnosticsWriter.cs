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
        var message = ex.Message;

        return new StartupDiagnosticsReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            GatewayVersion = typeof(StartupDiagnosticsWriter).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion,
            Exception = BuildExceptionDetail(ex),
            Configuration = new ConfigurationSummary
            {
                Environment = environmentName,
                BindAddress = config?.BindAddress,
                Port = config?.Port,
                Provider = config?.Llm.Provider,
                Model = config?.Llm.Model,
                Endpoint = config?.Llm.Endpoint,
                ApiKeyConfigured = !string.IsNullOrWhiteSpace(config?.Llm.ApiKey),
                AuthTokenConfigured = !string.IsNullOrWhiteSpace(config?.AuthToken),
                MemoryProvider = config?.Memory?.Provider,
                MemoryStoragePath = config?.Memory?.StoragePath,
                WorkspacePath = config?.WorkspacePath,
                IsNonLoopbackBind = startup?.IsNonLoopbackBind,
                RuntimeMode = config?.RuntimeMode,
                DynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported
        },
            Environment = new EnvironmentSummary
            {
                OSDescription = RuntimeInformation.OSDescription,
                OSArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                RuntimeVersion = Environment.Version.ToString(),
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessorCount = Environment.ProcessorCount,
                CurrentDirectory = Environment.CurrentDirectory,
                WorkingSetMB = Math.Round(Process.GetCurrentProcess().WorkingSet64 / 1048576.0, 1),
                EnvironmentVariables = GetSafeEnvironmentVariables(),
                CommandLine = Environment.CommandLine,
            },
            CategorizedDiagnosis = BuildDiagnosis(ex, config, message)
        };
    }

    private static ExceptionDetail BuildExceptionDetail(Exception ex)
    {
        var detail = new ExceptionDetail
        {
            Type = ex.GetType().FullName ?? ex.GetType().Name,
            Message = ex.Message,
            StackTrace = ex.StackTrace,
            HResult = ex.HResult,
        };

        if (ex.InnerException != null)
        {
            var inner = new List<InnerExceptionDetail>();
            CollectInnerExceptions(ex.InnerException, inner, 1);
            detail = detail with { InnerException = inner };
        }

        return detail;
    }

    private static void CollectInnerExceptions(Exception? ex, List<InnerExceptionDetail> list, int depth)
    {
        while (ex != null)
        {
            list.Add(new InnerExceptionDetail
            {
                Type = ex.GetType().FullName ?? ex.GetType().Name,
                Message = ex.Message,
                StackTrace = ex.StackTrace,
                Depth = depth
            });
            ex = ex.InnerException;
            depth++;
        }
    }

    private static Dictionary<string, string?> GetSafeEnvironmentVariables()
    {
        var vars = new Dictionary<string, string?>();
        var safeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ASPNETCORE_ENVIRONMENT",
            "ASPNETCORE_URLS",
            "DOTNET_ENVIRONMENT",
            "OPENCLAW_AUTH_TOKEN",
            "OPENCLAW_BIND_ADDRESS",
            "OPENCLAW_PORT",
            "MODEL_PROVIDER",
            "MODEL_PROVIDER_ENDPOINT",
            "MODEL_PROVIDER_MODEL",
            "DOCKER_HOST",
            "HTTP_PROXY",
            "HTTPS_PROXY",
            "NO_PROXY",
            "PATH",
            "HOME",
            "USERPROFILE",
            "COMPUTERNAME"
        };

        foreach (var key in safeKeys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            // Redact API keys — only show whether they're set, not the value.
            if (key.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("OPENCLAW_AUTH_TOKEN", StringComparison.OrdinalIgnoreCase))
            {
                vars[key] = string.IsNullOrWhiteSpace(value) ? null : "(set)";
            }
            else
            {
                vars[key] = value;
            }
        }

        return vars;
    }

    private static FailureDiagnosis BuildDiagnosis(
        Exception ex,
        GatewayConfig? config,
        string message)
    {
        if (message.Contains("Unsupported LLM provider") || message.Contains("is not available"))
        {
            return new FailureDiagnosis
            {
                Category = "unsupported_provider",
                Summary = $"Provider '{config?.Llm.Provider}' is not a built-in provider. DeepSeek, Zhipu, Moonshot and other OpenAI-compatible APIs can be used via the 'openai-compatible' provider with an explicit endpoint.",
                RootCause = FirstExceptionMessage(ex) ?? message,
                SuggestedFixes = new List<string>
                {
                    $"Provider '{config?.Llm.Provider}' is not in the built-in list. " +
                    "For OpenAI-compatible APIs (DeepSeek / Groq / Together / etc.), " +
                    "set OpenClaw:Llm:Provider to 'openai-compatible' (or the specific provider alias if supported) " +
                    "and ensure OpenClaw:Llm:Endpoint is configured.",
                    "Supported built-in providers: openai, anthropic, claude, gemini, google, ollama, embedded, azure-openai, openai-compatible, aperture, groq, together, lmstudio, deepseek, amazon-bedrock."
                }
            };
        }

        if (message.Contains("MODEL_PROVIDER_KEY must be set") ||
            message.Contains("API key was not configured") ||
            message.Contains("ApiKey must be set"))
        {
            return new FailureDiagnosis
            {
                Category = "missing_api_key",
                Summary = "No API key was configured for the selected LLM provider.",
                RootCause = FirstExceptionMessage(ex) ?? message,
                SuggestedFixes = new List<string>
                {
                    "Set the MODEL_PROVIDER_KEY environment variable before launch.",
                    "Alternatively, configure OpenClaw:Llm:ApiKey in your settings file."
                }
            };
        }

        if (message.Contains("Endpoint must be set") ||
            message.Contains("endpoint is missing"))
        {
            return new FailureDiagnosis
            {
                Category = "missing_endpoint",
                Summary = "The selected LLM provider requires an endpoint URL that was not configured.",
                RootCause = FirstExceptionMessage(ex) ?? message,
                SuggestedFixes = new List<string>
                {
                    "Set MODEL_PROVIDER_ENDPOINT to the provider base URL.",
                    "Alternatively, configure OpenClaw:Llm:Endpoint in your settings file."
                }
            };
        }

        if (IsPortInUse(ex))
        {
            var port = config?.Port ?? 18789;
            return new FailureDiagnosis
            {
                Category = "port_in_use",
                Summary = $"Port {port} is already in use by another process.",
                RootCause = string.Join("; ", WalkExceptionChain(ex).Select(e => e.Message)),
                SuggestedFixes = new List<string>
                {
                    $"Change OpenClaw:Port to an available port (current: {port}).",
                    $"Set OPENCLAW_PORT={port + 1} to try the next available port.",
                    "Use 'netstat -ano | findstr :<port>' (Windows) or 'lsof -i :<port>' (macOS/Linux) to identify the process holding the port."
                }
            };
        }

        // Unexpected / fallthrough
        return new FailureDiagnosis
        {
            Category = "unexpected_startup_failure",
            Summary = "The gateway exited before it finished starting. See the exception details above.",
            RootCause = FirstExceptionMessage(ex) ?? message,
            SuggestedFixes = new List<string>
            {
                "Review the exception details in this diagnostics file.",
                "Check that all required environment variables are set.",
                "Verify the configuration file syntax is correct.",
                "Run with '--doctor' for interactive troubleshooting."
            }
        };
    }

    private static string? FirstExceptionMessage(Exception ex)
    {
        var first = WalkExceptionChain(ex).FirstOrDefault();
        return first != default ? first.Message : null;
    }

    private static List<(string Type, string Message)> WalkExceptionChain(Exception ex)
    {
        var chain = new List<(string, string)>();
        var current = ex;
        while (current != null)
        {
            chain.Add((current.GetType().Name, current.Message));
            current = current.InnerException;
        }
        return chain;
    }

    private static bool IsPortInUse(Exception ex)
    {
        var message = ex.Message;
        var baseMsg = ex.GetBaseException().Message;
        return message.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("port is already in use", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("address in use", StringComparison.OrdinalIgnoreCase) ||
               baseMsg.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
               baseMsg.Contains("address in use", StringComparison.OrdinalIgnoreCase);
    }
}
