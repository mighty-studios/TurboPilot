using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using TurboPilot.Ai;
using TurboPilot.Mediation;
using TurboPilot.Permissions;

namespace TurboPilot.Tests;

internal static class MediatedChatChecks
{
	internal const string SummaryMarker = "SUMMARY_BOOTSTRAP_SENTINEL";

	internal static FakeLocalRuntime CreateRuntime()
	{
		return new FakeLocalRuntime
		{
			Generate = (system, input, _) =>
			{
				using var document = JsonDocument.Parse(input);
				string result;
				if (system.Contains("# Reword a Prompt"))
				{
					var prompt = document.RootElement.GetProperty("prompt").GetString()!;
					result = JsonSerializer.Serialize(new
					{
						prompt = prompt.StartsWith("Please kindly ", StringComparison.Ordinal)
							? prompt[14..].Replace(" Thank you very much.", "", StringComparison.Ordinal) : prompt,
						meaningPreserved = true,
					});
				}
				else if (system.Contains("# Prepare Output"))
					result = """{"links":[],"headings":["Results"]}""";
				else if (system.Contains("# Monitor Output"))
					result = document.RootElement.GetProperty("response").GetString()!.Contains("Everything is verified.")
						? """{"warnings":[{"kind":"unsupported-claim","message":"This claim may need verification.","quote":"Everything is verified."}]}"""
						: """{"warnings":[]}""";
				else
					result = JsonSerializer.Serialize(new { summary = SummaryMarker + ": Continue documenting README.md without changing code." });
				return Task.FromResult(new LocalCompletion(result));
			},
		};
	}

	public static async Task RunAsync()
	{
		using var workspace = new TestWorkspace();
		await using var provider = new LocalProvider();
		var local = CreateRuntime();
		var configuration = new MediatorConfiguration(Path.Combine(workspace.Root, "mediator"));
		var options = new MediatorSettings { Enabled = true, MinimumRewriteTokens = 0 };
		configuration.Save(options);
		var chatOptions = new ChatSessionOptions
		{
			WorkspaceFolder = workspace.Workspace, Model = "test-model", UseByok = true,
			ByokEndpoint = provider.Endpoint, ContextWindowTokens = 32768,
		};
		var file = workspace.Write("workspace\\README.md", "Fixture file.");
		var processingLog = new ConcurrentQueue<MediatorDiagnostic>();
		ChatService CreateChat()
		{
			var chat = workspace.CreateChat();
			chat.MediatorFactory = (id, folder) =>
			{
				var mediator = new MediatorService(id, folder, configuration.Load(), local, configuration,
					new MediationStore(id, folder, Path.Combine(workspace.Root, "worklogs")));
				mediator.DiagnosticReceived += processingLog.Enqueue;
				return mediator;
			};
			return chat;
		}
		string id;
		SummaryBootstrap bootstrap;
		string rendered;
		await using (var chat = CreateChat())
		{
			var diagnostics = new ConcurrentQueue<MediatorDiagnostic>();
			var notices = new ConcurrentQueue<string>();
			chat.MediatorDiagnosticReceived += diagnostics.Enqueue;
			chat.MediatorNoticeReceived += notices.Enqueue;
			await chat.StartAsync(chatOptions);
			id = chat.SessionId!;
			provider.Replies.Enqueue(new LocalProvider.Reply("Results\r\n\r\nSee `README.md`.\r\n\r\nEverything is verified."));
			await RuntimeChecks.SendAndWaitAsync(chat, "Please kindly summarize README.md. Thank you very much.");
			Check.True(provider.Requests.Last().GetRawText().Contains("summarize README.md."), "Forward the locally reduced prompt.");
			Check.True(!provider.Requests.Last().GetRawText().Contains("Please kindly"), "Do not send the unreduced courtesy text: "
				+ provider.Requests.Last().GetProperty("messages").EnumerateArray().Last(message => message.GetProperty("role").GetString() == "user").GetRawText()
				+ " Local: " + JsonSerializer.Serialize(processingLog.ToArray()));
			Check.True(chat.Transcript.Contains("Please kindly summarize README.md."), "Keep the user's actual prompt in Raw.");
			Check.True(chat.RenderedTranscript.Contains("kp-path:" + Uri.EscapeDataString(file)), "Prepare the completed reply for Rendered.");
			Check.True(!chat.Transcript.Contains("kp-path:") && !chat.Transcript.Contains(SummaryMarker), "Do not replace Raw with formatting or local summaries.");
			Check.Equal(0, diagnostics.Count, "Keep diagnostics hidden by default");
			Check.True(notices.Any(notice => notice.Contains("Possible unsupported-claim")), "Warn outside the session transcript.");
			Check.True(!chat.Transcript.Contains("Possible unsupported-claim"), "Do not inject local warnings into session output.");
			rendered = chat.RenderedTranscript;
			Check.Equal(rendered, workspace.Store.ReadRenderedTranscript(id), "Persist prepared output separately");
			bootstrap = await chat.GetRestartSummaryAsync() ?? throw new InvalidOperationException("The completed conversation needs a restart summary.");
			Check.True(bootstrap.Summary.Contains(SummaryMarker), "Make the current summary available.");
			await chat.ConfigureMediatorAsync(options with { DebugRaw = true });
			provider.Replies.Enqueue(new LocalProvider.Reply("Second response."));
			await RuntimeChecks.SendAndWaitAsync(chat, "Please kindly continue.");
			Check.True(diagnostics.Count > 0, "Expose optional diagnostics through a separate event.");
			Check.True(!chat.Transcript.Contains("[mediator") && !chat.RenderedTranscript.Contains(SummaryMarker), "Do not persist local diagnostics into either conversation transcript.");
		}
		await using (var resumed = CreateChat())
		{
			await resumed.ResumeAsync(id, chatOptions);
			Check.True(resumed.RenderedTranscript.StartsWith(rendered, StringComparison.Ordinal), "Restore prepared output during cold resume.");
			Check.True((await resumed.GetRestartSummaryAsync())?.Summary.Contains(SummaryMarker) == true, "Restore the saved summary.");
		}
		await using (var next = CreateChat())
		{
			await next.StartAsync(chatOptions);
			var before = provider.Requests.Count;
			next.SetBootstrap(bootstrap);
			Check.Equal(before, provider.Requests.Count, "Attaching background context must not trigger autonomous model work");
			Check.True(!next.Transcript.Contains(SummaryMarker), "Keep the bootstrap out of session output.");
			Check.Throws<InvalidOperationException>(() => next.SetBootstrap(bootstrap with { Workspace = Path.Combine(workspace.Root, "other") }));
			provider.Replies.Enqueue(new LocalProvider.Reply("Continued with context."));
			await RuntimeChecks.SendAndWaitAsync(next, "Continue the documented task.");
			var request = provider.Requests.Last().GetRawText();
			Check.True(request.Contains(SummaryMarker) && request.Contains("Current user request"), "Send accepted context with the first post-launch prompt.");
			Check.True(!workspace.Store.Load(next.SessionId!).BootstrapPending, "Consume pending bootstrap after acceptance.");
			Check.True(!next.Transcript.Contains(SummaryMarker), "Hide bootstrap content after sending.");
		}
		await using (var canceled = CreateChat())
		{
			await canceled.StartAsync(chatOptions);
			var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			local.Generate = async (_, _, token) =>
			{
				entered.TrySetResult();
				await Task.Delay(Timeout.Infinite, token);
				return new LocalCompletion("");
			};
			var before = provider.Requests.Count;
			var sending = canceled.SendAsync("Please kindly cancel this prompt.");
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			await canceled.AbortAsync().WaitAsync(TimeSpan.FromSeconds(5));
			await Check.ThrowsAsync<OperationCanceledException>(() => sending);
			Check.Equal(before, provider.Requests.Count, "Stop must cancel local preparation before any remote request");
			Check.True(!canceled.IsWorking, "Stop must release local processing state.");
		}
		local = CreateRuntime();
		await using (var canceledOutput = CreateChat())
		{
			var originalGenerate = local.Generate!;
			var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			local.Generate = async (system, input, token) =>
			{
				if (system.Contains("# Prepare Output"))
				{
					entered.TrySetResult();
					await Task.Delay(Timeout.Infinite, token);
				}
				return await originalGenerate(system, input, token);
			};
			await canceledOutput.StartAsync(chatOptions);
			provider.Replies.Enqueue(new LocalProvider.Reply("Results\r\n\r\nPreserve this response while formatting is canceled."));
			await canceledOutput.SendAsync("Continue.");
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			await canceledOutput.AbortAsync().WaitAsync(TimeSpan.FromSeconds(5));
			await Check.UntilAsync(() => !canceledOutput.IsWorking, "Canceling output preparation left the session busy.");
			var worklog = new MediationStore(canceledOutput.SessionId!, workspace.Workspace, Path.Combine(workspace.Root, "worklogs"))
				.Load(canceledOutput.SessionId!, workspace.Workspace);
			Check.True(worklog.Entries.Any(entry => entry.Role == "assistant" && entry.Content.Contains("Preserve this response")),
				"Canceling local processing must not lose captured output.");
			Check.True(canceledOutput.Transcript.Contains("Preserve this response"), "Keep streamed Raw content after cancellation.");
		}
		await CheckAgenticTurnAsync(workspace, provider, chatOptions, CreateChat, () => local = CreateRuntime());
		await CheckFailedSummaryOnceAsync(provider, chatOptions, CreateChat, () => local = CreateRuntime());
		Check.Equal(0, provider.Errors.Count, "The mediated loopback provider must not hide failures");
		Console.WriteLine("PASS mediated native chat, separate output persistence, diagnostic isolation, restart context, and cancellation");
	}

	// Both idle events end a turn; a summary pass that failed at the first must not run again at the second.
	private static async Task CheckFailedSummaryOnceAsync(LocalProvider provider, ChatSessionOptions chatOptions,
		Func<ChatService> createChat, Func<FakeLocalRuntime> resetRuntime)
	{
		var local = resetRuntime();
		var working = local.Generate!;
		local.Generate = (system, input, token) => system.Contains("# Maintain a Summary")
			? Task.FromResult(new LocalCompletion("{broken")) : working(system, input, token);
		await using var chat = createChat();
		await chat.StartAsync(chatOptions);
		provider.Replies.Enqueue(new LocalProvider.Reply("Done."));
		await RuntimeChecks.SendAndWaitAsync(chat, "Continue.");
		Check.Equal(2, local.Requests.Count(request => request.System.Contains("# Maintain a Summary")),
			"Correct a failed summary once without repeating the pass at session idle");
		Console.WriteLine("PASS failed summary pass is not repeated within a turn");
	}

	// Mirrors a live turn: narration with a tool call, a permission reply, a model question, then the answer.
	private static async Task CheckAgenticTurnAsync(TestWorkspace workspace, LocalProvider provider, ChatSessionOptions chatOptions,
		Func<ChatService> createChat, Func<FakeLocalRuntime> resetRuntime)
	{
		var local = resetRuntime();
		var scopeKey = PermissionService.MakeKey(workspace.Workspace);
		var scopes = PermissionService.Current.Workspaces;
		scopes.TryGetValue(scopeKey, out var originalScope);
		scopes[scopeKey] = new PermissionScope { Operations = [] };
		try
		{
			await using var turn = createChat();
			await turn.StartAsync(chatOptions);
			provider.Replies.Enqueue(new LocalProvider.Reply("Checking `README.md` before answering.", ToolName: "powershell",
				ToolArguments: """{"command":"Write-Output MediatedToolProbe","description":"Write a fixture marker"}"""));
			provider.Replies.Enqueue(new LocalProvider.Reply("", ToolName: "ask_user",
				ToolArguments: """{"question":"Which summary style?","choices":["short","long"],"allowFreeform":false}"""));
			provider.Replies.Enqueue(new LocalProvider.Reply("Results\r\n\r\nSee `README.md`."));
			await turn.SendAsync("Review README.md.");
			await Check.UntilAsync(() => turn.HasPendingQuestion && turn.Transcript.Contains("Permission requested"), "The permission request did not wait.");
			await turn.SendAsync("1");
			await Check.UntilAsync(() => turn.HasPendingQuestion && turn.Transcript.Contains("Which summary style?"), "The model question did not wait.");
			await turn.SendAsync("2");
			await Check.UntilAsync(() => !turn.IsWorking && !turn.HasPendingQuestion && turn.Transcript.Contains("See `README.md`."),
				"The agentic turn and its local processing did not finish.", timeoutSeconds: 30);
			var requests = local.Requests.ToList();
			var formatting = requests.Where(request => request.System.Contains("# Prepare Output")).ToList();
			Check.Equal(1, formatting.Count, "Format only the turn's final message with the local model");
			Check.True(formatting[0].Input.Contains("See `README.md`."), "Prepare the final answer rather than interim narration.");
			Check.Equal(1, requests.Count(request => request.System.Contains("# Monitor Output")), "Monitor only the turn's final message");
			Check.Equal(1, requests.Count(request => request.System.Contains("# Maintain a Summary")), "Summarize the whole turn once");
			Check.True(turn.RenderedTranscript.Split("kp-path:").Length - 1 >= 2, "Link files in interim narration and in the answer.");

			var worklog = new MediationStore(turn.SessionId!, workspace.Workspace, Path.Combine(workspace.Root, "worklogs"))
				.Load(turn.SessionId!, workspace.Workspace);
			Check.Equal("user,assistant,answer,assistant", string.Join(",", worklog.Entries.Where(entry => entry.Role != "tool").Select(entry => entry.Role)),
				"Record the turn without permission replies or empty tool-request messages");
			var shell = worklog.Entries.FirstOrDefault(entry => entry.Role == "tool" && entry.Content.StartsWith("powershell succeeded"));
			Check.True(shell?.Content.Contains("MediatedToolProbe") == true,
				"Record a compact tool result with its tool name: " + JsonSerializer.Serialize(worklog.Entries));
			var answer = worklog.Entries.Single(entry => entry.Role == "answer");
			Check.True(answer.Content.Contains("Which summary style?") && answer.Content.Contains("Answer: long"),
				"Record a model question's answer with the question: " + answer.Content);
		}
		finally
		{
			if (originalScope is null)
				scopes.Remove(scopeKey);
			else
				scopes[scopeKey] = originalScope;
		}
		Console.WriteLine("PASS agentic turn: final-message processing, one summary pass, and compact worklog entries");
	}
}
