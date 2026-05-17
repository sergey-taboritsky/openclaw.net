namespace OpenClaw.Companion.Models;

public enum ChatRole : byte
{
    System = 0,
    User = 1,
    Assistant = 2
}

public sealed record ChatMessage
{
    public required ChatRole Role { get; init; }
    public required string Text { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public string RoleLabel => Role switch
    {
        ChatRole.User => "You",
        ChatRole.Assistant => "OpenClaw",
        _ => "System"
    };

    public bool IsUser => Role == ChatRole.User;
    public bool IsAssistant => Role == ChatRole.Assistant;
    public bool IsSystem => Role == ChatRole.System && !IsToolEvent && !IsError;
    public bool IsToolEvent => Role == ChatRole.System &&
        (Text.StartsWith("Agent invoked tool:", StringComparison.OrdinalIgnoreCase) ||
         Text.Contains("tool approval", StringComparison.OrdinalIgnoreCase) ||
         Text.Contains("requires operator approval", StringComparison.OrdinalIgnoreCase));

    public bool IsError => Role == ChatRole.System &&
        (Text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
         Text.Contains(" failed", StringComparison.OrdinalIgnoreCase) ||
         Text.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
         Text.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
         Text.Contains("denied", StringComparison.OrdinalIgnoreCase));

    public bool IsStreamingPlaceholder => Role == ChatRole.Assistant && string.IsNullOrWhiteSpace(Text);
}
