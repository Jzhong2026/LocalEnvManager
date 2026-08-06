# 实施计划：项目部署管理功能

## 涉及文件
- `LocalEnvManager/Models/ManagedProject.cs`：新增字段与展示属性
- `LocalEnvManager/Models/DeploymentMode.cs`：保持枚举不变
- `LocalEnvManager/Services/ProjectUpdateRunner.cs`：按部署方式执行 + 备份同步
- `LocalEnvManager/ViewModels/ShellViewModel.cs`：解析分类 BasePath 传给运行器
- `LocalEnvManager/Views/ShellView.xaml`：详情展示增加"目标路径"列
- `LocalEnvManager/Views/EditProjectDialog.xaml`：新增命令/脚本/目标路径/备份目录输入
- `LocalEnvManager/Views/EditProjectDialog.xaml.cs`：新增文件夹/文件选择器处理

## 步骤
1. **ManagedProject 模型**
   - 重命名 `OverlayFolderPath` → `BackupFolder`（备份目录）。
   - 新增 `Command`（dotnet/uvicorn 命令参数）与 `TargetPath`（目标路径）。
   - 新增展示属性：`DeploymentModeDisplay`、`ProjectPathDisplay`、`CommandDetailLabel`、`CommandDetailValue`、`BackupFolderDisplay`、`TargetPathDisplay`。
   - 调整 `UsesCommand`（dotnet publish / dotnet build / uvicorn）、`UsesScriptPath`（PowerShell）、`UsesPort`（uvicorn）与 `DisplayInfo`。

2. **路径解析**
   - 在 `ShellViewModel.ExecuteUpdateAsync` 中定位项目所属分类，将 `BasePath` 传入运行器。
   - 运行器内统一解析：路径若为绝对且存在则直接用；否则以 BasePath 为基准组合后使用。

3. **ProjectUpdateRunner**
   - dotnet publish/build：运行 `dotnet <Command>`（Command 为空时默认 `publish`/`build`）。
   - uvicorn：运行 `uvicorn <Command>`。
   - PowerShell：运行 `powershell -ExecutionPolicy Bypass -File <ScriptPath>`。
   - 新增 `CopyDirectory(source, dest)`：递归复制并覆盖。
   - 命令成功（退出码 0）后调用备份同步；uvicorn 在启动前同步。

4. **ShellView 详情**
   - 详情网格由 3 列扩展为 4 列，新增"目标路径"展示 `TargetPathDisplay`。

5. **EditProjectDialog**
   - 新增命令输入框（UsesCommand 时显示，含模式相关提示）。
   - 脚本路径改为文件选择器（UsesScriptPath 时显示）。
   - 源路径、目标路径、备份目录均提供文件夹选择器。
   - Overlay → Backup 文案与绑定重命名。

6. **EditProjectDialog.xaml.cs**
   - 新增 `BrowseTargetFolder_Click`、`BrowseBackupFolder_Click`、`BrowseScriptFile_Click`。

7. **构建与验证**
   - `dotnet build` 通过，启动应用，确认无 XAML 资源错误、中文显示正常、详情非空。
