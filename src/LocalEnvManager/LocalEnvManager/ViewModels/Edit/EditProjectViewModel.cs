using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Caliburn.Micro;
using LocalEnvManager.Models;
using LocalEnvManager.Services;
using Microsoft.Win32;

namespace LocalEnvManager.ViewModels
{
    /// <summary>
    /// View-model for the "编辑项目" modal dialog.
    ///
    /// The dialog edits a <see cref="Draft"/> copy, not the live project. Only
    /// <see cref="Complete"/> copies the draft back into the shared model (which
    /// the catalog persists automatically). <see cref="Cancel"/> (and the close
    /// button) discard the draft, so the buttons now mean what they say.
    /// </summary>
    public sealed class EditProjectViewModel : Screen
    {
        private readonly IWindowManager _windowManager;
        private readonly ManagedProject _original;
        private readonly IReadOnlyList<ManagedProject> _otherProjects;

        private string _nameError = string.Empty;
        private DeploymentMode? _lastObservedDeploymentMode;

        public EditProjectViewModel(
            ManagedProject project,
            IReadOnlyList<ManagedProject> dependencyOptions,
            IWindowManager windowManager)
        {
            _windowManager = windowManager;
            _original = project;
            _otherProjects = dependencyOptions;
            Draft = Clone(project);
            DependencyOptions = dependencyOptions;
            DisplayName = "编辑项目";

            // Treat the persisted mode as already observed so opening the dialog
            // never rewrites an existing Command.
            _lastObservedDeploymentMode = Draft.DeploymentMode;

            Draft.PropertyChanged += OnDraftPropertyChanged;
            ValidateName();
        }

        protected override Task OnDeactivateAsync(bool close, CancellationToken cancellationToken)
        {
            if (close)
            {
                Draft.PropertyChanged -= OnDraftPropertyChanged;
            }
            return base.OnDeactivateAsync(close, cancellationToken);
        }

        /// <summary>Editable copy; every binding in the dialog targets this instance.</summary>
        public ManagedProject Draft { get; }

        /// <summary>True when the Command input should be shown for the current DeploymentMode.</summary>
        public bool UsesCommand => true;

        /// <summary>Target path is meaningful for every mode: publish/build write
        /// there, while PowerShell and uvicorn sync the overlay folder into it.</summary>
        public bool UsesTargetPath => true;

        /// <summary>Overlay folder is supported by every mode (see runner sync step).</summary>
        public bool UsesBackupFolder => true;

        /// <summary>Always show IIS AppPool so users can preconfigure it for any mode.</summary>
        public bool UsesIisAppPool => true;

        /// <summary>Other projects the user can pick as a dependency.</summary>
        public IReadOnlyList<ManagedProject> DependencyOptions { get; }

        public string NameError
        {
            get => _nameError;
            private set
            {
                if (_nameError == value) return;
                _nameError = value;
                NotifyOfPropertyChange();
                NotifyOfPropertyChange(nameof(HasNameError));
                NotifyOfPropertyChange(nameof(CanComplete));
            }
        }

        public bool HasNameError => !string.IsNullOrEmpty(NameError);

        public bool CanComplete => !HasNameError;

        private void OnDraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ManagedProject.Name))
            {
                ValidateName();
                return;
            }

            if (e.PropertyName is nameof(ManagedProject.DeploymentMode))
            {
                var current = Draft.DeploymentMode;

                // First observation (or genuinely a different mode) → auto-fill.
                // Equal to last observation (a PropertyChanged round-trip from
                // our own write, or a no-op rebind) → leave Command alone.
                if (!_lastObservedDeploymentMode.HasValue || _lastObservedDeploymentMode.Value != current)
                {
                    // Only overwrite an empty Command. Once the user has typed
                    // anything into the field we treat it as "their command" and
                    // stop auto-filling, so cycling modes for comparison doesn't
                    // destroy their work.
                    if (string.IsNullOrWhiteSpace(Draft.Command))
                    {
                        Draft.Command = CommandExampleCatalog.GetDefaultCommand(
                            current, Draft.TargetPath);
                    }
                }

                _lastObservedDeploymentMode = current;

                NotifyOfPropertyChange(nameof(UsesCommand));
                NotifyOfPropertyChange(nameof(UsesTargetPath));
                NotifyOfPropertyChange(nameof(UsesBackupFolder));
                NotifyOfPropertyChange(nameof(UsesIisAppPool));
            }
            else if (e.PropertyName is nameof(ManagedProject.TargetPath))
            {
                // TargetPath splicing only happens when Command is still the
                // auto-filled DotNetPublish default and TargetPath was just
                // filled in — without this guard, editing TargetPath after the
                // user customised Command would silently rewrite their command.
                if (Draft.DeploymentMode == DeploymentMode.DotNetPublish
                    && string.IsNullOrWhiteSpace(Draft.Command))
                {
                    Draft.Command = CommandExampleCatalog.GetDefaultCommand(
                        Draft.DeploymentMode, Draft.TargetPath);
                }
            }
        }

        private void ValidateName()
        {
            var name = Draft.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                NameError = "项目名称不能为空。";
                return;
            }

            var duplicate = _otherProjects.Any(candidate =>
                string.Equals(candidate.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase));
            NameError = duplicate ? "项目名称已存在，请使用唯一名称。" : string.Empty;
        }

        // ===== Folder browsing =====

        public void BrowseSourceFolder()
        {
            if (PickFolder(out var path)) Draft.SourcePath = path;
        }

        public void BrowseTargetFolder()
        {
            if (PickFolder(out var path)) Draft.TargetPath = path;
        }

        public void BrowseBackupFolder()
        {
            if (PickFolder(out var path)) Draft.BackupFolder = path;
        }

        public void BrowseLogsFolder()
        {
            if (PickFolder(out var path)) Draft.LogsDirectory = path;
        }

        private static bool PickFolder(out string path)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择目录"
            };
            if (dialog.ShowDialog() == true)
            {
                path = dialog.FolderName;
                return true;
            }
            path = string.Empty;
            return false;
        }

        // ===== Help buttons =====
        // All help text is sourced from ParameterHelpCatalog so the EditProject
        // dialog stays generic and the wording can be edited centrally. The
        // constants below are the stable keys the catalog exposes for each field.

        public void HelpProjectName() => ShowHelp(ParameterHelpCatalog.ProjectName);
        public void HelpDeploymentMode() => ShowHelp(ParameterHelpCatalog.DeploymentMode);
        public void HelpSourcePath() => ShowHelp(ParameterHelpCatalog.SourcePath);
        public void HelpCommand() => ShowHelp(ParameterHelpCatalog.Command);
        public void HelpTargetPath() => ShowHelp(ParameterHelpCatalog.TargetPath);
        public void HelpBackupFolder() => ShowHelp(ParameterHelpCatalog.BackupFolder);
        public void HelpIisAppPool() => ShowHelp(ParameterHelpCatalog.IisAppPool);
        public void HelpPullLatest() => ShowHelp(ParameterHelpCatalog.PullLatest);
        public void HelpDependsOn() => ShowHelp(ParameterHelpCatalog.DependsOn);
        public void HelpLogsDirectory() => ShowHelp(ParameterHelpCatalog.LogsDirectory);

        private void ShowHelp(string catalogKey)
        {
            var help = ParameterHelpCatalog.Get(catalogKey);
            // Use the ParameterHelpViewModel(ParameterHelp) ctor so the dialog
            // renders the catalog's full description plus any clickable example
            // rows (e.g. the 9 Command examples live there).
            var helpVm = new ParameterHelpViewModel(help);
            // Fire-and-forget — the help dialog is informational; we don't need
            // its return value and the parent dialog stays responsive.
            _ = _windowManager.ShowDialogAsync(helpVm);
        }

        // ===== Dialog lifecycle =====

        /// <summary>Apply the draft and close. Validation failures keep the dialog open.</summary>
        public void Complete()
        {
            ValidateName();
            if (!CanComplete) return;

            ApplyDraft();
            _ = TryCloseAsync(true);
        }

        /// <summary>Discard the draft and close.</summary>
        public void Cancel() => _ = TryCloseAsync(false);

        /// <summary>The title-bar close button behaves like Cancel.</summary>
        public void Close() => Cancel();

        public void ClearDependency() => Draft.DependsOn = string.Empty;

        private void ApplyDraft()
        {
            _original.Name = Draft.Name.Trim();
            _original.SourcePath = Draft.SourcePath;
            _original.Command = Draft.Command;
            _original.DeploymentMode = Draft.DeploymentMode;
            _original.TargetPath = Draft.TargetPath;
            _original.BackupFolder = Draft.BackupFolder;
            _original.IisAppPoolName = Draft.IisAppPoolName;
            _original.PullLatest = Draft.PullLatest;
            _original.DependsOn = Draft.DependsOn;
            _original.LogsDirectory = Draft.LogsDirectory;
        }

        private static ManagedProject Clone(ManagedProject source)
        {
            return new ManagedProject
            {
                Name = source.Name,
                SourcePath = source.SourcePath,
                Command = source.Command,
                DeploymentMode = source.DeploymentMode,
                TargetPath = source.TargetPath,
                BackupFolder = source.BackupFolder,
                IisAppPoolName = source.IisAppPoolName,
                PullLatest = source.PullLatest,
                DependsOn = source.DependsOn,
                LogsDirectory = source.LogsDirectory
            };
        }
    }
}