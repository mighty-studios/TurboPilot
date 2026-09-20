using System.IO;

namespace TurboPilot.Customizations;

/// <summary>
/// Minimal YAML front matter reader for the leading block delimited by
/// --- lines, the convention shared by prompts, skills and instructions.
/// Only top-level scalar key: value pairs are returned; nested structures,
/// lists and multiline values are not interpreted. Read failures yield an
/// empty result so the details pane degrades to file info alone.
/// </summary>
public static class FrontMatter
{
	/// <summary>An empty parse result.</summary>
	public static readonly FrontMatterData Empty = new() { Fields = [] };

	/// <summary>
	/// Reads and parses the front matter block of a file. Returns
	/// <see cref="Empty"/> when the file is missing, unreadable or has
	/// no front matter.
	/// </summary>
	public static FrontMatterData Read(string filePath)
	{
		try
		{
			using var reader = new StreamReader(filePath);

			string? first = reader.ReadLine();
			if (first?.Trim().TrimStart('\uFEFF') != "---")
				return Empty;

			var fields = new List<KeyValuePair<string, string>>();
			string? line;
			while ((line = reader.ReadLine()) is not null)
			{
				string trimmed = line.Trim();
				if (trimmed == "---" || trimmed == "...")
					break;

				int colon = trimmed.IndexOf(':');
				if (colon <= 0)
					continue;

				string key = trimmed[..colon].Trim();
				string value = trimmed[(colon + 1)..].Trim().Trim('"', '\'');
				if (key.Length > 0)
					fields.Add(new KeyValuePair<string, string>(key, value));
			}

			return new FrontMatterData { Fields = fields };
		}
		catch (IOException)
		{
			return Empty;
		}
		catch (UnauthorizedAccessException)
		{
			return Empty;
		}
	}
}

/// <summary>
/// The top-level scalar pairs parsed from a front matter block, in file
/// order.
/// </summary>
public sealed class FrontMatterData
{
	public required IReadOnlyList<KeyValuePair<string, string>> Fields { get; init; }

	/// <summary>Value of a key (case-insensitive), or null when absent.</summary>
	public string? Get(string key)
	{
		foreach (var (k, v) in Fields)
		{
			if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
				return v;
		}
		return null;
	}
}
