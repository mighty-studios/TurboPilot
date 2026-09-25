using System.IO;
using TurboPilot.Customizations;
using YamlDotNet.Core;

namespace TurboPilot.Ai;

internal static class ApplicationInstructions
{
	internal const string FileName = "turbopilot.instructions.md";
	internal static string DefaultPath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"TurboPilot", "instructions", FileName);

	internal static string Read(string? path = null)
	{
		path ??= DefaultPath;
		try
		{
			if (!File.Exists(path))
			{
				Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
				File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", FileName), path, overwrite: false);
			}
			var document = FrontMatter.ReadDocument(path);
			return $"## TurboPilot application instructions\r\nSource: {path}\r\n\r\n{document.Body}";
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlException)
		{
			throw new InvalidOperationException($"Cannot load TurboPilot application instructions from '{path}': {ex.Message}", ex);
		}
	}
}
