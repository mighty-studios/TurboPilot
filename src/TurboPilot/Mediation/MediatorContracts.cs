namespace TurboPilot.Mediation;

public sealed record MediatorWarning(string Kind, string Message, string Quote);
public sealed record MediatorDiagnostic(MediatorOperation Operation, string Input, string Output, string? Note = null);
public sealed record PromptPreparation(string Prompt, int OriginalTokens, int PreparedTokens, bool Rewritten);
public sealed record OutputPreparation(string Markdown, IReadOnlyList<MediatorWarning> Warnings);
public sealed record FormatLink(string Text, string Path, bool Image = false);
public sealed record FormatSuggestions(IReadOnlyList<FormatLink> Links, IReadOnlyList<string> Headings);

public sealed record MediationEntry
{
	public required long Sequence { get; init; }
	public required string Role { get; init; }
	public required string Content { get; init; }
	public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
	public bool Interrupted { get; init; }
	public string? RenderedContent { get; set; }
}

public sealed record MediationState
{
	public required string SessionId { get; init; }
	public string? Workspace { get; init; }
	public List<MediationEntry> Entries { get; init; } = [];
	public string Summary { get; set; } = "";
	public long SummaryThrough { get; set; }
	public DateTimeOffset? SummaryUpdatedAt { get; set; }
}

public interface IMediatorSession : IAsyncDisposable
{
	event Action<string>? StatusChanged;
	event Action<string>? NoticeReceived;
	event Action<MediatorDiagnostic>? DiagnosticReceived;
	bool Enabled { get; }
	bool IsBusy { get; }
	bool DebugRaw { get; }
	bool PreparesOutput { get; }
	string Status { get; }
	Task ConfigureAsync(MediatorSettings settings, CancellationToken cancellationToken = default);
	Task RecordAsync(string role, string content, bool interrupted = false, CancellationToken cancellationToken = default);
	Task<PromptPreparation> PreparePromptAsync(string prompt, IReadOnlyList<string>? attachments = null, CancellationToken cancellationToken = default);
	Task<OutputPreparation> PrepareOutputAsync(string output, CancellationToken cancellationToken = default);
	Task<string?> GetSummaryAsync(CancellationToken cancellationToken = default);
	string? CurrentSummary { get; }
}
