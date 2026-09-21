using System.IO;
using GitHub.Copilot;
using TurboPilot.Permissions;

namespace TurboPilot.Ai;

/// <summary>
/// Everything the main window gathered in the Settings dialog that a
/// Copilot SDK session needs at creation time.
/// </summary>
public sealed class ChatSessionOptions
{
	/// <summary>Workspace folder the session runs in; null when none was picked.</summary>
	public string? WorkspaceFolder { get; init; }

	/// <summary>Model id selected in the Settings dialog; empty lets the runtime choose.</summary>
	public string Model { get; init; } = "";

	/// <summary>Reasoning effort; null when the model or service offers none.</summary>
	public string? ReasoningEffort { get; init; }

	/// <summary>Session mode: Standard, Plan or Autopilot.</summary>
	public string Mode { get; init; } = "Standard";

	/// <summary>
	/// Context window of the selected model in tokens, as advertised by the
	/// service at query time. 0 when unknown; the service then falls back to
	/// the runtime's own catalog.
	/// </summary>
	public int ContextWindowTokens { get; init; }

	/// <summary>True when the session talks to a BYOK OpenAI-compatible server.</summary>
	public bool UseByok { get; init; }

	/// <summary>Composed BYOK base URL. Unused for the CLI provider.</summary>
	public string ByokEndpoint { get; init; } = "";

	/// <summary>BYOK API key. Unused for the CLI provider.</summary>
	public string ByokApiKey { get; init; } = "";
}

/// <summary>
/// Owns one live Copilot SDK session and turns its event stream into the
/// simple callbacks the main window needs: streaming text, turn lifecycle,
/// usage numbers, and questions that must be answered in chat.
///
/// The session is created with streaming on, so assistant text arrives as
/// AssistantMessageDeltaEvent pieces. A turn ends when the runtime reports
/// the session idle. Permission requests consult the Permissions store: a
/// pre-approved operation (or any request in Autopilot mode) runs silently;
/// anything else becomes a chat question the user answers with their next
/// message. Model questions (the SDK's user-input requests) work the same
/// way: there are no pop-up dialogs in this app.
///
/// Events fire on the SDK's worker threads. Subscribers that touch the UI
/// must marshal to the dispatcher themselves.
/// </summary>
public sealed class ChatService : IAsyncDisposable
{
	private readonly object _pendingLock = new();

	private CopilotClient? _client;
	private CopilotSession? _session;
	private ChatSessionOptions _options = new();

	// The question currently awaiting the user's next chat message. Both
	// model questions and permission prompts share this slot: only one can
	// be outstanding at a time because the runtime blocks the turn on it.
	private TaskCompletionSource<string>? _pendingAnswer;

	// Deltas seen since the last idle. The final assistant message repeats
	// the streamed text, so it is only used when nothing streamed.
	private int _turnDeltas;

	private long _sessionAicNano;

	public ChatService()
	{
	}

	// -- Callbacks -------------------------------------------------------------

	/// <summary>Streaming assistant text, verbatim, as each piece arrives.</summary>
	public event Action<string>? DeltaReceived;

	/// <summary>Short meta lines for the transcript (tool activity, notices).</summary>
	public event Action<string>? StatusReceived;

	/// <summary>An error reported by the runtime.</summary>
	public event Action<string>? ErrorReceived;

	/// <summary>
	/// A question the user must answer in chat: the model's own question or
	/// a permission prompt, already formatted as display text.
	/// </summary>
	public event Action<string>? QuestionReceived;

	/// <summary>The current turn has ended; the session is idle again.</summary>
	public event Action? TurnIdle;

	/// <summary>Context or credit usage changed; re-read the usage properties.</summary>
	public event Action? UsageChanged;

	// -- Mediator hooks ---------------------------------------------------------
	//
	// Pass-through seams for the Mediator: one filter on the way to the
	// model, one on the way back. They stay identity functions until the
	// Mediator lands.

	/// <summary>Transforms the user's prompt before it is sent. Null sends it verbatim.</summary>
	public Func<string, string>? UserPromptFilter { get; set; }

	/// <summary>Transforms assistant text before it reaches the transcript. Null passes it through.</summary>
	public Func<string, string>? ModelOutputFilter { get; set; }

	// -- State ------------------------------------------------------------------

	/// <summary>Id of the live session, or null before start.</summary>
	public string? SessionId => _session?.SessionId;

	/// <summary>True while a prompt is in flight.</summary>
	public bool IsWorking { get; private set; }

	/// <summary>True when the next chat message should answer a pending question.</summary>
	public bool HasPendingQuestion
	{
		get
		{
			lock (_pendingLock)
				return _pendingAnswer is not null;
		}
	}

	/// <summary>Context window of the active model in tokens; 0 when unknown.</summary>
	public int ContextWindowTokens { get; private set; }

	/// <summary>Prompt tokens used by the most recent user-driven call.</summary>
	public int ContextUsedTokens { get; private set; }

	/// <summary>Copilot credits spent this session, accumulated across all calls.</summary>
	public double AicUsed => _sessionAicNano / 1_000_000_000.0;

	// -- Lifecycle ----------------------------------------------------------------

	/// <summary>
	/// Starts the bundled CLI and creates the session described by
	/// <paramref name="options"/>. Throws to the caller on failure; a failed
	/// start leaves no half-built session behind.
	/// </summary>
	public async Task StartAsync(ChatSessionOptions options, CancellationToken cancellationToken = default)
	{
		_options = options;
		ContextWindowTokens = options.ContextWindowTokens;

		_client = new CopilotClient(new CopilotClientOptions
		{
			WorkingDirectory = string.IsNullOrWhiteSpace(options.WorkspaceFolder)
				? null
				: options.WorkspaceFolder,
		});

		await _client.StartAsync(cancellationToken);

		// The dialog may not have carried a window (older saved settings, or
		// a model list that reported none). Ask the running runtime once.
		if (ContextWindowTokens <= 0)
			ContextWindowTokens = await LookupContextWindowAsync(options.Model, cancellationToken);

		try
		{
			_session = await _client.CreateSessionAsync(new SessionConfig
			{
				SessionId = GenerateSessionId(options.WorkspaceFolder),
				Model = string.IsNullOrWhiteSpace(options.Model) ? null : options.Model,
				ReasoningEffort = string.IsNullOrWhiteSpace(options.ReasoningEffort) ? null : options.ReasoningEffort,
				Streaming = true,
				SystemMessage = BuildSystemMessage(),
				OnPermissionRequest = HandlePermissionRequestAsync,
				OnUserInputRequest = HandleUserInputRequestAsync,
				Provider = BuildProviderConfig(),
			}, cancellationToken);
		}
		catch
		{
			await _client.DisposeAsync();
			_client = null;
			throw;
		}

		_session.On<SessionEvent>(HandleSessionEvent);
	}

	/// <summary>
	/// Sends one prompt (plus any file attachments) to the session and marks
	/// the turn working. Returns as soon as the runtime accepts the message;
	/// the reply arrives through the events.
	/// </summary>
	public async Task SendAsync(string prompt, IReadOnlyList<string>? attachmentPaths = null, CancellationToken cancellationToken = default)
	{
		if (_session is null)
			throw new InvalidOperationException("No active session.");

		var text = UserPromptFilter?.Invoke(prompt) ?? prompt;

		var message = new MessageOptions { Prompt = text };
		if (attachmentPaths is { Count: > 0 })
		{
			message.Attachments = attachmentPaths.Select(BuildAttachment).ToList();
		}

		IsWorking = true;
		try
		{
			await _session.SendAsync(message, cancellationToken);
		}
		catch
		{
			IsWorking = false;
			throw;
		}
	}

	/// <summary>
	/// Aborts the turn in flight, if any, and releases any pending chat
	/// question so the runtime's handler unblocks.
	/// </summary>
	public async Task AbortAsync()
	{
		ReleasePendingQuestion(canceled: true);

		var session = _session;
		if (session is null) return;
		try
		{
			await session.AbortAsync();
		}
		catch
		{
			// Best effort: an abort on an already-idle session is a no-op
			// that the runtime may report as an error.
		}
	}

	public async ValueTask DisposeAsync()
	{
		ReleasePendingQuestion(canceled: true);

		var session = _session;
		_session = null;
		if (session is not null)
		{
			try { await session.DisposeAsync(); } catch { }
		}

		var client = _client;
		_client = null;
		if (client is not null)
		{
			try { await client.DisposeAsync(); } catch { }
		}
	}

	// -- Answering questions ------------------------------------------------------

	/// <summary>
	/// Routes the user's next chat message to the pending question. Returns
	/// false when nothing is waiting, meaning the message is a new prompt.
	/// </summary>
	public bool TryAnswerPending(string answer)
	{
		TaskCompletionSource<string>? tcs;
		lock (_pendingLock)
			tcs = _pendingAnswer;

		if (tcs is null) return false;
		tcs.TrySetResult(answer);
		return true;
	}

	private void ReleasePendingQuestion(bool canceled)
	{
		TaskCompletionSource<string>? tcs;
		lock (_pendingLock)
		{
			tcs = _pendingAnswer;
			_pendingAnswer = null;
		}

		if (tcs is null) return;
		if (canceled) tcs.TrySetCanceled();
	}

	/// <summary>
	/// Publishes a question and waits for the user's next chat message.
	/// Throws OperationCanceledException when the turn is aborted first.
	/// </summary>
	private async Task<string> AwaitAnswerAsync(string questionText)
	{
		var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_pendingLock)
			_pendingAnswer = tcs;

		QuestionReceived?.Invoke(questionText);

		try
		{
			return await tcs.Task;
		}
		finally
		{
			lock (_pendingLock)
			{
				if (ReferenceEquals(_pendingAnswer, tcs))
					_pendingAnswer = null;
			}
		}
	}

	private async Task<UserInputResponse> HandleUserInputRequestAsync(
		UserInputRequest request, UserInputInvocation invocation)
	{
		var text = FormatQuestion(request.Question ?? "", request.Choices, request.AllowFreeform ?? true);
		string answer;
		try
		{
			answer = await AwaitAnswerAsync(text);
		}
		catch (OperationCanceledException)
		{
			answer = "(user cancelled)";
		}

		return new UserInputResponse { Answer = answer, WasFreeform = true };
	}

#pragma warning disable GHCP001 // PermissionDecision is an evaluation-stage API
	private async Task<GitHub.Copilot.Rpc.PermissionDecision> HandlePermissionRequestAsync(
		PermissionRequest request, PermissionInvocation invocation)
	{
		var kind = request.Kind ?? "";

		// Autopilot runs everything; otherwise the pre-approved operations
		// from the Permissions dialog run silently.
		if (_options.Mode == "Autopilot"
			|| PermissionService.IsOperationAllowed(kind, _options.WorkspaceFolder))
		{
			return await PermissionHandler.ApproveAll(request, invocation);
		}

		string answer;
		try
		{
			answer = await AwaitAnswerAsync(FormatPermissionPrompt(request, kind));
		}
		catch (OperationCanceledException)
		{
			return GitHub.Copilot.Rpc.PermissionDecision.Reject(null);
		}

		if (IsYes(answer))
			return await PermissionHandler.ApproveAll(request, invocation);

		return GitHub.Copilot.Rpc.PermissionDecision.Reject(null);
	}
#pragma warning restore GHCP001

	private static string FormatQuestion(string question, IList<string>? choices, bool allowFreeform)
	{
		var text = "Question: " + question;
		if (choices is { Count: > 0 })
			text += "\r\n" + string.Join("  |  ", choices.Select((c, i) => $"{i + 1}. {c}"));
		if (allowFreeform)
			text += "\r\n(Type your answer and press Send.)";
		return text;
	}

	private static string FormatPermissionPrompt(PermissionRequest request, string kind)
	{
		var detail = request switch
		{
			PermissionRequestShell shell => shell.FullCommandText,
			PermissionRequestWrite write => write.FileName,
			PermissionRequestRead read => read.Path,
			PermissionRequestUrl url => url.Url,
			_ => null,
		};
		var label = string.IsNullOrWhiteSpace(detail) ? kind : $"{kind}: {detail}";
		return $"Permission requested - {label}\r\nReply yes to allow or no to deny.";
	}

	private static bool IsYes(string answer)
	{
		var a = answer.Trim().TrimEnd('!', '.');
		return a.Equals("yes", StringComparison.OrdinalIgnoreCase)
			|| a.Equals("y", StringComparison.OrdinalIgnoreCase)
			|| a.Equals("allow", StringComparison.OrdinalIgnoreCase)
			|| a.Equals("approve", StringComparison.OrdinalIgnoreCase);
	}

	// -- Session events -----------------------------------------------------------

	private void HandleSessionEvent(SessionEvent evt)
	{
		switch (evt)
		{
			case AssistantMessageDeltaEvent delta:
				_turnDeltas++;
				var piece = delta.Data.DeltaContent ?? "";
				if (piece.Length > 0)
					DeltaReceived?.Invoke(ModelOutputFilter?.Invoke(piece) ?? piece);
				break;

			case AssistantMessageEvent msg:
				// With streaming on the deltas already carried the text; the
				// final event only matters when nothing streamed.
				if (_turnDeltas == 0 && !string.IsNullOrEmpty(msg.Data.Content))
					DeltaReceived?.Invoke(ModelOutputFilter?.Invoke(msg.Data.Content) ?? msg.Data.Content);
				break;

			case ToolExecutionStartEvent tool:
				StatusReceived?.Invoke($"[tool] {tool.Data.ToolName ?? "tool"}");
				break;

			case SessionErrorEvent error:
				ErrorReceived?.Invoke(error.Data.Message ?? "Unknown error");
				break;

			case AssistantUsageEvent usage:
				HandleUsage(usage.Data);
				break;

			case SessionIdleEvent:
				IsWorking = false;
				_turnDeltas = 0;
				TurnIdle?.Invoke();
				break;
		}
	}

	private void HandleUsage(AssistantUsageData data)
	{
		// Credits accumulate across every call, sub-agents included.
		if (data.CopilotUsage is { } usage && usage.TotalNanoAiu > 0)
		{
			_sessionAicNano += (long)usage.TotalNanoAiu;
			UsageChanged?.Invoke();
		}

		// The context meter tracks the main user-driven call only: sub-agent
		// and sampling calls report an initiator and are skipped. The schema
		// says Initiator is absent for user calls; some models send "user".
		if ((string.IsNullOrEmpty(data.Initiator)
				|| string.Equals(data.Initiator, "user", StringComparison.OrdinalIgnoreCase))
			&& data.InputTokens is { } input)
		{
			ContextUsedTokens = (int)input;
			UsageChanged?.Invoke();
		}
	}

	// -- Session config -------------------------------------------------------------

	/// <summary>
	/// Session id from the workspace leaf folder and the clock, e.g.
	/// "MyApp-09-21-2026-101830". The CLI persists the transcript under this
	/// id, which is what makes a session recallable.
	/// </summary>
	private static string GenerateSessionId(string? workspaceFolder)
	{
		var leaf = !string.IsNullOrWhiteSpace(workspaceFolder)
			? new DirectoryInfo(workspaceFolder).Name
			: "TurboPilot";
		return $"{leaf}-{DateTime.Now:MM-dd-yyyy-HHmmss}";
	}

	/// <summary>
	/// Appended directives for the session: a short identity line plus the
	/// mode's behavior rule. Custom instructions and skills attach here when
	/// the customization wiring is extended.
	/// </summary>
	private SystemMessageConfig? BuildSystemMessage()
	{
		var parts = new List<string>
		{
			"You are running inside TurboPilot, a retro desktop client. "
				+ "The user sees your output as markdown in a terminal-styled transcript.",
		};

		switch (_options.Mode)
		{
			case "Plan":
				parts.Add(
					"PLAN MODE: Before taking any action, lay out a numbered step-by-step plan "
						+ "and wait for the user to confirm before executing. Always show your reasoning.");
				break;
			case "Autopilot":
				parts.Add(
					"AUTOPILOT MODE: Work autonomously to complete the user's goal end-to-end. "
						+ "Use all available tools without asking for confirmation at each step. "
						+ "Summarize what you did when finished.");
				break;
		}

		return new SystemMessageConfig
		{
			Content = string.Join("\n\n", parts),
			Mode = SystemMessageMode.Append,
		};
	}

	/// <summary>
	/// BYOK sessions route the runtime's model calls to the configured
	/// OpenAI-compatible server instead of the Copilot service. Null keeps
	/// the CLI's own provider.
	/// </summary>
	private ProviderConfig? BuildProviderConfig()
	{
		if (!_options.UseByok || string.IsNullOrWhiteSpace(_options.ByokEndpoint))
			return null;

		var config = new ProviderConfig
		{
			Type = "openai",
			WireApi = "completions",
			Transport = "http",
			BaseUrl = _options.ByokEndpoint.Trim(),
			ApiKey = string.IsNullOrWhiteSpace(_options.ByokApiKey) ? "local" : _options.ByokApiKey.Trim(),
			ModelId = _options.Model,
		};

		// Without the ceiling the runtime falls back to its own default
		// limit, which throws off both the context meter and compaction.
		if (ContextWindowTokens > 0)
			config.MaxPromptTokens = ContextWindowTokens;

		return config;
	}

	/// <summary>
	/// Asks the running runtime for its model catalog and returns the
	/// selected model's prompt window. Best effort: 0 when the model is not
	/// listed or the call fails.
	/// </summary>
	private async Task<int> LookupContextWindowAsync(string modelId, CancellationToken cancellationToken)
	{
		if (_client is null || string.IsNullOrWhiteSpace(modelId)) return 0;
		try
		{
			var models = await _client.ListModelsAsync(cancellationToken);
			foreach (var m in models)
			{
				if (!string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase)) continue;
				var limits = m.Capabilities?.Limits;
				if (limits is null) return 0;
				if (limits.MaxPromptTokens is { } maxPrompt) return (int)maxPrompt;
				return limits.MaxContextWindowTokens > 0 ? (int)limits.MaxContextWindowTokens : 0;
			}
		}
		catch
		{
			// Catalog lookup is a nicety; the meter just stays blank.
		}
		return 0;
	}

	// -- Attachments ------------------------------------------------------------------

	/// <summary>
	/// Builds the SDK attachment for a path. Images travel as base64 blobs
	/// with their MIME type so the model actually sees the picture;
	/// everything else travels as a path reference.
	/// </summary>
	private static Attachment BuildAttachment(string path)
	{
		var mime = GetImageMimeType(path);
		if (mime is not null && File.Exists(path))
		{
			try
			{
				return new AttachmentBlob
				{
					Data = Convert.ToBase64String(File.ReadAllBytes(path)),
					DisplayName = Path.GetFileName(path),
					MimeType = mime,
				};
			}
			catch
			{
				// Unreadable image: fall through to a path reference.
			}
		}

		return new AttachmentFile
		{
			Path = path,
			DisplayName = Path.GetFileName(path),
		};
	}

	private static string? GetImageMimeType(string path) =>
		Path.GetExtension(path).ToLowerInvariant() switch
		{
			".png" => "image/png",
			".jpg" or ".jpeg" => "image/jpeg",
			".gif" => "image/gif",
			".webp" => "image/webp",
			".bmp" => "image/bmp",
			_ => null,
		};
}
