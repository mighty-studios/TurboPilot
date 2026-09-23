using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using TurboPilot.Ai;
using TurboPilot.Dialogs;
using TurboPilot.Mediation;

namespace TurboPilot.Tests;

internal static class UiChecks
{
	public static Task RunAsync()
	{
		var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var thread = new Thread(() =>
		{
			Exception? failure = null;
			var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
			application.Startup += async (_, _) =>
			{
				try { await RunWindowChecksAsync(application); }
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

	private static async Task RunWindowChecksAsync(Application application)
	{
		using var workspace = new TestWorkspace();
		await using var provider = new LocalProvider();
		var library = workspace.CreateLibrary();
		TurbolandTheme.Wpf.TurbolandTheme.Apply(application, TurbolandTheme.Core.ThemeMode.Authentic);
		var window = new MainWindow(workspace.Store, workspace.CreateChat, _ => library, Path.Combine(workspace.Root, "browser"));
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
			Check.Equal("Start or resume a session to begin.", Status(window), "Initial status");
			window.Show();
			await Check.UntilAsync(() => Field<bool>(window, "_webViewReady"), "The rendered output did not initialize.");
			CheckMediatorDialog(window, workspace);

			var starting = InvokeTask(window, "StartChatAsync", options, null);
			Check.True(Status(window).StartsWith("Starting.."), "Show Starting while connecting.");
			Check.True(!Control<Button>(window, "buttonSend").IsEnabled, "Disable Send while connecting.");
			await starting.WaitAsync(TimeSpan.FromSeconds(45));
			Check.True(window.IsSessionActive && Status(window).StartsWith("Ready.."), "Enable a fully started session.");
			Check.True(Control<Button>(window, "buttonSend").IsEnabled, "Enable Send after startup.");
			Check.True(!Status(window).Contains("AiC="), "Do not show cloud credits for a local provider.");

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

	private static void CheckMediatorDialog(MainWindow window, TestWorkspace workspace)
	{
		var configuration = new MediatorConfiguration(Path.Combine(workspace.Root, "mediator-dialog"));
		var runtime = new FakeLocalRuntime();
		var accepted = new MediatorDialog(configuration, runtime);
		accepted.Loaded += (_, _) =>
		{
			var selected = Control<ComboBox>(accepted, "comboModel").SelectedItem as LocalModelDescriptor;
			Check.Equal(MediatorSettings.DefaultModelAlias, selected?.Alias, "Select the preferred compatible model");
			Control<CheckBox>(accepted, "checkEnabled").IsChecked = true;
			Control<CheckBox>(accepted, "checkDebug").IsChecked = true;
			Check.True(Control<Button>(accepted, "buttonOk").IsEnabled, "Allow enabling a downloaded model.");
			Click(accepted, "buttonOk");
		};
		Check.Equal(true, accepted.ShowDialog(window), "Save local options on OK");
		var saved = configuration.Load();
		Check.True(saved.Enabled && saved.DebugRaw, "Persist the selected flags.");
		Check.Equal(0, runtime.Loads, "The settings dialog must not start generation.");
		var canceled = new MediatorDialog(configuration, runtime);
		canceled.Loaded += (_, _) =>
		{
			Control<ComboBox>(canceled, "comboModel").SelectedIndex = 1;
			Check.True(!Control<Button>(canceled, "buttonOk").IsEnabled, "Require downloading a new model before enabling it.");
			Control<CheckBox>(canceled, "checkReword").IsChecked = false;
			Invoke(canceled, "OnCancel", canceled, new RoutedEventArgs());
		};
		Check.Equal(false, canceled.ShowDialog(window), "Cancel the edited local options");
		Check.Equal(saved, configuration.Load(), "Cancel must not persist edits");
		Console.WriteLine("PASS Mediator dialog model selection, download gating, OK, and Cancel");
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
