using System.IO;
using System.Text;
using GitHub.Copilot;
using TurboPilot.Ai;
using TurboPilot.Customizations;
using TurboPilot.Dialogs;
using TurboPilot.Sessions;

namespace TurboPilot.Tests;

internal static class CoreChecks
{
	public static async Task RunAsync()
	{
		CheckConfiguration();
		CheckStorage();
		await CheckQuestionsAsync();
		await CheckEventsAsync();
		CheckHistoryAndStatus();
		Console.WriteLine("PASS configuration, history storage, questions, streaming events, usage, and prompt navigation");
	}

	private static void CheckConfiguration()
	{
		using var workspace = new TestWorkspace();
		var library = workspace.CreateLibrary();
		var serverFile = workspace.Write("servers.mcp.json", """
			{"servers":{"enabled":{"type":"http","url":"http://127.0.0.1:5000/mcp","headers":{"Authorization":"fixture-value"}},"no-tools":{"type":"http","url":"http://127.0.0.1:5000/mcp","tools":[]},"disabled":{"type":"unsupported"}}}
			""");
		library.McpServers["enabled"] = new() { Name = "enabled", Element = "enabled", FilePath = serverFile };
		library.McpServers["disabled"] = new() { Name = "disabled", Element = "disabled", FilePath = serverFile, Enabled = false };
		var options = new ChatSessionOptions
		{
			WorkspaceFolder = workspace.Workspace,
			Model = "test-model",
			ReasoningEffort = "high",
			Mode = "writer.agent",
			Customizations = library,
		};
		var config = new SessionConfig();
		SessionConfiguration.Apply(config, options);
		var system = config.SystemMessage!.Content!;
		Check.True(system.Contains("ENABLED_INSTRUCTION_SENTINEL") && system.Contains("ENABLED_SKILL_SENTINEL"), "Enabled content must be preloaded.");
		Check.True(!system.Contains("DISABLED_"), "Disabled content must not be preloaded.");
		Check.True(system.Contains("**/*.cs"), "Instruction file scope must survive.");
		Check.Equal(false, config.EnableConfigDiscovery, "Automatic discovery must not undo the user's choices");
		Check.Equal(true, config.SkipCustomInstructions, "Unselected instructions must stay disabled");
		Check.Equal(false, config.EnableOnDemandInstructionDiscovery, "On-demand loading must honor instruction choices");
		Check.Equal(1, config.SkillDirectories!.Count, "Only enabled skill paths may be registered");
		Check.True(config.SkillDirectories[0].EndsWith("enabled-skill"), "Pass the selected skill folder, not its siblings.");
		Check.Equal("writer.agent", config.Agent, "Activate the selected agent");
		Check.Equal(1, config.CustomAgents!.Count, "Exclude disabled agents");
		Check.Equal("Writes fixture responses", config.CustomAgents[0].Description, "Parse multiline YAML");
		Check.Equal(0, config.CustomAgents[0].Tools!.Count, "Preserve an explicit empty tool allowlist");
		Check.Equal(1, config.CustomAgents[0].Skills!.Count, "Agents cannot preload disabled skills");
		Check.Equal("high", config.ReasoningEffort, "Apply reasoning effort");
		Check.Equal(1, config.McpServers!.Count, "Only enabled servers may be started");
		Check.Equal("*", config.McpServers["enabled"].Tools!.Single(), "Register all tools explicitly when no server filter is supplied");
		Check.Equal(0, McpConfig.ReadServerConfiguration(serverFile, "no-tools").Tools!.Count, "Preserve an explicit empty server tool filter");
		Check.True(!system.Contains("fixture-value"), "Server credentials must not enter the system prompt.");

		var resume = new ResumeSessionConfig();
		SessionConfiguration.Apply(resume, options with { ApplyInstructions = false, PreloadSkills = false });
		Check.True(!resume.SystemMessage!.Content!.Contains("_SENTINEL"), "Both loading switches must apply to resumed sessions.");
		Check.Equal(false, resume.EnableSkills, "Preload off must disable automatic skill loading");
		Check.Equal(0, resume.SkillDirectories!.Count, "Preload off must not register skill folders");
		Check.Equal(0, resume.CustomAgents![0].Skills!.Count, "Preload off must also apply to custom agents");
		Check.Throws<InvalidOperationException>(() => SessionConfiguration.Apply(new SessionConfig(), options with { Mode = "disabled-agent" }));
		Check.Throws<InvalidOperationException>(() => SessionConfiguration.Apply(new SessionConfig(), options with { UseByok = true }));

		var provider = new SessionConfig();
		SessionConfiguration.Apply(provider, options with
		{
			UseByok = true, ByokEndpoint = "http://127.0.0.1:5001/v1", ByokApiKey = "fixture-key", ContextWindowTokens = 32768,
		});
		Check.Equal("http://127.0.0.1:5001/v1", provider.Provider!.BaseUrl, "Use the selected endpoint");
		Check.Equal("fixture-key", provider.Provider.ApiKey, "Use the selected credential only for the provider");
		Check.True(provider.Provider.MaxPromptTokens == 32768, "Apply the selected context limit.");
		var malformed = workspace.Write("malformed.md", "---\nname: [broken\n---\nbody");
		Check.Throws<YamlDotNet.Core.YamlException>(() => FrontMatter.ReadDocument(malformed));
	}

	private static void CheckStorage()
	{
		using var workspace = new TestWorkspace();
		var options = new ChatSessionOptions
		{
			WorkspaceFolder = workspace.Workspace, Model = "test-model", Mode = "Plan",
			UseByok = true, ByokEndpoint = "http://127.0.0.1:5001/v1", ByokApiKey = "DO_NOT_PERSIST_FIXTURE_KEY",
		};
		var record = workspace.Store.Create("saved-123", options, "**You:** first\r\n");
		workspace.Store.AppendTranscript(record.SessionId, "streamed answer\r\n");
		record.Prompts.Add("first");
		record.AicNano = 13_000_000_000;
		record.ContextUsedTokens = 20480;
		record.ContextWindowTokens = 131072;
		workspace.Store.Save(record);
		var loaded = workspace.Store.Load(record.SessionId);
		Check.Equal(options.Mode, loaded.Options.Mode, "Restore the mode");
		Check.Equal("", loaded.Options.ByokApiKey, "Do not restore keys from transcript metadata");
		Check.Equal("first", loaded.Prompts.Single(), "Restore prompt history");
		Check.Equal(13_000_000_000L, loaded.AicNano, "Restore credit usage");
		Check.True(loaded.UsesApiKey, "Remember when resuming needs credentials without storing the credentials.");
		Check.Equal("**You:** first\r\nstreamed answer\r\n", workspace.Store.ReadTranscript(record.SessionId), "Restore exactly the saved transcript");
		Check.True(!File.ReadAllText(Path.Combine(workspace.HistoryDirectory, "saved-123.json")).Contains("DO_NOT_PERSIST"), "Do not persist provider credentials.");
		Check.True(!Directory.EnumerateFiles(workspace.HistoryDirectory, "*.tmp").Any(), "Atomic saves must not leave temporary files.");
		workspace.Write("history\\broken.json", "{broken");
		Check.Equal(1, workspace.Store.List(out var errors).Count, "A corrupt entry must not hide healthy sessions");
		Check.Equal(1, errors.Count, "Report corrupt history explicitly");
		workspace.Write("history\\null.json", """{"sessionId":"null","options":{},"description":null}""");
		Check.Equal(1, workspace.Store.List(out errors).Count, "Reject invalid null fields before opening a dialog");
		Check.Equal(2, errors.Count, "Report invalid metadata shapes");
		Check.Throws<ArgumentException>(() => workspace.Store.ReadTranscript("..\\escape"));
		Check.Throws<IOException>(() => workspace.Store.Create(record.SessionId, options, "replacement"));

		var settings = new Settings { ByokEndpoint = options.ByokEndpoint, ByokApiKey = "current-fixture-key" };
		Check.Equal("current-fixture-key", MainWindow.RestoreSessionOptions(loaded, settings).ByokApiKey, "Resume with the current matching credential");
		settings.ByokEndpoint = "http://127.0.0.1:5002/v1";
		Check.Throws<InvalidOperationException>(() => MainWindow.RestoreSessionOptions(loaded, settings));
		var publicEndpoint = new SessionRecord { SessionId = "public", Options = options, UsesApiKey = false };
		Check.Equal("", MainWindow.RestoreSessionOptions(publicEndpoint, settings).ByokApiKey, "Never reuse a credential belonging to another endpoint");
	}

	private static async Task CheckQuestionsAsync()
	{
		await using var chat = new ChatService();
		var first = chat.HandleUserInputRequestAsync(
			new UserInputRequest { Question = "First?", Choices = ["red", "blue"], AllowFreeform = false }, new());
		var second = chat.HandleUserInputRequestAsync(
			new UserInputRequest { Question = "Second?", Choices = ["small", "large"], AllowFreeform = true }, new());
		Check.True(chat.HasPendingQuestion, "Questions must remain pending until answered.");
		Check.True(!chat.Transcript.Contains("Second?"), "Present only the oldest unanswered question.");
		Check.Throws<InvalidOperationException>(() => chat.TryAnswerPending("invalid"));
		Check.True(!first.IsCompleted && !second.IsCompleted, "An invalid answer must not release a question.");
		Check.True(chat.TryAnswerPending("2"), "Answer the first question.");
		Check.Equal("blue", (await first).Answer, "Resolve numbered choices to their text");
		Check.Equal(false, (await first).WasFreeform, "A numbered choice is not freeform");
		Check.True(chat.Transcript.Contains("Second?"), "Show the next queued question.");
		chat.TryAnswerPending("custom size");
		Check.Equal("custom size", (await second).Answer, "Accept permitted freeform input");
		Check.Equal(true, (await second).WasFreeform, "Mark freeform answers correctly");
		Check.True(!chat.HasPendingQuestion, "Clear answered questions immediately.");
		Check.Equal("yes", ChatService.ResolveAnswer("1", ["yes", "no"], false, true).Answer, "Numbered permission approval");
		Check.Equal("no", ChatService.ResolveAnswer("deny", ["yes", "no"], false, true).Answer, "Permission denial alias");
		Check.Throws<InvalidOperationException>(() => ChatService.ResolveAnswer("maybe", ["yes", "no"], false, true));
		var plan = chat.HandleExitPlanModeRequestAsync(new ExitPlanModeRequest
		{
			Summary = "Implement the plan?", Actions = ["interactive", "autopilot"],
		}, new());
		chat.TryAnswerPending("1");
		Check.True((await plan).Approved, "Confirm a plan in chat.");
		Check.Equal("interactive", (await plan).SelectedAction, "Return the selected plan action");
		var stay = chat.HandleExitPlanModeRequestAsync(new ExitPlanModeRequest
		{
			Summary = "Keep planning?", Actions = ["interactive", "autopilot"],
		}, new());
		chat.TryAnswerPending("3");
		Check.True(!(await stay).Approved, "Allow remaining in Plan without executing.");
		var canceled = chat.HandleUserInputRequestAsync(new UserInputRequest { Question = "Canceled?" }, new());
		await chat.DisposeAsync();
		Check.Equal("(user canceled)", (await canceled).Answer, "Disposal must release question handlers");
	}

	private static async Task CheckEventsAsync()
	{
		await using var chat = new ChatService();
		var displayed = new StringBuilder();
		var errors = new List<string>();
		chat.TranscriptReceived += text => displayed.Append(text);
		chat.ErrorReceived += errors.Add;
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"assistant.turn_start","data":{"turnId":"turn-1"}}"""));
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"assistant.message_delta","data":{"messageId":"m1","deltaContent":"streamed"}}"""));
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"assistant.message","data":{"messageId":"m1","content":"streamed"}}"""));
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"assistant.message","data":{"messageId":"m2","content":"final only"}}"""));
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"assistant.message","agentId":"child","data":{"messageId":"m3","content":"do not duplicate subagent output"}}"""));
		Check.Equal("streamed\r\n\r\nfinal only\r\n\r\n", displayed.ToString(), "Deduplicate by message, not by turn");
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"assistant.usage","data":{"model":"fixture","inputTokens":20480,"copilotUsage":{"totalNanoAiu":13000000000}}}"""));
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"assistant.usage","agentId":"child","data":{"model":"fixture","initiator":"sub-agent","inputTokens":7,"copilotUsage":{"totalNanoAiu":1000000000}}}"""));
		Check.Equal(20480, chat.ContextUsedTokens, "Subagent usage must not replace the main context meter");
		Check.Equal(14d, chat.AicUsed, "Accumulate credits from all calls");
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"session.usage_info","data":{"currentTokens":21504,"tokenLimit":131072,"messagesLength":3}}"""));
		Check.Equal(21504, chat.ContextUsedTokens, "Use runtime context snapshots");
		Check.Equal(131072, chat.ContextWindowTokens, "Use the runtime context limit");
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"session.idle","data":{}}"""));
		Check.True(!chat.IsWorking, "Idle must end the working state.");
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"session.mcp_server_status_changed","data":{"serverName":"fixture","status":"failed","error":"Connection refused"}}"""));
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"session.mcp_servers_loaded","data":{"servers":[{"name":"fixture","status":"failed","error":"Connection refused"}]}}"""));
		Check.Equal(1, chat.Transcript.Split("Connection refused").Length - 1, "Surface server failures without duplicate notices");
		Check.Equal(0, errors.Count, "All event fixtures must be processed");
	}

	private static void CheckHistoryAndStatus()
	{
		var history = new PromptHistory();
		history.Add("first");
		history.Add("second");
		Check.Equal("second", history.NavigateBack("draft"), "Recall newest prompt");
		Check.Equal("first", history.NavigateBack("second"), "Recall older prompt");
		Check.True(!history.CanGoBack, "Disable the oldest boundary.");
		Check.Equal("second", history.NavigateForward(), "Move toward newer prompts");
		Check.Equal("draft", history.NavigateForward(), "Restore the unsent draft");
		Check.True(!history.CanGoForward, "Disable the draft boundary.");
		Check.Equal("Working.. 20/128K AiC=13", MainWindow.FormatStatus("Working..", 20480, 131072, 13, true), "Compact cloud status");
		Check.Equal("Ready.. 20/128K", MainWindow.FormatStatus("Ready..", 20480, 131072, 13, false), "Hide credits for other providers");
		Check.Equal("Waiting..", MainWindow.FormatStatus("Waiting..", 0, 0, 0, false), "Do not invent an unknown context limit");
	}
}
