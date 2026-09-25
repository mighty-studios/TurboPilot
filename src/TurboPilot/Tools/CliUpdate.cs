using System.Net.Http;
using System.Text.Json;

namespace TurboPilot.Tools;

/// <summary>
/// Tells the user when the bundled Copilot CLI is behind its latest
/// release. A stale CLI fails in ways that read as application faults, so
/// naming the version gap is worth one transcript line.
///
/// GitHub Releases is the source of truth rather than the npm package:
/// npm has been seen publishing ahead of the official release, which
/// produces update notices for a version nobody can install yet.
/// `releases/latest` excludes drafts and pre-releases on its own.
///
/// Everything here is best-effort. No network, a rate limit, or an
/// unparseable tag all mean the same thing: say nothing. An update notice
/// is a courtesy, and a courtesy that interrupts is a defect.
/// </summary>
internal static class CliUpdate
{
	internal const string LatestReleaseUrl =
		"https://api.github.com/repos/github/copilot-cli/releases/latest";

	private static readonly HttpClient Http = CreateClient();

	// The check is per application run, not per session: starting three
	// sessions in an afternoon is not a reason to ask GitHub three times.
	private static int _checked;

	/// <summary>
	/// Resets the once-per-run guard. For checks that need to observe the
	/// first call more than once.
	/// </summary>
	internal static void ResetForTesting() => Interlocked.Exchange(ref _checked, 0);

	/// <summary>
	/// The notice for a CLI-backed session, or null when there is nothing
	/// worth saying: already checked this run, no version reported, the
	/// lookup failed, or the CLI is current.
	/// </summary>
	internal static async Task<string?> CheckAsync(string? currentVersion,
		Func<CancellationToken, Task<string?>>? lookup = null, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(currentVersion))
			return null;
		if (Interlocked.Exchange(ref _checked, 1) != 0)
			return null;
		var latest = await (lookup ?? LatestVersionAsync)(cancellationToken);
		return latest is null ? null : Notice(currentVersion, latest);
	}

	/// <summary>
	/// The notice text for a known pair of versions, or null when the
	/// current version is not behind. Separate from the lookup so the
	/// wording can be checked without a network.
	/// </summary>
	internal static string? Notice(string currentVersion, string latestVersion) =>
		IsNewer(currentVersion, latestVersion)
			? $"Copilot CLI {Normalize(currentVersion)} -> {Normalize(latestVersion)} available. "
				+ "Open a Copilot terminal and run /update"
			: null;

	/// <summary>
	/// Queries GitHub for the latest published CLI release tag. Returns
	/// null for every failure, including an offline machine.
	/// </summary>
	internal static async Task<string?> LatestVersionAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await Http.GetAsync(LatestReleaseUrl, cancellationToken);
			if (!response.IsSuccessStatusCode)
				return null;
			using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
			return document.RootElement.TryGetProperty("tag_name", out var tag)
				&& tag.ValueKind == JsonValueKind.String
				&& !string.IsNullOrWhiteSpace(tag.GetString())
					? tag.GetString()
					: null;
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
		{
			return null;
		}
	}

	/// <summary>
	/// Whether <paramref name="latest"/> is a later version than
	/// <paramref name="current"/>. Compares numeric parts in order and
	/// treats a missing part as zero, so 1.0 and 1.0.0 are the same
	/// version. Anything that does not parse is treated as "not newer",
	/// because a confident wrong answer is worse than silence.
	/// </summary>
	internal static bool IsNewer(string current, string latest)
	{
		var left = Parts(current);
		var right = Parts(latest);
		if (left.Length == 0 || right.Length == 0)
			return false;
		for (var index = 0; index < Math.Max(left.Length, right.Length); index++)
		{
			var mine = index < left.Length ? left[index] : 0;
			var theirs = index < right.Length ? right[index] : 0;
			if (theirs != mine)
				return theirs > mine;
		}
		return false;
	}

	/// <summary>The version as "v1.2.3", however it was tagged.</summary>
	private static string Normalize(string version)
	{
		var trimmed = version.Trim();
		return trimmed.StartsWith('v') || trimmed.StartsWith('V') ? "v" + trimmed[1..] : "v" + trimmed;
	}

	/// <summary>
	/// The leading numeric parts of a version tag. Stops at the first
	/// part that is not a plain number, which drops pre-release and build
	/// suffixes such as "-beta.1" rather than guessing at their order.
	/// </summary>
	private static int[] Parts(string version)
	{
		var trimmed = version.Trim().TrimStart('v', 'V');
		var numbers = new List<int>();
		foreach (var part in trimmed.Split('.'))
		{
			if (!int.TryParse(part, out var number) || number < 0)
				break;
			numbers.Add(number);
		}
		return [.. numbers];
	}

	private static HttpClient CreateClient()
	{
		var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
		// The GitHub API rejects requests without a user agent.
		client.DefaultRequestHeaders.UserAgent.ParseAdd("TurboPilot");
		client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
		return client;
	}
}
