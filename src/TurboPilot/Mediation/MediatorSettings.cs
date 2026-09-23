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

	public void Validate()
	{
		if (string.IsNullOrWhiteSpace(ModelAlias))
			throw new InvalidOperationException("Select a local model.");
		if (CallTimeoutSeconds is < 1 or > 300)
			throw new InvalidOperationException("The local call timeout must be between 1 and 300 seconds.");
	}
}

public enum MediatorOperation
{
	RewritePrompt,
	BeautifyOutput,
	UpdateSummary,
	MonitorOutput,
}
