using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TurboPilot.Mediation;

public sealed class MediatorService : IMediatorSession
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		RespectNullableAnnotations = true,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	};
	private readonly ILocalModelRuntime _runtime;
	private readonly MediatorConfiguration _configuration;
	private readonly MediationStore _store;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly CancellationTokenSource _lifetime = new();
	private readonly object _disposeLock = new();
	private Task? _disposeTask;
	private MediatorSettings _settings;
	private MediationState _state;
	private int _failures;
	private bool _offline;
	private bool _disabled;
	private int _contextTokens = 4096;
	private bool _catalogRead;
	private string _status = "Mediator off";
	private volatile bool _busy;

	public event Action<string>? StatusChanged;
	public event Action<string>? NoticeReceived;
	public event Action<MediatorDiagnostic>? DiagnosticReceived;
	public bool Enabled => _settings.Enabled;
	public bool IsBusy => _busy;
	public bool DebugRaw => _settings.DebugRaw;
	public bool PreparesOutput => Enabled && _settings.BeautifyOutput;
	public string Status => _status;
	public string? CurrentSummary => _state.SummaryThrough == _state.Entries.Count && !string.IsNullOrWhiteSpace(_state.Summary)
		? _state.Summary : null;

	public MediatorService(string sessionId, string? workspace, MediatorSettings settings,
		ILocalModelRuntime runtime, MediatorConfiguration configuration, MediationStore? store = null)
	{
		settings.Validate();
		_settings = settings;
		_runtime = runtime;
		_configuration = configuration;
		_store = store ?? new MediationStore(sessionId, workspace);
		_state = _store.Load(sessionId, workspace);
		_status = settings.Enabled ? "Mediator ready" : "Mediator off";
	}

	public Task ConfigureAsync(MediatorSettings settings, CancellationToken cancellationToken = default) =>
		WithGateAsync(async token =>
		{
			settings.Validate();
			await _runtime.UnloadAsync(token).ConfigureAwait(false);
			_settings = settings;
			_failures = 0;
			_offline = false;
			_disabled = false;
			_catalogRead = false;
			SetStatus(settings.Enabled ? "Mediator ready" : "Mediator off");
			return true;
		}, cancellationToken);

	public Task RecordAsync(string role, string content, bool interrupted = false, CancellationToken cancellationToken = default) =>
		WithGateAsync(async token =>
		{
			_state.Entries.Add(new MediationEntry
			{
				Sequence = _state.Entries.Count + 1, Role = role, Content = content, Interrupted = interrupted,
			});
			SaveState();
			if (_settings.Enabled && _settings.MaintainSummary)
				await UpdateSummaryCoreAsync(token).ConfigureAwait(false);
			return true;
		}, cancellationToken);

	public Task<PromptPreparation> PreparePromptAsync(string prompt, IReadOnlyList<string>? attachments = null,
		CancellationToken cancellationToken = default) => WithGateAsync(async token =>
	{
		var originalTokens = MediationText.CountTokens(prompt);
		var original = new PromptPreparation(prompt, originalTokens, originalTokens, false);
		if (!CanRun || !_settings.RewordPrompts || string.IsNullOrWhiteSpace(prompt))
			return original;
		var input = JsonSerializer.Serialize(new
		{
			prompt, protectedText = MediationText.GetProtectedText(prompt),
			summary = CurrentSummary ?? "", attachments = attachments ?? [],
		});
		var response = await RunAsync(MediatorOperation.RewritePrompt, input, 1200, raw =>
		{
			var result = Parse<RewriteResponse>(raw);
			if (string.IsNullOrWhiteSpace(result.Prompt))
				throw new InvalidDataException("The rewritten prompt was empty.");
			return result;
		}, token).ConfigureAwait(false);
		if (response is null)
			return original;
		var rewritten = response.Prompt.Trim();
		var preparedTokens = MediationText.CountTokens(rewritten);
		if (!response.MeaningPreserved || preparedTokens >= originalTokens || !MediationText.PreservesLiterals(prompt, rewritten))
		{
			DiagnosticReceived?.Invoke(new(MediatorOperation.RewritePrompt, input, rewritten, "Original retained: reduction or literal-preservation checks did not pass."));
			return original;
		}
		return new PromptPreparation(rewritten, originalTokens, preparedTokens, true);
	}, cancellationToken);

	public Task<OutputPreparation> PrepareOutputAsync(string output, CancellationToken cancellationToken = default) =>
		WithGateAsync(async token =>
		{
			if (!_settings.Enabled)
				return new OutputPreparation(output, []);
			var warnings = _settings.MonitorOutput ? MediationText.FindRepetition(output).ToList() : [];
			var rendered = output;
			if (_settings.BeautifyOutput && CanRun)
			{
				var input = JsonSerializer.Serialize(new { response = output });
				var response = await RunAsync(MediatorOperation.BeautifyOutput, input, 1200, raw =>
				{
					var result = Parse<FormattingResponse>(raw);
					if (result.Links.Any(link => link is null || link.Text is null || link.Path is null)
						|| result.Headings.Any(heading => heading is null))
						throw new InvalidDataException("The formatting response contained an invalid suggestion.");
					return result;
				}, token).ConfigureAwait(false);
				if (response is not null)
					rendered = OutputFormatter.Apply(output, new(response.Links, response.Headings), _state.Workspace,
						message => NoticeReceived?.Invoke(message));
			}
			if (_settings.MonitorOutput && CanRun)
			{
				var prompt = _state.Entries.LastOrDefault(entry => entry.Role == "user")?.Content ?? "";
				var input = JsonSerializer.Serialize(new { prompt, response = output, summary = CurrentSummary ?? "" });
				var response = await RunAsync(MediatorOperation.MonitorOutput, input, 700, raw =>
				{
					var result = Parse<MonitoringResponse>(raw);
					if (result.Warnings.Any(warning => warning is null || warning.Kind is not ("repetition" or "contradiction" or "unsupported-claim")
						|| string.IsNullOrWhiteSpace(warning.Quote) || !output.Contains(warning.Quote, StringComparison.Ordinal)
						|| string.IsNullOrWhiteSpace(warning.Message)))
						throw new InvalidDataException("A monitoring warning lacked a supported kind or an exact response excerpt.");
					return result;
				}, token).ConfigureAwait(false);
				if (response is not null)
					warnings.AddRange(response.Warnings.Take(3).Select(warning => warning with
					{
						Message = warning.Message.Length > 240 ? warning.Message[..240] : warning.Message,
						Quote = warning.Quote.Length > 180 ? warning.Quote[..180] : warning.Quote,
					}));
			}
			return new OutputPreparation(rendered, warnings.DistinctBy(warning => warning.Quote).Take(3).ToList());
		}, cancellationToken);

	public Task<string?> GetSummaryAsync(CancellationToken cancellationToken = default) => WithGateAsync(async token =>
	{
		if (!_settings.Enabled || !_settings.MaintainSummary)
			return null;
		await UpdateSummaryCoreAsync(token).ConfigureAwait(false);
		return CurrentSummary;
	}, cancellationToken);

	private async Task UpdateSummaryCoreAsync(CancellationToken cancellationToken)
	{
		if (!CanRun || _state.SummaryThrough >= _state.Entries.Count)
			return;
		var summary = _state.Summary;
		var through = _state.SummaryThrough;
		foreach (var entry in _state.Entries.Where(entry => entry.Sequence > through).ToArray())
		{
			foreach (var chunk in MediationText.SplitForContext(entry.Content, Math.Max(256, Math.Min(1200, _contextTokens / 3))))
			{
				var input = JsonSerializer.Serialize(new
				{
					previousSummary = summary,
					entries = new[] { new { entry.Role, Content = chunk, entry.Interrupted } },
				});
				var result = await RunAsync(MediatorOperation.UpdateSummary, input, 900, raw =>
				{
					var response = Parse<SummaryResponse>(raw);
					if (string.IsNullOrWhiteSpace(response.Summary) || MediationText.CountTokens(response.Summary) > 900)
						throw new InvalidDataException("The summary was empty or exceeded its one-page limit.");
					return response;
				}, cancellationToken).ConfigureAwait(false);
				if (result is null) return;
				summary = result.Summary.Trim();
			}
			_state.Summary = summary;
			_state.SummaryThrough = entry.Sequence;
			_state.SummaryUpdatedAt = DateTimeOffset.UtcNow;
			SaveState();
		}
	}

	private bool CanRun => _settings.Enabled && !_disabled && !_offline;

	private async Task<T?> RunAsync<T>(MediatorOperation operation, string input, int maxOutputTokens,
		Func<string, T> parse, CancellationToken cancellationToken) where T : class
	{
		if (!CanRun) return null;
		using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		budget.CancelAfter(TimeSpan.FromSeconds(_settings.CallTimeoutSeconds));
		_busy = true;
		SetStatus("Mediating..");
		try
		{
			_configuration.EnsureDocuments();
			var instructions = _configuration.ReadInstructions(operation);
			if (!_catalogRead)
			{
				var catalog = await _runtime.ListModelsAsync(budget.Token).ConfigureAwait(false);
				var selected = catalog.FirstOrDefault(model => model.Alias.Equals(_settings.ModelAlias, StringComparison.OrdinalIgnoreCase))
					?? throw new LocalModelUnavailableException($"Local model '{_settings.ModelAlias}' is unavailable.");
				_contextTokens = (int)Math.Clamp(selected.ContextTokens ?? 4096, 2048, int.MaxValue);
				_catalogRead = true;
			}
			if (MediationText.CountTokens(instructions) + MediationText.CountTokens(input) + maxOutputTokens + 128 > _contextTokens)
				throw new InvalidDataException("The local task exceeds the model's context budget; original content was retained.");
			await _runtime.LoadAsync(_settings.ModelAlias, budget.Token).ConfigureAwait(false);
			var response = await _runtime.GenerateAsync(instructions, input, maxOutputTokens, budget.Token).ConfigureAwait(false);
			budget.Token.ThrowIfCancellationRequested();
			DiagnosticReceived?.Invoke(new(operation, input, response.Text));
			var result = parse(response.Text);
			_failures = 0;
			SetStatus("Mediator ready");
			return result;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			SetStatus("Mediator ready");
			throw;
		}
		catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
		{
			if (ex is LocalModelUnavailableException) _offline = true;
			else if (++_failures >= 3) _disabled = true;
			var message = budget.IsCancellationRequested ? "The local call timed out." : ex.Message;
			SetStatus(_offline ? "Mediator offline" : _disabled ? "Mediator disabled" : "Mediator error");
			NoticeReceived?.Invoke($"{message} Original chat content is preserved."
				+ (_disabled || _offline ? " Re-enable in Mediator settings or start a new session to retry." : ""));
			DiagnosticReceived?.Invoke(new(operation, input, "", message));
			return null;
		}
		finally
		{
			_busy = false;
			StatusChanged?.Invoke(_status);
		}
	}

	private void SaveState()
	{
		try { _store.Save(_state); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			NoticeReceived?.Invoke("Cannot save the Mediator worklog; it remains in memory: " + ex.Message);
		}
	}

	private static T Parse<T>(string json) where T : class
	{
		var text = json.Trim();
		if (text.StartsWith("```json", StringComparison.Ordinal))
		{
			var end = text.IndexOf("```", 7, StringComparison.Ordinal);
			if (end < 0)
				throw new InvalidDataException("The local response contained an unfinished JSON block.");
			text = text[7..end].Trim();
		}
		var bytes = Encoding.UTF8.GetBytes(text);
		var reader = new Utf8JsonReader(bytes);
		using var document = JsonDocument.ParseValue(ref reader);
		var remainder = Encoding.UTF8.GetString(bytes.AsSpan((int)reader.BytesConsumed)).Trim();
		if (remainder.StartsWith('{') || remainder.StartsWith('['))
			throw new InvalidDataException("The local response contained multiple conflicting results.");
		// Some local backends append prose despite requesting structured output.
		return document.RootElement.Deserialize<T>(JsonOptions)
			?? throw new InvalidDataException("The local response contained no result.");
	}

	private void SetStatus(string status)
	{
		_status = status;
		StatusChanged?.Invoke(status);
	}

	private async Task<T> WithGateAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
		await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
		try { return await operation(linked.Token).ConfigureAwait(false); }
		finally { _gate.Release(); }
	}

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
			SaveState();
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
			await _runtime.UnloadAsync(timeout.Token).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
			_lifetime.Dispose();
		}
	}

	private sealed record RewriteResponse
	{
		public required string Prompt { get; init; }
		public required bool MeaningPreserved { get; init; }
	}
	private sealed record SummaryResponse
	{
		public required string Summary { get; init; }
	}
	private sealed record FormattingResponse
	{
		public required List<FormatLink> Links { get; init; }
		public required List<string> Headings { get; init; }
	}
	private sealed record MonitoringResponse
	{
		public required List<MediatorWarning> Warnings { get; init; }
	}
}
