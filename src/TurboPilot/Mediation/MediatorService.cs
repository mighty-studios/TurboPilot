using System.IO;
using System.Text;
using System.Text.Encodings.Web;
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
	// Local requests are never embedded in markup, so apostrophes and non-ASCII text stay readable
	// instead of reaching a small model as \u0027 escapes.
	private static readonly JsonSerializerOptions InputOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
	// Catalogs can advertise 128K windows, but on-device evaluation of that much text cannot finish
	// within a call timeout. Larger inputs are trimmed or skipped instead of timing out.
	private const int PracticalContextTokens = 4096;
	private const int EvidenceItems = 3;
	private const int EvidenceChars = 600;
	private const int PromptExcerptChars = 2000;
	private const int SummaryChunkTokens = 400;
	private readonly ILocalModelRuntime _runtime;
	private readonly MediatorConfiguration _configuration;
	private readonly MediationStore _store;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly CancellationTokenSource _lifetime = new();
	private readonly object _disposeLock = new();
	private readonly object _stateLock = new();
	private Task? _disposeTask;
	private MediatorSettings _settings;
	private MediationState _state;
	private int _failures;
	private bool _offline;
	private bool _disabled;
	private int _contextTokens = PracticalContextTokens;
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
	public bool SummaryEnabled => Enabled && _settings.MaintainSummary;
	public bool HasHistory { get { lock (_stateLock) return _state.Entries.Count > 0; } }
	public string Status => _status;
	public string? CurrentSummary
	{
		get
		{
			lock (_stateLock)
				return _state.SummaryThrough == _state.Entries.Count && !string.IsNullOrWhiteSpace(_state.Summary)
					? _state.Summary : null;
		}
	}

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

	public long Capture(string role, string content, bool interrupted = false)
	{
		lock (_stateLock)
		{
			var sequence = _state.Entries.Count + 1;
			_state.Entries.Add(new MediationEntry
			{
				Sequence = sequence, Role = role, Content = content, Interrupted = interrupted,
			});
			SaveState();
			return sequence;
		}
	}

	public Task RecordAsync(string role, string content, bool interrupted = false, CancellationToken cancellationToken = default)
	{
		Capture(role, content, interrupted);
		return WithGateAsync(async token =>
		{
			if (_settings.Enabled && _settings.MaintainSummary)
				await UpdateSummaryCoreAsync(token).ConfigureAwait(false);
			return true;
		}, cancellationToken);
	}

	public bool ShouldRewrite(string prompt) => CanRewrite(prompt, MediationText.CountTokens(prompt));

	private bool CanRewrite(string prompt, int tokens) =>
		CanRun && _settings.RewordPrompts && !string.IsNullOrWhiteSpace(prompt) && tokens >= _settings.MinimumRewriteTokens;

	public async Task<PromptPreparation> PreparePromptAsync(string prompt, IReadOnlyList<string>? attachments = null,
		CancellationToken cancellationToken = default)
	{
		var originalTokens = MediationText.CountTokens(prompt);
		var original = new PromptPreparation(prompt, originalTokens, originalTokens, false);
		if (!CanRewrite(prompt, originalTokens))
			return original;
		return await WithGateAsync(async token =>
		{
			// Only the prompt and attachment names are supplied: background context and folder names
			// invite a small model to add details the user never wrote.
			var input = JsonSerializer.Serialize(new
			{
				prompt, protectedText = MediationText.GetProtectedText(prompt),
				attachments = (attachments ?? []).Select(Path.GetFileName).ToArray(),
			}, InputOptions);
			var response = await RunAsync(MediatorOperation.RewritePrompt, FirstFitting(input), 1200, raw =>
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
		}, cancellationToken).ConfigureAwait(false);
	}

	public Task<OutputPreparation> PrepareOutputAsync(string output, long? responseSequence = null,
		CancellationToken cancellationToken = default) => WithGateAsync(async token =>
		{
			if (!_settings.Enabled)
				return new OutputPreparation(output, []);
			var warnings = _settings.MonitorOutput ? MediationText.FindRepetition(output).ToList() : [];
			var suggestions = new FormatSuggestions([], []);
			// File links need no model, so it is consulted only when a plain standalone line could become a heading.
			if (_settings.BeautifyOutput && CanRun && OutputFormatter.HasHeadingCandidate(output))
			{
				var input = JsonSerializer.Serialize(new { response = output }, InputOptions);
				var response = await RunAsync(MediatorOperation.BeautifyOutput, FirstFitting(input), 1200, raw =>
				{
					var result = Parse<FormattingResponse>(raw);
					if (result.Links.Any(link => link is null || link.Text is null || link.Path is null)
						|| result.Headings.Any(heading => heading is null))
						throw new InvalidDataException("The formatting response contained an invalid suggestion.");
					return result;
				}, token).ConfigureAwait(false);
				if (response is not null)
					suggestions = new(response.Links, response.Headings);
			}
			// Validated file links do not depend on the local model, so they survive its failures.
			var rendered = _settings.BeautifyOutput ? Format(output, suggestions) : output;
			if (_settings.MonitorOutput && CanRun)
			{
				var response = await RunAsync(MediatorOperation.MonitorOutput, FirstFitting(MonitorInputs(output, responseSequence)), 700, raw =>
				{
					var result = Parse<MonitoringResponse>(raw);
					if (result.Warnings.Any(warning => warning is null))
						throw new InvalidDataException("The monitoring response contained an empty warning.");
					return result;
				}, token).ConfigureAwait(false);
				if (response is not null)
				{
					// Ungrounded warnings are discarded rather than corrected: a correction rarely grounds them,
					// and an imprecise answer is not a local failure.
					var grounded = response.Warnings.Where(warning => warning.Kind is "repetition" or "contradiction" or "unsupported-claim"
						&& !string.IsNullOrWhiteSpace(warning.Message) && !string.IsNullOrWhiteSpace(warning.Quote)
						&& output.Contains(warning.Quote, StringComparison.Ordinal)).ToList();
					if (grounded.Count < response.Warnings.Count)
						DiagnosticReceived?.Invoke(new(MediatorOperation.MonitorOutput, "", "",
							$"Discarded {response.Warnings.Count - grounded.Count} warning(s) without a supported kind and an exact response excerpt."));
					warnings.AddRange(grounded.Take(3).Select(warning => warning with
					{
						Message = warning.Message.Length > 240 ? warning.Message[..240] : warning.Message,
						Quote = warning.Quote.Length > 180 ? warning.Quote[..180] : warning.Quote,
					}));
				}
			}
			return new OutputPreparation(rendered, warnings.DistinctBy(warning => warning.Quote).Take(3).ToList());
		}, cancellationToken);

	public OutputPreparation FormatOutput(string output)
	{
		var settings = _settings;
		if (!settings.Enabled)
			return new OutputPreparation(output, []);
		return new OutputPreparation(
			settings.BeautifyOutput ? Format(output, new FormatSuggestions([], [])) : output,
			settings.MonitorOutput ? MediationText.FindRepetition(output) : []);
	}

	private string Format(string output, FormatSuggestions suggestions) =>
		OutputFormatter.Apply(output, suggestions, _state.Workspace, message => NoticeReceived?.Invoke(message));

	// Candidates in order of preference: the summary and then the tool evidence are dropped
	// when the response itself needs the room. Context comes from the exchange that produced the
	// response, even when a newer prompt was recorded while this preparation waited in the queue.
	private string[] MonitorInputs(string output, long? responseSequence)
	{
		string prompt, summary;
		string[] evidence;
		lock (_stateLock)
		{
			var prior = _state.Entries.Take(responseSequence is { } sequence ? (int)Math.Clamp(sequence - 1, 0, _state.Entries.Count)
				: _state.Entries.Count).ToList();
			var request = prior.FindLastIndex(entry => entry.Role == "user");
			prompt = request >= 0 ? prior[request].Content : "";
			summary = _state.Summary;
			evidence = prior.Skip(request + 1).Where(entry => entry.Role == "tool").TakeLast(EvidenceItems)
				.Select(entry => MediationText.Excerpt(entry.Content, EvidenceChars)).ToArray();
		}
		prompt = MediationText.Excerpt(prompt, PromptExcerptChars);
		string Serialize(string background, string[] items) => JsonSerializer.Serialize(
			new { prompt, response = output, summary = background, evidence = items }, InputOptions);
		return new[] { Serialize(summary, evidence), Serialize("", evidence), Serialize("", []) }.Distinct().ToArray();
	}

	public Task<string?> GetSummaryAsync(CancellationToken cancellationToken = default) => WithGateAsync(async token =>
	{
		if (!_settings.Enabled || !_settings.MaintainSummary)
			return null;
		await UpdateSummaryCoreAsync(token).ConfigureAwait(false);
		return CurrentSummary;
	}, cancellationToken);

	private async Task UpdateSummaryCoreAsync(CancellationToken cancellationToken)
	{
		string summary;
		MediationEntry[] pending;
		lock (_stateLock)
		{
			if (!CanRun || _state.SummaryThrough >= _state.Entries.Count)
				return;
			summary = _state.Summary;
			pending = _state.Entries.Where(entry => entry.Sequence > _state.SummaryThrough).ToArray();
		}
		// Tool output stays in the worklog as monitoring evidence; assistant messages report its outcome.
		// Entries are cut into small chunks and packed by serialized size, so every request fits and
		// pending entries share as few calls as possible: each rewrite of the summary can drop a detail.
		var items = new List<SummaryItem>();
		foreach (var entry in pending.Where(entry => entry.Role != "tool" && !string.IsNullOrWhiteSpace(entry.Content)))
		{
			var chunks = MediationText.SplitForContext(entry.Content, SummaryChunkTokens);
			for (var index = 0; index < chunks.Count; index++)
				items.Add(new(entry.Sequence, index == chunks.Count - 1, entry.Role, chunks[index], entry.Interrupted));
		}
		var start = 0;
		while (start < items.Count)
		{
			var used = 0;
			var unfit = false;
			var previous = summary;
			var result = await RunAsync(MediatorOperation.UpdateSummary, room =>
			{
				used = FitSummaryBatch(previous, items, start, room, out var input);
				unfit = used == 0;
				return input;
			}, 900, raw =>
			{
				var response = Parse<SummaryResponse>(raw);
				if (string.IsNullOrWhiteSpace(response.Summary) || MediationText.CountTokens(response.Summary) > 900)
					throw new InvalidDataException("The summary was empty or exceeded its one-page limit.");
				return response;
			}, cancellationToken).ConfigureAwait(false);
			if (result is null)
			{
				if (unfit)
					NoticeReceived?.Invoke("The summary cannot be updated: the Mediator instructions leave too little room for conversation text.");
				return;
			}
			summary = result.Summary.Trim();
			start += used;
			// Progress is durable only where a batch ended on an entry boundary.
			if (items[start - 1].EndsEntry)
				CommitSummary(summary, start < items.Count ? items[start].Sequence - 1 : pending[^1].Sequence);
		}
		if (items.Count == 0)
			CommitSummary(summary, pending[^1].Sequence);
	}

	// Packs as many chunks as fit: per-chunk estimates first, then an exact count of the whole request.
	private static int FitSummaryBatch(string summary, List<SummaryItem> items, int start, int room, out string? input)
	{
		string Serialize(int count) => JsonSerializer.Serialize(new
		{
			previousSummary = summary, entries = SummaryEntries(items, start, count),
		}, InputOptions);
		var estimate = MediationText.CountTokens(Serialize(0));
		var count = 0;
		while (start + count < items.Count)
		{
			var cost = MediationText.CountTokens(JsonSerializer.Serialize(SummaryEntries(items, start + count, 1)[0], InputOptions)) + 1;
			if (estimate + cost > room)
				break;
			estimate += cost;
			count++;
		}
		for (; count > 0; count--)
		{
			input = Serialize(count);
			if (MediationText.CountTokens(input) <= room)
				return count;
		}
		input = null;
		return 0;
	}

	// Chunks of one entry that share a request are rejoined, so an entry appears split only where a request ends.
	private static List<object> SummaryEntries(List<SummaryItem> items, int start, int count)
	{
		var entries = new List<object>();
		for (var index = start; index < start + count;)
		{
			var first = items[index];
			var content = new StringBuilder();
			for (; index < start + count && items[index].Sequence == first.Sequence; index++)
				content.Append(items[index].Content);
			entries.Add(first.Interrupted
				? new { role = first.Role, content = content.ToString(), interrupted = true }
				: (object)new { role = first.Role, content = content.ToString() });
		}
		return entries;
	}

	private void CommitSummary(string summary, long through)
	{
		lock (_stateLock)
		{
			_state.Summary = summary;
			_state.SummaryThrough = through;
			_state.SummaryUpdatedAt = DateTimeOffset.UtcNow;
			SaveState();
		}
	}

	private bool CanRun => _settings.Enabled && !_disabled && !_offline;

	// Picks the first candidate that fits the room left by the instructions and the output reserve.
	private static Func<int, string?> FirstFitting(params string[] candidates) =>
		room => candidates.FirstOrDefault(candidate => MediationText.CountTokens(candidate) <= room);

	private async Task<T?> RunAsync<T>(MediatorOperation operation, Func<int, string?> fitInput, int maxOutputTokens,
		Func<string, T> parse, CancellationToken cancellationToken) where T : class
	{
		if (!CanRun) return null;
		using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		budget.CancelAfter(TimeSpan.FromSeconds(_settings.CallTimeoutSeconds));
		_busy = true;
		SetStatus("Mediating..");
		var input = "";
		try
		{
			_configuration.EnsureDocuments();
			var instructions = _configuration.ReadInstructions(operation);
			if (!_catalogRead)
			{
				var catalog = await _runtime.ListModelsAsync(budget.Token).ConfigureAwait(false);
				var selected = catalog.FirstOrDefault(model => model.Alias.Equals(_settings.ModelAlias, StringComparison.OrdinalIgnoreCase))
					?? throw new LocalModelUnavailableException($"Local model '{_settings.ModelAlias}' is unavailable.");
				_contextTokens = (int)Math.Clamp(selected.ContextTokens ?? PracticalContextTokens, 2048, PracticalContextTokens);
				_catalogRead = true;
			}
			if (fitInput(_contextTokens - MediationText.CountTokens(instructions) - maxOutputTokens - 128) is not { } fitting)
			{
				// Oversized content is a property of the input rather than a model fault, so it never counts toward disabling.
				DiagnosticReceived?.Invoke(new(operation, "", "",
					"Skipped: the content exceeds the local processing budget; original content was retained."));
				SetStatus("Mediator ready");
				return null;
			}
			input = fitting;
			await _runtime.LoadAsync(_settings.ModelAlias, budget.Token).ConfigureAwait(false);
			var response = await _runtime.GenerateAsync(instructions, input, maxOutputTokens, budget.Token).ConfigureAwait(false);
			budget.Token.ThrowIfCancellationRequested();
			DiagnosticReceived?.Invoke(new(operation, input, response.Text));
			T result;
			try { result = parse(response.Text); }
			catch (Exception ex) when (ex is JsonException or InvalidDataException)
			{
				var repair = JsonSerializer.Serialize(new
				{
					request = "Correct the invalid response to match the task's exact JSON schema. Preserve source meaning. Return all required keys and no extra keys.",
					originalInput = JsonSerializer.Deserialize<JsonElement>(input),
					invalidResponse = response.Text,
					validationError = ex.Message,
				}, InputOptions);
				if (MediationText.CountTokens(instructions) + MediationText.CountTokens(repair) + maxOutputTokens + 128 > _contextTokens)
					throw new InvalidDataException("The local response was invalid and cannot be corrected within the context budget.", ex);
				DiagnosticReceived?.Invoke(new(operation, input, response.Text, "Requesting one schema correction within the original timeout."));
				response = await _runtime.GenerateAsync(instructions, repair, maxOutputTokens, budget.Token).ConfigureAwait(false);
				budget.Token.ThrowIfCancellationRequested();
				DiagnosticReceived?.Invoke(new(operation, repair, response.Text));
				result = parse(response.Text);
			}
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
		try { lock (_stateLock) _store.Save(_state); }
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

	private sealed record SummaryItem(long Sequence, bool EndsEntry, string Role, string Content, bool Interrupted);
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
