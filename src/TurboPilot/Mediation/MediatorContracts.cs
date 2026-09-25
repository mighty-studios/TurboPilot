namespace TurboPilot.Mediation;

public sealed record MediatorDiagnostic(string Purpose, string Input, string Output, string? Note = null);

public sealed record MediationEntry
{
	public required long Sequence { get; init; }
	public required string Role { get; init; }
	public required string Content { get; init; }
	public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
	public bool Interrupted { get; init; }
}

public sealed record MediationState
{
	public required string SessionId { get; init; }
	public string? Workspace { get; init; }
	public List<MediationEntry> Entries { get; init; } = [];
}

// Observes the conversation for a future on-device purpose. It never changes what is sent or shown.
public interface IMediatorSession : IAsyncDisposable
{
	event Action<string>? StatusChanged;
	event Action<string>? NoticeReceived;
	event Action<MediatorDiagnostic>? DiagnosticReceived;
	bool Enabled { get; }
	bool DebugRaw { get; }
	bool HasHistory { get; }
	string Status { get; }
	void Capture(string role, string content, bool interrupted = false);
	Task ConfigureAsync(MediatorSettings settings, CancellationToken cancellationToken = default);
}
