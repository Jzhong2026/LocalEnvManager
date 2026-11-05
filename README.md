# LocalEnvManager

Windows 桌面端本地项目更新/部署控制台：按分类维护项目，勾选后一键执行
`git pull → 构建/发布 → 覆盖目录同步 → 启动服务`，并把输出实时显示在界面中。

## 功能

- **项目管理**：分类 / 项目增删改；分类名可直接点击重命名；项目字段包括源路径、命令、目标路径、覆盖目录、IIS 应用池、依赖项目、日志目录。
- **四种部署方式**
  - `dotnet publish` / `dotnet build`
  - PowerShell 命令 / 脚本
  - uvicorn（启动型服务，等待端口绑定后返回）
- **执行更新**：分支查看、全选、按依赖并行调度、逐项目 Stop、批次取消、状态汇总。
- **实时日志**：uvicorn 显示进程 stdout/stderr；其他模式自动发现并 tail 最新日志文件；另有按项目分页的完整运行日志。
- **IIS 适配**：发布前回收配置的应用池；检测 w3wp 文件占用。
- **安全边界**：端口被占用时，只允许结束本应用为该项目的启动进程树；未知进程一律不自动终止，直接取消启动并提示。

## 环境要求

- Windows 10/11
- .NET 10 SDK（构建）/ .NET 10 Desktop Runtime（运行）
- 应用以管理员权限启动（`app.manifest` 中 `requireAdministrator`），用于 IIS 应用池回收和进程管理

## 构建与运行

```powershell
# 构建
dotnet build src/LocalEnvManager/LocalEnvManager.slnx -c Release

# 运行（测试机）
dotnet run --project src/LocalEnvManager/LocalEnvManager/LocalEnvManager.csproj

# 测试
dotnet test src/LocalEnvManager/tests/LocalEnvManager.Tests/LocalEnvManager.Tests.csproj
```

发布（应用运行时不可覆盖自身，建议发布到临时目录后再替换）：

```powershell
dotnet publish src/LocalEnvManager/LocalEnvManager/LocalEnvManager.csproj -c Release -o publish-tmp
```

## 数据与日志位置

| 内容 | 路径 |
|---|---|
| 项目配置 | `%LOCALAPPDATA%\LocalEnvManager\projects.json` |
| 配置损坏备份 | `projects.json.corrupt-yyyyMMdd-HHmmss`（读取失败时自动生成，原文件保留） |
| 运行日志（磁盘） | `%LOCALAPPDATA%\LocalEnvManager\logs\runtime-yyyyMMdd.log` |

配置采用原子写入（临时文件 + 替换）。首次启动会写入示例项目；**合法的空分类列表不会被示例数据覆盖**；读取失败会先备份原文件再继续。

## 配置说明

- 所有路径使用**绝对路径**（不再有分类 BasePath）。
- **覆盖目录**：命令成功后递归覆盖到目标路径，覆盖同名文件，但**不会删除**目标路径中多出的文件。uvicorn 模式在启动前同步。
- **依赖项目**：依赖必须在本批次中一起勾选；未勾选、依赖失败或依赖成环时，本项目会被跳过并标记失败。
- **日志目录**：留空时自动发现，优先目标路径的父目录，其次源路径的父目录；同时尝试项目名与 kebab-case（`LLMAgentApi` → `llm-agent-api`）。
- **项目名称**必须唯一，用作依赖解析与进程登记键。

## 架构

```
ShellView (自定义标题栏 + 3 个页面)
└─ ContentHostViewModel
   ├─ ManageViewModel     配置管理
   ├─ ExecuteViewModel    执行调度 + 实时日志宿主
   └─ RunLogsViewModel    按项目分页的运行日志
        ↑
   IProjectCatalog（单例数据源，防抖 + 原子持久化）
   IProjectUpdateRunner / ProjectLogsWatcher / GitBranchService / RuntimeLogger
```

- MVVM：Caliburn.Micro；页面之间通过 `IEventAggregator` 消息通信，不直接引用。
- 执行结果使用类型化 `ProjectExecutionOutcome`（Success / Failed / Cancelled），取消不再依赖中文消息匹配。
- 日志 tail 使用跨块 UTF-8 解码与半行缓冲，避免多字节字符被截断或半行当成完整行。
- 测试：`tests/LocalEnvManager.Tests`（配置加载/备份、UTF-8 行组装、枚举持久化、结果类型）；CI 见 `.github/workflows/build.yml`。

## 已知限制

- 运行状态与界面日志只保存在内存，重启后清空；磁盘运行日志保留完整输出。
- 没有部署历史/版本快照/回滚（覆盖目录不是备份）。
- dotnet / PowerShell 的输出在进程退出后一次性显示；uvicorn 为逐行实时。
- 项目之间并行执行，未提供并发上限。
- 应用以管理员身份常驻运行；配置文件位于当前用户可写目录，请勿在不信任的账户环境使用。
