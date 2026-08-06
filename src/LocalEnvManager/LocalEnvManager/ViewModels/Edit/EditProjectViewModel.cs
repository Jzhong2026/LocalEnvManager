using System.Collections.Generic;
using System.ComponentModel;
using Caliburn.Micro;
using LocalEnvManager.Models;
using LocalEnvManager.Services;
using Microsoft.Win32;

namespace LocalEnvManager.ViewModels
{
    /// <summary>
    /// View-model for the "Edit Project" modal dialog. Wraps a single
    /// <see cref="ManagedProject"/> instance: every binding in the dialog writes
    /// directly to that instance via two-way binding, so the catalog's
    /// PropertyChanged listener will persist the changes automatically.
    ///
    /// The view-model owns the dialog's lifecycle (it implements
    /// <see cref="Screen"/>) and exposes folder-browse / show-help commands that
    /// would otherwise force code-behind. Launched via
    /// <c>IWindowManager.ShowDialogAsync(vm)</c>.
    /// </summary>
    public sealed class EditProjectViewModel : Screen
    {
        private readonly IWindowManager _windowManager;

        public EditProjectViewModel(
            ManagedProject project,
            IReadOnlyList<ManagedProject> dependencyOptions,
            IWindowManager windowManager)
        {
            _windowManager = windowManager;
            Project = project;
            DependencyOptions = dependencyOptions;
            DisplayName = "Edit Project";

            // Re-publish visibility flags whenever DeploymentMode changes so the
            // dialog sections (Command / Target / Backup) can bind
            // directly to these booleans without any code-behind.
            Project.PropertyChanged += OnProjectPropertyChanged;
        }

        protected override System.Threading.Tasks.Task OnDeactivateAsync(bool close, System.Threading.CancellationToken cancellationToken)
        {
            if (close)
            {
                Project.PropertyChanged -= OnProjectPropertyChanged;
            }
            return base.OnDeactivateAsync(close, cancellationToken);
        }


        /// <summary>The project record being edited; the dialog binds straight to this instance.</summary>
        public ManagedProject Project { get; }

        /// <summary>True when the Command input should be shown for the current DeploymentMode.</summary>
        public bool UsesCommand => true;

        /// <summary>True for DeploymentModes that deploy files to a Target Path.</summary>
        public bool UsesTargetPath => Project.DeploymentMode
            is DeploymentMode.DotNetPublish
            or DeploymentMode.DotNetBuild;

        /// <summary>True for DeploymentModes that copy a backup folder into the target.</summary>
        public bool UsesBackupFolder => Project.DeploymentMode
            is DeploymentMode.DotNetPublish
            or DeploymentMode.DotNetBuild;

        /// <summary>Always show IIS AppPool so users can preconfigure it for any mode.</summary>
        public bool UsesIisAppPool => true;

        /// <summary>Other projects the user can pick as a dependency.</summary>
        public IReadOnlyList<ManagedProject> DependencyOptions { get; }

        // Tracks the last DeploymentMode we observed so we only auto-fill Command
        // on a *real* mode switch (the user's edit of an unrelated field, or the
        // initial construction of the VM with the existing persisted mode, must
        // not stomp on a Command the user already typed).
        private DeploymentMode? _lastObservedDeploymentMode;

        private void OnProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ManagedProject.DeploymentMode))
            {
                var current = Project.DeploymentMode;

                // First observation (or genuinely a different mode) → auto-fill.
                // Equal to last observation (a PropertyChanged round-trip from
                // our own write, or a no-op rebind) → leave Command alone.
                if (!_lastObservedDeploymentMode.HasValue || _lastObservedDeploymentMode.Value != current)
                {
                    // Only overwrite an empty Command. Once the user has typed
                    // anything into the field we treat it as "their command" and
                    // stop auto-filling, so cycling modes for comparison doesn't
                    // destroy their work.
                    if (string.IsNullOrWhiteSpace(Project.Command))
                    {
                        Project.Command = CommandExampleCatalog.GetDefaultCommand(
                            current, Project.TargetPath);
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
                if (Project.DeploymentMode == DeploymentMode.DotNetPublish
                    && string.IsNullOrWhiteSpace(Project.Command))
                {
                    Project.Command = CommandExampleCatalog.GetDefaultCommand(
                        Project.DeploymentMode, Project.TargetPath);
                }
            }
        }

        // ===== Folder browsing =====

        public void BrowseSourceFolder()
        {
            if (PickFolder(out var path)) Project.SourcePath = path;
        }

        public void BrowseTargetFolder()
        {
            if (PickFolder(out var path)) Project.TargetPath = path;
        }

        public void BrowseBackupFolder()
        {
            if (PickFolder(out var path)) Project.BackupFolder = path;
        }

        public void BrowseLogsFolder()
        {
            if (PickFolder(out var path)) Project.LogsDirectory = path;
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

        public void Close() => TryCloseAsync();
        public void ClearDependency() => Project.DependsOn = string.Empty;
    }
}