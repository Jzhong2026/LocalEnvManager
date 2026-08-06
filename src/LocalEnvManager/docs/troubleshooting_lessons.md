# 排错记录与经验沉淀

> 收纳开发过程中遇到的真实 bug、根因、以及最终落地的修复方案。**任何人在动手修复 bug 之前,先在整个文档里翻一遍** —— 八成你已经踩过的坑别人已经填了。

---

## 索引

| # | 症状 | 类别 |
|---|---|---|
| 1 | 弹窗说明内容空白 | Caliburn.Micro / 服务调用 |
| 2 | 切换 DeploymentMode 时 Command 不自动填充 | 视图模型 |
| 3 | `BackupFolder` 命名误导(实际是"覆盖目录") | 待跟进 |
| 4 | 执行列表 6 行项目,内容全空白 + Stop 按钮全显 | Caliburn.Micro `Action.Target` vs `TargetWithoutContext` |
| 5 | uvicorn 启动成功后 Stop 按钮立刻消失 | 状态模型耦合 |
| 6 | 点击 Stop 按钮 → WPF 进程崩溃 `$parent.$parent.StopProject` | Caliburn.Micro 路径语法 |
| 7 | Click Stop 后 UI 永久卡死 | .NET `Process.Dispose()` 同步读流 |
| 8 | Click Stop 按钮没反应、也没日志 | action target 解析失败 |
| 9 | Execute 列表行高亮错位 | ManagedProject.IsSelected 镜像 |
| 10 | Reset 列表后行高亮停在旧项目 | 同上 |
| 11 | dotnet build 失败:PowerShell 进程持锁 DLL | 工具链 |
| 12 | Release publish 失败:DLL 被当前 PowerShell 锁定 | 工具链 |
| 13 | Caliburn.Micro `async Task` action 异常被吞 | Caliburn.Micro 反射调用 |

---

## 1. 弹窗说明内容空白

**症状**:点击编辑对话框里的 `?` 帮助按钮,弹窗**有标题无正文** —— 内容只剩标题行,Description / 示例都消失。

**根因**:`EditProjectViewModel.HelpXxx` 全部走写死的两参数重载:

````csharp
public void HelpSourcePath() => ShowHelp("Source Path", "源目录 / 工作目录。git pull 会在这里执行。");
private void ShowHelp(string title, string content)
{
    var helpVm = new ParameterHelpViewModel(title, content);
    _ = _windowManager.ShowDialogAsync(helpVm);
}
````

但 `ParameterHelpCatalog` 早就写好了 10 个字段的完整中文说明 + Command 的 9 条可点击复制示例,**0 处调用**。`ParameterHelpCatalog` 是死代码。

**修复**:所有 `HelpXxx` 改成查 catalog:

````csharp
public void HelpSourcePath() => ShowHelp(ParameterHelpCatalog.SourcePath);
// ... 其余 9 个类似

private void ShowHelp(string catalogKey)
{
    var help = ParameterHelpCatalog.Get(catalogKey);
    var helpVm = new ParameterHelpViewModel(help);
    _ = _windowManager.ShowDialogAsync(helpVm);
}
````

走 `ParameterHelpViewModel(ParameterHelp)` 构造重载,这样 `Command` 弹窗里的 9 条可点击复制示例能正确显示。

**影响文件**:
- `ViewModels/Edit/EditProjectViewModel.cs`

---

## 2. 切换 DeploymentMode 时 Command 不自动填充

**症状**:在编辑项目对话框里切换部署模式(Dotnet publish / dotnet build / PowerShell / uvicorn),Command 输入框**保持不变**,需要用户手动改。

**根因**:`CommandExampleCatalog.GetDefaultCommand(mode, targetPath)` 早就写好了 4 种模式的默认值。但 `EditProjectViewModel.OnProjectPropertyChanged` 监听 `DeploymentMode` 时**只刷新可见性标志**,从来没写 `Project.Command`。

**修复**:
1. 在 `RunProjectPipelineAsync` 启动时对 uvicorn/PowerShell 设置 `project.HasRunningProcess = true`。
2. 加 `_lastObservedDeploymentMode` 字段防自循环(自己的写入触发的 PropertyChanged 不要再次覆盖)。

````csharp
private void OnProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
{
    if (e.PropertyName is nameof(ManagedProject.DeploymentMode))
    {
        var current = Project.DeploymentMode;
        if (!_lastObservedDeploymentMode.HasValue || _lastObservedDeploymentMode.Value != current)
        {
            // 只有 Command 为空才自动填,防止冲掉用户已输入的命令
            if (string.IsNullOrWhiteSpace(Project.Command))
            {
                Project.Command = CommandExampleCatalog.GetDefaultCommand(current, Project.TargetPath);
            }
        }
        _lastObservedDeploymentMode = current;
        // ...刷新 UsesCommand/UsesTargetPath/UsesBackupFolder/UsesIisAppPool
    }
    else if (e.PropertyName is nameof(ManagedProject.TargetPath))
    {
        // DotnetPublish 模式下,TargetPath 填好之后把 -o "<TargetPath>" 拼进去
        if (Project.DeploymentMode == DeploymentMode.DotNetPublish
            && string.IsNullOrWhiteSpace(Project.Command))
        {
            Project.Command = CommandExampleCatalog.GetDefaultCommand(Project.DeploymentMode, Project.TargetPath);
        }
    }
}
````

**保护机制**:已经输入过 Command → 不覆盖;来回切模式 → 不冲掉用户编辑。

**影响文件**:
- `ViewModels/Edit/EditProjectViewModel.cs`

参考:`CommandExampleCatalog.GetDefaultCommand(DeploymentMode, string targetPath)` 默认值表:

| DeploymentMode | 默认 Command |
|---|---|
| dotnet publish | `publish -c Release -p:Platform="any cpu"` (TargetPath 非空时追加 `-o "<TargetPath>"`) |
| dotnet build | `build -c Debug` |
| PowerShell | `.\build_release.ps1` |
| uvicorn | `.venv\Scripts\python -m uvicorn hosting.main:app --host 0.0.0.0 --port 8000` |

---

## 3. `BackupFolder` 命名误导(实际是"覆盖目录")

**症状**:字段叫 `BackupFolder` / UI 标签 "备份目录" / "Backup Folder",但运行时**它的实际行为是**:部署成功后把这个文件夹的内容**复制到 `TargetPath`**(覆盖同名文件),用于本地特殊配置覆盖。

**根因**:在 `plan_deployment.md` 初始命名时从 `OverlayFolderPath` 重命名为 `BackupFolder`("备份目录"),但语义从来没改。

**当前状态**:未修复。`ParameterHelpCatalog[BackupFolder]` 的中文说明已经反映出真实用途(覆盖),但字段名 / UI 标签仍是别名。

**建议**(待用户确认后再做):
- 重命名 property:`BackupFolder` → `OverlayFolder`
- UI 标签:"备份目录" → "覆盖目录"
- 运行时日志:`已同步 BackupFolder` → `已同步 OverlayFolder`
- 升级路径:`JsonProjectStore.Load` 检测到旧 `BackupFolder` 且新字段为空时迁移。

**影响文件**(预计):
- `Models/ManagedProject.cs`
- `Services/ProjectStore.cs`、`Services/IProjectCatalog.cs`
- `Services/ProjectUpdateRunner.cs`
- `Services/ParameterHelpCatalog.cs`
- `ViewModels/Edit/EditProjectViewModel.cs`、`Views/Edit/EditProjectView.xaml`
- `Views/Manage/ManageView.xaml`

---

## 4. 执行列表 6 行项目,内容全空白 + Stop 按钮全显

**症状**:执行更新标签下,`UpdateSummary` 显示「共 6 项」(数据正确加载),但每行项目:
- 「项目」列空白(无 Name / SourcePath)
- 「模式」列只有图标,没文字
- 「分支」列只有图标,没文字
- 「状态」列空白
- 「停止」按钮 6 行**都显示**(应该只 Running 时显示)

**根因**:我在行 Border 上用了:

````xml
<Border ... cal:Action.Target="{Binding DataContext, ElementName=Root}" ...>
````

`cal:Action.Target` 会把行的 `DataContext` **改写**成 `ExecuteViewModel`(绑定的值)。之后所有行内的 `Binding Name` / `Binding SourcePath` / `Binding IsStopEnabled` 都会去 `ExecuteViewModel` 上找对应属性 —— 全部解析失败,显示空。

**Stop 按钮全显**:`IsStopEnabled` 解析失败得到 `false` → `Visibility = Collapsed` —— **但 BCL 内置 `BooleanToVisibilityConverter` 对 `null` Binding 视为 `Visible`**,所以按钮都显示出来。

**修复**:改用 `cal:Action.TargetWithoutContext`,只指定 action 解析目标,不修改行 `DataContext`:

````xml
<Border ... cal:Action.TargetWithoutContext="{Binding DataContext, ElementName=Root}" ...>
    <Button ... cal:Action.TargetWithoutContext="{Binding DataContext, ElementName=Root}"
            cal:Message.Attach="[Event Click] = [Action StopProject($dataContext)]" ... />
</Border>
````

**关键经验**:
- **`cal:Action.Target`**:把 `DataContext` 改成绑定值。一般**只在 `Action.Target` 显式想要一个独立对象时**用。
- **`cal:Action.TargetWithoutContext`**:只指定 action target,DataContext 保持不变。**嵌套 DataTemplate 里几乎总要这个**。
- 决定性测试:发现行内 binding 解析失败 → 第一反应检查 `Action.Target` vs `TargetWithoutContext`。

**影响文件**:
- `Views/Execute/ExecuteView.xaml`

---

## 5. uvicorn 启动成功后 Stop 按钮立刻消失

**症状**:uvicorn 启动成功,状态变 "成功",但 Stop 按钮马上消失,用户无法终止后台进程。

**根因**:我之前在 `LastRunState` setter 里写了:

````csharp
if (value != ExecutionState.Running && _hasRunningProcess)
{
    _hasRunningProcess = false;
    NotifyOfPropertyChange(nameof(HasRunningProcess));
}
````

uvicorn 启动成功 → `LastRunState = Success` → setter 把 `HasRunningProcess` 清零 → `IsStopEnabled` 变 false → Stop 按钮消失。但 launcher 进程其实还在跑,绑定 8000 端口。

**问题本质**:`LastRunState`(完成态)和 `HasRunningProcess`(进程存活态)是**两个独立维度**。LastRunState 翻转时不该清 HasRunningProcess。

**修复**:
1. `LastRunState` setter **只通知**,不碰 `HasRunningProcess`。
2. `RunProjectPipelineAsync` 在拿到 `result` 后明确赋值:

````csharp
project.HasRunningProcess =
    project.DeploymentMode == DeploymentMode.Uvicorn && result.Success;
````

- uvicorn + Success → `HasRunningProcess = true`(service 在跑)
- PowerShell + 任何结果 → `HasRunningProcess = false`(脚本已退出)
- dotnet publish / build + 任何结果 → `HasRunningProcess = false`(命令进程已退出)

进程自然退出事件由 runner/Stop 路径负责清零。

**影响文件**:
- `Models/ManagedProject.cs`(`LastRunState` setter 删掉清零逻辑,新增 `HasRunningProcess` 派生字段)
- `ViewModels/Execute/ExecuteViewModel.cs`(`RunProjectPipelineAsync` 显式赋值)

---

## 6. 点击 Stop 按钮 → WPF 进程崩溃 `$parent.$parent.StopProject`

**症状**:点 Stop → WPF 整个进程崩溃,Windows 事件日志报:

```
System.Exception: No target found for method $parent.$parent.StopProject.
   at Caliburn.Micro.ActionMessage.Invoke(Object eventArgs)
   ...
   at System.Windows.Controls.Primitives.ButtonBase.OnClick()
```

**根因**:我误用了 `$parent.$parent.StopProject($dataContext)` 当 action 名字。Caliburn.Micro 把整串字符当作方法名去找,找不到 → 抛 `ActionNotFoundException` → 整个 WPF 进程崩溃。

`$parent.X` 是**参数路径**(`$dataContext` 才有 `$parent` 修饰),不是 action 修饰。

**修复**:直接用 `ElementName` 引用外层 UserControl:

````xml
<UserControl x:Class="..." x:Name="Root" ...>
    ...
    <Button cal:Action.TargetWithoutContext="{Binding DataContext, ElementName=Root}"
            cal:Message.Attach="[Event Click] = [Action StopProject($dataContext)]" />
</UserControl>
````

action 名恢复纯方法名 `[Action StopProject($dataContext)]`。

**影响文件**:
- `Views/Execute/ExecuteView.xaml`

---

## 7. Click Stop 后 UI 永久卡死

**症状**:点击 Stop → UI 永久无响应,即使等 30 秒也不恢复。

**根因**:Stop 成功后我写了:

````csharp
if (s_uvicornProcesses.TryRemove(key, out _))
{
    try { process.Dispose(); } catch { }
}
````

uvicorn 的 launcher 用了 `RedirectStandardOutput = true`。**.NET `Process.Dispose()` 在这种配置下会同步等待 stdout 读取泵退出**。uvicorn worker 一直在喷日志(stdout 永不结束)→ `Dispose()` 永久挂起 → UI 死锁。

**修复**:
1. **永远不要再对 `RedirectStandardOutput = true` 的 `Process` 调用 `Dispose()`**。
2. 把 kill 流程整个挪到 worker 线程,加 5 秒硬超时:

````csharp
private const int StopTimeoutMs = 5000;

var task = Task.Run(() => KillUvicornProcessTree(process, projectName, logProgress));
if (!task.Wait(StopTimeoutMs))
{
    Report(logProgress, $"[{projectName}] Stop 等待超过 {StopTimeoutMs / 1000}s,放弃等待。");
}
// 严禁 process.Dispose() — 走 GC + 进程自然退出
````

**影响文件**:
- `Services/ProjectUpdateRunner.cs`

---

## 8. Click Stop 按钮没反应、也没日志

**症状**:点 Stop → 弹窗无变化、按钮无变化、runtime log 里 `[Stop]` 关键字搜不到。

**根因**:Caliburn.Micro 反射调用 `ExecuteViewModel.StopProject` 但**找不到方法**。最常见的原因是 action target 解析失败:
- `cal:Action.TargetWithoutContext` 没绑 / 绑错
- `cal:Message.Attach` 里 action 名带 `$parent` 修饰

**诊断步骤**:
1. 打开 `%LOCALAPPDATA%\LocalEnvManager\logs\runtime-yyyyMMdd.log`,搜 `Stop` 关键字 —— 看有没有 `[Stop] 点击按钮: ...` 这行。
2. **有**:`StopProject` 被调到了,问题在内部(StopAsync 找不到进程 / Kill 失败 / 早返回)。
3. **没有**:Caliburn.Micro 没解析到 `StopProject` → 检查 `Action.Target*` 配置、action 名。
4. 打开事件查看器 → Windows 日志 → Application → 找 `LocalEnvManager.exe` 的 Error 1000/1026,看 .NET 异常堆栈。

**修复**:见 #4 (用 `TargetWithoutContext` + `ElementName=Root`) + #6 (用纯方法名)。

**影响文件**:
- `Views/Execute/ExecuteView.xaml`

---

## 9. Execute 列表行高亮错位

**症状**:点击 PyAgents 行 → 高亮的是 plus-core。

**根因**:行高亮由 [`ExecuteProjectRowStyle`](Themes/Controls.xaml) 的 `DataTrigger Binding="{Binding IsSelected}"` 驱动。但 `ExecuteViewModel.SelectedProject` setter **不刷 `ManagedProject.IsSelected`** —— 全代码库里只有 `ManageViewModel.SelectedProject` setter 写这个属性。

**修复**:在 `ExecuteViewModel.SelectedProject` setter 里镜像 `IsSelected`:

````csharp
public ManagedProject? SelectedProject
{
    get => _selectedProject;
    set
    {
        if (Set(ref _selectedProject, value))
        {
            foreach (var project in _catalog.Projects)
            {
                var isSelected = project == value;
                if (project.IsSelected != isSelected)
                {
                    project.IsSelected = isSelected;
                }
            }
            // ... publish ProjectSelectedDetailMessage, etc.
        }
    }
}
````

**影响文件**:
- `ViewModels/Execute/ExecuteViewModel.cs`

---

## 10. Reset 列表后行高亮停在旧项目

**症状**:同 #9 —— 是同一个 bug 的另一个症状。从 Manage 标签选某个项目 → 切到 Execute 标签 → 之前选中的项目行高亮**还在**。

**修复**:同 #9,镜像 `IsSelected` 即可。

---

## 11. dotnet build 失败:PowerShell 进程持锁 DLL

**症状**:

```
error MSB3027: Could not copy "obj\Debug/...dll" to "bin\Debug/...dll".
The process cannot access the file '...dll' because it is being used by another process.
The file is locked by: "Windows PowerShell (52876)"
```

**根因**:PowerShell 5.1 宿主在执行 `dotnet build` 时,MSBuild 临时加载 DLL 进当前 PowerShell 进程。**当前终端就是锁定者** —— `Stop-Process` 会自杀。

**解决**:
- 换 Release publish(产物不写 `bin\Debug`):
  ```powershell
  dotnet publish src\LocalEnvManager\LocalEnvManager\LocalEnvManager.csproj -c Release -o publish
  ```
- 或者在新 PowerShell 窗口重新 build(不在同一进程)。

**影响文件**:无(工具链问题)。

---

## 12. Release publish 失败:DLL 被当前 PowerShell 锁定

**症状**:`dotnet publish -c Release -o bin\Release\...` 仍报 `apphost.exe` 被锁。

**根因**:MSBuild 的 apphost 复制步骤强制写回 `bin\Release\win-x64\LocalEnvManager.exe`,跟 `-o` 路径无关。

**解决**:
- 先 publish 到 temp 目录:
  ```powershell
  dotnet publish ... -o publish-tmp
  ```
- 用户关掉当前测试 EXE → `Copy-Item publish-tmp\*` 覆盖到 `bin\Release\...`。
- 或者让用户关掉 app 后再 publish 到原路径。

**影响文件**:无(工具链)。

---

## 13. Caliburn.Micro `async Task` action 异常被吞

**症状**:`cal:Message.Attach="[Action MyAsyncMethod($dataContext)]"`,方法签名 `async Task MyAsyncMethod(...)`。Caliburn.Micro 反射同步 invoke,拿到 `Task` 不 await,异常被吞。

**修复**:包成同步 `void` 包装,fire-and-forget 内部 `async` 任务:

````csharp
public void StopProject(ManagedProject project)
{
    if (project is null) return;
    _logger.Append(LogEntry.LogLevel.Info, $"[Stop] click: {project.Name}");
    _ = StopProjectCoreAsync(project);
}

private async Task StopProjectCoreAsync(ManagedProject project)
{
    try
    {
        // ... 实际逻辑
    }
    catch (Exception ex)
    {
        _logger.Append(LogEntry.LogLevel.Error, $"Stop 失败: {ex.Message}");
    }
}
````

异步体异常必须自己 try/catch + 写日志。

**影响文件**:
- `ViewModels/Execute/ExecuteViewModel.cs`(StopProject 包成同步 void)

---

## 附录:调试工具箱

| 工具 | 用途 |
|---|---|
| `%LOCALAPPDATA%\LocalEnvManager\logs\runtime-yyyyMMdd.log` | 应用日志(`IRuntimeLogger.Append` 写这里) |
| `%LOCALAPPDATA%\LocalEnvManager\projects.json` | 项目配置(原子写入,temp + `File.Replace`) |
| `%LOCALAPPDATA%\LocalEnvManager\logs\` | uvicorn / IIS 站点日志 |
| Windows 事件查看器 → Application → 1000 / 1026 | WPF 进程崩溃堆栈 |
| `dotnet publish -c Release -o publish-tmp` | 不锁 DLL 的临时 build |
| `(Get-Process LocalEnvManager).StartTime` | 确认 app 是不是最新 build |

## 附录:Caliburn.Micro 嵌套 DataTemplate 速查

| 想做的事 | 用哪条 |
|---|---|
| 行内 binding 解析到行项目,点击触发 ViewModel action | `cal:Action.TargetWithoutContext="{Binding DataContext, ElementName=Root}"` + `cal:Message.Attach="[Action Method($dataContext)]"` |
| 把行内容**改成** ViewModel(整行变成 VM) | `cal:Action.Target="{Binding ...}"` —— 慎用,行内容会找不到 |
| 加 `$dataContext` 参数 | ✅ `[Action Method($dataContext)]` |
| 加 `$parent` 修饰 | ❌ 这是参数路径修饰,不能用在 action 名上 |

## 附录:状态机纪律

| 状态字段 | 含义 | 谁可以写 |
|---|---|---|
| `LastRunState` | 上次运行的最终结果(Idle / Running / Success / Failed) | pipeline 主流程 |
| `HasRunningProcess` | 当前是否真的有进程在跑(uvicorn service 常驻) | runner 启动 + Stop 路径 |
| `IsSelected` | UI 行高亮 | `SelectedProject` setter 镜像 |
| `IsChecked` | "Run selected" 是否包含 | 用户勾选 |

**核心原则**:状态字段之间不要互相影响。每个字段由"主流程"独占写入,setter 只通知相关依赖属性。

## 附录:工具链最佳实践

| 场景 | 命令 |
|---|---|
| 用户开着 app,我要改代码 | `dotnet publish -c Release -o publish-tmp` → 让用户关 EXE → `Copy-Item` 覆盖 |
| 用户关了 app,直接改 | `dotnet publish -c Release -o bin\Release\...` |
| Debug 编译被当前 PowerShell 锁 | 换 Release publish 到 temp |
| 修改 WPF XAML 后 | 重新 publish + 关 app + 启动 |
