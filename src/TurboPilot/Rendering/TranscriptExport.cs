using System.IO;
using System.Text;

namespace TurboPilot.Rendering;

/// <summary>
/// Builds a saved copy of a transcript.
///
/// The HTML form is the Rendered document as it stands on screen, not a
/// second rendering of the same markdown: the diagrams are already SVG
/// and the code is already colored, so taking the live document avoids
/// shipping a renderer with every saved file and avoids the saved file
/// disagreeing with the tab it came from. Styling is inlined for the
/// same reason, and the disclosure cards keep working because they were
/// written as plain HTML rather than script.
/// </summary>
public static class TranscriptExport
{
	/// <summary>Formats the export can be written in.</summary>
	public enum Format
	{
		/// <summary>The Rendered document with its styling inlined.</summary>
		Html,

		/// <summary>The markdown the Rendered tab was built from.</summary>
		Markdown,

		/// <summary>The Raw tab, exactly as printed.</summary>
		Text,
	}

	/// <summary>The extension each format is written with, without a dot.</summary>
	public static string ExtensionOf(Format format) => format switch
	{
		Format.Html => "html",
		Format.Markdown => "md",
		_ => "txt",
	};

	/// <summary>
	/// The format a chosen file name asks for. The extension decides,
	/// because that is what the user sees and what the shell will open
	/// the file with; an unknown extension is saved as text rather than
	/// refused.
	/// </summary>
	public static Format FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
	{
		".html" or ".htm" => Format.Html,
		".md" or ".markdown" => Format.Markdown,
		_ => Format.Text,
	};

	/// <summary>
	/// A file name for a transcript, dated so repeated saves do not
	/// silently overwrite each other. Anything a file name cannot hold
	/// becomes a hyphen.
	/// </summary>
	public static string SuggestedName(string label, DateTime when, Format format)
	{
		var stem = new StringBuilder();
		foreach (var c in label)
			stem.Append(Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '-' : c);

		var cleaned = stem.ToString().Trim('-');
		if (cleaned.Length == 0) cleaned = "transcript";
		if (cleaned.Length > 60) cleaned = cleaned[..60].TrimEnd('-');

		return $"{cleaned}-{when:yyyyMMdd-HHmmss}.{ExtensionOf(format)}";
	}

	/// <summary>
	/// A standalone page carrying the transcript body and everything
	/// needed to display it. The font is inlined as data when its file
	/// can be read, because the stylesheet names it by a path that only
	/// resolves inside the application folder.
	/// </summary>
	public static string Html(string title, string pageCss, string themeCss, string body, string? fontDataUrl)
	{
		if (fontDataUrl is { Length: > 0 })
			themeCss = themeCss.Replace($"url('{BorlandVisionTheme.FontFileUrl}')", $"url('{fontDataUrl}')");

		var sb = new StringBuilder();
		sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"UTF-8\">\n");
		sb.Append("<title>").Append(Escape(title)).Append("</title>\n<style>\n");
		sb.Append(pageCss).Append('\n').Append(themeCss).Append("\n</style>\n</head>\n<body>\n");
		sb.Append("<div id=\"output\">\n").Append(body).Append("\n</div>\n</body>\n</html>\n");
		return sb.ToString();
	}

	/// <summary>
	/// The plain-text forms. Both get a heading naming what was saved,
	/// since a transcript on its own says nothing about where it is from.
	/// </summary>
	public static string Plain(string title, string transcript, Format format)
	{
		var heading = format == Format.Markdown ? $"# {title}\n\n" : $"{title}\n\n";
		return heading + transcript.Replace("\r\n", "\n").TrimEnd() + "\n";
	}

	/// <summary>
	/// The font file as a data URL, or null when it cannot be read. A
	/// missing font is not worth failing an export over: the stylesheet
	/// names fallbacks.
	/// </summary>
	public static string? FontDataUrl(string webFolder)
	{
		try
		{
			var path = Path.Combine(webFolder, BorlandVisionTheme.FontFileUrl.Replace('/', Path.DirectorySeparatorChar));
			if (!File.Exists(path)) return null;
			return "data:font/ttf;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static string Escape(string text) => text
		.Replace("&", "&amp;")
		.Replace("<", "&lt;")
		.Replace(">", "&gt;");
}
