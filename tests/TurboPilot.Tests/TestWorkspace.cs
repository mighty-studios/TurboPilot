using System.IO;
using GitHub.Copilot;
using TurboPilot.Ai;
using TurboPilot.Customizations;
using TurboPilot.Sessions;

namespace TurboPilot.Tests;

internal sealed class TestWorkspace : IDisposable
{
	public string Root { get; } = Path.Combine(Path.GetTempPath(), "TurboPilot.Tests", Guid.NewGuid().ToString("N"));
	public string Workspace => Path.Combine(Root, "workspace");
	public string HistoryDirectory => Path.Combine(Root, "history");
	public SessionStore Store { get; }

	public TestWorkspace()
	{
		Directory.CreateDirectory(Workspace);
		Store = new SessionStore(HistoryDirectory);
	}

	public string Write(string relativePath, string content)
	{
		var path = Path.Combine(Root, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content.Replace("\r\n", "\n").Replace("\n", "\r\n"));
		return path;
	}

	public CustomizationLibrary CreateLibrary()
	{
		var library = new CustomizationLibrary();
		var instruction = Write("workspace\\.github\\instructions\\enabled.instructions.md",
			"---\napplyTo: '**/*.cs'\ndescription: >-\n  A multiline\n  description\n---\nENABLED_INSTRUCTION_SENTINEL");
		var disabledInstruction = Write("workspace\\.github\\instructions\\disabled.instructions.md",
			"---\napplyTo: '**'\n---\nDISABLED_INSTRUCTION_SENTINEL");
		library.Instructions[instruction] = new() { FilePath = instruction, Name = "enabled" };
		library.Instructions[disabledInstruction] = new() { FilePath = disabledInstruction, Name = "disabled", Enabled = false };

		var skill = Write("workspace\\.github\\skills\\enabled-skill\\SKILL.md",
			"---\nname: enabled-skill\ndescription: Fixture skill\n---\nENABLED_SKILL_SENTINEL");
		var disabledSkill = Write("workspace\\.github\\skills\\disabled-skill\\SKILL.md",
			"---\nname: disabled-skill\ndescription: Disabled fixture\n---\nDISABLED_SKILL_SENTINEL");
		library.Skills[skill] = new() { FilePath = skill, Name = "enabled-skill" };
		library.Skills[disabledSkill] = new() { FilePath = disabledSkill, Name = "disabled-skill", Enabled = false };

		var agent = Write("workspace\\.github\\agents\\writer.agent.md",
			"---\nname: Writer\ndescription: >-\n  Writes fixture\n  responses\ntools: []\ninfer: false\nskills:\n  - enabled-skill\n  - disabled-skill\n---\nAGENT_SENTINEL");
		library.Agents[agent] = new() { FilePath = agent, Name = "writer.agent" };
		library.Agents["missing"] = new() { FilePath = Path.Combine(Root, "missing.agent.md"), Name = "disabled-agent", Enabled = false };
		Write("workspace\\AGENTS.md", "UNSELECTED_WORKSPACE_INSTRUCTION_SENTINEL");
		return library;
	}

	public ChatService CreateChat() => new(Store, options => new CopilotClient(new CopilotClientOptions
	{
		WorkingDirectory = options.WorkspaceFolder,
		BaseDirectory = Path.Combine(Root, "runtime"),
		LogLevel = CopilotLogLevel.Error,
	}));

	public void Dispose()
	{
		Directory.Delete(Root, recursive: true);
	}
}
