using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;
using TurbolandTheme.Wpf.Controls;
using TurboPilot.Dialogs;

namespace TurboPilot;

public partial class MainWindow : TurbolandWindow
{
	// The single file: URI that the WebView2 is allowed to navigate to.
	private string? _outputHtmlUri;
	private bool _webViewReady = false;

	// The verbatim transcript shown in the Raw tab. It is the source of
	// truth for both output views: the Rendered WebView2 always mirrors
	// this buffer, so a full re-sync is possible at any time (e.g. after
	// the page (re)loads).
	private readonly StringBuilder _outputText = new();

	// Attachments tracking
	private readonly List<string> _attachments = new();

	public MainWindow()
	{
		InitializeComponent();

		// Seed the transcript through the shared API so Raw and Rendered
		// start in sync.
		AppendOutput("Welcome to TurboPilot!\r\n");
		AppendOutput("This is the Raw output tab with retro styling.\r\n");
		AppendOutput("Blue background with yellow text.\r\n");
		AppendOutput("Support for white, red, green, and black text.\r\n");

		// Initialize WebView2 asynchronously
		_ = InitializeWebViewAsync();
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

	// ── New Session ──────────────────────────────────────────────────────────

	/// <summary>
	/// Opens the New Session dialog to select a workspace folder.
	/// </summary>
	private void OnNewSession(object sender, RoutedEventArgs e)
	{
		var dialog = new Dialogs.ChooseFolderDialog();
		if (dialog.ShowDialog(this) != true || string.IsNullOrEmpty(dialog.WorkspacePath))
			return;

		// TODO: Start the AI session with the selected workspace folder
		// For now, just update the status bar
		statusTextBlock.Text = $"Session started in: {dialog.WorkspacePath}";
	}
}
