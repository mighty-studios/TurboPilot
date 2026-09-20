using System.IO;

namespace TurboPilot.Permissions;

/// <summary>
/// Decides whether a path is covered by a permission entry pattern.
///
/// A pattern is a Windows path whose folder segments may contain:
///   *   any run of characters within one segment
///   ?   a single character within one segment
///   ... any number of whole segments, in any position
///
/// A pattern with no ellipsis covers the folder it names and the entries
/// directly inside it, but nothing deeper: C:\MyFiles matches
/// C:\MyFiles\a.txt and not C:\MyFiles\Sub\a.txt. Writing C:\MyFiles\...
/// extends the entry to everything under C:\MyFiles. Environment variables
/// are expanded and case is ignored, as on Windows.
/// </summary>
public static class PermissionMatcher
{
	/// <summary>
	/// The segment that stands for any number of nested folders.
	/// </summary>
	public const string Ellipsis = "...";

	/// <summary>
	/// True when <paramref name="targetPath"/> is covered by
	/// <paramref name="pattern"/>. A pattern that names a folder covers the
	/// folder itself and whatever sits directly in it; reaching deeper needs
	/// an ellipsis.
	/// </summary>
	public static bool IsMatch(string pattern, string targetPath)
	{
		string[] patternSegments = SplitSegments(Expand(pattern));
		string[] targetSegments = SplitSegments(Expand(targetPath));
		if (patternSegments.Length == 0 || targetSegments.Length == 0)
			return false;

		if (MatchSegments(patternSegments, targetSegments, 0, 0))
			return true;

		// The pattern may name the folder the target lives in instead of the
		// target itself.
		return targetSegments.Length > 1
			&& MatchSegments(patternSegments, targetSegments[..^1], 0, 0);
	}

	/// <summary>
	/// True when <paramref name="targetPath"/> is the folder itself or lies
	/// somewhere below it. Wildcards are treated as literal characters, so
	/// this is only meaningful for plain paths.
	/// </summary>
	public static bool IsUnder(string targetPath, string folder)
	{
		string[] target = SplitSegments(Expand(targetPath));
		string[] prefix = SplitSegments(Expand(folder));
		if (prefix.Length == 0 || target.Length < prefix.Length)
			return false;

		for (int i = 0; i < prefix.Length; i++)
		{
			if (!string.Equals(target[i], prefix[i], StringComparison.OrdinalIgnoreCase))
				return false;
		}

		return true;
	}

	/// <summary>
	/// The concrete folder a pattern is anchored to, used to warn when an
	/// entry points somewhere that is not on disk. The ellipsis drops out of
	/// the anchor; a wildcard segment ends it at the folder before that
	/// segment.
	/// </summary>
	public static string AnchorFolder(string pattern)
	{
		string expanded = Expand(pattern);
		if (expanded.Length == 0)
			return string.Empty;

		bool unc = expanded.StartsWith(@"\\", StringComparison.Ordinal);
		var anchor = new List<string>();
		foreach (string segment in expanded.Split('\\'))
		{
			if (segment.Length == 0 || segment == Ellipsis)
				continue;
			if (segment.Contains('*') || segment.Contains('?'))
				break;
			anchor.Add(segment);
		}

		if (anchor.Count == 0)
			return string.Empty;

		// A drive root needs its separator back: "C:" alone is not a path.
		if (anchor.Count == 1 && anchor[0].Length == 2 && anchor[0][1] == ':')
			return anchor[0] + "\\";

		return (unc ? @"\\" : string.Empty) + string.Join('\\', anchor);
	}

	/// <summary>
	/// True when the folder a pattern is anchored to exists. A pattern that
	/// is nothing but wildcards has no anchor and reports false.
	/// </summary>
	public static bool AnchorExists(string pattern)
	{
		string anchor = AnchorFolder(pattern);
		return anchor.Length > 0 && Directory.Exists(anchor);
	}

	/// <summary>
	/// True when the pattern contains an ellipsis, so it reaches into
	/// subfolders.
	/// </summary>
	public static bool IsRecursive(string pattern) =>
		Expand(pattern).Split('\\').Contains(Ellipsis, StringComparer.Ordinal);

	// ------------------------------------------------------------------ matching

	/// <summary>
	/// Walks the pattern and target segment lists together. The ellipsis
	/// consumes any number of target segments, including none, so
	/// C:\A\...\B also matches C:\A\B.
	/// </summary>
	private static bool MatchSegments(string[] pattern, string[] target, int pi, int ti)
	{
		while (pi < pattern.Length)
		{
			string segment = pattern[pi];
			if (segment == Ellipsis)
			{
				for (int taken = ti; taken <= target.Length; taken++)
				{
					if (MatchSegments(pattern, target, pi + 1, taken))
						return true;
				}

				return false;
			}

			if (ti >= target.Length)
				return false;
			if (!SegmentMatches(segment, target[ti]))
				return false;

			pi++;
			ti++;
		}

		return ti == target.Length;
	}

	/// <summary>
	/// Matches one folder segment against a pattern segment that may use *
	/// and ?. Neither wildcard crosses a folder boundary.
	/// </summary>
	private static bool SegmentMatches(string pattern, string value)
	{
		if (!pattern.Contains('*') && !pattern.Contains('?'))
			return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);

		return Glob(pattern, 0, value, 0);
	}

	private static bool Glob(string pattern, int pi, string value, int vi)
	{
		while (true)
		{
			if (pi == pattern.Length)
				return vi == value.Length;

			char pc = pattern[pi];
			if (pc == '*')
			{
				while (pi < pattern.Length && pattern[pi] == '*')
					pi++;
				if (pi == pattern.Length)
					return true;

				for (int k = vi; k <= value.Length; k++)
				{
					if (Glob(pattern, pi, value, k))
						return true;
				}

				return false;
			}

			if (vi == value.Length)
				return false;
			if (pc != '?' && char.ToUpperInvariant(pc) != char.ToUpperInvariant(value[vi]))
				return false;

			pi++;
			vi++;
		}
	}

	// ------------------------------------------------------------------ paths

	/// <summary>
	/// Trims, unquotes and expands environment variables, and normalizes
	/// separators. Trailing separators are left alone: the segment split
	/// drops empty entries anyway.
	/// </summary>
	private static string Expand(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return string.Empty;

		string trimmed = path.Trim().Trim('"');
		if (trimmed.Length == 0)
			return string.Empty;

		return Environment.ExpandEnvironmentVariables(trimmed).Replace('/', '\\');
	}

	/// <summary>
	/// Splits a normalized path into folder segments. Empty segments from
	/// repeated or trailing separators are dropped, and a UNC path keeps its
	/// leading pair of separators attached to the server name so that
	/// \\server\share and server\share cannot collide.
	/// </summary>
	private static string[] SplitSegments(string path)
	{
		if (path.Length == 0)
			return [];

		bool unc = path.StartsWith(@"\\", StringComparison.Ordinal);
		List<string> segments = [.. path.Split('\\', StringSplitOptions.RemoveEmptyEntries)];
		if (unc && segments.Count > 0)
			segments[0] = @"\\" + segments[0];

		return [.. segments];
	}
}
