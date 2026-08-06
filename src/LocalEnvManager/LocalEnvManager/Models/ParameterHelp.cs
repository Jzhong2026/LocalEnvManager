using System.Collections.Generic;

namespace LocalEnvManager.Models
{
	// Help content shown in the parameter help popup. Each field in EditProjectDialog
	// has an associated ParameterHelp that explains what the field means and gives
	// a concrete usage example. Centralised in ParameterHelpCatalog so the dialog
	// itself stays generic and the text is easy to edit / extend.
	public sealed class ParameterHelp
	{
		public ParameterHelp(string title, string description, string example)
			: this(title, description, example, null)
		{
		}

		public ParameterHelp(
			string title,
			string description,
			string example,
			IReadOnlyList<HelpExample>? examples)
		{
			Title = title;
			Description = description;
			Example = example;
			Examples = examples;
		}

		// Short label shown at the top of the popup (e.g. "Source Path").
		public string Title { get; }

		// Plain-language explanation of what the field controls.
		public string Description { get; }

		// One or more concrete examples. Use \n to separate multiple lines.
		// Rendered as a monospace block. Optional — leave blank when the
		// per-row Examples list is provided instead.
		public string Example { get; }

		// Optional clickable example rows. Each row shows the example text and
		// a Copy button that puts the Value string on the clipboard. When this
		// list is present the Example text is hidden (the rows already show it).
		public IReadOnlyList<HelpExample>? Examples { get; }
	}

	// One row inside ParameterHelp.Examples. Label is shown above Value; Value is
	// what gets copied to the clipboard.
	public sealed class HelpExample
	{
		public HelpExample(string label, string value)
		{
			Label = label;
			Value = value;
		}

		public string Label { get; }
		public string Value { get; }
	}
}
