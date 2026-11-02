# 文件夹图标设置（FolderIconStudio）

一个轻量的 Windows 文件夹图标工具：把 ICO 或图片导入为图标库，通过右键菜单或主窗口一键套用到文件夹。图标文件保存在目标文件夹内部，**重装系统、换电脑、移动文件夹后仍然有效**。

- 无需管理员权限（只写当前用户的 `HKCU`）
- 单文件绿色版，复制 exe 即可运行
- 关闭程序后右键菜单仍然可用
- 支持多个图标库、多级子文件夹、恢复默认图标、图标属性修复

## 目录

- [功能](#功能)
- [运行环境](#运行环境)
- [构建与发布](#构建与发布)
- [项目结构](#项目结构)
- [实现原理](#实现原理)
  - [图标为什么能跟着文件夹走](#图标为什么能跟着文件夹走)
  - [让资源管理器立即刷新](#让资源管理器立即刷新)
  - [右键菜单](#右键菜单)
  - [单实例与命令转发](#单实例与命令转发)
  - [图标库存储](#图标库存储)
- [命令行参数](#命令行参数)
- [数据位置与卸载](#数据位置与卸载)
- [已知限制](#已知限制)
- [常见问题](#常见问题)
- [开发注意事项](#开发注意事项)

## 功能

- **图标库**：新建、重命名、删除图标库；导入 `.ico`、`.png`、`.jpg`、`.jpeg`、`.bmp`（图片自动转成多尺寸 ICO），按内容哈希去重。
- **目标文件夹**：添加多个文件夹（按钮或拖拽），支持删除列表项、清空列表、递归应用到所有子文件夹。
- **应用 / 恢复 / 修复**：
  - 应用图标：把图标写入每个目标文件夹，并修正资源管理器需要的属性。
  - 恢复默认：删除本程序写入的图标与 `desktop.ini` 中的图标引用，不影响文件夹里的其他内容。
  - 修复图标：文件夹被复制、解压或换电脑后图标可能失效，用此功能重新激活（只处理确实带有本程序图标文件的文件夹）。
- **右键菜单**：
  - `设置文件夹图标(&I)...`：打开选择窗口。
  - 每个图标库一个级联菜单，直接点击图标即可应用；图标较多时自动分页。
  - `恢复默认文件夹图标(&R)`。
- 支持文件夹空白处右键（`Directory\Background`）。
- 单实例运行：重复启动会把右键请求转发给已运行的窗口。

## 运行环境

- Windows 10 / Windows 11（x64）
- 开发需要 .NET 8 SDK（`net8.0-windows`、WPF）
- 运行发布版不需要安装 .NET（自包含单文件）

## 构建与发布

```powershell
# 调试构建
dotnet build -c Release

# 生成单文件绿色版（输出到 publish 目录）
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish
```

发布目录中只需保留 `FolderIconStudio.exe`，可以删除 `.pdb`。首次启动会把内嵌的菜单图标释放到 `%LOCALAPPDATA%\FolderIconStudio\`，因此 exe 可以随意移动。

## 项目结构

```
FolderIconStudio.csproj     项目与发布配置（应用名、图标、版本）
App.xaml / App.xaml.cs      应用入口、共享样式、命令行分发、单实例装配
MainWindow.xaml(.cs)        主窗口：图标库 + 目标文件夹
QuickApplyWindow.xaml(.cs)  右键“设置文件夹图标”打开的快速选择窗口
TextPromptWindow.cs         新建/重命名图标库的输入对话框
app-icon.ico                应用图标，同时作为右键菜单图标（嵌入资源）
Services/
  IconLibraryService.cs     图标库：JSON 索引、导入转换、去重、分组
  IcoEncoder.cs             图片转多尺寸 ICO（256/64/48/32/16）
  FolderIconService.cs      应用/恢复/修复图标，遍历文件夹
  DesktopIni.cs             desktop.ini 的最小化读写
  FolderCustomSettings.cs   调用 SHGetSetFolderCustomSettings
  ShellRefresh.cs           通知资源管理器并重建图标缓存
  ShellMenuService.cs       注册/删除 HKCU 右键菜单
  SingleInstance.cs         互斥体 + 命名管道，单实例转发
```

## 实现原理

### 图标为什么能跟着文件夹走

每个被设置过图标的文件夹里会有两个隐藏文件：

- `.foldericon.<sha256前16位>.ico` —— 图标本体，只使用文件名引用，不带盘符或绝对路径。
- `desktop.ini` —— 内容为：

```ini
[.ShellClassInfo]
IconResource=.foldericon.b6020af1e19fef0c.ico,0
```

文件名带内容哈希，原因是资源管理器会缓存文件夹图标；只有 `desktop.ini` 里的引用发生变化时，它才愿意重新提取位图。使用稳定文件名会导致换成相似图标后仍显示旧图。

配套的属性设置：

- 图标文件与 `desktop.ini`：`Hidden | System`
- 文件夹：`ReadOnly`（这是资源管理器读取 `desktop.ini` 的前提，不影响文件读写）

应用图标时优先调用外壳 API `SHGetSetFolderCustomSettings`；若系统不支持，则回退到直接写 `desktop.ini`。恢复默认时会清除图标键，`desktop.ini` 若已无其他内容则删除文件，并去掉文件夹的 `ReadOnly` 属性。

### 让资源管理器立即刷新

仅写文件不会让已打开的窗口换图，`ShellRefresh` 依次执行：

1. `SHChangeNotify` 通知文件夹属性、条目和父目录变化；
2. `SHChangeNotify(SHCNE_ASSOCCHANGED)`；
3. `ie4uinit.exe -ClearIconCache` 清理当前用户的图标缓存，等待 250 ms 后 `ie4uinit.exe -show` 重绘。

这些调用是“尽力而为”的：失败只影响立即重绘，图标本身已经落盘，下次刷新仍会显示。

### 右键菜单

只写当前用户注册表，不需要管理员权限：

```
HKCU\Software\Classes\Directory\shell\
HKCU\Software\Classes\Directory\Background\shell\
```

条目：

- `FolderIconStudio` —— 设置文件夹图标（打开选择窗口）
- `FolderIconStudio.G000`、`G001`…… —— 每个图标库/分页一个级联菜单，使用 `ExtendedSubCommandsKey` 展开
- `FolderIconStudio.Restore` —— 恢复默认图标

**关键限制：资源管理器的静态级联菜单最多显示 16 项（含根项），第 17 项及之后会被静默丢弃。** 因此：

- 每个图标库单独占一个顶级级联，而不是把全部图标塞进一个菜单；
- 单个图标库超过 15 个图标时按页拆分，页数均分（例如 20 个图标 → 10 + 10，标题显示 `配网 (1/2)`、`配网 (2/2)`）。

点击图标执行 `FolderIconStudio.exe --apply-icon "<文件夹>" "<图标路径>"`，应用完成后进程直接退出，不会弹出主窗口。

菜单图标不直接指向 exe：资源管理器按路径缓存菜单图标，升级后可能仍显示旧图标。程序把内嵌的 `app-icon.ico` 释放为 `%LOCALAPPDATA%\FolderIconStudio\app-icon.<哈希>.ico`，每次构建内容变化都会得到新路径。

图标库内容变化（导入、删除、改名、切换等）会触发重建菜单；程序启动时也会重建一次。

### 单实例与命令转发

- 互斥体：`Local\FolderIconStudio.Mutex`
- 命名管道：`FolderIconStudio.Pipe`
- 消息：`icon|<文件夹>`（打开快速选择窗口）、`open|`（打开主窗口）

主实例在后台监听管道，收到请求后通过 `Dispatcher` 打开窗口。若转发失败（主实例已退出），当前进程会自行处理这次请求。

### 图标库存储

索引位于 `%LOCALAPPDATA%\FolderIconStudio\icons\library.json`，图标文件与索引同目录：

```json
{
  "Version": 2,
  "ActiveGroupId": "5f0feedc6d114fc0ae1be19b02ac7eab",
  "Groups": [
    {
      "Id": "5f0feedc6d114fc0ae1be19b02ac7eab",
      "Name": "配网",
      "Icons": [
        { "FileName": "b97b900f8a594c928fa36f21bc52105d.ico", "Name": "app-icon" }
      ]
    }
  ]
}
```

- 导入时按 SHA-256 去重（同一图标库内）；重名图标自动追加 `(2)`、`(3)`。
- 读写使用临时文件 + 覆盖移动，避免写入中断损坏索引。
- 兼容旧的单库数组格式，启动时自动迁移为多库结构。
- 索引文件丢失或损坏时会回退到空库，不会删除磁盘上的图标文件。

## 命令行参数

| 参数 | 作用 |
| --- | --- |
| `--set-icon "<folder>"` | 打开指定文件夹的快速选择窗口（右键菜单使用） |
| `--apply-icon "<folder>" "<ico>"` | 直接把图标应用到文件夹，完成后退出 |
| `--clear-icon "<folder>"` | 恢复该文件夹的默认图标，完成后退出 |
| 无参数 | 打开主窗口 |

## 数据位置与卸载

| 内容 | 位置 |
| --- | --- |
| 图标库 | `%LOCALAPPDATA%\FolderIconStudio\icons\` |
| 菜单图标缓存 | `%LOCALAPPDATA%\FolderIconStudio\app-icon.<hash>.ico` |
| 右键菜单 | `HKCU\Software\Classes\Directory\shell\FolderIconStudio*`（Background 同理） |
| 文件夹图标 | 目标文件夹内的 `.foldericon.*.ico` + `desktop.ini` |

卸载步骤：

1. 在程序里对需要还原的文件夹执行“恢复默认”；
2. 删除 `%LOCALAPPDATA%\FolderIconStudio\`；
3. 删除注册表中的 `FolderIconStudio*` 项（下次运行程序也会自动清理旧项）；
4. 删除 exe。

## 已知限制

- Windows 11 的右键菜单默认折叠，需要先点“显示更多选项”（或使用系统设置恢复完整菜单）。
- 静态级联菜单最多 16 项，已通过“每库一个级联 + 自动分页”规避；单页上限 15 个图标。
- 文件夹的 `ReadOnly` 属性会被置位，这是资源管理器读取 `desktop.ini` 的必要条件；不会阻止在文件夹内新建或修改文件。
- 只注册文件夹与文件夹空白处的右键菜单，不处理驱动器根目录、库或网络位置的只读路径。
- 递归应用会跳过目录联接/符号链接（reparse point），避免循环；无权限的子目录会被跳过并在结果中报告。
- 图标库按用户存储，不随 exe 一起便携；迁移电脑需要复制 `%LOCALAPPDATA%\FolderIconStudio\icons\`。

## 常见问题

**应用后图标没变？**
程序会主动清理图标缓存，通常 1 秒内生效。若仍未刷新，按 `F5` 刷新窗口，或在任务管理器中重启“Windows 资源管理器”。

**文件夹复制/解压/换电脑后图标丢了？**
`desktop.ini` 与隐藏图标文件可能未被一起复制，或文件夹属性被重置。选中文件夹后使用“修复图标”；仍无效则重新应用一次。

**右键菜单里少了图标？**
确认对应图标库非空；超过 15 个图标会分页显示。菜单在图标库变化时会自动重建。

**右键菜单注册失败？**
程序会提示但主窗口仍可使用。检查是否有安全软件拦截 `HKCU\Software\Classes` 写入，或以当前用户身份重新运行。

**为什么菜单里图标名称重复？**
不同图标库允许同名图标；菜单按库分组显示，名称只在库内去重。

## 开发注意事项

- 修改 `app-icon.ico` 后无需额外操作，构建时作为资源嵌入，并用于 exe 图标与菜单图标。
- 菜单项数量相关逻辑集中在 `ShellMenuService.MaxIconsPerPage`，调整前请先验证 16 项上限行为。
- 所有耗时操作（应用、恢复、修复、图标缓存重建）都在后台线程执行，UI 通过 `SetBusy` 禁用交互。
- 写注册表、改文件夹属性、删除文件都属于有副作用的操作，新增功能时请保持“只处理自己创建的内容”的原则。
- `publish/`、`bin/`、`obj/` 已在 `.gitignore` 中排除，仓库只保存源码与文档。
