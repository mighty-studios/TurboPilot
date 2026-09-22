namespace TurboPilot;

/// <summary>
/// The prompts sent during this run of the application, cycled through
/// newest to oldest the way a shell cycles its history. A cursor of -1
/// means the user is on the draft: whatever is in the prompt box that has
/// not been sent yet. Browsing away from the draft saves it so stepping
/// back off the end restores it untouched.
/// </summary>
internal sealed class PromptHistory
{
	private readonly List<string> _items = [];

	/// <summary>Index of the entry on display, or -1 while on the draft.</summary>
	private int _cursor = -1;

	/// <summary>The unsent text set aside when browsing began.</summary>
	private string _draft = string.Empty;

	/// <summary>True when an older entry is reachable.</summary>
	public bool CanGoBack => _items.Count > 0 && (_cursor == -1 || _cursor > 0);

	/// <summary>True when browsing, so a newer entry or the draft is reachable.</summary>
	public bool CanGoForward => _cursor != -1;

	/// <summary>
	/// Records a sent prompt and drops back to the draft position. The
	/// draft is discarded with it: the text that was being typed has
	/// either been sent or replaced.
	/// </summary>
	public void Add(string prompt)
	{
		_items.Add(prompt);
		_cursor = -1;
		_draft = string.Empty;
	}

	/// <summary>
	/// Steps to the older entry, setting <paramref name="currentDraft"/>
	/// aside the first time browsing begins. Returns the text to show.
	/// </summary>
	public string NavigateBack(string currentDraft)
	{
		if (_items.Count == 0)
			return currentDraft;

		if (_cursor == -1)
		{
			_draft = currentDraft;
			_cursor = _items.Count - 1;
		}
		else if (_cursor > 0)
		{
			_cursor--;
		}

		return _items[_cursor];
	}

	/// <summary>
	/// Steps to the newer entry, restoring the draft once the most recent
	/// prompt has been passed. Returns the text to show.
	/// </summary>
	public string NavigateForward()
	{
		if (_cursor == -1)
			return _draft;

		_cursor++;
		if (_cursor >= _items.Count)
		{
			_cursor = -1;
			return _draft;
		}

		return _items[_cursor];
	}
}
