using System.IO;
using TurboPilot.Rendering;

namespace TurboPilot.Tests;

internal static class RenderingChecks
{
	public static void Run()
	{
		using var workspace = new TestWorkspace();
		var readme = workspace.Write("workspace\\README.md", "readme");
		var deep = workspace.Write("workspace\\src\\app\\Deep.cs", "deep");
		workspace.Write("workspace\\src\\a\\dup.txt", "a");
		var second = workspace.Write("workspace\\src\\b\\dup.txt", "b");
		workspace.Write("workspace\\bin\\Debug\\Only.dll", "build output");
		var image = Path.Combine(workspace.Workspace, "preview.png");
		File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="));
		var outside = workspace.Write("outside.png", "not an image");
		var index = new WorkspaceFileIndex(workspace.Workspace);
		var notes = new List<string>();
		string Format(string text) => OutputFormatter.Apply(text, workspace.Workspace, index, notes.Add);
		static string Link(string path) => "kp-path:" + Uri.EscapeDataString(path);

		var prepared = Format("See README.md, `src\\app\\Deep.cs`, and preview.png.");
		Check.True(prepared.Contains(Link(readme)) && prepared.Contains("[`src\\app\\Deep.cs`](" + Link(deep) + ")"),
			"Link workspace-relative references and keep code-formatted labels.");
		Check.True(prepared.Contains("data:image/png;base64,"), "Preview a referenced image.");
		Check.True(Format("Open Deep.cs next.").Contains(Link(deep)), "Link a bare name found once in the workspace.");
		Check.True(!Format("Compare the dup.txt files.").Contains("kp-path:"), "Leave an ambiguous name unlinked.");
		Check.True(Format("Edit b/dup.txt now.").Contains(Link(second)), "Use a partial path to choose between matches.");
		Check.True(!Format("Rebuild Only.dll first.").Contains("kp-path:"), "Ignore build output folders.");
		Check.True(!Format("Not ../README.md here.").Contains("kp-path:"), "Do not search upward for parent references.");
		var protectedText = $"[existing](https://example.com/README.md) https://example.com/README.md\r\n```\r\nREADME.md\r\n```\r\n~~~\r\nDeep.cs\r\n~~~\r\n{outside}";
		var kept = Format(protectedText);
		Check.True(kept.Contains("[existing](https://example.com/README.md)") && kept.Contains("```\r\nREADME.md\r\n```")
			&& kept.Contains("~~~\r\nDeep.cs\r\n~~~"), "Leave existing links, URLs, and code as written.");
		Check.True(!kept.Contains(Uri.EscapeDataString(outside)), "Do not link files outside the permitted scope.");
		Check.Equal(0, notes.Count, "Readable files must not produce notices");
		Console.WriteLine("PASS rule-based Rendered links, image previews, workspace name lookup, and protected regions");

		CheckNotices();
	}

	private static void CheckNotices()
	{
		var (questionText, questionRendered) = NoticeFormatter.Question(
			"Pick a **plan**", ["Ship it", "Keep planning"], allowFreeform: true);
		Check.True(questionText.StartsWith("Question: Pick a **plan**", StringComparison.Ordinal)
			&& questionText.Contains("1. Ship it  |  2. Keep planning")
			&& questionText.Contains("(Reply with an option number, its text, or your own answer and press Send.)"),
			"Keep the plain question line in the Raw tab.");
		Check.True(questionRendered.StartsWith("<div class=\"kp-card kp-question\">", StringComparison.Ordinal)
			&& questionRendered.EndsWith("</div>", StringComparison.Ordinal),
			"Frame a question as a card.");
		Check.True(questionRendered.Contains("</div>\r\n\r\nPick a **plan**\r\n\r\n"),
			"Leave the question body as markdown, set off by blank lines.");
		Check.True(questionRendered.Contains("<ol><li>Ship it</li><li>Keep planning</li></ol>"),
			"List the choices as an ordered list.");

		var (permissionText, permissionRendered) = NoticeFormatter.Permission("shell", "dir <C:\\a & b>");
		Check.True(permissionText.StartsWith("Permission requested - shell: dir <C:\\a & b>", StringComparison.Ordinal),
			"Keep the plain permission line in the Raw tab.");
		Check.True(permissionRendered.Contains("<code>dir &lt;C:\\a &amp; b&gt;</code>"),
			"Escape the requested operation and show it as code.");
		Check.True(!permissionRendered.Contains("<C:"), "A command can never be read as markup.");

		var (statusText, statusRendered) = NoticeFormatter.Status("error", "MCP <server> failed");
		Check.Equal("[error] MCP <server> failed", statusText, "Keep the plain status line in the Raw tab");
		Check.True(statusRendered.Contains("class=\"kp-status kp-status-error\"")
			&& statusRendered.Contains("<span class=\"kp-status-tag\">error</span>")
			&& statusRendered.Contains("MCP &lt;server&gt; failed"),
			"Tag and escape a status line.");

		var (bannerText, bannerRendered) = NoticeFormatter.Banner("Session 1 | model | mode");
		Check.Equal("--- Session 1 | model | mode ---", bannerText, "Keep the plain banner in the Raw tab");
		Check.Equal("<div class=\"kp-banner\">Session 1 | model | mode</div>", bannerRendered, "Frame a banner");
		Console.WriteLine("PASS notice cards, status lines, and banners with escaped content");
	}
}
