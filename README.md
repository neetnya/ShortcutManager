# 快捷方式管理器

一个便携式的小工具：把常用的文件/文件夹收进一个居中的小窗口，分组管理、双击即开。

![界面预览](docs/preview.png)

## 特性

- **启动快** — 冷启动到窗口出现约 **500 ms**
- **小窗口居中** — 打开即出现在屏幕中央，可自由拖动/缩放；**刻意不支持最大化**（窗口按内容设计，拉满屏只会浪费）
- **分组 = Tab** — 分组数量不限，点击 tab 切换；分组可重命名、**拖动标签排序**、删除；
  标签栏放不下时用最右的两个 **‹ › 三角箭头**翻页（按住连续翻动，没有原生滚动条）
- **每行固定四个** — 一行四格、紧凑排列，数量不限，超出自动滚动
- **自动取图标** — 文件夹显示文件夹图标，文件显示系统里该文件类型的图标，EXE 显示它自己的图标
  （提取失败时显示中性兜底图，不会误显示成本工具的图标）
- **默认名字** — 文件夹名 / 不含扩展名的文件名，例如 `report.docx` → `report`
- **双击打开** — 双击格子打开文件或文件夹（用系统默认程序）
- **右键菜单** — 打开 / 在资源管理器中显示 / 重命名 / 编辑路径 / 发送到其他分组 / 移除 / 打开所在目录 / 复制路径
- **拖拽排序** — 按住格子拖到目标位置，左侧或右侧出现蓝色插入线，松手即完成排序
- **多选删除** — 按住 `Ctrl` 点选、`Shift` 选一段，按 `Delete` 或右键「移除 N 项」一次删掉多个
- **拖入即导入** — 从资源管理器把文件/文件夹直接拖进窗口就能添加
- **最小化到托盘** — 点最小化按钮（或 `Esc` / `Ctrl+W`）收进托盘继续后台运行，单击托盘图标即恢复
- **关闭即退出** — 点右上角 `✕` 或 `Ctrl+Q` 真的退出，不驻留后台
- **便携** — 配置存在 exe 同目录的 `config.json`，整个文件夹拷走即可迁移；
  位于程序目录内的条目自动存为**相对路径**，迁移后依然有效

## 快速开始

直接运行 `dist\ShortcutManager\ShortcutManager.exe`。

第一次打开是空的，用下面任一方式添加内容：

1. 从资源管理器把文件/文件夹**拖进窗口**
2. 点底部**「导入文件」**或**「导入文件夹」**（可多选）

然后就可以双击打开了。

## 操作一览

| 操作 | 方式 |
| --- | --- |
| 打开 | 双击格子 / 选中后按 `Enter` |
| 多选 | 按住 `Ctrl` 点选，或 `Shift` 选一段（多选时只有「移除 / 复制路径」可按项操作） |
| 重命名 | 右键 → 重命名 / 选中后按 `F2`（`Enter` 确认，`Esc` 取消） |
| 移除 | 右键 → 移除 / 选中后按 `Delete`（多选时一次删掉全部） |
| 发送到其他分组 | 右键 → 发送到其他分组 → 选目标分组 |
| 排序 | 按住格子拖到目标位置 |
| 切换分组 | 点击 tab；标签栏放不下时点右侧 `‹` `›` 翻页（按住连续翻） |
| 新建分组 | 右键 tab → 新建分组 / `Ctrl+T` |
| 删除分组 | 右键 tab → 删除分组 |
| 重命名分组 | 双击 tab / 右键 tab → 重命名分组 |
| 调整分组顺序 | 按住 tab 拖到目标位置（拖到标签栏空白处 = 移到最后） |
| 最小化到托盘 | 点最小化按钮 / `Esc` / `Ctrl+W` |
| 还原窗口 | 单击托盘图标 / 托盘右键 → 显示主窗口 / 再运行一次 exe |
| 退出程序 | 点窗口关闭按钮 / `Ctrl+Q` / 托盘右键 → 退出 |

> 移除只把条目从列表里删掉，**不会删除磁盘上的文件**。

### 窗口行为

窗口是**普通窗口**：不置顶，失去焦点时不做任何特殊处理。
点到别的程序，本窗口就和其他软件一样被盖到后面，不会被自动最小化或隐藏。

**不支持最大化**：标题栏没有最大化按钮，双击标题栏、系统菜单、`Win+↑` 都不会把窗口放大；
拖拽边框仍然可以自由调整大小（最小 520 × 380）。

### 最小化与退出

**最小化收进托盘，关闭才是真退出**：

- 最小化（点标题栏 `—` / `Esc` / `Ctrl+W`）→ 窗口与任务栏按钮一起消失，
  托盘出现本程序图标，程序继续后台运行（数据早已落盘，不占用 CPU）
- 恢复 → **单击托盘图标**（窗口直接出现在前台，不会被别的窗口盖住），或托盘右键 → 显示主窗口；
  窗口收在托盘里时**再双击一次 exe** 也会把它唤到前台（不会开第二个进程）
- 退出 → 点窗口右上角 `✕`、按 `Ctrl+Q`，或托盘右键 → 退出。退出时会保存配置并摘掉托盘图标

托盘图标在第一次最小化时创建，之后一直保留到程序退出。

## 数据与便携性

配置文件位置按以下顺序决定：

1. **exe 同目录的 `config.json`**（首选，保证便携）
2. 如果 exe 所在目录不可写（例如放在只读介质、`Program Files`），
   自动回退到 `%APPDATA%\ShortcutManager\config.json`

配置采用**原子写入**（先写临时文件再替换），写入过程中断电不会损坏已有配置。
如果配置文件被外部破坏，程序会把它备份为 `config.json.bak` 并回到默认分组，不会静默丢数据。

添加（或右键编辑路径）时，如果目标与 exe 同目录或位于其子目录，
`config.json` 里会存成相对路径（如 `tools\report.docx`），使用时再按程序目录解析回完整路径；
目录之外的条目仍存绝对路径。这样连同数据一起把整个文件夹拷到别的机器/盘符，条目不会失效。

## 运行环境

需要 **.NET 9 桌面运行时**（Windows Desktop Runtime）。

- 已装：直接跑，`dist` 只有 4 个文件、约 **0.25 MB**
- 没装：去 <https://dotnet.microsoft.com/download/dotnet/9.0> 下载
  **Desktop Runtime**（x64）安装即可

> 之所以不打包成自包含单文件：那样体积会到 60–80 MB。
> 如果你更看重「拷到任何机器都能跑」，把 `src/ShortcutManager.csproj`
> 里的发布参数加上 `-r win-x64 --self-contained true /p:PublishSingleFile=true` 重新发布即可
> （见 `tools/publish.ps1`）。

## 从源码构建

```powershell
# 开发构建
dotnet build src\ShortcutManager.csproj -c Release

# 生成便携包到 dist\ShortcutManager\
powershell -ExecutionPolicy Bypass -File tools\publish.ps1
```

## 项目结构

```
src/
  App.xaml(.cs)              程序入口、单实例、退出
  MainWindow.xaml(.cs)       界面与全部交互（tab / 网格 / 右键 / 拖拽 / 重命名 / 编辑路径 / 托盘）
  PathEditDialog.xaml(.cs)   编辑路径对话框
  TrayIcon.cs                托盘图标（只借用 WinForms 的 NotifyIcon，不引入窗体）
  Converters.cs              bool -> Visibility
  WindowEffects.cs           Win11 圆角 + 禁用最大化（都是尽力而为，失败不影响使用）
  Models/
    ShortcutItem.cs          一个条目：路径 + 覆盖名 + 默认命名规则
    ShortcutGroup.cs         一个分组：名字 + 条目列表
  Services/
    ConfigStore.cs           便携式配置读写（原子写入 + 损坏容灾）
    PathResolver.cs          便携相对路径：存储转换 + 解析
    ShellIcons.cs            Win32 Shell API 取系统图标 + 兜底图标
    Launcher.cs              双击打开 / 资源管理器定位
    ImportService.cs         路径导入 + 图标预热
  ViewModels/
    MainViewModel.cs         分组集合、选中态、增删改排序、保存
    ShortcutGroupViewModel.cs
    ShortcutViewModel.cs
  SelfTest.cs                --selftest：图标 / 配置 / 命名规则自检
  LogicTests.cs              --test：80 项逻辑回归测试
tools/
  publish.ps1                生成 dist\ShortcutManager\ 便携包
  verify.ps1                 逻辑测试 + 自检 + 启动耗时 + 托盘行为 + 界面行为 + 截图
  make-icon.ps1              重新生成 app.ico
```

## 自检与测试

程序内置了不依赖界面的自检，方便回归：

```powershell
$exe = "dist\ShortcutManager\ShortcutManager.exe"

# 图标提取、配置读写、命名规则
& $exe --selftest      # 结果写入 exe 同目录 selftest.log，退出码 0 = 全通过

# 80 项逻辑测试：分组/条目增删改、排序（含分组拖拽排序）、跨组移动、去重、
# 相对路径转换与导入、多选批量移除、配置往返与损坏容灾
& $exe --test          # 结果写入 logic-test.log，退出码 0 = 全通过
```

`--test` 会临时占用真实 `config.json` 跑配置往返用例，
因此在 **任何写入之前** 就把你的真实配置备份下来，跑完原样还原（`config.json.bak` 也一样）。

完整验证（含截图与启动耗时）：

```powershell
powershell -ExecutionPolicy Bypass -File tools\verify.ps1     # 逻辑测试 + 自检 + 启动耗时 + 截图
powershell -ExecutionPolicy Bypass -File tools\publish.ps1    # 生成 dist\ShortcutManager\
```

### 开发用调试参数

| 参数 | 作用 |
| --- | --- |
| `--selftest` | 图标/配置/命名自检，写 `selftest.log` 后退出 |
| `--test` | 逻辑回归测试，写 `logic-test.log` 后退出 |
| `--diag` | 启动后把窗口尺寸/DPI 变换写入 `diag.log` |
| `--shot <路径>` | 启动后渲染自身并保存 PNG，然后退出（用 `RenderTargetBitmap`，不受 DPI 影响） |
| `--icon <路径>` | 图标提取诊断（不启动界面），逐步结果写入 `icon-diag.log` |
| `--import <路径>` | 启动后直接把该路径导入当前分组 |
| `--traytest` | 自动走一遍「最小化 → 托盘 → 右键不恢复 → 单击左键恢复 → 关闭」，结果写入 `tray-test.log`，退出码 0 = 全通过。断言不止看 WPF 属性，还会查 **Win32 层**的 `IsIconic` 与前台窗口归属 |
| `--uitest` | 自动断言界面行为并写入 `ui-test.log`：最大化按钮已移除且最大化请求被拒、标签栏箭头在分组少时不显示/多时可翻且到边界即禁用、`Ctrl/Shift` 多选一次移除多项且不动磁盘文件。测试期间不写 `config.json` |

> `--traytest` / `--uitest` / `--shot` / `--diag` / `--import` 属于自动化入口，
> **启动时不做单实例拦阻**——否则用户正开着程序时，这些验证会被静默拦掉并返回成功退出码（假绿）。

## 实现备注

- **每行四个**用 `UniformGrid Columns="4"` 实现，格子宽度随窗口自适应，
  不会出现第四列被裁掉的问题。
- **图标取用**走 `SHGetFileInfo`（`SHGFI_ICON | SHGFI_LARGEICON`），
  对不存在的路径回退到 `SHGFI_USEFILEATTRIBUTES`，因此删掉的文件也还能显示类型图标；
  该调用失败时再用 `ExtractAssociatedIcon` 直接从文件资源里取一次；
  都失败才用程序内绘制的兜底图（灰蓝色方块），**刻意不用本程序自己的图标**，
  否则用户无法分辨「提取失败」和「本来就是这张图标」。
- **图标提取串行化**：Shell 图标缓存对同一路径的并发首次提取会偶发返回空句柄
  （表现为图标显示成兜底图）。因此同一时刻只允许一个提取在跑，
  且提取失败**不写缓存**，下次重绘/刷新会再试一次。图标结果永久缓存，并在后台线程预热。
- **窗口没有开亚克力/模糊**。实测第三方背景模糊会让整窗内容发灰发虚，
  对这类小工具得不偿失；只在 Win11 上加了系统圆角。
- **窗口行为刻意保持普通**：不置顶、不监听 `Deactivated`。
  早期版本试过「失焦自动隐藏/最小化」，需要一套抑制计数来排除右键菜单、
  对话框、输入法、拖拽等场景，既复杂又容易出现「窗口莫名消失」的观感，现已全部移除。
  收进托盘只由用户显式的**最小化**动作触发。
- **拖拽排序**用 WPF 原生 `DragDrop`，按住即拖（与桌面软件惯例一致）：
  靠近目标格子左半边显示左插入线，右半边显示右插入线。
  分组标签用同一套做法（数据格式区分 `ShortcutManager.GroupTab` / `ShortcutManager.TileItem`），
  因此拖格子不会误触分组排序；原来的「右移 / 左移」菜单项已移除。
  拖拽只在**实际发生位移**时才写盘（落点等于原位就不 `Save`）。
- **托盘**用 `System.Drawing` + WinForms 的 `NotifyIcon`（`UseWindowsForms` 已开启，
  但代码里不引入任何 WinForms 窗体，也不会启动第二个消息循环）。
  最小化时 `ShowInTaskbar = false` + `Hide()`：窗口和任务栏按钮一起消失，程序仍在运行；
  **单击左键**即唤出（不注册 `DoubleClick`，否则双击会连着触发两次），
  恢复时走 `App.ForceForeground`（先 `ShowWindow(SW_RESTORE)` 解掉 Win32 层的最小化，
  再 `AttachThreadInput` + `SetForegroundWindow`），所以窗口会真的还原并直接落在最前面，
  而不是只在任务栏闪一下。
  **注意恢复的顺序**：必须 `Show()` 之后再设 `WindowState = Normal`。
  窗口处于隐藏状态时改 `WindowState` 只改了 WPF 的属性、传不到 Win32 窗口，
  之后 `Show()` 会把它按**最小化**的样子显示出来
  （症状：点托盘像没反应，还得自己点一下任务栏按钮）。
  关闭仍然走 `Close()`，由 `ShutdownMode.OnMainWindowClose` 结束进程，
  退出前先 `Visible = false` 再 `Dispose()`，避免托盘里留下点一下才消失的幽灵图标。
- **重命名冲突**：如果新名字等于默认名，会把覆盖名清空而不是存一份冗余副本，
  这样以后文件改名了显示名也会跟着变。
- **标签栏不用原生滚动条**：分组的 `ScrollViewer` 把横向滚动条设为 `Hidden`，
  最右再放两个 `RepeatButton`（三角 `Path`）做翻页。箭头只在
  `ScrollableWidth > 0`（内容比视口宽）时 `Visible`，其余时候 `Collapsed` 不占位；
  处于两端时对应方向的按钮 `IsEnabled = false`（判定留 1.5 px 容差，抵消布局舍入）。
  箭头出现后视口变窄只会让内容“更放不下”，所以不会出现显隐抖动。
  切换分组/新建分组/拖拽排序后会调用 `BringGroupIntoView` 把目标标签滚进可视区。
- **最大化是真的从窗口上拿掉的**：WPF 没有「保留拖拽缩放、但不要最大化」的开关
  （`ResizeMode` 要么连最大化按钮一起带上，要么把边框和缩放一起去掉），
  所以直接改 Win32 窗口样式去掉 `WS_MAXIMIZEBOX`，再去消息层补漏：
  拦 `WM_SYSCOMMAND`/`SC_MAXIMIZE`（系统菜单、`Win+↑`）和标题栏双击，
  最后用一个 `StateChanged` 兜底——万一还是进了最大化（例如 Aero Snap 拖到屏幕顶部），
  立刻退回 `Normal`。
- **多选删除**：磁贴 `ListBox` 用 `SelectionMode="Extended"`（`Ctrl` 加选、`Shift` 选段，
  视觉沿用原来的选中底色，多选就是多格同时高亮）。删除走
  `MainViewModel.RemoveItems`，一次写盘而不是每项一次 `Save`；
  右键落在**未选中**的磁贴上会先把选择收敛到它（和资源管理器一致），
  落在已选中的磁贴上则保留整个多选，所以「移除 N 项」能批量生效。
  多选时右键菜单只留逐项操作（移除、复制路径）可用，避免「重命名 5 个」这种歧义。
