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
	}
}
