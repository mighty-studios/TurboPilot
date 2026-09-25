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
using TurboPilot.Rendering;
using TurboPilot.Sessions;
using TurboPilot.Tools;

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
	// with prepared formatting.
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

	// The helper functions dotted into a Tools shell. Null uses the
	// per-user file; tests point it somewhere disposable.
	private readonly string? _scriptsPath;
	private readonly SemaphoreSlim _sessionChange = new(1, 1);
	private readonly HashSet<string> _historySessions = [];
	private CancellationTokenSource? _startCancellation;
	private CancellationTokenSource? _restartCancellation;
	private bool _sessionChanging;
	private bool _sendingInput;
	private bool _closing;
	private bool _closeRequested;
	private bool _closeApproved;

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
		string? scriptsPath = null)
	{
		_sessionStore = sessionStore;
		_createChat = createChat ?? (() => new ChatService(_sessionStore));
		_collectCustomizations = collectCustomizations ?? CustomizationService.Rescan;
		_scriptsPath = scriptsPath;
		_webViewDataFolder = webViewDataFolder ?? System.IO.Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboPilot", "webview2-default");
		InitializeComponent();

		// Cues are a saved preference, not session state, so they are live
		// from startup rather than from the first session.
		Sounds.Enabled = Settings.Load().PlaySounds;

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

		// Ctrl+Enter sends the current input from anywhere in the window.
		PreviewKeyDown += Input_PreviewKeyDown;

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
		// One entry covers the whole lifecycle, so its caption has to say
		// which half of it is on offer.
		menuNewSession.Header = active ? "C_hange Session..." : "_Begin Session...";
		menuNewSession.IsEnabled = !_sessionChanging && !_closing;
		menuPastSessions.IsEnabled = !_sessionChanging && !_closing;
		menuCompactContext.IsEnabled = ready && !_sendingInput;
		menuResetContext.IsEnabled = ready && !_sendingInput;

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
	/// while a live session exists, otherwise a notice that none is
	/// active. Both are written for machines and run long, so the badge
	/// shows shortened forms and keeps the full pair in its tooltip.
	/// </summary>
	private void UpdateSessionInfo()
	{
		if (_chat is not null && _sessionId is not null)
		{
			var model = ShortText.Model(_sessionModel);
			sessionInfo.Text = model.Length == 0
				? ShortText.SessionId(_sessionId)
				: $"{model} | {ShortText.SessionId(_sessionId)}";
			sessionInfo.ToolTip = string.IsNullOrEmpty(_sessionModel)
				? _sessionId
				: $"{_sessionModel}\r\n{_sessionId}";
		}
		else
		{
			sessionInfo.Text = "No session active";
			sessionInfo.ToolTip = null;
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
	// from prepared Rendered content.

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
	/// Session menu: opens the Session Settings dialog, which begins a
	/// session or changes the running one depending on what is chosen
	/// there.
	/// </summary>
	private async void OnNewSession(object sender, RoutedEventArgs e) => await OpenSettingsDialogAsync();

	/// <summary>
	/// Shows the Session Settings dialog. Accepting brings the
	/// session-dependent controls online, refreshes the customization
	/// lists for the new workspace and starts the streaming session
	/// with the gathered options, or applies the changes to the running
	/// session when that is all they amount to.
	/// </summary>
	private async Task OpenSettingsDialogAsync()
	{
		try
		{
			var dialog = new Dialogs.SettingsDialog(ActiveWorkspacePath, IsSessionActive ? _chat?.RequestedOptions : null);
			dialog.ShowDialog(this);

			if (dialog.BeginRequested && dialog.Result is { WorkspaceFolder.Length: > 0 } options)
				await StartFromSettingsAsync(options);
		}
		catch (Exception ex)
		{
			AppendOutput($"\r\n[error] Cannot open session settings: {ex.Message}\r\n\r\n");
		}
	}

	private async Task StartFromSettingsAsync(ChatSessionOptions options)
	{
		// The readme is offered after the session-change state is cleared,
		// so the send is an ordinary prompt against a settled session.
		var offerReadme = false;
		var previousWorkspace = IsSessionActive ? ActiveWorkspacePath : null;
		using var cancellation = new CancellationTokenSource();
		_restartCancellation = cancellation;
		_sessionChanging = true;
		SetSessionActive(IsSessionActive);
		try
		{
			var current = IsSessionActive ? _chat : null;
			var change = current is null ? SessionChange.Fresh : SessionChanges.Classify(current.RequestedOptions, options);
			if (change == SessionChange.None)
				return;
			if (change == SessionChange.Live)
			{
				try
				{
					if (await current!.ApplyLiveChangesAsync(options, cancellation.Token))
						ShowNotice("The changes apply when the current turn ends.");
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					AppendOutput($"\r\n[error] Cannot apply the changes to the running session: {ex.Message}\r\n\r\n");
				}
				return;
			}
			SummaryBootstrap? bootstrap = null;
			if (change == SessionChange.Restart && current!.HasConversation
				&& YesNoDialog.Ask(this, "Carry a summary of this session into the new one?", "Restart Context"))
			{
				try
				{
					bootstrap = await current.PrepareHandoffAsync(cancellation.Token);
					cancellation.Token.ThrowIfCancellationRequested();
					if (bootstrap is null)
						MessageDialog.Ok(this, "The session has nothing to carry over. The new session will start without context.", "Restart Context");
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					MessageDialog.Ok(this, "Cannot prepare the hand-off: " + ex.Message + "\r\nThe new session will start without it.", "Restart Context");
				}
			}
			cancellation.Token.ThrowIfCancellationRequested();
			await StartChatCoreAsync(options, null, bootstrap);
			// Only a session on a project the user has not opened yet is
			// worth orienting. A session carrying a hand-off summary
			// already knows the project, and restarting the same workspace
			// to change a model or a setting does not make it new again.
			offerReadme = bootstrap is null
				&& !string.Equals(previousWorkspace, options.WorkspaceFolder, StringComparison.OrdinalIgnoreCase);
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
		finally
		{
			_restartCancellation = null;
			_sessionChanging = false;
			RefreshChatState();
		}
		if (offerReadme)
			await OfferWorkspaceReadmeAsync();
	}

	/// <summary>
	/// Offers to open a session on a new project by sending its readme, so
	/// the model starts with the project's own description of itself. The
	/// file goes as an attachment on a prompt that asks only for a summary.
	/// Declining, a workspace without a readme, and a session that failed
	/// to start all leave the transcript as it is.
	/// </summary>
	private async Task OfferWorkspaceReadmeAsync()
	{
		var chat = _chat;
		if (chat is null || !IsSessionActive || _closing)
			return;
		if (WorkspaceReadme.Find(ActiveWorkspacePath) is not { } readme)
			return;
		if (!YesNoDialog.Ask(this, WorkspaceReadme.Question(readme), "Project Readme"))
			return;
		_sendingInput = true;
		SetSessionActive(IsSessionActive);
		try { await chat.SendAsync(WorkspaceReadme.Prompt(readme), [readme]); }
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			var (text, rendered) = NoticeFormatter.Status("error", "Cannot send the readme: " + ex.Message);
			ForActiveChat(chat, () => chat.AddNotice(text, rendered));
		}
		finally
		{
			_sendingInput = false;
			RefreshChatState();
		}
	}

	/// <summary>
	/// Ends the active session: disposes the SDK session, gates the
	/// session-dependent controls back off and clears the workspace
	/// scope. Session history and settings persist; only the live
	/// session state goes away. There is no menu entry for this: a
	/// session ends when another replaces it or the window closes.
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
			// A resume arrives carrying the lists its session ran with, and
			// those stand. Anything else scans the roots afresh for the
			// workspace it is about to open.
			options = options with
			{
				Customizations = (resumeId is not null && options.Customizations.HasItems
					? options.Customizations
					: _collectCustomizations(options.WorkspaceFolder)).Clone(),
			};
			await EndChatCoreAsync();
			cancellation.Token.ThrowIfCancellationRequested();

			var chat = _createChat();
			_chat = chat;
			chat.TranscriptReceived += text => ForActiveChat(chat, () => AppendRawOutput(text));
			chat.RenderedReceived += text => ForActiveChat(chat, () => AppendRenderedOutput(text));
			chat.RenderedReplaced += text => ForActiveChat(chat, () => ReplaceRenderedOutput(text));
			chat.NoticeReceived += text => ForActiveChat(chat, () => ShowNotice(text));
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
			noticeTextBlock.Text = "";
			noticeTextBlock.Visibility = Visibility.Collapsed;
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
				ShowNotice("Context from the previous session will accompany your next prompt.");
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

	/// <summary>
	/// Recomputes the status phrase and plays the cue for any transition
	/// worth hearing. The phrase is the state, so comparing the previous
	/// one to the next is the whole test: a turn that ends, and a model
	/// that stops to ask, are the two moments a user reading another
	/// window needs to be told about.
	/// </summary>
	private void RefreshChatState()
	{
		var previous = _statusBase;
		_statusBase = _sessionChanging ? "Starting.."
			: _chat?.HasPendingQuestion == true ? "Waiting.."
			: _chat?.IsWorking == true ? "Working.." : "Ready..";
		if (_statusBase != previous && IsSessionActive)
		{
			if (_statusBase == "Waiting..")
				Sounds.PlayAttention();
			else if (_statusBase == "Ready.." && previous is "Working.." or "Waiting..")
				Sounds.PlayTurnComplete();
		}
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
			// The session's own grants come back before it connects, so the
			// first permission check of the resumed session answers the way
			// the original session would have.
			var permissionsRestored = Permissions.PermissionService.Restore(options.WorkspaceFolder, selected.Permissions);
			await StartChatAsync(options, selected.SessionId);
			if (permissionsRestored && _chat is not null)
			{
				var chat = _chat;
				var (text, rendered) = NoticeFormatter.Status("permissions",
					"Restored the folder grants and approved operations saved with this session.");
				chat.AddNotice(text, rendered);
			}
		}
		catch (Exception ex)
		{
			AppendOutput($"\r\n[error] Cannot open saved session: {ex.Message}\r\n\r\n");
		}
	}

	/// <summary>
	/// The options a saved session resumes on. The customization lists
	/// come from the session's own snapshot when it has one, so the
	/// resumed session runs with the instructions, skills, agents and
	/// servers it had rather than whatever is enabled today; a session
	/// saved before snapshots existed falls back to a fresh scan. The
	/// BYOK key is never stored, so it has to come from settings, and it
	/// only applies when the endpoint still matches the one saved.
	/// </summary>
	internal static ChatSessionOptions RestoreSessionOptions(SessionRecord saved, Settings settings)
	{
		var options = saved.Options;
		if (saved.Customizations.HasItems)
			options = options with { Customizations = saved.Customizations.Clone() };
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
		Sounds.PlayPromptSent();
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
	/// Ctrl+Enter sends, like every other chat front end this machine has
	/// ever run. The handler is on the window rather than the prompt box
	/// so the shortcut works wherever focus happens to be: tunneling
	/// starts at the root, so this still runs first when the prompt box
	/// has focus, and handling the key there stops it reaching the
	/// editor as a newline.
	/// </summary>
	private void Input_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (e.Handled) return;
		if (!IsSendShortcut(e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key,
			System.Windows.Input.Keyboard.Modifiers))
			return;
		e.Handled = true;
		if (buttonSend.IsEnabled)
			_ = SendCurrentInputAsync();
	}

	/// <summary>
	/// Whether a key press is the Send shortcut. Enter alone is a newline
	/// in the prompt editor, so only Ctrl+Enter sends, and no other
	/// modifier may be along for the ride: Ctrl+Shift+Enter belongs to
	/// whatever claims it later, not to Send.
	/// </summary>
	internal static bool IsSendShortcut(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers) =>
		key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Return
		&& modifiers == System.Windows.Input.ModifierKeys.Control;

	// ── Session context ──────────────────────────────────────────────────────

	/// <summary>
	/// Session menu: asks the runtime to compact the conversation, which
	/// replaces the older turns with a summary and frees the tokens they
	/// were holding. The transcript on screen is left alone: it is the
	/// record of what happened, and compaction changes only what the
	/// model still carries.
	/// </summary>
	private async void OnCompactContext(object sender, RoutedEventArgs e)
	{
		var chat = _chat;
		if (chat is null || !IsSessionActive || _sendingInput || _closing) return;
		if (!YesNoDialog.Ask(this,
			"Compact this session's context? Older turns are replaced by a summary.\r\n\r\n"
			+ "The transcript on screen is unchanged.", "Compact Context"))
			return;

		_sendingInput = true;
		SetSessionActive(IsSessionActive);
		try { await chat.CompactContextAsync(); }
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			var (text, rendered) = NoticeFormatter.Status("error", "Cannot compact the context: " + ex.Message);
			ForActiveChat(chat, () => chat.AddNotice(text, rendered));
		}
		finally
		{
			_sendingInput = false;
			RefreshChatState();
		}
	}

	/// <summary>
	/// Session menu: throws the conversation away and reconnects on the
	/// same settings, so the model starts from nothing. This is the
	/// deliberate opposite of the restart hand-off: no summary travels,
	/// which is the point. The transcript is kept and a banner marks
	/// where the context was dropped.
	/// </summary>
	private async void OnResetContext(object sender, RoutedEventArgs e)
	{
		var chat = _chat;
		if (chat is null || !IsSessionActive || _sendingInput || _closing) return;
		if (!YesNoDialog.Ask(this,
			"Reset this session's context? The model forgets the conversation so far.\r\n\r\n"
			+ "The transcript on screen is kept, and nothing is carried into the new session.",
			"Reset Context"))
			return;

		var options = chat.RequestedOptions;
		var transcript = OutputText;
		var rendered = RenderedText;
		await StartChatCoreAsync(options, null, null);
		if (_chat is null)
			return;
		// StartChatCoreAsync clears both views for the new session. The
		// conversation is gone from the model, not from the record of it.
		ClearOutput();
		AppendRawOutput(transcript);
		AppendRenderedOutput(rendered);
		var (text, banner) = NoticeFormatter.Status("reset", "Context cleared. The model starts from nothing.");
		_chat.AddNotice(text, banner);
	}

	// -- Tools -----------------------------------------------------------------

	/// <summary>
	/// The Tools menu opens an external program on the session's workspace
	/// folder. The menu is disabled without a session, so a missing
	/// workspace here means the folder was removed while the session ran.
	/// Each program is separate from the app: it is started and left alone,
	/// and a failure to start it is reported without disturbing the session.
	/// </summary>
	private void OpenTool(string name, Func<string, System.Diagnostics.ProcessStartInfo> build)
	{
		var workspace = ActiveWorkspacePath;
		if (string.IsNullOrEmpty(workspace) || !System.IO.Directory.Exists(workspace))
		{
			MessageDialog.Ok(this, "The session workspace folder is not available.", name);
			return;
		}
		try { ExternalTools.Start(build(workspace)); }
		catch (Exception ex)
		{
			MessageDialog.Ok(this, $"Could not open {name}: {ex.Message}", name);
		}
	}

	private void OnOpenPowerShell(object sender, RoutedEventArgs e)
	{
		string? scripts = null;
		// A helper file that cannot be written still leaves a usable shell.
		try { scripts = ExternalTools.EnsureScripts(_scriptsPath); }
		catch (InvalidOperationException ex) { ShowNotice(ex.Message); }
		OpenTool("PowerShell", workspace => ExternalTools.BuildPowerShell(workspace, scripts));
	}

	private void OnOpenExplorer(object sender, RoutedEventArgs e) =>
		OpenTool("Explorer", ExternalTools.BuildExplorer);

	private void OnOpenVsCode(object sender, RoutedEventArgs e) =>
		OpenTool("VS Code", ExternalTools.BuildVsCode);

	private void ShowNotice(string text)
	{
		noticeTextBlock.Text = text;
		noticeTextBlock.ToolTip = text;
		noticeTextBlock.Visibility = Visibility.Visible;
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
		if (_closeRequested)
			return;
		_closeRequested = true;
		try
		{
			// Cleanup may finish synchronously. Leave WPF's Closing callback
			// before showing dialogs or calling Close again.
			await System.Windows.Threading.Dispatcher.Yield(
				System.Windows.Threading.DispatcherPriority.Normal);
			if (!Dialogs.YesNoDialog.Ask(this, "Exit the Program?"))
				return;
			_closing = true;
			await EndSessionAsync();
			_closeApproved = true;
			Close();
		}
		finally
		{
			_closeRequested = false;
		}
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
