using System.IO;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;

namespace TurboPilot.Mediation;

public sealed class FoundryModelRuntime : ILocalModelRuntime
{
	private static readonly SemaphoreSlim Initialization = new(1, 1);
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly CancellationTokenSource _lifetime = new();
	private readonly string _dataDirectory;
	private IModel? _model;
	private OpenAIChatClient? _client;
	private bool _ownsManager;
	private Task? _disposeTask;
	private readonly object _disposeLock = new();

	public FoundryModelRuntime(string? dataDirectory = null)
	{
		_dataDirectory = dataDirectory ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboPilot", "foundry");
	}

	public async Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
		await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
		try
		{
			var manager = await GetManagerAsync(linked.Token).ConfigureAwait(false);
			var catalog = await manager.GetCatalogAsync(linked.Token).ConfigureAwait(false);
			var cached = (await catalog.GetCachedModelsAsync(linked.Token).ConfigureAwait(false))
				.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
			var models = await catalog.ListModelsAsync(linked.Token).ConfigureAwait(false);
			return models.Where(IsTextModel).Select(model => new LocalModelDescriptor(
				model.Alias, model.Info.DisplayName ?? model.Alias,
				model.Info.Runtime?.DeviceType.ToString() ?? "Automatic",
				model.Info.FileSizeMb, cached.Contains(model.Id), model.Info.ContextLength, model.Info.License))
				.OrderBy(model => model.Alias, StringComparer.OrdinalIgnoreCase).ToList();
		}
		finally { _gate.Release(); }
	}

	public async Task PrepareAccelerationAsync(IProgress<LocalRuntimeProgress>? progress = null, CancellationToken cancellationToken = default)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
		await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
		try
		{
			var manager = await GetManagerAsync(linked.Token).ConfigureAwait(false);
			var providers = manager.DiscoverEps();
			if (providers.Length == 0)
			{
				progress?.Report(new("No additional acceleration providers were found. CPU execution remains available."));
				return;
			}
			var result = await manager.DownloadAndRegisterEpsAsync(
				(name, percent) => progress?.Report(new("Preparing " + name, percent)), linked.Token).ConfigureAwait(false);
			if (!result.Success)
				throw new InvalidOperationException("Hardware acceleration preparation failed: " + result.Status);
			progress?.Report(new("Hardware acceleration is ready.", 100));
		}
		finally { _gate.Release(); }
	}

	public async Task DownloadAsync(string alias, IProgress<LocalRuntimeProgress>? progress = null, CancellationToken cancellationToken = default)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
		await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
		try
		{
			var model = await FindModelAsync(alias, linked.Token).ConfigureAwait(false);
			progress?.Report(new("Downloading " + alias, 0));
			await model.DownloadAsync(percent => progress?.Report(new("Downloading " + alias, percent)),
				linked.Token).ConfigureAwait(false);
			progress?.Report(new("Model downloaded.", 100));
		}
		finally { _gate.Release(); }
	}

	public async Task LoadAsync(string alias, CancellationToken cancellationToken = default)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
		await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
		try
		{
			var model = await FindModelAsync(alias, linked.Token).ConfigureAwait(false);
			if (_model?.Id == model.Id && _client is not null && await model.IsLoadedAsync(linked.Token).ConfigureAwait(false))
				return;
			if (!await model.IsCachedAsync(linked.Token).ConfigureAwait(false))
				throw new LocalModelUnavailableException($"Download '{alias}' in Mediator settings before enabling it.");
			await UnloadCoreAsync(linked.Token).ConfigureAwait(false);
			_model = model;
			await model.LoadAsync(linked.Token).ConfigureAwait(false);
			_client = await model.GetChatClientAsync(linked.Token).ConfigureAwait(false);
			_client.Settings.Temperature = 0;
			_client.Settings.RandomSeed = 1;
			_client.Settings.N = 1;
		}
		catch (OperationCanceledException) { throw; }
		catch (Exception ex) when (linked.IsCancellationRequested)
		{
			throw new OperationCanceledException("Local model loading was canceled.", ex, linked.Token);
		}
		catch (Exception ex) when (ex is not LocalModelUnavailableException)
		{
			throw new LocalModelUnavailableException($"Unable to load local model '{alias}': {ex.Message}", ex);
		}
		finally { _gate.Release(); }
	}

	public async Task<LocalCompletion> GenerateAsync(string system, string input, int maxOutputTokens, CancellationToken cancellationToken = default)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
		await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
		try
		{
			var client = _client ?? throw new LocalModelUnavailableException("No local model is loaded.");
			client.Settings.MaxTokens = maxOutputTokens;
			var response = await client.CompleteChatAsync(
			[
				new ChatMessage { Role = "system", Content = system },
				new ChatMessage { Role = "user", Content = input },
			], linked.Token).ConfigureAwait(false);
			linked.Token.ThrowIfCancellationRequested();
			if (response.Error is not null)
				throw new InvalidOperationException("Local generation failed: " + response.Error.Message);
			var choice = response.Choices?.FirstOrDefault();
			var text = choice?.Message?.Content;
			if (string.IsNullOrWhiteSpace(text))
				throw new InvalidDataException("The local model returned an empty response.");
			if (choice?.FinishReason == "length")
				throw new InvalidDataException("The local response exceeded its output limit.");
			return new LocalCompletion(text, response.Usage?.PromptTokens, response.Usage?.CompletionTokens);
		}
		catch (Exception ex) when (linked.IsCancellationRequested && ex is not OperationCanceledException)
		{
			throw new OperationCanceledException("Local generation was canceled.", ex, linked.Token);
		}
		finally { _gate.Release(); }
	}

	public async Task UnloadAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try { await UnloadCoreAsync(cancellationToken).ConfigureAwait(false); }
		finally { _gate.Release(); }
	}

	private async Task UnloadCoreAsync(CancellationToken cancellationToken)
	{
		if (_model is null) return;
		await _model.UnloadAsync(cancellationToken).ConfigureAwait(false);
		_model = null;
		_client = null;
	}

	private async Task<IModel> FindModelAsync(string alias, CancellationToken cancellationToken)
	{
		var manager = await GetManagerAsync(cancellationToken).ConfigureAwait(false);
		var catalog = await manager.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
		var model = await catalog.GetModelAsync(alias, cancellationToken).ConfigureAwait(false);
		if (model is null || !IsTextModel(model))
			throw new LocalModelUnavailableException($"Local text model '{alias}' is unavailable for the current execution providers.");
		return await catalog.GetModelVariantAsync(model.Id, cancellationToken).ConfigureAwait(false) ?? model;
	}

	private async Task<FoundryLocalManager> GetManagerAsync(CancellationToken cancellationToken)
	{
		await Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (!FoundryLocalManager.IsInitialized)
			{
				await FoundryLocalManager.CreateAsync(new Configuration
				{
					AppName = "TurboPilot",
					AppDataDir = _dataDirectory,
					ModelCacheDir = Path.Combine(_dataDirectory, "models"),
					LogsDir = Path.Combine(_dataDirectory, "logs"),
				}, NullLogger.Instance, cancellationToken).ConfigureAwait(false);
				_ownsManager = true;
			}
			return FoundryLocalManager.Instance;
		}
		finally { Initialization.Release(); }
	}

	private static bool IsTextModel(IModel model) =>
		model.Info.Task?.ToLowerInvariant() is "chat-completion" or "chat" or "text-generation" or "conversational"
		|| (string.IsNullOrEmpty(model.Info.Task) && model.Info.PromptTemplate is not null);

	public ValueTask DisposeAsync()
	{
		lock (_disposeLock)
			return new ValueTask(_disposeTask ??= DisposeCoreAsync());
	}

	private async Task DisposeCoreAsync()
	{
		_lifetime.Cancel();
		await _gate.WaitAsync().ConfigureAwait(false);
		try
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
			await UnloadCoreAsync(timeout.Token).ConfigureAwait(false);
		}
		finally
		{
			try
			{
				if (_ownsManager && FoundryLocalManager.IsInitialized)
					FoundryLocalManager.Instance.Dispose();
			}
			finally
			{
				_gate.Release();
				_lifetime.Dispose();
			}
		}
	}
}
