# 实施计划：执行更新右侧面板 — 按模式分流（控制台 / 日志文件）

## 1. 背景与目标

`执行更新` 标签页右侧的「实时日志」面板目前只通过 `ProjectLogsWatcher`
去 `<TargetPath>\..\logs\` 目录 tail 最新改动的 log 文件。当项目是
uvicorn / FastAPI 模式时，子进程的 stdout/stderr 不会自动写到那个
目录，所以面板经常空着，提示「此项目未配置实时日志」。

需求：

- uvicorn 模式下，右侧面板显示**进程 stdout / stderr 实时流**。
- 其他模式（dotnet publish / build / PowerShell）继续走文件 tail。
- 选中哪个 project 就展示哪个 project 的输出。
- 进程已经在跑（uvicorn 是启动型）也能切到对应面板继续看。

非目标：保留 cmd.exe 弹窗（已用 `CreateNoWindow = true` 关掉，不动）。

## 2. 架构决策（用户最终拍板）

### 2.1 用 EventAggregator 解耦，绝不在 viewmodel 之间直接引用

- `ShellViewModel`（左侧 master 容器）只通过 `IEventAggregator` **发布**事件：
  - `ProjectSelectedMessage(projectId)` — 用户在左侧列表/右侧详情里切换了 project。
  - `ProjectRunStartedMessage(projectId, deploymentMode)` — 开始运行。
  - `ProjectRunFinishedMessage(projectId, success, message)` — 运行结束。
  - `ConsoleLineMessage(projectId, line)` — uvicorn 模式下的 stdout/stderr 行。
  - `LogFileChangedMessage(projectId)` — 文件 tail 的文件被切换（watcher 内部使用）。
- `ProjectLiveLogViewModel`（右侧 parent）**订阅**这些事件，自己决定给子 viewmodel
  推什么。它**不引用** `ShellViewModel`、不引用 `ManagedProject` 之外的业务对象。
- 子 viewmodel（`ConsoleLiveLogViewModel` / `LogFileLiveLogViewModel`）**只**对
  parent 暴露 `Lines / Available / Hint / FileName`，**对外**通过 parent 与
  EventAggregator 通信。

### 2.2 MVVM：view 里没有任何业务逻辑

- view 只做 `Binding` 和 `EventTrigger`（用 Caliburn 的 `cal:Message.Attach`
  触发 `IEventAggregator.Publish`）。例如切换 project：
  - 不在 view-code 里写 `vm.SelectedProject = ...`，而是用
    `cal:Message.Attach="[Event MouseLeftButtonUp]=[Action Publish($this.Tag.Id)]"`，
    viewmodel 收到一个 `SelectProjectMessage(projectId)`，自己去查 `ExecutionProjects`。
- 同样地，run / cancel 按钮也不在 code-behind 里调用 `RunSelectedAsync`，
  而是发 `RunSelectedMessage`。

### 2.3 每个 project 一个 viewmodel（多种类型）

- `ProjectLiveLogViewModel`（parent）只持一个 `Current: ILiveLogViewModel?`。
- `ILiveLogViewModel` 暴露：
  - `Kind`：`Console` | `LogFile`
  - `Lines : BindableCollection<string>`
  - `Available : bool`
  - `Hint : string`
  - `FileName : string`
- `ConsoleLiveLogViewModel` 实现：从 `ConsoleLineMessage` 里捞自己 projectId 的行。
- `LogFileLiveLogViewModel` 实现：内部用 `ProjectLogsWatcher`，从 `LogFileChangedMessage`
  跟踪当前文件（也支持直接由 parent 调 `TrackProject`）。
- parent 在收到 `ProjectSelectedMessage(projectId)` 时：
  - 如果新 project 是 uvicorn → `Current = new ConsoleLiveLogViewModel(projectId)`
  - 否则 → `Current = new LogFileLiveLogViewModel(projectId)`
  - 不复用旧的（避免不同 project 互相覆盖 buffer）。
- 这样 view 里只需要：
  `ContentControl Content="{Binding Current}"` + 两个 `DataTemplate`
  （`DataType="{x:Type local:ConsoleLiveLogViewModel}"` 和 logfile 那个）。

### 2.5 UI 形态（OQ-1：A）

- **单 `ContentControl`**，不画 tab 条。
- 理由：控制台和日志文件本质都是「日志」，只是来源不同；二选一展示，
  tab 条会让 UI 多一根 chrome 但信息量没增加。
- XAML 形状：
  ```xml
  <ContentControl Content="{Binding ProjectLiveLogs.Current}">
      <ContentControl.Resources>
          <DataTemplate DataType="{x:Type vm:ConsoleLiveLogViewModel}"> ... </DataTemplate>
          <DataTemplate DataType="{x:Type vm:LogFileLiveLogViewModel}"> ... </DataTemplate>
      </ContentControl.Resources>
  </ContentControl>
  ```

### 2.6 控制台消息「启动前」占位（OQ-2：B）

- `ConsoleLiveLogViewModel.Available` 在还没有任何 stdout 行过来时是 `false`。
- view 在 `Available == false` 时画一个「等待 stdout…」+ 三个小点
  做无限呼吸动画（用 `Storyboard` + `DoubleAnimation` on opacity）。
- 第一行 stdout 进来时立刻切到 dark console view 并停掉动画。

### 2.7 ConsoleLiveLogViewModel 内部 buffer 策略（OQ-3：保留 + 限额 + 重运行清空）

- `_lines` 保留，**只在 `ProjectRunStartedMessage(projectId, ...)` 触发时清空**。
- 上限 2000 行（与 `AppendLiveLine` 现状一致）。
- 切换 project 自然创建新 viewmodel 实例，所以「切到 B」会得到一个全新
  `ConsoleLiveLogViewModel`（B 的 buffer）；切回 A 拿到的是 A 之前那个实例
  （如果没被 GC，buffer 还在）。**注意**：parent 必须用 `WeakReference` 或
  `Dictionary<int, ILiveLogViewModel>` 持有 viewmodel 缓存，不要让 GC 收回。

### 2.8 LogFileLiveLogViewModel 持续 tail（OQ-4：A）

- 选中即订阅 `LogFileChangedMessage` 并启动 `ProjectLogsWatcher`。
- 切到非运行项目也能看到「之前那次运行的尾巴」。

### 2.4 用户对风险的容忍度

- Q4：多 project 并行运行时不强调「选中项目 ≠ 正在运行的项目」。在项目行右侧
  `LatestLogSnippet` 已经能看出运行状态；右侧面板按「选中」展示即可。
  **用户接受**。
- Q6：不显示「进程已退出 (exitcode N)」行。**用户接受**。

## 3. 步骤

1. **新增消息类型**（`LocalEnvManager/Messages/`）
   - `ProjectSelectedMessage(int ProjectId)`
   - `ProjectRunStartedMessage(int ProjectId, DeploymentMode Mode)`
   - `ProjectRunFinishedMessage(int ProjectId, bool Success, string Message)`
   - `ConsoleLineMessage(int ProjectId, string Line)`
   - `LogFileChangedMessage(int ProjectId, string FileName)`

2. **`ShellViewModel` 改造**
   - 注入 `IEventAggregator`。
   - 把现有 `LiveLogLines / LiveLogsAvailable / LiveLogsHint / LiveLogsFileName`
     标记 `[Obsolete]`，内部不再被订阅。
   - `OnSelectedProjectChanged` 不再操作 `_liveLogLines` / `_liveLogs.TrackProject`。
     只发 `Publish(new ProjectSelectedMessage(SelectedProject.Id))`。
   - `RunProjectAsync` 里把 stdout / stderr 推给 `IProgress<string>` 之外，
     **再 tee 一份**到 `ConsoleLineMessage`（uvicorn 专用），写到
     `new Progress<string>(line => _events.PublishOnUIThread(
        new ConsoleLineMessage(project.Id, line)))`。
     对 `RunCapturedProcessAsync`（dotnet/powershell）路径也加同样的 tee。
   - `RunSelectedAsync` 起头发 `ProjectRunStartedMessage`，
     `RunProjectAsync` 结束（成功/失败/异常）发 `ProjectRunFinishedMessage`。

3. **新增 `LocalEnvManager/ViewModels/ProjectLiveLogViewModel.cs`**
   - 注入 `IEventAggregator` 和 `IProjectLogsWatcherFactory`（下面第 4 步）。
   - 订阅 `ProjectSelectedMessage`：`Current = factory.Create(projectId, mode)`。
   - 暴露只读属性 `Current`，XAML 绑它。
   - 订阅 `ConsoleLineMessage` 并分发给 `Current`（如果是 Console）。
   - 订阅 `LogFileChangedMessage` 并更新 `Current.FileName`（如果是 LogFile）。

4. **抽出 `IProjectLogsWatcherFactory`**
   - `LogFileLiveLogViewModel` 自己 new 一个 `ProjectLogsWatcher` 是不行的——
     工厂是为了后续单测；本期实现就够了。
   - 工厂签名：`ILiveLogViewModel Create(int projectId, DeploymentMode mode)`。
   - 内部：根据 mode 分流到 `ConsoleLiveLogViewModel` 或 `LogFileLiveLogViewModel`。

5. **`LocalEnvManager/Views/ShellView.xaml` 改造**
   - 右侧详情卡片里：
     - 删除直接绑 `LiveLogLines` 的 ScrollViewer 和两个 `Border`。
     - 新增一个 `ContentControl Content="{Binding ProjectLiveLogs.Current}"`，
       用 `DataTemplate DataType="{x:Type local:ConsoleLiveLogViewModel}"` 和
       `DataType="{x:Type local:LogFileLiveLogViewModel}"` 分别画 dark console
       和 file-tail 视图。两个 viewmodel 共享视觉（dark console），只是数据源不同。
   - 把 `ShellView.xaml.cs` 里所有直接操作 viewmodel 状态的 code-behind
     （`SelectAll_Click` / `ProjectRow_Click` / `ExecuteProjectRow_Click` /
     `RefreshBranches_Click` / `RunSelected_Click` / `CancelRun_Click`）
     替换为 `cal:Message.Attach` 触发事件，viewmodel 端收到后发
     `EventAggregator.Publish` 对应 message。
   - `LiveLogScrollViewer` 自动滚动逻辑改到 viewmodel 里（订阅
     `ConsoleLiveLogViewModel.Lines.CollectionChanged`）。

6. **Bootstrapper**
   - 注册 `IProjectLogsWatcherFactory`（singleton）和
     `ProjectLiveLogViewModel`（singleton — 整个 app 只有右侧一个 parent）。

7. **构建与验证**
   - `dotnet build` 通过。
   - 启动 app，分别在 uvicorn 项目和 dotnet 项目上：
     - 切到「执行更新」tab；
     - 右侧面板正确显示「实时控制台」或「日志文件」视图；
     - 切到不同 project，视图正确切换、内容正确归零；
     - uvicorn 模式下点「运行」，**控制台消息实时出现在右侧**，且没有 cmd 弹窗。

## 4. 不再保留的旧东西

- `ShellViewModel.LiveLogLines` / `LiveLogsAvailable` / `LiveLogsHint` /
  `LiveLogsFileName` —— 标记 `[Obsolete]`，不再被 view 订阅。后续版本删。
- `ProjectLogsWatcher.SetSink` —— 工厂化后由 viewmodel 直接 new + 自己接
  `FileSystemWatcher` 事件，sink 模式不再需要。

## 5. 风险与权衡

- **WPF `ContentControl` + `DataTemplate` 切换时 ScrollViewer 滚动位置不保留**。
  接受 —— 切换 project 时本来就该重置视图。
- **`EventAggregator.Publish` 跨线程**。Caliburn 的 `EventAggregator` 默认在
  发布线程上回调；`OutputDataReceived` 是 ThreadPool 线程。
  解决：在 viewmodel 里统一用 `IEventAggregator.PublishOnUIThread(...)`（Caliburn
  自带 helper，没有的话自己用 `Application.Current.Dispatcher.BeginInvoke` 包一层）。
- **多 project 并行运行时 stdout 行归属**。`ConsoleLineMessage` 带 `projectId`，
  订阅方只取自己负责的。简单且无歧义。
- **历史回放**。`ConsoleLiveLogViewModel.Lines` 是 in-memory buffer，上限 2000 行。
  跨 project 切换会清空（这是预期行为）。如要保留历史，那是另一个 feature。

## 6. 已解决的 Open Questions

| OQ | 决策 | 备注 |
| --- | --- | --- |
| OQ-1 右侧 UI 形态 | **A**（单 ContentControl + DataTemplate） | 不画 tab 条；控制台与日志文件本质都是日志 |
| OQ-2 控制台启动前占位 | **B**（带三个小点的呼吸动画） | 第一行 stdout 进来时切到 dark console 并停掉动画 |
| OQ-3 console buffer | 保留 + 限额 2000 行 + 重运行时清空 | parent 用 `Dictionary<int, ILiveLogViewModel>` 缓存 |
| OQ-4 LogFileLiveLogViewModel 持续 tail | **A**（选中即订阅） | 切到非运行项目也能看历史尾巴 |
