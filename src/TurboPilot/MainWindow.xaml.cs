using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;
using TurbolandTheme.Wpf.Controls;
using TurboPilot.Ai;
using TurboPilot.Customizations;
using TurboPilot.Dialogs;
using TurboPilot.Sessions;
using TurboPilot.Mediation;

namespace TurboPilot;

public partial class MainWindow : TurbolandWindow
{
	// The single file: URI that the WebView2 is allowed to navigate to.
	private string? _outputHtmlUri;
	private bool _webViewReady = false;

	// The opening transcript is a document to read from the top, not a stream
	// to follow. Only the first sync gets that treatment.
	private bool _openedAtTop = false;
	private bool _rawOpenedAtTop = false;

	// Raw preserves streamed text; Rendered can replace completed messages
	// with prepared formatting without exposing local diagnostic traffic.
	private readonly StringBuilder _outputText = new();
	private readonly StringBuilder _renderedText = new();
	private readonly System.Windows.Documents.Run _rawOutputRun = new();

	// Attachments tracking
	private readonly List<string> _attachments = new();

	// Prompts sent this run, cycled through by the arrows beside the input
	// box. Kept across sessions: ending a session drops the live
	// connection, not what was typed into it.
	private readonly PromptHistory _promptHistory = new();

	// The live AI session, or null between sessions.
	private Ai.ChatService? _chat;
	private readonly SessionStore _sessionStore;
	private readonly Func<ChatService> _createChat;
	private readonly Func<string?, CustomizationLibrary> _collectCustomizations;
	private readonly string _webViewDataFolder;
	private readonly SemaphoreSlim _sessionChange = new(1, 1);
	private readonly HashSet<string> _historySessions = [];
	private CancellationTokenSource? _startCancellation;
	private CancellationTokenSource? _restartCancellation;
	private bool _sessionChanging;
	private bool _sendingInput;
	private bool _closing;
	private bool _closeApproved;
	private readonly MediatorConfiguration _mediatorConfiguration;
	private readonly ILocalModelRuntime _localRuntime;
	private readonly Func<string, string?, IMediatorSession>? _createMediator;

	// Model id and session id of the live session, shown in the
	// sessionInfo badge. Both null while no session is running.
	private string? _sessionModel;
	private string? _sessionId;

	// Status line state: a short base phrase plus usage suffixes. The base
	// is one of "Starting..", "Ready..", "Working.." or "Waiting..".
	private string _statusBase = "Ready..";
	private int _ctxUsed;
	private int _ctxTotal;
	private double _aic;
	private bool _showAic;

	public MainWindow() : this(new SessionStore())
	{
	}

	internal MainWindow(SessionStore sessionStore, Func<ChatService>? createChat = null,
		Func<string?, CustomizationLibrary>? collectCustomizations = null, string? webViewDataFolder = null,
		ILocalModelRuntime? localRuntime = null, MediatorConfiguration? mediatorConfiguration = null,
		Func<string, string?, IMediatorSession>? createMediator = null)
	{
		_sessionStore = sessionStore;
		_createChat = createChat ?? (() => new ChatService(_sessionStore));
		_collectCustomizations = collectCustomizations ?? CustomizationService.Rescan;
		_localRuntime = localRuntime ?? new FoundryModelRuntime();
		_mediatorConfiguration = mediatorConfiguration ?? new MediatorConfiguration();
		_createMediator = createMediator;
		_webViewDataFolder = webViewDataFolder ?? System.IO.Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboPilot", "webview2-default");
		InitializeComponent();

		// The Raw tab reads its face and ink from the same palette table the
		// Rendered tab is styled from, using the editor pair: yellow source
		// text on the blue field. The typeface is the theme's bundled DOS
		// face, set in XAML.
		richTextBoxOutput.Background = new SolidColorBrush(BorlandVisionTheme.RawBackgroundColor);
		richTextBoxOutput.Foreground = new SolidColorBrush(BorlandVisionTheme.RawForegroundColor);
		richTextBoxOutput.FontSize = BorlandVisionTheme.RawFontSize;
		richTextBoxOutput.Document.Blocks.Clear();
		richTextBoxOutput.Document.Blocks.Add(new System.Windows.Documents.Paragraph(_rawOutputRun));

		// Paint the WebView2 surface with the desktop blue before any
		// stylesheet lands, so there is no white flash on first show.
		webViewOutput.DefaultBackgroundColor = BorlandVisionTheme.DesktopBackgroundColorGdi;

		// Seed the transcript through the shared API so Raw and Rendered
		// start in sync.
		AppendOutput(LoadSampleTranscript());

		// Appending leaves the caret at the end, which is where the view
		// scrolls when it is first realized. The opening sample is read from
		// the top, so rewind it once the text view exists.
		richTextBoxOutput.Loaded += RichTextBoxOutput_FirstRealized;

		// Initialize WebView2 asynchronously
		_ = InitializeWebViewAsync();

		// Ctrl+Enter in the prompt box sends the current input.
		richTextBoxInput.PreviewKeyDown += Input_PreviewKeyDown;

		// No session is active until the user starts or resumes one.
		SetSessionActive(false);
	}

	// -- Session state ---------------------------------------------------------

	/// <summary>
	/// True while a session is active. Updated only by SetSessionActive.
	/// </summary>
	public bool IsSessionActive { get; private set; }

	/// <summary>
	/// The workspace folder of the active session, or null when none is
	/// running. Scopes the settings that belong to a workspace, such as
	/// its file access permissions.
	/// </summary>
	public string? ActiveWorkspacePath { get; private set; }

	/// <summary>
	/// Bulk-enables or disables every control that requires an active
	/// session. The Session and Help menus, the output tabs and the
	/// splitter stay available in both states: the user must always be
	/// able to start a session, read the transcript or get help. The
	/// Tools menu, the prompt input, the history navigation buttons and
	/// the attachments, Stop and Send buttons are gated on session state.
	/// The history arrows carry a second condition: they also need
	/// somewhere to cycle to, which UpdateHistoryButtons applies.
	/// </summary>
	public void SetSessionActive(bool active)
	{
		IsSessionActive = active;
		if (!active)
			ActiveWorkspacePath = null;

		var ready = active && !_sessionChanging && !_closing;
		menuTools.IsEnabled = ready;
		menuEndSession.IsEnabled = (active || _sessionChanging) && !_closing;
		menuNewSession.IsEnabled = !_sessionChanging && !_closing;
		menuPastSessions.IsEnabled = !_sessionChanging && !_closing;
		menuSettings.IsEnabled = !_sessionChanging && !_closing;
		menuMediator.IsEnabled = !_sessionChanging && !_closing;
		menuSummary.IsEnabled = ready && _chat?.SummaryEnabled == true;

		richTextBoxInput.IsEnabled = ready;
		UpdateHistoryButtons();

		buttonAttachments.IsEnabled = ready && !_sendingInput;
		buttonStop.IsEnabled = ready && _chat is not null && (_chat.IsWorking || _chat.HasPendingQuestion);
		buttonSend.IsEnabled = ready && !_sendingInput;

		// The prompt box only accepts typing while a session is running.
		richTextBoxInput.IsReadOnly = !ready;

		UpdateStatus();
		UpdateSessionInfo();
	}

	/// <summary>
	/// Repaints the session badge: the model in use and the session id
	/// while a live session exists, otherwise a notice that none is active.
	/// </summary>
	private void UpdateSessionInfo()
	{
		if (_chat is not null && _sessionId is not null)
		{
			sessionInfo.Text = string.IsNullOrEmpty(_sessionModel)
				? _sessionId
				: $"{_sessionModel} | {_sessionId}";
		}
		else
		{
			sessionInfo.Text = "No session active";
		}
	}

	// ── Splitter drag handler ────────────────────────────────────────────────

	/// <summary>
	/// Moves the divider while preserving proportional row sizing.
	/// </summary>
	private void SplitterThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
	{
		var totalHeight = OutputRow.ActualHeight + InputRow.ActualHeight;
		const double minOutput = 80;
		var minInput = 40 + labelUserPrompt.ActualHeight
			+ promptInputGrid.Margin.Top + promptInputGrid.Margin.Bottom;
		if (totalHeight < minOutput + minInput) return;

		var outputHeight = Math.Clamp(OutputRow.ActualHeight + e.VerticalChange, minOutput, totalHeight - minInput);
		// Pixel heights would leave unused space after the window grows.
		OutputRow.Height = new GridLength(outputHeight, GridUnitType.Star);
		InputRow.Height = new GridLength(totalHeight - outputHeight, GridUnitType.Star);
	}

	// ── Attachments ──────────────────────────────────────────────────────────

	/// <summary>
	/// Opens the attachments dialog when the +0 button is clicked. The
	/// dialog edits a copy of the pending list; OK replaces it and the
	/// button text follows the new count.
	/// </summary>
	private void ButtonAttachments_Click(object sender, RoutedEventArgs e)
	{
		var dialog = new AttachmentsDialog(_attachments);
		if (dialog.ShowDialog(this) != true) return;

		_attachments.Clear();
		_attachments.AddRange(dialog.Attachments);
		UpdateAttachmentButton();
	}

	/// <summary>
	/// Updates the attachments button text to show the current count.
	/// Format: "+N" where N is the number of attachments.
	/// </summary>
	private void UpdateAttachmentButton()
	{
		buttonAttachments.Content = $"+{_attachments.Count}";
	}

	// ── WebView2 initialization ──────────────────────────────────────────────

	private async Task InitializeWebViewAsync()
	{
		try
		{
			System.IO.Directory.CreateDirectory(_webViewDataFolder);

			var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment
				.CreateAsync(null, _webViewDataFolder);
			if (_closing) return;
			await webViewOutput.EnsureCoreWebView2Async(env);

			// ── Lock down the WebView2 so it behaves as a pure display
			// surface, not a general-purpose browser. Disable navigation
			// affordances the user could accidentally trigger.
			// Install the Borland Vision stylesheet and the diagram palette
			// before any script in output.html runs, so the first frame the
			// user sees is already themed. Registered on the environment, not
			// the page, so it survives every reload.
			await webViewOutput.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
				BorlandVisionTheme.BuildBootstrapScript());

			var settings = webViewOutput.CoreWebView2.Settings;
			settings.AreDefaultContextMenusEnabled = false;
			settings.AreBrowserAcceleratorKeysEnabled = false;
			settings.IsStatusBarEnabled = false;
			settings.AreDevToolsEnabled = false;

			// The Rendered tab posts JSON messages (e.g. file-path link
			// clicks) back to us via window.chrome.webview.postMessage.
			// Wire the receiver before navigation so no early message is
			// dropped on the floor.
			webViewOutput.CoreWebView2.WebMessageReceived += WebView_WebMessageReceived;

			// Intercept top-level navigations (clicked anchors, document.location
			// assignments) and popup window requests (target="_blank", window.open).
			// Any web URL is routed to the system default browser so the
			// Rendered tab never replaces the transcript with a remote page.
			webViewOutput.CoreWebView2.NavigationStarting += WebView_NavigationStarting;
			webViewOutput.CoreWebView2.FrameNavigationStarting += WebView_FrameNavigationStarting;
			webViewOutput.CoreWebView2.NewWindowRequested += WebView_NewWindowRequested;
			webViewOutput.CoreWebView2.NavigationCompleted += WebView_NavigationCompleted;

			// Navigate to the bundled output.html
			var htmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, "web", "output.html");
			if (System.IO.File.Exists(htmlPath))
			{
				_outputHtmlUri = new Uri(htmlPath).AbsoluteUri;
				webViewOutput.CoreWebView2.Navigate(_outputHtmlUri);
				// _webViewReady is set in NavigationCompleted, once
				// output.js is actually loaded and its functions exist.
			}
			else
			{
				throw new System.IO.FileNotFoundException("The rendered output page is missing.", htmlPath);
			}
		}
		catch (Exception ex)
		{
			if (!_closing)
			{
				AppendOutput($"\r\n[error] Rendered output is unavailable: {ex.Message}\r\n");
				outputTabs.SelectedIndex = 1;
			}
		}
	}

	// ── WebView2 event handlers ──────────────────────────────────────────────

	/// <summary>
	/// Handles JSON messages posted by the web renderer (currently the
	/// file-path link click/right-click bridge). Unknown message shapes
	/// are ignored silently so the renderer can introduce new message
	/// types without crashing older builds.
	/// </summary>
	private void WebView_WebMessageReceived(
		object? sender,
		Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
	{
		string json;
		try
		{
			json = e.TryGetWebMessageAsString();
		}
		catch
		{
			// Non-string payloads are ignored.
			return;
		}
		if (string.IsNullOrWhiteSpace(json)) return;

		// Parse and handle known message types
		try
		{
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;

			if (root.TryGetProperty("type", out var typeEl)
				&& root.TryGetProperty("path", out var pathEl))
			{
				var type = typeEl.GetString();
				var path = pathEl.GetString();
				if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(path)) return;

				HandleWebMessage(type, path);
			}
		}
		catch
		{
			// Malformed JSON — ignore silently
		}
	}

	/// <summary>
	/// Dispatches a web message by type. Extend this when adding new
	/// message types from the renderer.
	/// </summary>
	private void HandleWebMessage(string type, string path)
	{
		switch (type)
		{
			// Message names match the renderer's postPathMessage() calls.
			case "openPath":
				// Try to open the file in the default editor
				try
				{
					var startInfo = new ProcessStartInfo
					{
						FileName = path,
						UseShellExecute = true,
					};
					Process.Start(startInfo);
				}
				catch
				{
					// Best-effort — file may not exist
				}
				break;

			case "revealPath":
				// Reveal file in Explorer
				try
				{
					var dir = System.IO.Path.GetDirectoryName(path);
					if (!string.IsNullOrEmpty(dir))
					{
						var startInfo = new ProcessStartInfo
						{
							FileName = dir,
							UseShellExecute = true,
							Verb = "open",
						};
						Process.Start(startInfo);
					}
				}
				catch
				{
					// Best-effort
				}
				break;
		}
	}

	/// <summary>
	/// Blocks all navigations except our output.html and about: URLs.
	/// Web URLs are launched in the system default browser.
	/// </summary>
	private void WebView_NavigationStarting(
		object? sender,
		Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
	{
		if (string.IsNullOrEmpty(e.Uri)) return;

		// Allow only our exact output.html file URI
		if (_outputHtmlUri != null
			&& e.Uri.Equals(_outputHtmlUri, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		if (e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;

		e.Cancel = true;
		LaunchInDefaultBrowser(e.Uri);
	}

	/// <summary>
	/// Blocks all iframe/frame navigations. The Rendered tab should
	/// never load external content inside embedded frames.
	/// </summary>
	private void WebView_FrameNavigationStarting(
		object? sender,
		Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
	{
		if (string.IsNullOrEmpty(e.Uri)) return;

		// Allow about:blank (used by some internal frame init)
		if (e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;

		e.Cancel = true;
	}

	/// <summary>
	/// Marks the renderer live once output.html has loaded and replays the
	/// transcript buffer so the Rendered tab matches the Raw tab. If a
	/// navigation completes on an unexpected page, re-navigates to
	/// output.html (the sync below then runs again on the next completion).
	/// </summary>
	private void WebView_NavigationCompleted(
		object? sender,
		Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
	{
		if (_outputHtmlUri == null || _closing) return;
		if (!e.IsSuccess)
		{
			if (e.WebErrorStatus == Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.OperationCanceled)
				return;
			_webViewReady = false;
			AppendOutput($"\r\n[error] Rendered output failed to load: {e.WebErrorStatus}\r\n");
			outputTabs.SelectedIndex = 1;
			return;
		}

		var currentUri = webViewOutput.CoreWebView2.Source;
		if (currentUri != null
			&& currentUri.Equals(_outputHtmlUri, StringComparison.OrdinalIgnoreCase))
		{
			// output.js is loaded and its functions exist from here on.
			_webViewReady = true;
			SyncRenderedTranscript();
			return;
		}

		// We are on an unexpected page -- recover by re-navigating
		_webViewReady = false;
		webViewOutput.CoreWebView2.Navigate(_outputHtmlUri);
	}

	/// <summary>
	/// Handles target="_blank" anchors and window.open calls.
	/// Always sets Handled = true so the WebView2 runtime does
	/// not spawn a new popup window, then routes web URLs through
	/// the system default browser.
	/// </summary>
	private void WebView_NewWindowRequested(
		object? sender,
		Microsoft.Web.WebView2.Core.CoreWebView2NewWindowRequestedEventArgs e)
	{
		e.Handled = true;
		LaunchInDefaultBrowser(e.Uri);
	}

	/// <summary>
	/// Shell-executes url with the OS default handler,
	/// restricted to a small allowlist of safe schemes (http, https,
	/// mailto). Any other scheme is silently ignored.
	/// </summary>
	private static void LaunchInDefaultBrowser(string? url)
	{
		if (string.IsNullOrWhiteSpace(url)) return;

		if (!url.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
		 && !url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
		 && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = url,
				UseShellExecute = true,
			});
		}
		catch
		{
			// Best-effort — browser may not be available
		}
	}

	// ── Output API ───────────────────────────────────────────────────────────
	//
	// Common notices go to both views. Chat events keep Raw text separate
	// from prepared Rendered content and optional local diagnostics.

	/// <summary>
	/// The full verbatim transcript currently displayed in the Raw tab.
	/// </summary>
	public string OutputText => _outputText.ToString();
	public string RenderedText => _renderedText.ToString();

	/// <summary>
	/// The sample document both output views open with. It ships as markdown
	/// text rather than markup because the Raw tab has to show exactly what
	/// the Rendered tab renders. Line endings are normalized to CRLF so the
	/// buffer, the Raw tab and the file agree.
	/// </summary>
	private static string LoadSampleTranscript()
	{
		var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "SampleTranscript.md");
		try
		{
			var text = System.IO.File.ReadAllText(path);
			return text.Replace("\r\n", "\n").Replace("\n", "\r\n");
		}
		catch
		{
			// Missing or unreadable sample is not worth a dialog.
			return "TurboPilot\r\n";
		}
	}

	/// <summary>
	/// Appends text verbatim to the Raw tab and immediately syncs the
	/// Rendered tab to the same content. Safe to call from any thread;
	/// appends are applied in order on the UI thread.
	/// </summary>
	public void AppendOutput(string text)
	{
		if (string.IsNullOrEmpty(text)) return;
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action<string>(AppendOutput), text);
			return;
		}

		AppendRawOutput(text);
		AppendRenderedOutput(text);
	}

	private void AppendRawOutput(string text)
	{
		_outputText.Append(text);
		// Range-based appends normalize paragraph boundaries between chunks.
		_rawOutputRun.ContentEnd.InsertTextInRun(text);
		richTextBoxOutput.CaretPosition = _rawOutputRun.ContentEnd;
		richTextBoxOutput.ScrollToEnd();
	}

	private void AppendRenderedOutput(string text)
	{
		_renderedText.Append(text);
		PushToRenderer($"appendTranscript({JsString(text)})");
	}

	private void ReplaceRenderedOutput(string text)
	{
		_renderedText.Clear().Append(text);
		PushToRenderer($"setTranscript({JsString(text)})");
	}

	private void ReplaceRawOutput(string text)
	{
		_outputText.Clear().Append(text);
		_rawOutputRun.Text = text;
	}

	/// <summary>
	/// Clears the transcript from both the Raw tab and the Rendered tab.
	/// Safe to call from any thread.
	/// </summary>
	public void ClearOutput()
	{
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action(ClearOutput));
			return;
		}

		_outputText.Clear();
		_renderedText.Clear();
		_rawOutputRun.Text = "";
		PushToRenderer("clearAll()");
	}

	/// <summary>
	/// Executes renderer script once the page is loaded. Content written
	/// before the page is ready is not lost: the full transcript is
	/// replayed via SyncRenderedTranscript when navigation completes.
	/// </summary>
	private void PushToRenderer(string js)
	{
		if (!_webViewReady) return;
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
	}

	/// <summary>
	/// Restores the prepared display after the page loads or reloads.
	/// </summary>
	private void SyncRenderedTranscript()
	{
		if (!_webViewReady) return;
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync($"setTranscript({JsString(_renderedText.ToString())})");

		if (_openedAtTop) return;
		_openedAtTop = true;
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync("scrollToTop()");
	}

	/// <summary>
	/// Returns a JS-safe string literal for ExecuteScriptAsync.
	/// </summary>
	private static string JsString(string s)
	{
		if (string.IsNullOrEmpty(s)) return "''";
		return "'" + s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "'";
	}

	private void OnAbout(object sender, RoutedEventArgs e)
	{
		// Modal and owned: the OS keeps it above the main window (and its
		// WebView2 airspace), and the blocking call means it cannot stack
		// a duplicate.
		new AboutDialog
		{
			WebsiteUrl = "https://github.com/mighty-studios/TurboPilot"
		}.ShowDialog(this);
	}

	/// <summary>
	/// Opens the Raw tab at the first line of the transcript. Fires again on
	/// every later tab switch, so it guards itself.
	/// </summary>
	private void RichTextBoxOutput_FirstRealized(object sender, RoutedEventArgs e)
	{
		if (_rawOpenedAtTop) return;
		_rawOpenedAtTop = true;

		// ScrollToHome moves the caret and the viewport together, which is how
		// a text view is meant to be rewound. The call has to wait for layout,
		// because the text view does not exist until the tab holding it is
		// measured.
		richTextBoxOutput.Dispatcher.BeginInvoke(
			System.Windows.Threading.DispatcherPriority.Loaded,
			new Action(richTextBoxOutput.ScrollToHome));
	}

	// ── Prompt history navigation ────────────────────────────────────────────

	/// <summary>
	/// Older prompt: steps the input box back through what has been sent.
	/// Anything typed but not yet sent is set aside and returns when the
	/// user steps past the newest entry again.
	/// </summary>
	private void ButtonHistoryPrev_Click(object sender, RoutedEventArgs e)
	{
		SetInputText(_promptHistory.NavigateBack(GetInputText()));
		UpdateHistoryButtons();
	}

	/// <summary>
	/// Newer prompt: steps forward, restoring the set-aside draft once the
	/// most recent prompt has been passed.
	/// </summary>
	private void ButtonHistoryNext_Click(object sender, RoutedEventArgs e)
	{
		SetInputText(_promptHistory.NavigateForward());
		UpdateHistoryButtons();
	}

	/// <summary>
	/// Availability of the gutter arrows: a session must be running and
	/// there must be somewhere to step. With nothing sent yet both are off;
	/// browsing to the ends turns off the arrow that has nowhere to go.
	/// </summary>
	private void UpdateHistoryButtons()
	{
		var enabled = IsSessionActive && !_sessionChanging && !_closing && !_sendingInput;
		buttonHistoryPrev.IsEnabled = enabled && _promptHistory.CanGoBack;
		buttonHistoryNext.IsEnabled = enabled && _promptHistory.CanGoForward;
	}

	/// <summary>
	/// Replaces the prompt box with plain text and leaves the caret at the
	/// end, so a recalled prompt is ready to edit or resend.
	/// </summary>
	private void SetInputText(string text)
	{
		var paragraph = new System.Windows.Documents.Paragraph(
			new System.Windows.Documents.Run(text));
		richTextBoxInput.Document.Blocks.Clear();
		richTextBoxInput.Document.Blocks.Add(paragraph);
		richTextBoxInput.CaretPosition = paragraph.ContentEnd;
		richTextBoxInput.Focus();
	}

	// ── Session settings ─────────────────────────────────────────────────────

	/// <summary>
	/// Opens the Session Settings dialog from New Session.
	/// </summary>
	private async void OnNewSession(object sender, RoutedEventArgs e) => await OpenSettingsDialogAsync();

	/// <summary>
	/// Opens the Session Settings dialog from the menu.
	/// </summary>
	private async void OnSettings(object sender, RoutedEventArgs e) => await OpenSettingsDialogAsync();

	/// <summary>
	/// Shows the Session Settings dialog. Begin Session brings the
	/// session-dependent controls online, refreshes the customization
	/// lists for the new workspace and starts the streaming session
	/// with the gathered options. Ending a session is the Session menu's
	/// End Session item.
	/// </summary>
	private async Task OpenSettingsDialogAsync()
	{
		try
		{
			var dialog = new Dialogs.SettingsDialog(ActiveWorkspacePath, IsSessionActive);
			dialog.ShowDialog(this);

			if (dialog.BeginRequested && !string.IsNullOrEmpty(dialog.WorkspacePath))
			{
				await StartFromSettingsAsync(new ChatSessionOptions
				{
					WorkspaceFolder = dialog.WorkspacePath,
					Model = dialog.SelectedModel,
					ReasoningEffort = dialog.SelectedEffort,
					Mode = dialog.SelectedMode,
					ContextWindowTokens = dialog.SelectedContextWindowTokens,
					UseByok = dialog.Provider == "Byok",
					ByokEndpoint = dialog.ByokEndpoint,
					ByokApiKey = dialog.ByokApiKey,
					ApplyInstructions = dialog.ApplyInstructions,
					PreloadSkills = dialog.PreloadSkills,
				});
			}
		}
		catch (Exception ex)
		{
			AppendOutput($"\r\n[error] Cannot open session settings: {ex.Message}\r\n\r\n");
		}
	}

	private async Task StartFromSettingsAsync(ChatSessionOptions options)
	{
		using var cancellation = new CancellationTokenSource();
		_restartCancellation = cancellation;
		_sessionChanging = true;
		SetSessionActive(IsSessionActive);
		try
		{
			SummaryBootstrap? bootstrap = null;
			if (_chat is { SummaryEnabled: true } current && ShouldOfferBootstrap(current.Options, options)
				&& YesNoDialog.Ask(this, "Use the Mediator summary as context for the new session?", "Restart Context"))
			{
				try
				{
					bootstrap = await current.GetRestartSummaryAsync(cancellation.Token);
					cancellation.Token.ThrowIfCancellationRequested();
					if (bootstrap is null)
						MessageDialog.Ok(this, "No up-to-date summary is available. The new session will start without one.", "Mediator");
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					MessageDialog.Ok(this, "Cannot prepare restart context: " + ex.Message + "\r\nThe new session will start without it.", "Mediator");
				}
			}
			cancellation.Token.ThrowIfCancellationRequested();
			await StartChatCoreAsync(options, null, bootstrap);
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
		finally
		{
			_restartCancellation = null;
			_sessionChanging = false;
			RefreshChatState();
		}
	}

	internal static bool ShouldOfferBootstrap(ChatSessionOptions current, ChatSessionOptions next) =>
		!string.IsNullOrWhiteSpace(current.WorkspaceFolder)
		&& MediationStore.SameWorkspace(current.WorkspaceFolder, next.WorkspaceFolder)
		&& (current.Model != next.Model || current.ReasoningEffort != next.ReasoningEffort || current.Mode != next.Mode
			|| current.UseByok != next.UseByok || current.ByokEndpoint != next.ByokEndpoint || current.ByokApiKey != next.ByokApiKey
			|| current.ApplyInstructions != next.ApplyInstructions || current.PreloadSkills != next.PreloadSkills
			|| current.ContextWindowTokens != next.ContextWindowTokens);

	/// <summary>
	/// Session menu: ends the active session.
	/// </summary>
	private async void OnEndSessionClick(object sender, RoutedEventArgs e) => await EndSessionAsync();

	/// <summary>
	/// Ends the active session: disposes the SDK session, gates the
	/// session-dependent controls back off and clears the workspace
	/// scope. Session history and settings persist; only the live
	/// session state goes away.
	/// </summary>
	public async Task EndSessionAsync()
	{
		_restartCancellation?.Cancel();
		_startCancellation?.Cancel();
		await _sessionChange.WaitAsync();
		_sessionChanging = true;
		SetSessionActive(IsSessionActive);
		try
		{
			await EndChatCoreAsync();
		}
		finally
		{
			_sessionChanging = false;
			SetSessionActive(false);
			_sessionChange.Release();
		}
	}

	private async Task EndChatCoreAsync()
	{
		if (_chat is { } chat)
			await chat.DisposeAsync();
		_chat = null;
		_sessionModel = null;
		_sessionId = null;
		_ctxTotal = 0;
		_ctxUsed = 0;
		_aic = 0;
		_showAic = false;
		SetSessionActive(false);
	}

	// -- Chat round-trip ---------------------------------------------------------

	/// <summary>
	/// Starts the session described by the settings dialog and
	/// wires its events into the transcript and the status line. A failed
	/// start is reported in the transcript and drops back to no session.
	/// </summary>
	private Task StartChatAsync(ChatSessionOptions options, string? resumeId = null) =>
		StartChatCoreAsync(options, resumeId, null);

	private async Task StartChatCoreAsync(ChatSessionOptions options, string? resumeId, SummaryBootstrap? bootstrap)
	{
		await _sessionChange.WaitAsync();
		using var cancellation = new CancellationTokenSource();
		_startCancellation = cancellation;
		_sessionChanging = true;
		SetSessionActive(IsSessionActive);
		try
		{
			options = options with
			{
				Customizations = _collectCustomizations(options.WorkspaceFolder).Clone(),
			};
			await EndChatCoreAsync();
			cancellation.Token.ThrowIfCancellationRequested();

			var chat = _createChat();
			chat.MediatorFactory ??= _createMediator ?? ((id, workspace) => new MediatorService(
				id, workspace, _mediatorConfiguration.Load(), _localRuntime, _mediatorConfiguration));
			_chat = chat;
			chat.TranscriptReceived += text => ForActiveChat(chat, () => AppendRawOutput(text));
			chat.RenderedReceived += text => ForActiveChat(chat, () => AppendRenderedOutput(text));
			chat.RenderedReplaced += text => ForActiveChat(chat, () => ReplaceRenderedOutput(text));
			chat.MediatorNoticeReceived += text => ForActiveChat(chat, () => ShowMediatorNotice(text));
			chat.MediatorDiagnosticReceived += diagnostic => ForActiveChat(chat, () =>
			{
				if (chat.DebugRaw)
					AppendRawOutput($"\r\n[mediator {diagnostic.Operation}]\r\nInput: {diagnostic.Input}\r\nOutput: {diagnostic.Output}\r\n{diagnostic.Note}\r\n");
			});
			chat.ErrorReceived += text => ForActiveChat(chat, () => AppendOutput($"\r\n[error] {text}\r\n\r\n"));
			chat.StateChanged += () => ForActiveChat(chat, RefreshChatState);
			chat.UsageChanged += () => ForActiveChat(chat, () =>
			{
				_ctxUsed = chat.ContextUsedTokens;
				_ctxTotal = chat.ContextWindowTokens;
				_aic = chat.AicUsed;
				_sessionModel = chat.Model;
				UpdateSessionInfo();
				UpdateStatus();
			});

			ClearOutput();
			mediatorNotice.Text = "";
			mediatorNotice.Visibility = Visibility.Collapsed;
			_statusBase = "Starting..";
			_sessionModel = options.Model;
			_ctxTotal = options.ContextWindowTokens;
			_showAic = !options.UseByok;
			ActiveWorkspacePath = options.WorkspaceFolder;
			SetSessionActive(true);

			if (resumeId is null)
				await chat.StartAsync(options, cancellation.Token);
			else
				await chat.ResumeAsync(resumeId, options, cancellation.Token);
			if (bootstrap is not null)
			{
				chat.SetBootstrap(bootstrap);
				ShowMediatorNotice("The previous summary will accompany the next prompt as background context.");
			}

			cancellation.Token.ThrowIfCancellationRequested();
			_sessionId = chat.SessionId ?? throw new InvalidOperationException("The runtime did not return a session ID.");
			if (_historySessions.Add(_sessionId) && resumeId is not null && chat.Record is { } saved)
			{
				foreach (var prompt in saved.Prompts)
					_promptHistory.Add(prompt);
			}
			_ctxTotal = chat.ContextWindowTokens;
			_ctxUsed = chat.ContextUsedTokens;
			_aic = chat.AicUsed;
			_sessionModel = chat.Model;
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
		{
			await EndChatCoreAsync();
		}
		catch (Exception ex)
		{
			AppendOutput($"\r\n[error] Session start failed: {ex.Message}\r\n\r\n");
			await EndChatCoreAsync();
		}
		finally
		{
			_startCancellation = null;
			_sessionChanging = false;
			RefreshChatState();
			_sessionChange.Release();
		}
	}

	private void ForActiveChat(ChatService chat, Action action)
	{
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action(() => ForActiveChat(chat, action)));
			return;
		}
		if (ReferenceEquals(_chat, chat))
			action();
	}

	private void RefreshChatState()
	{
		_statusBase = _sessionChanging ? "Starting.."
			: _chat?.HasPendingQuestion == true ? "Waiting.."
			: _chat?.IsMediating == true ? "Mediating.."
			: _chat?.IsWorking == true ? "Working.." : "Ready..";
		SetSessionActive(_chat is not null);
	}

	private async void OnPastSessions(object sender, RoutedEventArgs e)
	{
		try
		{
			var sessions = _sessionStore.List(out var errors);
			if (errors.Count > 0)
				MessageDialog.Ok(this, string.Join("\r\n", errors), "Saved session errors");
			if (sessions.Count == 0)
			{
				MessageDialog.Ok(this, "No saved sessions yet.", "Past Sessions");
				return;
			}

			var dialog = new PastSessionsDialog(sessions);
			if (dialog.ShowDialog(this) != true || dialog.SelectedSession is not { } selected)
				return;
			if (IsSessionActive && !YesNoDialog.Ask(this,
				"Open this saved session? The current session will end.", "Past Sessions"))
				return;

			if (!dialog.ResumeRequested)
			{
				await EndSessionAsync();
				var transcript = _sessionStore.ReadTranscript(selected.SessionId);
				var rendered = _sessionStore.ReadRenderedTranscript(selected.SessionId);
				ClearOutput();
				AppendRawOutput(transcript);
				AppendRenderedOutput(rendered);
				return;
			}

			var options = RestoreSessionOptions(selected, Settings.Load());
			await StartChatAsync(options, selected.SessionId);
		}
		catch (Exception ex)
		{
			AppendOutput($"\r\n[error] Cannot open saved session: {ex.Message}\r\n\r\n");
		}
	}

	internal static ChatSessionOptions RestoreSessionOptions(SessionRecord saved, Settings settings)
	{
		var options = saved.Options;
		if (!options.UseByok)
			return options;
		var matchingEndpoint = string.Equals(options.ByokEndpoint.TrimEnd('/'),
			settings.ByokEndpoint?.TrimEnd('/'), StringComparison.Ordinal);
		if (saved.UsesApiKey && (!matchingEndpoint || string.IsNullOrWhiteSpace(settings.ByokApiKey)))
			throw new InvalidOperationException("Set the saved provider endpoint and its API key in Settings before resuming this session.");
		return options with { ByokApiKey = matchingEndpoint ? settings.ByokApiKey ?? "" : "" };
	}

	/// <summary>
	/// Repaints the status line: the base phrase followed by context use
	/// as whole-Ki "&lt;used&gt;/&lt;total&gt;K" and, for Copilot CLI
	/// sessions, credits as "AiC=&lt;value&gt;".
	/// </summary>
	private void UpdateStatus()
	{
		if (!IsSessionActive)
		{
			statusTextBlock.Text = "Start or resume a session to begin.";
			return;
		}

		statusTextBlock.Text = FormatStatus(_statusBase, _ctxUsed, _ctxTotal, _aic, _showAic);
		if (_chat?.MediationStatus is "Mediator offline" or "Mediator disabled" or "Mediator error")
			statusTextBlock.Text += " Med=" + _chat.MediationStatus[9..];
	}

	internal static string FormatStatus(string status, int used, int total, double credits, bool showCredits)
	{
		if (total > 0)
			status += $" {used / 1024}/{total / 1024}K";
		if (showCredits)
			status += $" AiC={credits:0}";
		return status;
	}

	/// <summary>
	/// Send button: dispatches the prompt box to the session, or answers
	/// an outstanding question when one is waiting.
	/// </summary>
	private async void ButtonSend_Click(object sender, RoutedEventArgs e)
	{
		await SendCurrentInputAsync();
	}

	/// <summary>
	/// Stop button: aborts the turn in flight and releases any pending
	/// question.
	/// </summary>
	private async void ButtonStop_Click(object sender, RoutedEventArgs e)
	{
		var chat = _chat;
		if (chat is null) return;

		try { await chat.AbortAsync(); }
		catch (Exception ex)
		{
			ForActiveChat(chat, () => chat.AddNotice("[error] Stop failed: " + ex.Message));
		}
		ForActiveChat(chat, RefreshChatState);
	}

	/// <summary>
	/// Takes the text out of the prompt box and sends it. While a turn is
	/// in flight the send interrupts it rather than queueing behind it,
	/// matching the behavior users expect from the retro front end. The
	/// user's message is echoed to the transcript so both output views
	/// carry the full exchange.
	/// </summary>
	private async Task SendCurrentInputAsync()
	{
		var chat = _chat;
		if (chat is null || _sessionChanging || _sendingInput || _closing) return;

		var text = GetInputText();
		if (string.IsNullOrWhiteSpace(text) && _attachments.Count == 0) return;

		// Only text enters the history. A send carrying nothing but an
		// attachment has no words worth recalling.
		if (!string.IsNullOrWhiteSpace(text))
		{
			_promptHistory.Add(text);
			UpdateHistoryButtons();
		}

		var attachments = _attachments.ToArray();
		_sendingInput = true;
		SetSessionActive(IsSessionActive);
		try
		{
			await chat.SendAsync(text, attachments);
			if (ReferenceEquals(_chat, chat))
			{
				if (GetInputText() == text)
					richTextBoxInput.Document.Blocks.Clear();
				_attachments.Clear();
				UpdateAttachmentButton();
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			ForActiveChat(chat, () => chat.AddNotice("[error] Send failed: " + ex.Message));
		}
		finally
		{
			_sendingInput = false;
			RefreshChatState();
		}
	}

	/// <summary>
	/// Plain text of everything typed in the prompt box.
	/// </summary>
	private string GetInputText()
	{
		var range = new System.Windows.Documents.TextRange(
			richTextBoxInput.Document.ContentStart,
			richTextBoxInput.Document.ContentEnd);
		return range.Text.Trim();
	}

	/// <summary>
	/// Ctrl+Enter in the prompt box sends, like every other chat front
	/// end this machine has ever run.
	/// </summary>
	private void Input_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (e.Key == System.Windows.Input.Key.Enter
			&& (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
		{
			e.Handled = true;
			_ = SendCurrentInputAsync();
		}
	}

	/// <summary>
	/// Opens the Customization dialog for editing the search folder list.
	/// Modal and owned: the OS keeps it above the main window (and its
	/// WebView2 airspace), and the blocking call means it cannot stack
	/// a duplicate. The dialog persists the list to settings itself.
	/// </summary>
	private void OnCustomization(object sender, RoutedEventArgs e)
	{
		new Dialogs.CustomizeDialog().ShowDialog(this);
	}

	// ── Permissions ──────────────────────────────────────────────────────────

	/// <summary>
	/// Opens the Permissions dialog. It edits the file access entries that
	/// belong to the active workspace, or the application defaults when no
	/// session is running. Modal and owned: the OS keeps it above the main
	/// window (and its WebView2 airspace), and the blocking call means it
	/// cannot stack a duplicate.
	/// </summary>
	private void OnPermissions(object sender, RoutedEventArgs e)
	{
		new Dialogs.PermissionsDialog(ActiveWorkspacePath).ShowDialog(this);
	}

	private async void OnMediator(object sender, RoutedEventArgs e)
	{
		try
		{
			var dialog = new MediatorDialog(_mediatorConfiguration, _localRuntime);
			if (dialog.ShowDialog(this) == true && dialog.Result is { } options && _chat is { } chat)
			{
				await chat.ConfigureMediatorAsync(options);
				ForActiveChat(chat, () =>
				{
					if (!chat.DebugRaw)
						ReplaceRawOutput(chat.Transcript);
					ShowMediatorNotice(options.Enabled ? "Mediator options applied." : "Mediator disabled; original chat is preserved.");
				});
			}
		}
		catch (Exception ex) { MessageDialog.Ok(this, "Cannot open Mediator settings: " + ex.Message, "Mediator"); }
	}

	private void ShowMediatorNotice(string text)
	{
		mediatorNotice.Text = "Mediator: " + text;
		mediatorNotice.ToolTip = mediatorNotice.Text;
		mediatorNotice.Visibility = Visibility.Visible;
	}

	private async void OnMediatorSummary(object sender, RoutedEventArgs e)
	{
		if (_chat is not { } chat) return;
		try
		{
			if (chat.IsWorking && !YesNoDialog.Ask(this, "Stop the current work and prepare an up-to-date summary?", "Mediator"))
				return;
			var summary = await chat.GetRestartSummaryAsync();
			if (ReferenceEquals(_chat, chat))
				new SummaryDialog(summary?.Summary ?? "No up-to-date summary is available.").ShowDialog(this);
		}
		catch (Exception ex) { ShowMediatorNotice("Cannot prepare the summary: " + ex.Message); }
	}

	// -- Exit ------------------------------------------------------------------

	/// <summary>
	/// The frame's close box and the Session menu's Exit both arrive here,
	/// so one guard covers both. The program quits only on an explicit Yes;
	/// No, Escape and the question's own close box all leave it running.
	/// </summary>
	private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
	{
		if (_closeApproved)
			return;
		e.Cancel = true;
		if (_closing || !Dialogs.YesNoDialog.Ask(this, "Exit the Program?"))
			return;
		_closing = true;
		await EndSessionAsync();
		try { await _localRuntime.DisposeAsync(); }
		catch (Exception ex) { MessageDialog.Ok(this, "Local runtime shutdown failed: " + ex.Message, "Mediator"); }
		_closeApproved = true;
		Close();
	}

	/// <summary>
	/// Session menu: quits through the same guard the close box uses, so the
	/// two paths cannot drift apart.
	/// </summary>
	private void OnExitClick(object sender, RoutedEventArgs e) => Close();

	protected override void OnClosed(EventArgs e)
	{
		webViewOutput.Dispose();
		base.OnClosed(e);
	}
}
