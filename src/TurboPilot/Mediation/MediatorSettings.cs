namespace TurboPilot.Mediation;

public sealed record MediatorSettings
{
	public const string DefaultModelAlias = "phi-3.5-mini";

	public bool Enabled { get; init; }
	public string ModelAlias { get; init; } = DefaultModelAlias;
	public bool RewordPrompts { get; init; } = true;
	public bool BeautifyOutput { get; init; } = true;
	public bool MaintainSummary { get; init; } = true;
	public bool MonitorOutput { get; init; } = true;
	public bool DebugRaw { get; init; }
	public int CallTimeoutSeconds { get; init; } = 60;
	// Prompts below this locally counted size are forwarded unchanged: the saving is too small to
	// justify the local delay or the risk of losing a requirement.
	public int MinimumRewriteTokens { get; init; } = 100;

	public void Validate()
	{
		if (string.IsNullOrWhiteSpace(ModelAlias))
			throw new InvalidOperationException("Select a local model.");
		if (CallTimeoutSeconds is < 1 or > 300)
			throw new InvalidOperationException("The local call timeout must be between 1 and 300 seconds.");
		if (MinimumRewriteTokens is < 0 or > 10_000)
			throw new InvalidOperationException("The minimum reword size must be between 0 and 10000 tokens.");
	}
}

public enum MediatorOperation
{
	RewritePrompt,
	BeautifyOutput,
	UpdateSummary,
	MonitorOutput,
}
