namespace TurboPilot.Mediation;

public sealed record LocalModelDescriptor(
	string Alias, string Name, string Device, int? SizeMb, bool Cached, long? ContextTokens, string? License)
{
	// Drop-downs show the alias; the closed box displays the selected item's text.
	public override string ToString() => Alias;
}

public sealed record LocalRuntimeProgress(string Message, double? Percent = null);
public sealed record LocalCompletion(string Text, long? InputTokens = null, long? OutputTokens = null);

public interface ILocalModelRuntime : IAsyncDisposable
{
	Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default);
	Task PrepareAccelerationAsync(IProgress<LocalRuntimeProgress>? progress = null, CancellationToken cancellationToken = default);
	Task DownloadAsync(string alias, IProgress<LocalRuntimeProgress>? progress = null, CancellationToken cancellationToken = default);
	Task LoadAsync(string alias, CancellationToken cancellationToken = default);
	Task<LocalCompletion> GenerateAsync(string system, string input, int maxOutputTokens, CancellationToken cancellationToken = default);
	Task UnloadAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalModelUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
