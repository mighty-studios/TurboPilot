using System.IO;

namespace TurboPilot.Mediation;

// The Mediator is parked for a future purpose. While enabled it records prompts, answers,
// replies, and tool results in a local worklog, and it can run the on-device model on request.
// It never changes what is sent to the session model or shown to the user.
public sealed class MediatorService : IMediatorSession
{
	// Catalogs can advertise 128K windows, but on-device evaluation of that much text cannot
	// finish within a call timeout, so larger requests are skipped instead of timing out.
	private const int PracticalContextTokens = 4096;
	private readonly ILocalModelRuntime _runtime;
	private readonly MediationStore _store;
	private readonly MediationState _state;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly CancellationTokenSource _lifetime = new();
	private readonly object _disposeLock = new();
	private readonly object _stateLock = new();
	private Task? _disposeTask;
	private MediatorSettings _settings;
	private int _failures;
	private bool _offline;
	private bool _disabled;
	private int _contextTokens = PracticalContextTokens;
	private bool _catalogRead;
	private string _status;
	private volatile bool _busy;

	public event Action<string>? StatusChanged;
	public event Action<string>? NoticeReceived;
	public event Action<MediatorDiagnostic>? DiagnosticReceived;
	public bool Enabled => _settings.Enabled;
	public bool IsBusy => _busy;
	public bool DebugRaw => _settings.DebugRaw;
	public bool HasHistory { get { lock (_stateLock) return _state.Entries.Count > 0; } }
	public string Status => _status;

	public MediatorService(string sessionId, string? workspace, MediatorSettings settings,
		ILocalModelRuntime runtime, MediationStore? store = null)
	{
		settings.Validate();
		_settings = settings;
		_runtime = runtime;
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

	public void Capture(string role, string content, bool interrupted = false)
	{
		if (!_settings.Enabled)
			return;
		lock (_stateLock)
		{
			_state.Entries.Add(new MediationEntry
			{
				Sequence = _state.Entries.Count + 1, Role = role, Content = content, Interrupted = interrupted,
			});
			SaveState();
		}
	}

	// Runs the on-device model once. Returns null when the Mediator is off, the request does not fit
	// the practical budget, or the call fails. Failures are reported, and three in a row disable
	// inference until the settings are applied again.
	public Task<string?> GenerateAsync(string purpose, string instructions, string input, int maxOutputTokens,
		CancellationToken cancellationToken = default) => WithGateAsync(async token =>
	{
		if (!CanRun) return null;
		using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
		budget.CancelAfter(TimeSpan.FromSeconds(_settings.CallTimeoutSeconds));
		_busy = true;
		SetStatus("Mediating..");
		try
		{
			if (!_catalogRead)
			{
				var catalog = await _runtime.ListModelsAsync(budget.Token).ConfigureAwait(false);
				var selected = catalog.FirstOrDefault(model => model.Alias.Equals(_settings.ModelAlias, StringComparison.OrdinalIgnoreCase))
					?? throw new LocalModelUnavailableException($"Local model '{_settings.ModelAlias}' is unavailable.");
				_contextTokens = (int)Math.Clamp(selected.ContextTokens ?? PracticalContextTokens, 2048, PracticalContextTokens);
				_catalogRead = true;
			}
			if (MediationText.CountTokens(instructions) + MediationText.CountTokens(input) + maxOutputTokens + 128 > _contextTokens)
			{
				// Oversized content is a property of the request rather than a model fault, so it never counts toward disabling.
				DiagnosticReceived?.Invoke(new(purpose, MediationText.Excerpt(input, 2000), "", "Skipped: the request exceeds the local processing budget."));
				SetStatus("Mediator ready");
				return null;
			}
			await _runtime.LoadAsync(_settings.ModelAlias, budget.Token).ConfigureAwait(false);
			var response = await _runtime.GenerateAsync(instructions, input, maxOutputTokens, budget.Token).ConfigureAwait(false);
			budget.Token.ThrowIfCancellationRequested();
			DiagnosticReceived?.Invoke(new(purpose, input, response.Text));
			_failures = 0;
			SetStatus("Mediator ready");
			return response.Text;
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			SetStatus("Mediator ready");
			throw;
		}
		catch (Exception ex) when (!token.IsCancellationRequested)
		{
			if (ex is LocalModelUnavailableException) _offline = true;
			else if (++_failures >= 3) _disabled = true;
			var message = budget.IsCancellationRequested ? "The local call timed out." : ex.Message;
			SetStatus(_offline ? "Mediator offline" : _disabled ? "Mediator disabled" : "Mediator error");
			NoticeReceived?.Invoke(message + (_disabled || _offline ? " Re-enable in Mediator settings or start a new session to retry." : ""));
			DiagnosticReceived?.Invoke(new(purpose, input, "", message));
			return null;
		}
		finally
		{
			_busy = false;
			StatusChanged?.Invoke(_status);
		}
	}, cancellationToken);

	private bool CanRun => _settings.Enabled && !_disabled && !_offline;

	private void SaveState()
	{
		try { lock (_stateLock) _store.Save(_state); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			NoticeReceived?.Invoke("Cannot save the Mediator worklog; it remains in memory: " + ex.Message);
		}
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
			if (_settings.Enabled)
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
}
