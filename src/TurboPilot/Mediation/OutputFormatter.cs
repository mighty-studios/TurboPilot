using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using TurboPilot.Permissions;

namespace TurboPilot.Mediation;

internal static class OutputFormatter
{
	private static readonly Regex ProtectedRegions = new(
		"""(?m)^[ \t]*(?<fence>`{3,}|~{3,})[^\r\n]*(?:\r?\n|$)[\s\S]*?(?:^[ \t]*\k<fence>[ \t]*(?:\r?\n|$)|\z)|!?\[[^\]\r\n]*\]\([^\r\n]*?\)|https?://[^\s<>]+|<[^>]+>|`+[^`\r\n]*`+""",
		RegexOptions.Compiled, TimeSpan.FromSeconds(1));
	private static readonly Regex BarePath = new(
		"""(?<![\w:/\\])(?:[A-Za-z]:[\\/])?(?:[\w.-]+[\\/])*[\w.-]+\.[A-Za-z0-9]{1,12}(?::\d+(?::\d+)?)?""",
		RegexOptions.Compiled, TimeSpan.FromSeconds(1));
	private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
		{ ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico" };
	private const long MaxImageBytes = 4 * 1024 * 1024;

	public static string Apply(string output, FormatSuggestions suggestions, string? workspace, Action<string> report)
	{
		var builder = new StringBuilder();
		var previews = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var offset = 0;
		foreach (Match region in ProtectedRegions.Matches(output))
		{
			builder.Append(PreparePlain(output[offset..region.Index], suggestions, workspace, previews, report));
			var value = region.Value;
			if (value.StartsWith('`') && !value.Contains('\n') && !value.StartsWith("```", StringComparison.Ordinal))
			{
				var path = value.Trim('`');
				builder.Append(Link(path, value, workspace, previews, report) ?? value);
			}
			else
				builder.Append(value);
			offset = region.Index + region.Length;
		}
		builder.Append(PreparePlain(output[offset..], suggestions, workspace, previews, report));
		foreach (var (path, data) in previews)
			builder.Append("\r\n\r\n![").Append(Escape(Path.GetFileName(path))).Append("](").Append(data).Append(')');
		return builder.ToString();
	}

	private static string PreparePlain(string text, FormatSuggestions suggestions, string? workspace,
		Dictionary<string, string> previews, Action<string> report)
	{
		var replacements = new List<(int Start, int Length, string Value)>();
		foreach (var item in suggestions.Links.Take(30))
		{
			if (string.IsNullOrWhiteSpace(item.Text) || item.Text != item.Path || item.Text.Contains('\n'))
				continue;
			var start = text.IndexOf(item.Text, StringComparison.Ordinal);
			while (start >= 0)
			{
				var linked = Link(item.Path, item.Text, workspace, previews, report);
				if (linked is not null)
					replacements.Add((start, item.Text.Length, linked));
				start = text.IndexOf(item.Text, start + item.Text.Length, StringComparison.Ordinal);
			}
		}
		foreach (Match match in BarePath.Matches(text))
		{
			var linked = Link(match.Value, match.Value, workspace, previews, report);
			if (linked is not null)
				replacements.Add((match.Index, match.Length, linked));
		}
		var result = new StringBuilder();
		var offset = 0;
		foreach (var replacement in replacements.OrderBy(item => item.Start).ThenByDescending(item => item.Length))
		{
			if (replacement.Start < offset) continue;
			result.Append(text[offset..replacement.Start]).Append(replacement.Value);
			offset = replacement.Start + replacement.Length;
		}
		result.Append(text[offset..]);
		var prepared = result.ToString();
		foreach (var heading in suggestions.Headings.Take(10))
		{
			if (string.IsNullOrWhiteSpace(heading) || heading.Length > 100 || heading.Contains('\n')
				|| heading.IndexOfAny(['`', '[', ']', '#', '|', '<', '>']) >= 0)
				continue;
			prepared = Regex.Replace(prepared, @"(?m)^" + Regex.Escape(heading) + @"\r?$",
				match => "## " + match.Value, RegexOptions.None, TimeSpan.FromSeconds(1));
		}
		return prepared;
	}

	private static string? Link(string reference, string label, string? workspace,
		Dictionary<string, string> previews, Action<string> report)
	{
		var referencePath = Regex.Replace(reference, @":\d+(?::\d+)?$", "", RegexOptions.None, TimeSpan.FromSeconds(1));
		if (referencePath.Contains("://", StringComparison.Ordinal) || referencePath.StartsWith(@"\\", StringComparison.Ordinal)
			|| referencePath.IndexOfAny(['\r', '\n', '"', '*', '?', '<', '>', '|']) >= 0)
			return null;
		string path;
		try
		{
			var windowsPath = referencePath.Replace('/', '\\');
			if (Path.IsPathFullyQualified(windowsPath))
				path = Path.GetFullPath(windowsPath);
			else if (!string.IsNullOrWhiteSpace(workspace) && !Path.IsPathRooted(windowsPath))
				path = Path.GetFullPath(windowsPath, workspace);
			else
				return null;
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return null;
		}
		if (!PermissionService.IsAllowed(path, PermissionAccess.Read, workspace) || !File.Exists(path))
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
		return $"[{Escape(label)}](kp-path:{Uri.EscapeDataString(path)})";
	}

	private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]");
}
