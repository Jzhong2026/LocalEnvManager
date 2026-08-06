using System;
using System.Collections.Generic;
using System.Windows;
using Caliburn.Micro;
using LocalEnvManager.Models;

namespace LocalEnvManager.ViewModels
{
    /// <summary>
    /// View-model for the parameter help dialog. Either renders a single plain
    /// example string (when <see cref="Examples"/> is null) or a clickable list
    /// of copy-to-clipboard rows. Built from either a <see cref="ParameterHelp"/>
    /// (when launched from the help catalog) or directly from a (title, content)
    /// pair (when launched ad-hoc from the EditProject dialog).
    /// </summary>
    public sealed class ParameterHelpViewModel : Screen
    {
        private readonly string? _plainExample;

        public ParameterHelpViewModel(string title, string description, string? plainExample = null)
        {
            Title = title;
            Description = description;
            _plainExample = plainExample;
            DisplayName = title;
        }

        public ParameterHelpViewModel(ParameterHelp help)
            : this(help.Title, help.Description, help.Examples is { Count: > 0 } ? null : help.Example)
        {
            Examples = help.Examples;
        }

        public string Title { get; }
        public string Description { get; }

        public IReadOnlyList<HelpExample>? Examples { get; }

        public bool HasExamples => Examples is { Count: > 0 };

        public string PlainExample => _plainExample ?? string.Empty;

        public void Close() => TryCloseAsync();

        public void CopyExample(HelpExample example)
        {
            if (example is null) return;
            try
            {
                Clipboard.SetText(example.Value);
            }
            catch
            {
                // Clipboard can be locked by RDP / Citrix sessions; ignore so the
                // dialog stays usable and the user can copy manually.
            }
        }
    }
}