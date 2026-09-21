using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;
using TurbolandTheme.Wpf.Controls;
using TurboPilot.Dialogs;

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

	// The verbatim transcript shown in the Raw tab. It is the source of
	// truth for both output views: the Rendered WebView2 always mirrors
	// this buffer, so a full re-sync is possible at any time (e.g. after
	// the page (re)loads).
	private readonly StringBuilder _outputText = new();

	// Attachments tracking
	private readonly List<string> _attachments = new();

	// The live AI session, or null between sessions.
	private Ai.ChatService? _chat;

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

	public MainWindow()
	{
		InitializeComponent();

		// The Raw tab reads its face and ink from the same palette table the
		// Rendered tab is styled from, using the editor pair: yellow source
		// text on the blue field. The typeface is the theme's bundled DOS
		// face, set in XAML.
		richTextBoxOutput.Background = new SolidColorBrush(BorlandVisionTheme.RawBackgroundColor);
		richTextBoxOutput.Foreground = new SolidColorBrush(BorlandVisionTheme.RawForegroundColor);
		richTextBoxOutput.FontSize = BorlandVisionTheme.RawFontSize;

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
	/// Once prompt history cycling exists, the history buttons will be
	/// further restricted to times when there is somewhere to cycle to.
	/// </summary>
	public void SetSessionActive(bool active)
	{
		IsSessionActive = active;
		if (!active)
			ActiveWorkspacePath = null;

		menuTools.IsEnabled = active;
		menuEndSession.IsEnabled = active;

		richTextBoxInput.IsEnabled = active;
		buttonHistoryPrev.IsEnabled = active;
		buttonHistoryNext.IsEnabled = active;

		buttonAttachments.IsEnabled = active;
		buttonStop.IsEnabled = active;
		buttonSend.IsEnabled = active;

		// The prompt box only accepts typing while a session is running.
		richTextBoxInput.IsReadOnly = !active;

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
	/// Handles dragging the splitter Thumb to resize the output/input panels.
	/// Positive delta (drag down) increases the input area and shrinks output.
	/// Negative delta (drag up) increases the output area and shrinks input.
	/// </summary>
	private void SplitterThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
	{
		var delta = e.VerticalChange;
		var outputHeight = OutputRow.ActualHeight + delta;
		var inputHeight = InputRow.ActualHeight - delta;

		// Minimum sizes: 80px for output, 40px for input
		const double minOutput = 80;
		const double minInput = 40;

		if (outputHeight >= minOutput && inputHeight >= minInput)
		{
			OutputRow.Height = new GridLength(outputHeight);
			InputRow.Height = new GridLength(inputHeight);
		}
	}

	// ── Attachments ──────────────────────────────────────────────────────────

	/// <summary>
	/// Opens the attachment dialog when the +0 button is clicked.
	/// Updates the button text to show the current attachment count.
	/// </summary>
	private void ButtonAttachments_Click(object sender, RoutedEventArgs e)
	{
		var dialog = new AttachmentDialog();
		dialog.Initialize();
		if (dialog.ShowDialog(this) != true) return;

		foreach (var path in dialog.AddedPaths)
		{
			if (!_attachments.Contains(path))
				_attachments.Add(path);
		}
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
			var userDataFolder = System.IO.Path.Combine(
				System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
				"TurboPilot", "webview2-default");
			System.IO.Directory.CreateDirectory(userDataFolder);

			var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment
				.CreateAsync(null, userDataFolder);
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
				// Fall back to Raw tab if assets are missing
				// (TabControl may not have tabPageRaw yet — best effort)
			}
		}
		catch
		{
			// WebView2 runtime not available — fall back to Raw tab
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
		if (_outputHtmlUri == null) return;

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
	// The Raw tab is the source of truth. AppendOutput and ClearOutput
	// write the verbatim text there and mirror the same content into the
	// Rendered WebView2, which displays it as rendered markdown with
	// Mermaid diagrams and inline images. Both views are read-only; all
	// writes go through this API.

	/// <summary>
	/// The full verbatim transcript currently displayed in the Raw tab.
	/// </summary>
	public string OutputText => _outputText.ToString();

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

		_outputText.Append(text);
		richTextBoxOutput.AppendText(text);
		PushToRenderer($"appendTranscript({JsString(text)})");
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
		richTextBoxOutput.Document.Blocks.Clear();
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
	/// Replaces the Rendered tab content with the full transcript buffer
	/// so it matches the Raw tab exactly.
	/// </summary>
	private void SyncRenderedTranscript()
	{
		if (!_webViewReady) return;
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync($"setTranscript({JsString(_outputText.ToString())})");

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

	// ── History navigation stubs ─────────────────────────────────────────────

	/// <summary>
	/// Handles the Previous (▲) history button click.
	/// TODO: Implement prompt history cycling to older prompts.
	/// </summary>
	private void ButtonHistoryPrev_Click(object sender, RoutedEventArgs e)
	{
		// TODO: Navigate to the previous (older) prompt in history
	}

	/// <summary>
	/// Handles the Next (▼) history button click.
	/// TODO: Implement prompt history cycling to newer prompts.
	/// </summary>
	private void ButtonHistoryNext_Click(object sender, RoutedEventArgs e)
	{
		// TODO: Navigate to the next (newer) prompt in history
	}

	// ── Session settings ─────────────────────────────────────────────────────

	/// <summary>
	/// Opens the Session Settings dialog from New Session.
	/// </summary>
	private void OnNewSession(object sender, RoutedEventArgs e) => OpenSettingsDialog();

	/// <summary>
	/// Opens the Session Settings dialog from the menu.
	/// </summary>
	private void OnSettings(object sender, RoutedEventArgs e) => OpenSettingsDialog();

	/// <summary>
	/// Shows the Session Settings dialog. Begin Session brings the
	/// session-dependent controls online, refreshes the customization
	/// lists for the new workspace and starts the Copilot SDK session
	/// with the gathered options. Ending a session is the Session menu's
	/// End Session item.
	/// </summary>
	private void OpenSettingsDialog()
	{
		var dialog = new Dialogs.SettingsDialog(ActiveWorkspacePath, IsSessionActive);
		dialog.ShowDialog(this);

		if (dialog.BeginRequested && !string.IsNullOrEmpty(dialog.WorkspacePath))
		{
			SetSessionActive(true);
			ActiveWorkspacePath = dialog.WorkspacePath;
			Customizations.CustomizationService.Rescan(dialog.WorkspacePath);
			_ = StartChatAsync(dialog);
		}
	}

	/// <summary>
	/// Session menu: ends the active session.
	/// </summary>
	private void OnEndSessionClick(object sender, RoutedEventArgs e) => EndSession();

	/// <summary>
	/// Ends the active session: disposes the SDK session, gates the
	/// session-dependent controls back off and clears the workspace
	/// scope. Session history and settings persist; only the live
	/// session state goes away.
	/// </summary>
	public void EndSession()
	{
		var chat = _chat;
		_chat = null;
		_sessionModel = null;
		_sessionId = null;
		if (chat is not null)
			_ = chat.DisposeAsync().AsTask();

		_ctxTotal = 0;
		_ctxUsed = 0;
		_aic = 0;
		_showAic = false;

		SetSessionActive(false);
	}

	// -- Chat round-trip ---------------------------------------------------------

	/// <summary>
	/// Starts the Copilot SDK session described by the settings dialog and
	/// wires its events into the transcript and the status line. A failed
	/// start is reported in the transcript and drops back to no session.
	/// </summary>
	private async Task StartChatAsync(Dialogs.SettingsDialog dialog)
	{
		var previous = _chat;
		_chat = null;
		_sessionModel = null;
		_sessionId = null;
		UpdateSessionInfo();
		if (previous is not null)
			await previous.DisposeAsync();

		_statusBase = "Starting..";
		_ctxUsed = 0;
		_ctxTotal = 0;
		_aic = 0;
		// Credits only exist against the Copilot service; a BYOK server
		// has no meter to read.
		_showAic = dialog.Provider != "Byok";
		UpdateStatus();

		var chat = new Ai.ChatService();
		chat.DeltaReceived += AppendOutput;
		chat.StatusReceived += line => AppendOutput($"\r\n{line}\r\n");
		chat.ErrorReceived += line => AppendOutput($"\r\n[error] {line}\r\n");
		chat.QuestionReceived += text =>
		{
			AppendOutput($"\r\n{text}\r\n\r\n");
			SetStatusBase("Waiting..");
		};
		chat.TurnIdle += () => SetStatusBase("Ready..");
		chat.UsageChanged += () => Dispatcher.BeginInvoke(() =>
		{
			_ctxUsed = chat.ContextUsedTokens;
			_aic = chat.AicUsed;
			UpdateStatus();
		});

		try
		{
			await chat.StartAsync(new Ai.ChatSessionOptions
			{
				WorkspaceFolder = dialog.WorkspacePath,
				Model = dialog.SelectedModel,
				ReasoningEffort = string.IsNullOrEmpty(dialog.SelectedEffort) ? null : dialog.SelectedEffort,
				Mode = dialog.SelectedMode,
				ContextWindowTokens = dialog.SelectedContextWindowTokens,
				UseByok = dialog.Provider == "Byok",
				ByokEndpoint = dialog.ByokEndpoint,
				ByokApiKey = dialog.ByokApiKey,
			});
		}
		catch (Exception ex)
		{
			await chat.DisposeAsync();
			AppendOutput($"\r\n[error] Session start failed: {ex.Message}\r\n\r\n");
			EndSession();
			return;
		}

		_chat = chat;
		_sessionModel = dialog.SelectedModel;
		_sessionId = chat.SessionId;
		_ctxTotal = chat.ContextWindowTokens;
		UpdateSessionInfo();
		AppendOutput($"\r\n--- Session {chat.SessionId} | {dialog.SelectedModel} | {dialog.SelectedMode} ---\r\n\r\n");
		SetStatusBase("Ready..");
	}

	/// <summary>
	/// Sets the status base phrase and repaints the status line. Safe to
	/// call from SDK event threads.
	/// </summary>
	private void SetStatusBase(string text)
	{
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action<string>(SetStatusBase), text);
			return;
		}

		_statusBase = text;
		UpdateStatus();
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

		var text = _statusBase;
		if (_ctxTotal > 0)
			text += $" {_ctxUsed / 1024}/{_ctxTotal / 1024}K";
		if (_showAic)
			text += $" AiC={_aic:0}";
		statusTextBlock.Text = text;
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

		await chat.AbortAsync();
		AppendOutput("\r\n[stopped] Turn interrupted\r\n\r\n");
		SetStatusBase("Ready..");
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
		if (chat is null) return;

		var text = GetInputText();
		if (string.IsNullOrWhiteSpace(text) && _attachments.Count == 0) return;

		richTextBoxInput.Document.Blocks.Clear();

		// The next message answers an outstanding question (model question
		// or permission prompt) instead of starting a new turn.
		if (chat.HasPendingQuestion)
		{
			AppendOutput($"\r\n**You:** {text}\r\n\r\n");
			chat.TryAnswerPending(text);
			SetStatusBase("Working..");
			return;
		}

		if (string.IsNullOrWhiteSpace(text)) return;

		if (chat.IsWorking)
		{
			await chat.AbortAsync();
			// Give the runtime's idle event a moment to land so the new
			// turn's Working status is not clobbered by the old turn's
			// idle. The cap keeps a missed event from wedging the send.
			var sw = Stopwatch.StartNew();
			while (chat.IsWorking && sw.ElapsedMilliseconds < 1500)
				await Task.Delay(50);
		}

		var attachments = _attachments.ToArray();
		_attachments.Clear();
		UpdateAttachmentButton();

		AppendOutput($"\r\n**You:** {text}\r\n\r\n");

		try
		{
			await chat.SendAsync(text, attachments);
			SetStatusBase("Working..");
		}
		catch (Exception ex)
		{
			AppendOutput($"\r\n[error] Send failed: {ex.Message}\r\n\r\n");
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
}
