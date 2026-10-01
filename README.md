# CMake · DAPLink 配置助手

<img src="docs/images/chip.png" width="64" height="64" alt="蓝色芯片图标">

Windows 图形工具，用于检测嵌入式 CMake 工程的构建环境，配置 VS Code **一键编译**和**一键烧录(DAPLINK)**，以及管理工程文件。

## 功能

- 选择或拖入 CMake 工程，识别 STM32 具体型号、构建预设和 OpenOCD Target。
- 检查 CMake、Ninja、ARM GCC、OpenOCD、CMSIS-DAP 脚本，弹窗说明待补全项。
- 自动下载安装缺失工具到 `D:\CMake_Tools`，校验 SHA-256，验证后保存路径；支持可配置镜像源。
- 写入 `.vscode/tasks.json`，实际编译并检查烧录配置，修改前备份已有文件。
- 从工程内文件夹逐项选择 `.c` / `.h`，添加到 `CMakeLists.txt`。
- 创建工程文件夹并复制选定文件，同名文件夹询问是否追加，同名文件自动改名。

## 下载与运行

前往 [GitHub Releases](https://github.com/MYFSillyBa/auto-CMake/releases/latest) 下载：

- **轻量版**：`CMakeDapLink-portable-win-x64.zip`，解压后直接运行，自带 .NET。
- **极致轻量版**：`CMakeDapLink-lite-win-x64.zip`，需要电脑已安装 .NET 9 Desktop Runtime。

| 版本 | 本地发布路径 | 大小 | 运行要求 |
| --- | --- | --- | --- |
| 轻量版 | `artifacts/publish/portable/CMakeDapLink.exe` | 约 48 MB | Windows x64，自带 .NET |
| 极致轻量版 | `artifacts/publish/lite/CMakeDapLink.exe` | 约 0.4 MB | Windows x64，.NET 9 Desktop Runtime |

两个版本功能相同。本地发布文件已保留；Git 仓库只保存源码和文档，克隆后可以自行构建。GitHub Actions 构建成功后会生成两个版本的下载产物。

## 使用

1. 打开程序，在“工程配置”选择含 `CMakeLists.txt` 的工程根目录，也可以拖入文件夹。
2. 查看检测摘要。工具缺失时点击“自动修复”；已有安装可使用“手动指定路径”。“镜像源”可以修改备用下载地址，“展开配置详情”可核对工具路径和 Target。
3. 确认芯片型号及 Target 匹配后点击“配置并验证”。程序运行 CMake 配置与编译、解析 OpenOCD 脚本，然后写入 VS Code 任务。
4. 在 VS Code 的“终端 → 运行任务”中选择“一键编译”或“一键烧录(DAPLINK)”。烧录任务先编译，再通过 CMSIS-DAP/SWD 下载 ELF、校验和复位。

配置验证阶段不烧录硬件，也不生成 `.ps1` 文件。重新配置会替换旧版的“一键启动”任务，其他任务保留；JSONC 注释重新序列化后会移除，原文件有备份。

### 添加源文件

在左侧“添加源文件”选择当前工程内的文件夹和 CMake 目标，逐项勾选需要的 `.c` / `.h`。勾选头文件时自动添加所在目录。确认后修改根目录 `CMakeLists.txt` 中本工具维护的配置块，保留原有手工配置并实际编译；再次扫描会保留先前排除的选择。

### 加入文件

在左侧“加入文件”输入工程根目录下的文件夹名称，选择或拖入文件，再点击“创建文件夹并加入”。同名文件夹弹出“是/否”确认，同名文件改为 `名称 (1).扩展名`，保留来源文件。完成后可前往“添加源文件”，选择需要参与构建的文件。

### 自动修复

点击“自动修复”创建 `D:\CMake_Tools`，仅补齐缺失或无法运行的工具，保留已有可用工具。安装使用 Windows x64 ZIP 完整包，下载前查询最新稳定发行版，下载后核对摘要，解压、检查版本，再编译临时 ARM ELF 并解析 DAP 配置。工具验证工程在安装目录内创建并清理，不写入用户工程。

工具路径保存在 `D:\CMake_Tools\tool-paths.json`，镜像设置保存在 `download-sources.json`。下载中可点击“停止下载”，已完成的安装保留。芯片型号、缺失源码和错误构建预设需要按弹窗说明处理。

官方入口：

- [CMake 发布索引](https://cmake.org/files/LatestRelease/cmake-latest-files-v1.json)
- [Ninja 官方发布页](https://github.com/ninja-build/ninja/releases/latest)
- [Arm GNU Toolchain](https://gitlab.arm.com/tooling/gnu-toolchains-for-arm)
- [OpenOCD 官网列出的发行包](https://openocd.org/pages/getting-openocd.html)中的 [xPack Windows 版本](https://github.com/xpack-dev-tools/openocd-xpack/releases/latest)

备用来源包括可修改的 GitHub 加速镜像和 SourceForge 发布镜像；必要时使用 xPack 备用发行版，日志会显示实际版本。备用发行版索引可切换国内 npmmirror，镜像可用性取决于网络和服务状态。

## 从源码构建

需要 Windows 和 .NET 9 SDK。Visual Studio 可直接打开 `CMakeDapLink.sln`，命令行在仓库根目录执行：

```powershell
dotnet build CMakeDapLink.sln -c Release
dotnet run --project src/CMakeDapLink.App
```

发布两个版本：

```powershell
dotnet publish src/CMakeDapLink.App -p:PublishProfile=Portable
dotnet publish src/CMakeDapLink.App -p:PublishProfile=Lite
```

发布目录包含 EXE 和 MIT 许可证。`Portable` 保留完整运行时并启用单文件压缩；`Lite` 依赖电脑已有的 .NET 9 Desktop Runtime。构建程序本身不需要安装 CMake 或 ARM 工具链。

可选检查项目位于 `tests`，使用控制台程序运行。核心检查命令为 `dotnet run --project tests/CMakeDapLink.Tests -c Release`。界面检查需要交互式 Windows 桌面；工具安装检查会实际操作 `D:\CMake_Tools`，按需运行。

## 项目结构

```text
auto-CMake/
├── .github/                 # 构建工作流、Issue 和 PR 模板
├── src/
│   ├── CMakeDapLink.App/     # Views、Controls、Infrastructure、Resources
│   └── CMakeDapLink.Core/    # 检测、构建、配置、文件管理及工具安装
├── tests/                   # 核心、界面及工具安装检查
├── docs/images/             # 文档静态图片
├── artifacts/publish/       # 两个本地发布版本，不提交 Git
├── CMakeDapLink.sln          # 解决方案入口
├── Directory.Build.props    # 共享构建属性
├── global.json              # SDK 版本选择
├── CONTRIBUTING.md          # 贡献说明
└── LICENSE                  # MIT 许可证
```

项目的 `bin/obj`、本地发布文件和个人 IDE 设置已加入 `.gitignore`。GitHub 工作流只构建并打包，不连接硬件或下载嵌入式工具。

## 参与贡献与许可证

欢迎通过 Issue 反馈问题或提交 Pull Request，见 [贡献说明](CONTRIBUTING.md)。本项目使用 [MIT License](LICENSE)，运行时下载的第三方工具遵循各自的许可证。
