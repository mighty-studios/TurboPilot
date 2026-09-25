namespace TurboPilot.Rendering;

/// <summary>
/// One step of the agent's plan, reduced to what the UI shows.
/// </summary>
public readonly record struct PlanStep(string Title, string Status)
{
	internal bool IsDone => string.Equals(Status, "done", StringComparison.OrdinalIgnoreCase);

	internal bool IsRunning => string.Equals(Status, "in_progress", StringComparison.OrdinalIgnoreCase);

	internal bool IsBlocked => string.Equals(Status, "blocked", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The agent's plan reduced to a position the status line can carry:
/// which step is running, out of how many.
///
/// The window is a narrow column, so progress cannot have a panel of its
/// own. It has to fit in the status text that is already there, which
/// means one short fragment and nothing else. The full list goes to the
/// transcript, where it scrolls away with the turn that produced it.
/// </summary>
public sealed class TaskProgress
{
	private const int TitleBudget = 28;

	private TaskProgress(int position, int total, string title, bool complete)
	{
		Position = position;
		Total = total;
		Title = title;
		Complete = complete;
	}

	/// <summary>The 1-based step the agent is on.</summary>
	internal int Position { get; }

	/// <summary>How many steps the plan has.</summary>
	internal int Total { get; }

	/// <summary>The title of the step the agent is on, clipped for the status line.</summary>
	internal string Title { get; }

	/// <summary>Whether every step is finished.</summary>
	internal bool Complete { get; }

	/// <summary>
	/// Reads a plan from the agent's todo rows, or null when there is no
	/// plan worth reporting. A single step is not a plan: an agent that
	/// records one todo is just doing the thing that was asked, and
	/// "[1/1]" tells the user nothing they did not already know.
	/// </summary>
	internal static TaskProgress? From(IReadOnlyList<PlanStep> steps)
	{
		if (steps.Count < 2)
			return null;

		// The step the agent is on is the one it marked running; failing
		// that, the first one it has not finished. A blocked step is
		// still where the work stands, so it counts as current.
		var index = IndexOf(steps, step => step.IsRunning);
		if (index < 0)
			index = IndexOf(steps, step => !step.IsDone);
		var complete = index < 0;
		if (complete)
			index = steps.Count - 1;

		return new TaskProgress(index + 1, steps.Count,
			ShortText.Clip(steps[index].Title, TitleBudget), complete);
	}

	/// <summary>
	/// The fragment appended to the status phrase, such as
	/// "[3/5] Writing tests". Empty once the plan is finished, because a
	/// completed plan is not where the user is any more.
	/// </summary>
	internal string StatusFragment => Complete
		? ""
		: Title.Length == 0 ? $"[{Position}/{Total}]" : $"[{Position}/{Total}] {Title}";

	private static int IndexOf(IReadOnlyList<PlanStep> steps, Func<PlanStep, bool> match)
	{
		for (var index = 0; index < steps.Count; index++)
		{
			if (match(steps[index]))
				return index;
		}
		return -1;
	}
}
