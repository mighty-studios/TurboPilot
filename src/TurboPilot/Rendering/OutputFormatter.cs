using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using TurboPilot.Permissions;

namespace TurboPilot.Rendering;

// Rule-based preparation of completed replies for the Rendered view. File references that resolve
// to readable files become links, and referenced images get inline previews. Code, existing links,
// and URLs are left as written.
internal static class OutputFormatter
{
	private static readonly Regex ProtectedRegions = new(
		"""(?m)^[ \t]*(?<fence>`{3,}|~{3,})[^\r\n]*(?:\r?\n|$)[\s\S]*?(?:^[ \t]*\k<fence>[ \t]*(?:\r?\n|$)|\z)|!?\[[^\]\r\n]*\]\([^\r\n]*?\)|https?://[^\s<>]+|<[^>]+>|`+[^`\r\n]*`+""",
		RegexOptions.Compiled, TimeSpan.FromSeconds(1));
	private static readonly Regex BarePath = new(
		"""(?<![\w:/\\])(?:[A-Za-z]:[\\/])?(?:[\w.-]+[\\/])*[\w.-]+\.[A-Za-z0-9]{1,12}(?::\d+(?::\d+)?)?""",
		RegexOptions.Compiled, TimeSpan.FromSeconds(1));
	private static readonly Regex LineSuffix = new(@":\d+(?::\d+)?$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
	private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
		{ ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico" };
	private const long MaxImageBytes = 4 * 1024 * 1024;

	public static string Apply(string output, string? workspace, WorkspaceFileIndex? index, Action<string> report)
	{
		var builder = new StringBuilder();
		var previews = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var offset = 0;
		foreach (Match region in ProtectedRegions.Matches(output))
		{
			builder.Append(PreparePlain(output[offset..region.Index], workspace, index, previews, report));
			var value = region.Value;
			if (value.StartsWith('`') && !value.Contains('\n') && !value.StartsWith("```", StringComparison.Ordinal))
				builder.Append(Link(value.Trim('`'), value, workspace, index, previews, report) ?? value);
			else
				builder.Append(value);
			offset = region.Index + region.Length;
		}
		builder.Append(PreparePlain(output[offset..], workspace, index, previews, report));
		foreach (var (path, data) in previews)
			builder.Append("\r\n\r\n![").Append(Escape(Path.GetFileName(path))).Append("](").Append(data).Append(')');
		return builder.ToString();
	}

	private static string PreparePlain(string text, string? workspace, WorkspaceFileIndex? index,
		Dictionary<string, string> previews, Action<string> report)
	{
		var result = new StringBuilder();
		var offset = 0;
		foreach (Match match in BarePath.Matches(text))
		{
			if (Link(match.Value, match.Value, workspace, index, previews, report) is not { } linked)
				continue;
			result.Append(text, offset, match.Index - offset).Append(linked);
			offset = match.Index + match.Length;
		}
		return result.Append(text, offset, text.Length - offset).ToString();
	}

	private static string? Link(string reference, string label, string? workspace, WorkspaceFileIndex? index,
		Dictionary<string, string> previews, Action<string> report)
	{
		var referencePath = LineSuffix.Replace(reference, "");
		if (referencePath.Contains("://", StringComparison.Ordinal) || referencePath.StartsWith(@"\\", StringComparison.Ordinal)
			|| referencePath.IndexOfAny(['\r', '\n', '"', '*', '?', '<', '>', '|']) >= 0)
			return null;
		string? path;
		try { path = Resolve(referencePath.Replace('/', '\\'), workspace, index); }
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return null;
		}
		if (path is null || !PermissionService.IsAllowed(path, PermissionAccess.Read, workspace) || !File.Exists(path))
			return null;
		if (ImageExtensions.Contains(Path.GetExtension(path)) && !previews.ContainsKey(path))
		{
			try
			{
				using var stream = File.OpenRead(path);
				if (stream.Length <= MaxImageBytes && previews.Count < 8)
				{
					var data = new byte[checked((int)stream.Length)];
					stream.ReadExactly(data);
					if (stream.ReadByte() != -1)
						throw new IOException("The image changed while its preview was being read.");
					var mime = Path.GetExtension(path).ToLowerInvariant() switch
					{
						".jpg" or ".jpeg" => "image/jpeg",
						".ico" => "image/x-icon",
						_ => "image/" + Path.GetExtension(path)[1..].ToLowerInvariant(),
					};
					previews.Add(path, $"data:{mime};base64,{Convert.ToBase64String(data)}");
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				report("Cannot prepare an image preview: " + ex.Message);
			}
		}
		var linkText = label.StartsWith('`') && label.EndsWith('`') ? label : Escape(label);
		return $"[{linkText}](kp-path:{Uri.EscapeDataString(path)})";
	}

	// References resolve against the workspace root first. A name or partial path that is not
	// there links when exactly one workspace file ends with it.
	private static string? Resolve(string reference, string? workspace, WorkspaceFileIndex? index)
	{
		if (Path.IsPathFullyQualified(reference))
			return Path.GetFullPath(reference);
		if (string.IsNullOrWhiteSpace(workspace) || Path.IsPathRooted(reference))
			return null;
		var direct = Path.GetFullPath(reference, workspace);
		return File.Exists(direct) ? direct : index?.Find(reference);
	}

	private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]");
}
