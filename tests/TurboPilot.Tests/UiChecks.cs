using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using TurboPilot.Ai;
using TurboPilot.Dialogs;

namespace TurboPilot.Tests;

internal static class UiChecks
{
	public static Task RunAsync(bool closingOnly = false)
	{
		var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var thread = new Thread(() =>
		{
			Exception? failure = null;
			var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
			application.Startup += async (_, _) =>
			{
				try
				{
					if (!closingOnly)
						await RunWindowChecksAsync(application);
					await RunClosingChecksAsync(application);
				}
				catch (Exception ex) { failure = ex; }
				finally { application.Shutdown(); }
			};
			application.Run();
			if (failure is null)
				finished.TrySetResult();
			else
				finished.TrySetException(failure);
		});
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		return finished.Task;
	}

	private static async Task RunClosingChecksAsync(Application application)
	{
		var unhandled = new List<Exception>();
		void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
		{
			unhandled.Add(e.Exception);
			e.Handled = true;
		}
		application.DispatcherUnhandledException += OnUnhandled;
		try
		{
			for (var scenario = 0; scenario < 2; scenario++)
			{
				using var workspace = new TestWorkspace();
				await using var provider = new LocalProvider();
				TurbolandTheme.Wpf.TurbolandTheme.Apply(application, TurbolandTheme.Core.ThemeMode.Authentic);
				var window = new MainWindow(workspace.Store, workspace.CreateChat, _ => new(),
					Path.Combine(workspace.Root, "browser"));
				TurbolandTheme.Wpf.TurbolandTheme.ApplyTo(window);
				var closed = false;
				window.Closed += (_, _) => closed = true;
				var webView = Control<WebView2>(window, "webViewOutput");
				var browserExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
				var browserObserved = false;
				LocalProvider.Reply? slowReply = null;
				var sessionChange = Field<SemaphoreSlim>(window, "_sessionChange");
				var sessionChangeHeld = false;
				try
				{
					window.Show();
					await Check.UntilAsync(() => Field<bool>(window, "_webViewReady"), "The close fixture did not initialize.");
					webView.CoreWebView2.Environment.BrowserProcessExited += (_, _) => browserExited.TrySetResult();
					browserObserved = true;
					if (scenario == 0)
					{
						foreach (var useCloseBox in new[] { false, true })
						{
							var answered = false;
							using var answer = DialogAction<YesNoDialog>(application, dialog =>
							{
								answered = true;
								if (useCloseBox) dialog.Close();
								else Invoke(dialog, "OnNo", dialog, new RoutedEventArgs());
							});
							if (useCloseBox) window.Close();
							else Invoke(window, "OnExitClick", window, new RoutedEventArgs());
							await Check.UntilAsync(() => answered || unhandled.Count > 0, "The exit confirmation was not shown.");
							await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
							Check.True(unhandled.Count == 0, "Declining exit must not throw: " + string.Join("\r\n", unhandled));
							Check.True(!closed && window.IsVisible, "No and the confirmation close box must keep the main window open.");
							Check.True(!Field<bool>(window, "_closing"), "Declining exit must not start shutdown.");
						}
					}
					if (scenario == 1)
					{
						await InvokeTask(window, "StartChatAsync", new ChatSessionOptions
						{
							WorkspaceFolder = workspace.Workspace, Model = "test-model", UseByok = true,
							ByokEndpoint = provider.Endpoint, ContextWindowTokens = 32768,
						}, null);
						slowReply = new LocalProvider.Reply("Response interrupted by application exit.", Hold: true);
						provider.Replies.Enqueue(slowReply);
						await Field<ChatService>(window, "_chat").SendAsync("Keep this turn active.");
						await slowReply.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
						// An unfinished session change holds the exit cleanup until it completes.
						await sessionChange.WaitAsync();
						sessionChangeHeld = true;
					}

					var confirmations = 0;
					var closeReturned = false;
					var confirmationDeferred = false;
					var errorNotices = 0;
					using var confirm = DialogAction<YesNoDialog>(application, dialog =>
					{
						confirmations++;
						confirmationDeferred = closeReturned;
						Invoke(dialog, "OnYes", dialog, new RoutedEventArgs());
					});
					using var acknowledge = DialogAction<MessageDialog>(application, dialog =>
					{
						errorNotices++;
						Invoke(dialog, "OnOk", dialog, new RoutedEventArgs());
					});
					window.Close();
					closeReturned = true;
					window.Close();
					if (scenario == 1)
					{
						await Check.UntilAsync(() => Field<bool>(window, "_closing") || unhandled.Count > 0, "Asynchronous shutdown did not start.");
						Check.True(!closed && window.IsSessionActive, "Wait for the unfinished session change before ending the session and closing.");
						window.Close();
						window.Close();
						await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
						// The dialog helper answers only the first confirmation, so look for another one directly.
						Check.True(!application.Windows.OfType<YesNoDialog>().Any(), "Do not repeat confirmation during asynchronous cleanup.");
						sessionChange.Release();
						sessionChangeHeld = false;
					}
					await Check.UntilAsync(() => closed || unhandled.Count > 0, "Confirmed shutdown did not close the window.");
					await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
					Check.True(unhandled.Count == 0, "Closing must not raise an unhandled exception: " + string.Join("\r\n", unhandled));
					Check.True(closed && confirmationDeferred, "Leave the original Closing event before showing confirmation and closing again.");
					Check.Equal(1, confirmations, "Confirm exit exactly once");
					Check.True(!window.IsSessionActive, "End the session before closing.");
					Check.Equal(0, errorNotices, "Exit without error notices");
					Check.Equal(0, provider.Errors.Count, "Stop an active response without provider failures");
				}
				finally
				{
					if (sessionChangeHeld)
						sessionChange.Release();
					slowReply?.Release.TrySetResult();
					if (!closed)
					{
						foreach (Window dialog in window.OwnedWindows.Cast<Window>().ToArray())
							dialog.Close();
						await window.EndSessionAsync();
						SetField(window, "_closing", true);
						SetField(window, "_closeApproved", true);
						window.Close();
					}
					if (browserObserved)
						await browserExited.Task.WaitAsync(TimeSpan.FromSeconds(15));
				}
			}
			Console.WriteLine("PASS real window-close confirmation, synchronous and asynchronous cleanup, and repeated requests");
		}
		finally { application.DispatcherUnhandledException -= OnUnhandled; }
	}

	private static async Task RunWindowChecksAsync(Application application)
	{
		using var workspace = new TestWorkspace();
		await using var provider = new LocalProvider();
		var library = workspace.CreateLibrary();
		TurbolandTheme.Wpf.TurbolandTheme.Apply(application, TurbolandTheme.Core.ThemeMode.Authentic);
		var window = new MainWindow(workspace.Store, workspace.CreateChat, _ => library, Path.Combine(workspace.Root, "browser"),
			workspace.ScriptsPath);
		TurbolandTheme.Wpf.TurbolandTheme.ApplyTo(window);
		var options = new ChatSessionOptions
		{
			WorkspaceFolder = workspace.Workspace,
			Model = "test-model",
			UseByok = true,
			ByokEndpoint = provider.Endpoint,
			ContextWindowTokens = 32768,
		};
		var webView = Control<WebView2>(window, "webViewOutput");
		try
		{
			Check.True(!window.IsSessionActive, "Start without an active session.");
			Check.True(!Control<Button>(window, "buttonSend").IsEnabled, "Disable Send before startup.");
			Check.True(!Control<MenuItem>(window, "menuTools").IsEnabled, "Offer no tools without a workspace to open them on.");
			Check.Equal("Start or resume a session to begin.", Status(window), "Initial status");
			window.Show();
			await Check.UntilAsync(() => Field<bool>(window, "_webViewReady"), "The rendered output did not initialize.");
			await CheckPromptLayoutAsync(window);

			var starting = InvokeTask(window, "StartChatAsync", options, null);
			Check.True(Status(window).StartsWith("Starting.."), "Show Starting while connecting.");
			Check.True(!Control<Button>(window, "buttonSend").IsEnabled, "Disable Send while connecting.");
			await starting.WaitAsync(TimeSpan.FromSeconds(45));
			Check.True(window.IsSessionActive && Status(window).StartsWith("Ready.."), "Enable a fully started session.");
			Check.True(Control<Button>(window, "buttonSend").IsEnabled, "Enable Send after startup.");
			Check.True(!Status(window).Contains("AiC="), "Do not show cloud credits for a local provider.");
			CheckTools(application, window, workspace);

			var reply = new LocalProvider.Reply("**UI streaming reply**\n\n```mermaid\ngraph TD\nA[Input] --> B[Output]\n```");
			provider.Replies.Enqueue(reply);
			SetInput(window, "UI prompt marker");
			Click(window, "buttonSend");
			await reply.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
			await Check.UntilAsync(() => Status(window).StartsWith("Ready..") && window.OutputText.Contains("**UI streaming reply**"),
				"The Send button did not complete its turn.");
			var chat = Field<ChatService>(window, "_chat");
			Check.Equal(chat.Transcript, window.OutputText, "The main window must display the service transcript");
			var raw = Control<RichTextBox>(window, "richTextBoxOutput");
			var rawText = new TextRange(raw.Document.ContentStart, raw.Document.ContentEnd).Text;
			Check.Equal(Normalize(window.OutputText), Normalize(rawText), "The Raw tab must contain the complete transcript");
			Check.Equal("", Input(window), "Clear accepted input");

			var rendered = false;
			for (var attempt = 0; attempt < 100 && !rendered; attempt++)
			{
				using var result = JsonDocument.Parse(await webView.CoreWebView2.ExecuteScriptAsync(
					"({bold: Array.from(document.querySelectorAll('#output strong')).some(e => e.textContent === 'UI streaming reply'), diagram: !!document.querySelector('#output svg')})"));
				rendered = result.RootElement.GetProperty("bold").GetBoolean()
					&& result.RootElement.GetProperty("diagram").GetBoolean();
				if (!rendered)
					await Task.Delay(100);
			}
			Check.True(rendered, "The Rendered tab must render streamed markdown and a Mermaid diagram.");
			SetInput(window, "unsent draft");
			Click(window, "buttonHistoryPrev");
			Check.Equal("UI prompt marker", Input(window), "Recall a sent prompt with the up arrow");
			Click(window, "buttonHistoryNext");
			Check.Equal("unsent draft", Input(window), "Restore the unsent draft with the down arrow");
			Console.WriteLine("PASS WPF startup, Send, compact status, prompt arrows, Raw, rendered markdown, and Mermaid");

			provider.Replies.Enqueue(new LocalProvider.Reply("", ToolName: "ask_user",
				ToolArguments: """{"question":"Pick an option","choices":["first","second"],"allowFreeform":false}"""));
			provider.Replies.Enqueue(new LocalProvider.Reply("UI answer accepted."));
			SetInput(window, "ask through the UI");
			Click(window, "buttonSend");
			await Check.UntilAsync(() => Status(window).StartsWith("Waiting.."), "The question did not put the UI into Waiting.");
			Check.True(window.OutputText.Contains("1. first") && window.OutputText.Contains("2. second"), "Show choices in the transcript.");
			var carded = false;
			for (var attempt = 0; attempt < 100 && !carded; attempt++)
			{
				using var result = JsonDocument.Parse(await webView.CoreWebView2.ExecuteScriptAsync(
					"({title: (document.querySelector('#output .kp-question .kp-card-title')||{}).textContent || ''," +
					" body: (document.querySelector('#output .kp-question p')||{}).textContent || ''," +
					" choices: document.querySelectorAll('#output .kp-question .kp-card-choices li').length})"));
				carded = result.RootElement.GetProperty("title").GetString() == "Question"
					&& result.RootElement.GetProperty("body").GetString() == "Pick an option"
					&& result.RootElement.GetProperty("choices").GetInt32() == 2;
				if (!carded)
					await Task.Delay(100);
			}
			Check.True(carded, "The Rendered tab must draw a question as a card with its choices.");
			Check.True(application.Windows.Count == 1, "Model questions must not create pop-up windows.");
			SetInput(window, "2");
			Click(window, "buttonSend");
			await Check.UntilAsync(() => Status(window).StartsWith("Ready..") && window.OutputText.Contains("UI answer accepted."),
				"The in-chat answer did not complete.");
			Console.WriteLine("PASS WPF chat questions and numbered answers without pop-up dialogs");

			var attachments = Field<List<string>>(window, "_attachments");
			attachments.Add(Path.Combine(workspace.Workspace, "missing.txt"));
			Invoke(window, "UpdateAttachmentButton");
			SetInput(window, "keep this failed prompt");
			Click(window, "buttonSend");
			await Check.UntilAsync(() => window.OutputText.Contains("The attachment no longer exists"), "The missing attachment was not reported.");
			Check.Equal("keep this failed prompt", Input(window), "Retain input when sending fails");
			Check.Equal("+1", Control<Button>(window, "buttonAttachments").Content, "Retain unsent attachments");
			attachments.Clear();
			Invoke(window, "UpdateAttachmentButton");
			var sessionId = chat.SessionId!;
			await window.EndSessionAsync();
			var displayedAtEnd = window.OutputText;
			await Task.Run(() => Invoke(window, "ForActiveChat", chat, new Action(() => window.AppendOutput("STALE_EVENT_SENTINEL"))));
			await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
			Check.Equal(displayedAtEnd, window.OutputText, "Ignore callbacks from an ended session");
			Check.True(!Control<Button>(window, "buttonSend").IsEnabled, "Disable input after ending a session.");

			await ChoosePastSessionAsync(application, window, sessionId, "buttonView");
			Check.Equal(workspace.Store.ReadTranscript(sessionId), window.OutputText, "View saved history without starting a runtime");
			Check.True(!window.IsSessionActive, "Offline viewing must not create a live session.");
			await ChoosePastSessionAsync(application, window, sessionId, "buttonResume");
			await Check.UntilAsync(() => window.IsSessionActive && Status(window).StartsWith("Ready.."),
				"The Past Sessions menu did not resume the selected session.", timeoutSeconds: 45);
			Check.Equal(sessionId, Field<ChatService>(window, "_chat").SessionId, "Resume the selected ID in the main window");
			Check.True(window.OutputText.Contains("UI streaming reply"), "Recall earlier output in both tabs.");
			Console.WriteLine("PASS failed-send recovery, stale-event isolation, Past Sessions viewing, and UI resume");

			await window.EndSessionAsync();
			var canceledStart = InvokeTask(window, "StartChatAsync", options, null);
			var ending = window.EndSessionAsync();
			await Task.WhenAll(canceledStart, ending).WaitAsync(TimeSpan.FromSeconds(45));
			Check.True(!window.IsSessionActive && Field<ChatService?>(window, "_chat") is null, "Ending during startup must not leave a late session behind.");
			Check.Equal("Start or resume a session to begin.", Status(window), "Return to the initial state after canceling startup");
			Console.WriteLine("PASS ending during startup without a ghost session");
			await CheckSessionWindowAsync(application, window, workspace, provider, options);
			Check.Equal(0, provider.Errors.Count, "The UI provider must not hide request failures");
		}
		finally
		{
			await window.EndSessionAsync();
			var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var hasBrowser = webView.CoreWebView2 is not null;
			if (hasBrowser)
				webView.CoreWebView2!.Environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
			SetField(window, "_closing", true);
			SetField(window, "_closeApproved", true);
			window.Close();
			if (hasBrowser)
				await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
		}
	}

	private static async Task CheckSessionWindowAsync(Application application, MainWindow window, TestWorkspace workspace,
		LocalProvider provider, ChatSessionOptions options)
	{
		workspace.Write("workspace\\README.md", "Fixture document.");
		await InvokeTask(window, "StartChatAsync", options, null);
		provider.Replies.Enqueue(new LocalProvider.Reply("Results\r\n\r\nUpdated `README.md`."));
		SetInput(window, "Please summarize README.md.");
		Click(window, "buttonSend");
		await Check.UntilAsync(() => Status(window).StartsWith("Ready..") && window.RenderedText.Contains("kp-path:"),
			"The linked UI response did not finish.");
		Check.True(window.OutputText.Contains("Updated `README.md`.") && !window.OutputText.Contains("kp-path:"), "Raw must preserve the original reply.");
		var chat = Field<ChatService>(window, "_chat");
		var sessionId = chat.SessionId;

		var live = options with { Model = "test-model-two" };
		await InvokeTask(window, "StartFromSettingsAsync", live with { Mode = "Plan" });
		Check.True(ReferenceEquals(chat, Field<ChatService>(window, "_chat")) && chat.SessionId == sessionId, "Keep the session for a live change.");
		await Check.UntilAsync(() => window.OutputText.Contains("--- Changed to test-model-two | Plan ---")
			&& Control<TextBlock>(window, "sessionInfo").Text.StartsWith("test-model-two"),
			"Show the live change in the transcript and the session badge.");
		await InvokeTask(window, "StartFromSettingsAsync", live);
		Check.True(Status(window).StartsWith("Ready.."), "Return to Ready after a live change.");

		var restart = live with { ApplyInstructions = false };
		provider.Replies.Enqueue(SessionFeatureChecks.HandoffReply());
		using (var timer = DialogAction<YesNoDialog>(application, dialog => Invoke(dialog, "OnYes", dialog, new RoutedEventArgs())))
			await InvokeTask(window, "StartFromSettingsAsync", restart);
		var restarted = Field<ChatService>(window, "_chat");
		Check.True(!ReferenceEquals(chat, restarted) && restarted.Record!.BootstrapPending
			&& restarted.Record.Bootstrap!.Summary.Contains(SessionFeatureChecks.HandoffMarker), "Attach the hand-off after restart consent.");
		Check.True(!window.OutputText.Contains(SessionFeatureChecks.HandoffMarker), "Do not display hand-off context.");
		provider.Replies.Enqueue(new LocalProvider.Reply("Restarted with context."));
		SetInput(window, "Continue after restart.");
		Click(window, "buttonSend");
		await Check.UntilAsync(() => Status(window).StartsWith("Ready..") && window.RenderedText.Contains("Restarted with context."),
			"The restarted model did not continue.");
		var request = provider.Requests.Last().GetRawText();
		Check.True(request.Contains(SessionFeatureChecks.HandoffMarker) && request.Contains("Please summarize README.md."),
			"Pass the hand-off and the earlier request to the new model.");

		using (var timer = DialogAction<YesNoDialog>(application, dialog => Invoke(dialog, "OnNo", dialog, new RoutedEventArgs())))
			await InvokeTask(window, "StartFromSettingsAsync", live);
		Check.True(Field<ChatService>(window, "_chat").Record!.Bootstrap is null, "Declining the hand-off must start without it.");

		var fresh = Field<ChatService>(window, "_chat");
		await InvokeTask(window, "StartFromSettingsAsync", restart);
		Check.True(!ReferenceEquals(fresh, Field<ChatService>(window, "_chat")) && Field<ChatService>(window, "_chat").Record!.Bootstrap is null,
			"Restart a session without a conversation without offering a hand-off.");
		provider.Replies.Enqueue(new LocalProvider.Reply("Context for a canceled restart."));
		SetInput(window, "Give the next session something to carry.");
		Click(window, "buttonSend");
		await Check.UntilAsync(() => Status(window).StartsWith("Ready..") && window.OutputText.Contains("Context for a canceled restart."),
			"The context turn did not finish.");
		var held = SessionFeatureChecks.HandoffReply(hold: true);
		provider.Replies.Enqueue(held);
		try
		{
			using (var timer = DialogAction<YesNoDialog>(application, dialog => Invoke(dialog, "OnYes", dialog, new RoutedEventArgs())))
			{
				var restarting = InvokeTask(window, "StartFromSettingsAsync", live);
				await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
				await Task.WhenAll(window.EndSessionAsync(), restarting).WaitAsync(TimeSpan.FromSeconds(30));
			}
		}
		finally { held.Release.TrySetResult(); }
		Check.True(!window.IsSessionActive, "End during hand-off preparation must cancel the pending restart.");
		Console.WriteLine("PASS UI Rendered links, live session changes, hand-off restart consent, and cancellation");
	}
	/// <summary>
	/// The Tools menu. Each item opens a separate program, so the check
	/// stops at the point of launch: it aims the window at a folder that
	/// does not exist, which makes every handler report instead of
	/// starting anything, and confirms the PowerShell item still
	/// provisions the helper functions on its way there.
	/// </summary>
	private static void CheckTools(Application application, MainWindow window, TestWorkspace workspace)
	{
		Check.True(Control<MenuItem>(window, "menuTools").IsEnabled, "Offer the tools of a running session.");
		var restore = window.ActiveWorkspacePath;
		SetProperty(window, "ActiveWorkspacePath", Path.Combine(workspace.Root, "removed-workspace"));
		try
		{
			foreach (var item in new[] { "menuOpenPowershell", "menuOpenExplorer", "menuOpenVsCode" })
			{
				var reported = false;
				using (DialogAction<MessageDialog>(application, dialog => { reported = true; dialog.Close(); }))
					Control<MenuItem>(window, item).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
				Check.True(reported, $"{item} must report a workspace folder that is no longer there.");
			}
		}
		finally { SetProperty(window, "ActiveWorkspacePath", restore); }
		Check.True(File.Exists(workspace.ScriptsPath), "Open Powershell must provision the helper functions.");
		Console.WriteLine("PASS Tools menu availability, workspace guard, and helper provisioning");
	}

	private static IDisposable DialogAction<T>(Application application, Action<T> action) where T : Window
	{
		var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
		timer.Tick += (_, _) =>
		{
			var dialog = application.Windows.OfType<T>().FirstOrDefault();
			if (dialog is null || !dialog.IsLoaded) return;
			timer.Stop();
			action(dialog);
		};
		timer.Start();
		return new TimerStop(timer);
	}

	private sealed class TimerStop(DispatcherTimer timer) : IDisposable
	{
		public void Dispose() => timer.Stop();
	}

	private static async Task CheckPromptLayoutAsync(MainWindow window)
	{
		var main = Control<Grid>(window, "MainGrid");
		var prompt = Control<Grid>(window, "promptInputGrid");
		var label = Control<Label>(window, "labelUserPrompt");
		var output = Control<Grid>(window, "modelOutputGrid");
		var outputLabel = Control<Label>(window, "labelModelOutput");
		var outputTabs = Control<TabControl>(window, "outputTabs");
		var input = Control<RichTextBox>(window, "richTextBoxInput");
		var body = (Grid)input.Parent;
		var history = (Border)((Grid)Control<Button>(window, "buttonHistoryPrev").Parent).Parent;
		var splitter = Control<Thumb>(window, "SplitterThumb");
		var notice = Control<TextBlock>(window, "noticeTextBlock");
		var outputRow = main.RowDefinitions[0];
		var inputRow = main.RowDefinitions[2];
		var originalSize = new Size(window.Width, window.Height);
		var originalOutput = outputRow.Height;
		var originalInput = inputRow.Height;
		var originalNotice = notice.Text;
		var originalVisibility = notice.Visibility;

		async Task LayoutAsync()
		{
			await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
		}

		void Near(double expected, double actual, string message) =>
			Check.True(Math.Abs(expected - actual) <= 1.5, $"{message}: expected {expected:0.##}, got {actual:0.##}.");

		void CheckBounds()
		{
			Check.True(outputLabel.IsVisible && outputLabel.ActualHeight > 0, "Keep the output label visible.");
			var outputLabelBottom = outputLabel.TranslatePoint(new Point(0, outputLabel.ActualHeight), output).Y;
			Near(outputLabelBottom + outputLabel.Margin.Bottom, outputTabs.TranslatePoint(new Point(), output).Y,
				"Place the tabs immediately below Model Output");
			Near(output.ActualHeight, outputTabs.TranslatePoint(new Point(0, outputTabs.ActualHeight), output).Y,
				"Keep the output tabs filling their pane");
			var labelBottom = label.TranslatePoint(new Point(0, label.ActualHeight), prompt).Y;
			var bodyTop = body.TranslatePoint(new Point(), prompt).Y;
			Check.True(label.IsVisible && label.ActualHeight > 0, "Keep the prompt label visible.");
			Check.True(bodyTop >= labelBottom - 1.5, "Place the editor below the label.");
			Near(bodyTop, history.TranslatePoint(new Point(), prompt).Y, "Shift the history gutter with the editor");
			var bodyBottom = body.TranslatePoint(new Point(0, body.ActualHeight), main).Y;
			Near(main.ActualHeight - prompt.Margin.Bottom, bodyBottom, "Anchor the prompt body to the bottom of the available area");
			var trailingRows = body.RowDefinitions.Skip(Grid.GetRow(input) + Grid.GetRowSpan(input)).Sum(row => row.ActualHeight);
			var inputBottom = input.TranslatePoint(new Point(0, input.ActualHeight), main).Y;
			Near(bodyBottom - trailingRows - input.Margin.Bottom, inputBottom, "Fill the editor's available height without a bottom gap");
			Near(body.ActualWidth - input.Margin.Left - input.Margin.Right, input.ActualWidth, "Fill the editor's available width");
			Check.True(input.ActualHeight > 0, "Keep an editable input area.");
		}

		async Task DragAsync(double change)
		{
			splitter.RaiseEvent(new DragDeltaEventArgs(0, change) { RoutedEvent = Thumb.DragDeltaEvent });
			await LayoutAsync();
			Check.True(outputRow.Height.IsStar && inputRow.Height.IsStar, "Splitter dragging must retain stretch-sized rows.");
			CheckBounds();
		}

		try
		{
			Check.Equal("User Prompt", label.Content, "Display the requested label");
			Check.Equal("Model Output", outputLabel.Content, "Display the output heading");
			Check.True(ReferenceEquals(outputTabs, outputLabel.Target), "Associate the output label with its tabs.");
			Check.True(ReferenceEquals(input, label.Target), "Associate the label with the prompt editor.");
			window.Height = 640;
			await LayoutAsync();
			CheckBounds();
			var initialHeight = input.ActualHeight;
			window.Height = 760;
			await LayoutAsync();
			CheckBounds();
			Check.True(input.ActualHeight > initialHeight, "Grow the input area when the window grows.");
			var beforeDrag = input.ActualHeight;
			await DragAsync(70);
			Near(beforeDrag - 70, input.ActualHeight, "Move the divider down by the requested distance");
			window.Height = 860;
			await LayoutAsync();
			CheckBounds();
			window.Height = 560;
			await LayoutAsync();
			CheckBounds();
			await DragAsync(-90);
			window.Width = 1000;
			window.Height = 740;
			await LayoutAsync();
			CheckBounds();
			notice.Text = "A visible notice still occupies only its assigned layout area.";
			notice.Visibility = Visibility.Visible;
			await LayoutAsync();
			CheckBounds();
			notice.Visibility = Visibility.Collapsed;
			await LayoutAsync();
			await DragAsync(10000);
			await DragAsync(-10000);
			Console.WriteLine("PASS User Prompt label and gap-free input layout across window resizing and splitter dragging");
		}
		finally
		{
			notice.Text = originalNotice;
			notice.Visibility = originalVisibility;
			outputRow.Height = originalOutput;
			inputRow.Height = originalInput;
			window.Width = originalSize.Width;
			window.Height = originalSize.Height;
			await LayoutAsync();
		}
	}

	private static async Task ChoosePastSessionAsync(Application application, MainWindow window, string sessionId, string button)
	{
		var chosen = false;
		var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
		timer.Tick += (_, _) =>
		{
			var dialog = application.Windows.OfType<PastSessionsDialog>().FirstOrDefault();
			if (dialog is null) return;
			timer.Stop();
			Control<TextBox>(dialog, "textSearch").Text = sessionId;
			Click(dialog, button);
			chosen = true;
		};
		timer.Start();
		try
		{
			Control<MenuItem>(window, "menuPastSessions").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
			await Check.UntilAsync(() => chosen, "The Past Sessions menu did not open its picker.");
		}
		finally { timer.Stop(); }
	}

	private static T Control<T>(FrameworkElement owner, string name) where T : class =>
		owner.FindName(name) as T ?? throw new InvalidOperationException($"Missing control '{name}'.");

	private static T Field<T>(object owner, string name) =>
		(T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

	private static void SetField(object owner, string name, object value) =>
		owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);

	private static void SetProperty(object owner, string name, object? value) =>
		owner.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
			.GetSetMethod(nonPublic: true)!.Invoke(owner, [value]);

	private static object? Invoke(object owner, string name, params object?[] arguments) =>
		owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments);

	private static Task InvokeTask(object owner, string name, params object?[] arguments) =>
		(Task)Invoke(owner, name, arguments)!;

	private static void Click(FrameworkElement owner, string name) =>
		Control<Button>(owner, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

	private static string Status(MainWindow window) => Control<TextBlock>(window, "statusTextBlock").Text;
	private static string Input(MainWindow window) => (string)Invoke(window, "GetInputText")!;
	private static void SetInput(MainWindow window, string text) => Invoke(window, "SetInputText", text);
	private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');
}
