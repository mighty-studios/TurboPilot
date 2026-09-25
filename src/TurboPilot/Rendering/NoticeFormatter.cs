using System.Text;

namespace TurboPilot.Rendering;

/// <summary>
/// Builds the two forms of every transcript notice the application writes
/// itself: the plain line shown in the Raw tab, and the marked-up form
/// shown in the Rendered tab.
///
/// The Rendered form is a small block of literal HTML embedded in the
/// markdown stream. A div on its own line opens an HTML block that ends at
/// the following blank line, so any markdown between the opening and
/// closing tags is still parsed normally; that matters for questions,
/// whose text can carry a whole plan. Everything else is emitted as
/// escaped HTML so a file path or a shell command can never be read as
/// markup. Styling lives in web/output.css and BorlandVisionTheme.cs.
/// </summary>
internal static class NoticeFormatter
{
	/// <summary>A question with its answer choices.</summary>
	public static (string Text, string Rendered) Question(string question, IReadOnlyList<string> choices, bool allowFreeform)
	{
		var hint = allowFreeform
			? "Reply with an option number, its text, or your own answer and press Send."
			: "Reply with an option number or its text and press Send.";
		var text = new StringBuilder("Question: ").Append(question);
		if (choices.Count > 0)
			text.Append("\r\n").Append(string.Join("  |  ", choices.Select((choice, index) => $"{index + 1}. {choice}")));
		text.Append("\r\n(").Append(hint).Append(')');
		return (text.ToString(), Card("question", "Question", question, null, choices, hint));
	}

	/// <summary>A permission request for one operation.</summary>
	public static (string Text, string Rendered) Permission(string kind, string? detail)
	{
		var label = string.IsNullOrWhiteSpace(detail) ? kind : $"{kind}: {detail}";
		var text = $"Permission requested - {label}\r\n1. yes (allow)  |  2. no (deny)\r\nReply with an option and press Send.";
		var rendered = Card("permission", "Permission requested", null,
			string.IsNullOrWhiteSpace(detail)
				? Escape(kind)
				: Escape(kind) + ": <code>" + Escape(detail) + "</code>",
			["yes (allow)", "no (deny)"],
			"Reply with an option and press Send.");
		return (text, rendered);
	}

	/// <summary>A tagged status line, such as a tool step or an error.</summary>
	public static (string Text, string Rendered) Status(string tag, string text)
	{
		var rendered = new StringBuilder("<div class=\"kp-status kp-status-").Append(ClassOf(tag)).Append("\">")
			.Append("<span class=\"kp-status-tag\">").Append(Escape(tag)).Append("</span>")
			.Append("<span class=\"kp-status-text\">").Append(Escape(text)).Append("</span>")
			.Append("</div>");
		return ($"[{tag}] {text}", rendered.ToString());
	}

	/// <summary>A session banner separating one run of the transcript from the next.</summary>
	public static (string Text, string Rendered) Banner(string text) =>
		($"--- {text} ---", "<div class=\"kp-banner\">" + Escape(text) + "</div>");

	/// <summary>
	/// The agent's plan as a checklist. Each step carries its state in a
	/// box drawn from ASCII, so the Raw tab reads the same as the
	/// Rendered one and neither depends on a font that has the glyphs.
	/// </summary>
	public static (string Text, string Rendered) Checklist(IReadOnlyList<PlanStep> steps)
	{
		var text = new StringBuilder("Plan:");
		var rendered = new StringBuilder("<div class=\"kp-card kp-plan\">")
			.Append("<div class=\"kp-card-title\">Plan</div>")
			.Append("<div class=\"kp-plan-steps\">");
		foreach (var step in steps)
		{
			var box = step.IsDone ? "[x]" : step.IsRunning ? "[>]" : step.IsBlocked ? "[!]" : "[ ]";
			var state = step.IsDone ? "done" : step.IsRunning ? "running" : step.IsBlocked ? "blocked" : "pending";
			text.Append("\r\n").Append(box).Append(' ').Append(step.Title);
			rendered.Append("<div class=\"kp-plan-step kp-plan-").Append(state).Append("\">")
				.Append("<span class=\"kp-plan-box\">").Append(box).Append("</span>")
				.Append("<span class=\"kp-plan-title\">").Append(Escape(step.Title)).Append("</span>")
				.Append("</div>");
		}
		rendered.Append("</div></div>");
		return (text.ToString(), rendered.ToString());
	}

	/// <summary>
	/// A tool call. The headline is the one line worth seeing without
	/// asking; everything else is behind a disclosure, shut by default,
	/// because a turn can run twenty tools and a transcript that showed
	/// all of them in full would be unreadable.
	///
	/// The disclosure is plain HTML rather than script, so it works the
	/// same in a saved copy of the transcript as it does on screen.
	/// </summary>
	public static (string Text, string Rendered) Tool(string name, string headline, string body,
		string? state = null, string? outcome = null)
	{
		var kind = state switch
		{
			"ok" => "kp-tool-ok",
			"failed" => "kp-tool-failed",
			_ => "kp-tool-running",
		};
		var text = new StringBuilder("[tool] ").Append(name);
		if (headline.Length > 0)
			text.Append("  ").Append(headline);

		var rendered = new StringBuilder("<div class=\"kp-card kp-tool ").Append(kind).Append("\">")
			.Append("<details><summary>")
			.Append("<span class=\"kp-tool-name\">").Append(Escape(name)).Append("</span>")
			.Append("<span class=\"kp-tool-head\">").Append(Escape(headline)).Append("</span>")
			.Append("</summary>");
		if (body.Length > 0)
			rendered.Append("<pre class=\"kp-tool-body\">").Append(Escape(body)).Append("</pre>");
		if (!string.IsNullOrEmpty(outcome))
			rendered.Append("<pre class=\"kp-tool-outcome\">").Append(Escape(outcome)).Append("</pre>");
		if (body.Length == 0 && string.IsNullOrEmpty(outcome))
			rendered.Append("<div class=\"kp-tool-outcome\">No detail was reported.</div>");
		rendered.Append("</details></div>");
		return (text.ToString(), rendered.ToString());
	}

	/// <summary>
	/// A block of already-aligned text under a heading, for listings the
	/// program writes itself. The body keeps its own spacing: it is laid
	/// out in columns before it gets here, and reflowing it would undo
	/// the only thing making it readable.
	/// </summary>
	public static (string Text, string Rendered) Listing(string title, string body)
	{
		var rendered = new StringBuilder("<div class=\"kp-card kp-listing\">")
			.Append("<div class=\"kp-card-title\">").Append(Escape(title)).Append("</div>")
			.Append("<pre class=\"kp-listing-body\">").Append(Escape(body)).Append("</pre>")
			.Append("</div>");
		return (title + ":\r\n" + body, rendered.ToString());
	}

	private static string Card(string kind, string title, string? body, string? detail,		IReadOnlyList<string> choices, string hint)
	{
		var sb = new StringBuilder("<div class=\"kp-card kp-").Append(kind).Append("\">")
			.Append("<div class=\"kp-card-title\">").Append(Escape(title)).Append("</div>");
		if (detail is not null)
			sb.Append("<div class=\"kp-card-detail\">").Append(detail).Append("</div>");
		// A blank line closes the HTML block so the body is parsed as markdown.
		sb.Append("\r\n\r\n");
		if (!string.IsNullOrWhiteSpace(body))
			sb.Append(body).Append("\r\n\r\n");
		if (choices.Count > 0)
		{
			sb.Append("<div class=\"kp-card-choices\"><ol>");
			foreach (var choice in choices)
				sb.Append("<li>").Append(Escape(choice)).Append("</li>");
			sb.Append("</ol></div>");
		}
		sb.Append("<div class=\"kp-card-hint\">").Append(Escape(hint)).Append("</div>")
			.Append("</div>");
		return sb.ToString();
	}

	/// <summary>Reduces a tag to the letters that can safely name a style class.</summary>
	private static string ClassOf(string tag)
	{
		var sb = new StringBuilder(tag.Length);
		foreach (var character in tag)
		{
			if (char.IsAsciiLetter(character))
				sb.Append(char.ToLowerInvariant(character));
		}
		return sb.Length == 0 ? "note" : sb.ToString();
	}

	private static string Escape(string text) => text
		.Replace("&", "&amp;")
		.Replace("<", "&lt;")
		.Replace(">", "&gt;")
		.Replace("\"", "&quot;");
}
