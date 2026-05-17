using OpenClaw.Companion.Services;
using OpenClaw.Companion.ViewModels;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CompanionRuntimeConsoleTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }

    [Theory]
    [InlineData("home", 0)]
    [InlineData("sessions", 4)]
    [InlineData("runtime events", 8)]
    [InlineData("payment lab", 14)]
    public void NavigateToSectionCommand_SelectsRuntimeConsoleSection(string section, int expectedIndex)
    {
        var viewModel = CreateViewModel();

        viewModel.NavigateToSectionCommand.Execute(section);

        Assert.Equal(expectedIndex, viewModel.SelectedSectionIndex);
    }

    [Fact]
    public async Task PaymentMutationCommands_RequireConfirmationBeforeClientUse()
    {
        var viewModel = CreateViewModel();
        viewModel.VirtualCardMerchantName = "Example Merchant";
        viewModel.VirtualCardAmountMinor = "1200";
        viewModel.VirtualCardCurrency = "USD";

        await viewModel.IssueVirtualCardCommand.ExecuteAsync(null);

        Assert.Equal("", viewModel.PaymentResultText);
        Assert.Equal("Payment Lab not loaded.", viewModel.PaymentsStatus);
    }

    [Fact]
    public async Task AutomationDeleteCommand_RequiresConfirmationBeforeClientUse()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedAutomation = new AutomationRow
        {
            AutomationId = "daily-summary",
            Name = "Daily Summary",
            Schedule = "@daily",
            Delivery = "cron",
            State = "enabled",
            Tags = ""
        };

        await viewModel.DeleteSelectedAutomationCommand.ExecuteAsync(null);

        Assert.Equal("Automations not loaded.", viewModel.AutomationsStatus);
    }

    [Fact]
    public void RuntimeEventRow_FormatsMetadataPreview()
    {
        var row = RuntimeEventRow.FromEntry(new OpenClaw.Core.Models.RuntimeEventEntry
        {
            Id = "evt_1",
            TimestampUtc = DateTimeOffset.Parse("2026-05-16T12:00:00Z"),
            Component = "approvals",
            Action = "queued",
            Severity = "info",
            SessionId = "sess_1",
            ChannelId = "webchat",
            SenderId = "user_1",
            Summary = "Approval queued.",
            Metadata = new Dictionary<string, string> { ["tool"] = "openai-http" }
        });

        Assert.Equal("approvals", row.Component);
        Assert.Equal("queued", row.Action);
        Assert.Contains("openai-http", row.RawJson, StringComparison.Ordinal);
    }

    private MainWindowViewModel CreateViewModel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openclaw-companion-runtime-console-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return new MainWindowViewModel(new SettingsStore(dir), new GatewayWebSocketClient());
    }
}
