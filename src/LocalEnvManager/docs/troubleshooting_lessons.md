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
| 14 | 顶部 Tab 选中无高亮 | Caliburn.Micro `IPageViewModel.IsActive` 未通知 |
| 15 | Tab 选中高亮太弱、用户看不出区别 | 样式 / 视觉对比度 |
| 16 | Log tab 源码从不在 git 里 — fresh clone build 挂 | `.gitignore` 过宽 |

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

## 14. 顶部 Tab 选中无高亮

**症状**:顶部三个 tab(项目管理 / 执行更新 / 运行日志)点哪个都切换 ContentControl 内容,但**点中的按钮没有任何视觉反馈** —— 背景不变、字色不变、字重不变。看着像是「点了没生效」(其实页已经切了)。

**根因**:`PageTabItemStyle`(Themes/Controls.xaml)靠 `DataTrigger` 监听 `Tag.IsActive` 来加高亮:

````xml
<DataTrigger Binding="{Binding Tag.IsActive, RelativeSource={RelativeSource TemplatedParent}}" Value="True">
    <Setter TargetName="TabBorder" Property="Background" Value="{StaticResource PrimaryLightBrush}" />
    <Setter Property="Foreground" Value="{StaticResource PrimaryDarkBrush}" />
    <Setter Property="FontWeight" Value="SemiBold" />
</DataTrigger>
````

`ShellView.xaml` 把每个按钮的 `Tag` 绑到对应的 `IPageViewModel`:

````xml
<Button ... Tag="{Binding Host.Pages[0]}" Content="{Binding Host.Pages[0].DisplayName}" />
````

切换 tab 时,`ContentHostViewModel.ActivePage` setter 翻每个 page 的 `IsActive`:

````csharp
foreach (var page in Pages)
{
    var active = page == value;
    if (page.IsActive != active)
    {
        page.IsActive = active;
    }
}
````

但三个 page VM 的 `IsActive` **全是裸 auto-property,没有任何通知**:

````csharp
public new bool IsActive { get; set; }   // ← 写了不通知
````

`new` 是必须的(要 hide `Screen.IsActive`),但需要 `Set(ref ..., value)` 才会触发 `PropertyChanged`。结果:
- `IsActive` 字段值确实翻了;
- `DataTrigger` 监听 `Tag.IsActive` → 永远听不到变化 → 永远不高亮。

**修复**:三个 page VM 都改成标准 notifying property:

````csharp
private bool _isActive;

public new bool IsActive
{
    get => _isActive;
    set => Set(ref _isActive, value);
}
````

`Set(ref ...)` 来自 `Screen` 基类,内部会比较新旧值,变化时自动 `NotifyOfPropertyChange(nameof(IsActive))`,触发所有 `Binding` / `DataTrigger` 重新求值。

**关键经验**:
- **裸 `new` auto-property 不会通知**。`Screen` / `PropertyChangedBase` 基类只对 `Set(ref ...)` 调用发事件。
- 任何 UI 上需要响应的属性(哪怕只是布尔翻转),必须走 `Set(ref ..., value)` 或显式 `NotifyOfPropertyChange`。
- 决定性测试:UI 状态不更新 → 用 Snoop/WPF Inspector 看 binding 值有没有变化 → 没变化就是 VM 不通知,有变化就是 binding 路径错。

**影响文件**:
- `ViewModels/Manage/ManageViewModel.cs`(新增 `_isActive` 字段 + `Set` setter)
- `ViewModels/Execute/ExecuteViewModel.cs`(同上)
- `ViewModels/RunLogs/RunLogsViewModel.cs`(同上)

---

## 15. Tab 选中高亮太弱、用户看不出区别

**症状**:修了 #14 之后,Tag.IsActive 通知通了,DataTrigger 也能触发了,但**用户反馈** 「点哪个都看不出区别」—— 实际是**有**高亮(浅蓝背景 + 深蓝字 + SemiBold),不过对比度太弱,看着像没生效。

**根因**:原来的视觉反馈就三件套:
- 背景:`#DBEAFE`(PrimaryLightBrush,极浅蓝) vs 页面背景 `#F7F9FC`(几乎一样的浅灰)
- 字色:`#1D4ED8` vs `#475569`(都偏暗,亮度差很小)
- 字重:`SemiBold` vs `Medium`(区别细微)

三项加在一起,在普通屏幕上肉眼几乎分辨不出来 —— 点了之后「好像」没变。

**修复**:在 `PageTabItemStyle` 里把激活态改成**强对比 + 形态区分**:

1. **背景**:换成 `PrimaryBrush` 实色 `#3B82F6`(主蓝),与页面背景 `#F7F9FC` 形成强对比。
2. **字色**:白色 `White`,字重 `Bold`。
3. **底部 accent bar**:激活的 tab 下方加一条 3px `PrimaryDarkBrush` 颜色条 + 圆角。形态上的标记 —— 即使色弱用户也能看到。
4. **阴影**:激活的 tab 套 `DialogShadow`,与未激活的扁平 tab 形成「浮起来」的视觉差。
5. **hover**:不激活的 tab hover 时给 `#F1F5F9` 背景 + `TextPrimaryBrush` 字色,跟激活态的蓝色块有明显距离。

**关键经验**:
- 设计 UI 状态时,不要只靠"颜色微调"。**至少要有 1 个形态上的区分**(下划线、accent bar、阴影、边框)作为辅证。
- 颜色对比度至少 AA(普通文本 4.5:1,UI 元素 3:1)。`#DBEAFE` 配 `#F7F9FC` 不到 1.2:1,远低于门槛。
- 用户报"看不出区别" → 第一反应是 **加强视觉对比**,不是 second-guess 业务逻辑。

**影响文件**:
- `Themes/Controls.xaml`(`PageTabItemStyle` 重写)

---

## 16. Log tab 源码从不在 git 里 — fresh clone build 挂

**症状**:把 `1a83d59 fix style issue` 推送之后,任何人(或 CI)从 `Jzhong2026/LocalEnvManager` fresh clone,跑 `dotnet build` 都会挂:

```
error CS0246: 未能找到类型或命名空间名"LogViewModel"(是否缺少 using 指令或程序集引用?)
```

**根因**:`src/LocalEnvManager/LocalEnvManager/ViewModels/Log/LogViewModel.cs` 和 `Views/Log/LogView.xaml` 这两个文件**从来就没进过 git**(`git log --all --diff-filter=A` 返回空)。但 `AppBootstrapper.cs` 在 IoC 容器里注册了它、`ContentHostViewModel.cs` 通过构造器注入它、`ShellView.xaml` 的 `LogTabButton` 把它绑到了 `Host.Pages[2]`。本地能 build 纯粹因为这两个文件**正好**留在工作目录里。

**为什么 git 一直看不见它们**:`.gitignore:26` 写的是 `[Ll]og/` —— 大小写都能匹配,本意是忽略 build 输出的 `Log/` 目录(Serilog 之类的运行时日志目录),但**也匹配了**合法的源目录 `ViewModels/Log/` 和 `Views/Log/`。`git check-ignore -v` 一查就明白:

```
.gitignore:26:[Ll]og/  src/LocalEnvManager/LocalEnvManager/ViewModels/Log/LogViewModel.cs
.gitignore:26:[Ll]og/  src/LocalEnvManager/LocalEnvManager/Views/Log/LogView.xaml
```

`.gitignore` 是 git 的硬墙 —— `git add` / `git add -f` 都加不进去的 ignore 文件,本地 IDE 也搜不到,新人拉代码后 build 才看到坑。

**修复**:不靠 `.gitignore` 字符串修补(容易踩下一个同名目录),直接把源码目录改名成不会被误伤的 `RunLogs/`:
- `ViewModels/Log/LogViewModel.cs` → `ViewModels/RunLogs/RunLogsViewModel.cs`(类名同步)
- `Views/Log/LogView.xaml` → `Views/RunLogs/RunLogsView.xaml`
- `AppBootstrapper.cs:42`、`ContentHostViewModel.cs:24`、`ExecuteViewModel.cs:28`(cref 注释)同步改
- Caliburn.Micro 约定解析 `RunLogsViewModel` → `RunLogsView`,无需手动注册 ViewLocator

**关键经验**:
- **`.gitignore` 过宽是隐形炸弹**:本机 build 永远过、push 也永远过,只有别人拉代码才挂。**每个新加的源目录第一次 commit 前,跑一次 `git check-ignore -v <file>`** 确认它没被误伤。
- **CI 必须从 clean clone 跑一遍 `dotnet build`**:本地工作目录里的"残留文件"会骗过所有人。哪怕 push 之后立刻在另一台机器 clone 一次重 build 都能 catch 这种问题。
- **同样的字面量(`.Log/`)同时用于 build 输出和源目录,几乎一定会撞** —— `.gitignore` 命名要规避真实源目录常用词,或者用更精确的路径(比如 `bin/Log/`、`obj/Log/`、`[Bb]in/**/[Ll]og/`)。

**影响文件**:
- `ViewModels/RunLogs/RunLogsViewModel.cs`(原 `ViewModels/Log/LogViewModel.cs`,重命名)
- `Views/RunLogs/RunLogsView.xaml`(原 `Views/Log/LogView.xaml`,重命名)
- `AppBootstrapper.cs`(注册类型改名)
- `ViewModels/ContentHostViewModel.cs`(构造器参数改名)
- `ViewModels/Execute/ExecuteViewModel.cs`(`<see cref>` 改名)

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
