using System.Diagnostics;
using System.IO;

namespace TurboPilot.Tools;

/// <summary>
/// The Tools menu: each item opens an external program on the workspace
/// folder of the running session.
///
/// Launching is split from building so the arguments can be checked
/// without starting a process. Nothing here waits on the program it
/// starts; these are separate applications, not child work.
/// </summary>
internal static class ExternalTools
{
	internal const string ScriptsFileName = "scripts.ps1";

	/// <summary>
	/// The user's copy of the helper functions, outside any workspace.
	/// Created from the bundled default only when missing, and never
	/// overwritten, like the application instructions file.
	/// </summary>
	internal static string DefaultScriptsPath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"TurboPilot", "scripts", ScriptsFileName);

	/// <summary>
	/// Returns the path to the user's helper functions, writing the
	/// bundled default first if that file does not exist yet.
	/// </summary>
	internal static string EnsureScripts(string? path = null)
	{
		path = Path.GetFullPath(path ?? DefaultScriptsPath);
		try
		{
			if (!File.Exists(path))
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", ScriptsFileName), path, overwrite: false);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			throw new InvalidOperationException(
				$"Cannot prepare the helper functions at '{path}': {ex.Message}", ex);
		}
		return path;
	}

	/// <summary>
	/// A shell opened in the workspace with the helper functions loaded.
	/// The script is dotted rather than run so its functions stay defined
	/// in the session; a missing script leaves a plain shell rather than
	/// failing to open one.
	/// </summary>
	internal static ProcessStartInfo BuildPowerShell(string workspace, string? scriptsPath)
	{
		// -NoExit keeps the window open. The quotes inside the dotted path
		// are doubled because it is a PowerShell single-quoted literal.
		var arguments = scriptsPath is not null && File.Exists(scriptsPath)
			? $"-NoExit -Command \". '{scriptsPath.Replace("'", "''")}'\""
			: "-NoExit";
		return new ProcessStartInfo
		{
			FileName = ResolvePowerShell(),
			Arguments = arguments,
			WorkingDirectory = workspace,
			UseShellExecute = true,
		};
	}

	/// <summary>A File Explorer window on the workspace.</summary>
	internal static ProcessStartInfo BuildExplorer(string workspace) =>
		new()
		{
			FileName = "explorer.exe",
			Arguments = $"\"{workspace}\"",
			UseShellExecute = true,
		};

	/// <summary>
	/// Visual Studio Code opened on the workspace. `code` is a launcher
	/// shim on PATH, so the shell has to resolve it.
	/// </summary>
	internal static ProcessStartInfo BuildVsCode(string workspace) =>
		new()
		{
			FileName = "code",
			Arguments = $"\"{workspace}\"",
			WorkingDirectory = workspace,
			UseShellExecute = true,
		};

	internal static void Start(ProcessStartInfo startInfo) => Process.Start(startInfo)?.Dispose();

	/// <summary>
	/// Prefers PowerShell 7 so the user gets the profile and modules they
	/// actually work in, and falls back to the in-box Windows PowerShell.
	/// </summary>
	internal static string ResolvePowerShell()
	{
		foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
		{
			if (string.IsNullOrWhiteSpace(directory))
				continue;
			try
			{
				var candidate = Path.Combine(directory.Trim(), "pwsh.exe");
				if (File.Exists(candidate))
					return candidate;
			}
			catch (ArgumentException)
			{
				// A malformed PATH entry is not worth reporting.
			}
		}
		return "powershell.exe";
	}
}
