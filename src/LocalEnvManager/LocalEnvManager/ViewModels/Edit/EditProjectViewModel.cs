using System.Collections.Generic;
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
        }

        /// <summary>The project record being edited; the dialog binds straight to this instance.</summary>
        public ManagedProject Project { get; }

        /// <summary>Computed: the Command field is meaningful for non-uvicorn modes.</summary>
        public bool UsesCommand => Project.DeploymentMode != DeploymentMode.Uvicorn;

        /// <summary>Other projects the user can pick as a dependency.</summary>
        public IReadOnlyList<ManagedProject> DependencyOptions { get; }

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

        public void HelpProjectName() => ShowHelp("Project Name", "项目的显示名，用于标识 / 持久化 / 日志记录。");
        public void HelpDeploymentMode() => ShowHelp("Deployment Mode",
            "四种部署方式：\n• dotnet publish：发布 ASP.NET Core 应用\n• dotnet build：仅编译\n• PowerShell：执行任意 PowerShell 命令\n• uvicorn：启动 Python ASGI 服务（常驻）");
        public void HelpSourcePath() => ShowHelp("Source Path", "源目录 / 工作目录。git pull 会在这里执行。");
        public void HelpCommand() => ShowHelp("Command", "部署命令。切换 Deployment Mode 会自动填入示例；可点 ? 查看更多示例并复制。");
        public void HelpTargetPath() => ShowHelp("Target Path", "部署目录 / 运行目录。");
        public void HelpBackupFolder() => ShowHelp("Backup Folder", "构建后拷贝到 Target Path 的覆盖目录。");
        public void HelpIisAppPool() => ShowHelp("IIS AppPool", "可选 IIS 应用池名称；publish 前会先回收该 AppPool。");
        public void HelpPullLatest() => ShowHelp("更新前执行 git pull", "启用后每次更新前会先在源目录执行 git pull；失败时跳过该项目。");
        public void HelpDependsOn() => ShowHelp("依赖项目", "逗号分隔的项目名；勾选依赖项目时会先完成依赖再开始当前项目。");
        public void HelpLogsDirectory() => ShowHelp("Logs Directory", "留空时按 项目名/源路径/目标路径 自动发现 logs 目录。");

        private void ShowHelp(string title, string content)
        {
            var helpVm = new ParameterHelpViewModel(title, content);
            // Fire-and-forget — the help dialog is informational; we don't need
            // its return value and the parent dialog stays responsive.
            _ = _windowManager.ShowDialogAsync(helpVm);
        }

        // ===== Dialog lifecycle =====

        public void Close() => TryCloseAsync();
        public void ClearDependency() => Project.DependsOn = string.Empty;
    }
}