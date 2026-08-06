namespace LocalEnvManager.Models
{
	// A single "quick-pick" Command row shown in EditProjectDialog.
	// Each row carries a human-friendly label and the actual command string.
	// Clicking the row's Copy button writes Command into the project's Command
	// field AND copies it to the clipboard.
	public sealed class CommandExample
	{
		public CommandExample(string label, string command)
		{
			Label = label;
			Command = command;
		}

		public string Label { get; }
		public string Command { get; }
	}
}
