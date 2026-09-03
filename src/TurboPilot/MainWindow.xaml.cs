using System.Diagnostics;
using System.Linq;
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

	// Attachments tracking
	private readonly List<string> _attachments = new();
	private AttachmentDialog? _currentAttachmentDialog;

	public MainWindow()
	{
		InitializeComponent();

		// Set default text for Raw tab
		richTextBoxOutput.AppendText("Welcome to TurboPilot!\r\n");
		richTextBoxOutput.AppendText("This is the Raw output tab with retro styling.\r\n");
		richTextBoxOutput.AppendText("Blue background with yellow text.\r\n");
		richTextBoxOutput.AppendText("Support for white, red, green, and black text.\r\n");

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
		_currentAttachmentDialog = new AttachmentDialog();
		_currentAttachmentDialog.OnClose += OnAttachmentDialogClosed;
		Dialogs.Show(_currentAttachmentDialog);
	}

	/// <summary>
	/// Called when the attachment dialog closes. Adds any new paths
	/// to the attachment list and updates the button counter.
	/// </summary>
	private void OnAttachmentDialogClosed(bool accepted)
	{
		if (!accepted || _currentAttachmentDialog == null) return;

		foreach (var path in _currentAttachmentDialog.AddedPaths)
		{
			if (!_attachments.Contains(path))
				_attachments.Add(path);
		}
		_currentAttachmentDialog = null;
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
				_webViewReady = true;
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
			case "file-link-click":
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

			case "file-link-reveal":
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
	/// Safety net: if a navigation completes and we are NOT on our
	/// expected output.html page, re-navigate to output.html.
	/// </summary>
	private void WebView_NavigationCompleted(
		object? sender,
		Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
	{
		if (_outputHtmlUri == null || !_webViewReady) return;

		var currentUri = webViewOutput.CoreWebView2.Source;
		if (currentUri != null
			&& currentUri.Equals(_outputHtmlUri, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		// We are on an unexpected page -- recover by re-navigating
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

	// ── WebView2 JS bridge helpers ───────────────────────────────────────────

	/// <summary>
	/// Appends a markdown block to the WebView2 Rendered tab.
	/// </summary>
	public void AppendMarkdown(string id, string content)
	{
		if (!_webViewReady) return;
		var js = $"appendBlock({JsString(id)}, \"assistant\", \"\", {JsString(content)})";
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
	}

	/// <summary>
	/// Finalizes a streaming block in the WebView2.
	/// </summary>
	public void FinalizeBlock(string id)
	{
		if (!_webViewReady) return;
		var js = $"finalizeBlock({JsString(id)})";
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
	}

	/// <summary>
	/// Clears all output in the WebView2.
	/// </summary>
	public void ClearOutput()
	{
		if (!_webViewReady) return;
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync("clearAll()");
	}

	/// <summary>
	/// Appends a user prompt block to the WebView2.
	/// </summary>
	public void AppendUserPrompt(string id, string content)
	{
		if (!_webViewReady) return;
		var js = $"appendBlock({JsString(id)}, \"user\", \"\", {JsString(content)})";
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
	}

	/// <summary>
	/// Appends a status/meta message to the WebView2.
	/// </summary>
	public void AppendStatus(string id, string content)
	{
		if (!_webViewReady) return;
		var js = $"appendBlock({JsString(id)}, \"status\", \"\", {JsString(content)})";
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
	}

	/// <summary>
	/// Appends a tool activity block to the WebView2.
	/// </summary>
	public void AppendToolActivity(string id, string content)
	{
		if (!_webViewReady) return;
		var js = $"appendBlock({JsString(id)}, \"tool\", \"\", {JsString(content)})";
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
	}

	/// <summary>
	/// Appends a reasoning block to the WebView2.
	/// </summary>
	public void AppendReasoning(string id, string content)
	{
		if (!_webViewReady) return;
		var js = $"appendBlock({JsString(id)}, \"reasoning\", \"\", {JsString(content)})";
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
	}

	/// <summary>
	/// Appends an error block to the WebView2.
	/// </summary>
	public void AppendError(string id, string content)
	{
		if (!_webViewReady) return;
		var js = $"appendBlock({JsString(id)}, \"error\", \"\", {JsString(content)})";
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
	}

	/// <summary>
	/// Appends a thinking indicator to the WebView2.
	/// </summary>
	public void AppendThinking(string id)
	{
		if (!_webViewReady) return;
		var js = $"appendThinking({JsString(id)})";
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
	}

	/// <summary>
	/// Removes a thinking indicator from the WebView2.
	/// </summary>
	public void RemoveThinking(string id)
	{
		if (!_webViewReady) return;
		var js = $"removeThinking({JsString(id)})";
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
	}

	/// <summary>
	/// Appends a collapsible section (reasoning or tools) to the WebView2.
	/// </summary>
	public void AppendSection(string id, string sectionKind, string summaryText, bool defaultOpen)
	{
		if (!_webViewReady) return;
		var collapse = !defaultOpen;
		var js = $"appendSection({JsString(id)}, {JsString(sectionKind)}, {JsString(summaryText)}, {collapse})";
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
	}

	/// <summary>
	/// Updates the content of a section in the WebView2.
	/// </summary>
	public void SetSectionContent(string sectionId, string content, bool isMarkdown)
	{
		if (!_webViewReady) return;
		var js = $"setSectionContent({JsString(sectionId)}, {JsString(content)}, {isMarkdown})";
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
	}

	/// <summary>
	/// Closes a section in the WebView2.
	/// </summary>
	public void CloseSection(string id, string summaryText, bool collapse, bool hasFailure)
	{
		if (!_webViewReady) return;
		var js = $"closeSection({JsString(id)}, {JsString(summaryText)}, {collapse}, {hasFailure})";
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
	}

	/// <summary>
	/// Appends a tool line inside a tool group section.
	/// </summary>
	public void AppendSectionLine(string sectionId, string lineId, string html)
	{
		if (!_webViewReady) return;
		var js = $"appendSectionLine({JsString(sectionId)}, {JsString(lineId)}, {JsString(html)})";
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
	}

	/// <summary>
	/// Updates an existing tool line's innerHTML.
	/// </summary>
	public void UpdateSectionLine(string lineId, string html)
	{
		if (!_webViewReady) return;
		var js = $"updateSectionLine({JsString(lineId)}, {JsString(html)})";
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
	}

	/// <summary>
	/// Marks a tool line as failed.
	/// </summary>
	public void MarkSectionLineFailed(string lineId)
	{
		if (!_webViewReady) return;
		var js = $"markSectionLineFailed({JsString(lineId)})";
		_ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
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
		// Opening the same dialog twice should raise the existing one rather
		// than stack a duplicate on the host.
		AboutDialog? existing = Dialogs.Dialogs.OfType<AboutDialog>().FirstOrDefault();
		if (existing is not null)
		{
			Dialogs.BringToFront(existing);
			existing.FocusFirstControl();
			return;
		}

		Dialogs.Show(new AboutDialog
		{
			WebsiteUrl = "https://github.com/mighty-studios/TurboPilot"
		});
	}
}
