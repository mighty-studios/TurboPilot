using System.IO;

namespace TurboPilot.Sessions;

/// <summary>
/// The readme of a workspace, if it has one. A new session offers to send
/// it so the model starts with the project's own description of itself
/// rather than an empty transcript.
/// </summary>
internal static class WorkspaceReadme
{
	/// <summary>
	/// Names looked for in the workspace root, in order of preference.
	/// Windows matches them without regard to case, so `Readme.md` is
	/// found as well.
	/// </summary>
	private static readonly string[] Names = ["README.md", "README.txt"];

	/// <summary>
	/// The workspace readme, or null when the folder has none. Only the
	/// root is searched: a readme deeper in the tree describes a part of
	/// the project, not the project.
	/// </summary>
	internal static string? Find(string? workspace)
	{
		if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
			return null;
		foreach (var name in Names)
		{
			var candidate = Path.Combine(workspace, name);
			if (File.Exists(candidate))
				return candidate;
		}
		return null;
	}

	internal static string Question(string path) =>
		$"'{Path.GetFileName(path)}' was found in the workspace root.\r\n\r\n" +
		"Send it so the model can read up on the project before you begin?";

	/// <summary>
	/// The opening prompt. The file travels as an attachment rather than
	/// pasted text, so the runtime handles a long readme its own way. The
	/// wording stops the model at a summary: this is orientation, not a
	/// work request.
	/// </summary>
	internal static string Prompt(string path) =>
		$"Read the attached '{Path.GetFileName(path)}' from the workspace root to understand this project. " +
		"Summarize what the project is and how it is built in a few lines, then wait for my instructions. " +
		"Do not change any files, and do not start any work yet.";
}
