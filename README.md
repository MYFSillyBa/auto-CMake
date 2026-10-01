# CMake · DAPLink 配置助手

<img src="docs/images/chip.png" width="64" height="64" alt="蓝色芯片图标">

Windows 图形工具，用于检测嵌入式 CMake 工程的构建环境，配置 VS Code **一键编译**和**一键烧录(DAPLINK)**，以及管理工程文件。

## 功能

- 选择或拖入 CMake 工程，显示 STM32 型号的识别依据及冲突，选择构建预设和 Debug/Release 配置；多个 ELF 时自行选择固件。
- 检查 CMake、Ninja、ARM GCC、OpenOCD、CMSIS-DAP 脚本，弹窗说明待补全项。
- 自动下载缺失工具，校验 SHA-256 后安装；支持自选安装目录、镜像源、离线 ZIP、版本切换和旧版本清理。
- 修改前显示文件差异，确认后写入 `.vscode/tasks.json` 或 `CMakeLists.txt`，保存可恢复的修改记录，保留用户任务和 JSONC 注释。
- 显示 CMake、编译和链接问题的位置与处理建议，可复制或导出完整日志。
- 通过目录树、搜索和过滤逐项选择 C/C++、汇编及头文件，添加到指定 CMake 目标。
- 创建工程文件夹并复制选定文件，同名文件夹询问是否追加，同名文件自动改名。

## 下载与运行

前往 [GitHub Releases](https://github.com/MYFSillyBa/auto-CMake/releases/latest) 下载：

- **轻量版**：[CMakeDapLink-portable-win-x64.exe](https://github.com/MYFSillyBa/auto-CMake/releases/latest/download/CMakeDapLink-portable-win-x64.exe)，下载后直接运行，自带 .NET。
- **极致轻量版**：[CMakeDapLink-lite-win-x64.exe](https://github.com/MYFSillyBa/auto-CMake/releases/latest/download/CMakeDapLink-lite-win-x64.exe)，需要电脑已安装 .NET 9 Desktop Runtime。

| 版本 | 本地发布路径 | 大小 | 运行要求 |
| --- | --- | --- | --- |
| 轻量版 | `artifacts/publish/portable/CMakeDapLink.exe` | 约 48 MB | Windows x64，自带 .NET |
| 极致轻量版 | `artifacts/publish/lite/CMakeDapLink.exe` | 约 0.6 MB | Windows x64，.NET 9 Desktop Runtime |

两个版本功能相同。本地发布文件已保留；Git 仓库只保存源码和文档，克隆后可以自行构建。GitHub Actions 构建成功后会生成两个版本的下载产物。

## 使用

1. 打开程序，在“工程配置”选择含 `CMakeLists.txt` 的工程根目录，也可以拖入文件夹。
2. 查看检测摘要。工具缺失时点击“自动修复”；已有安装可使用“手动指定路径”。“镜像源”可以修改备用下载地址，“展开配置详情”可核对工具路径和 Target。
3. 查看“识别详情”，确认芯片型号及 Target，选择配置预设及对应编译预设；预设未固定构建类型时可另选 Debug/Release，也可选择直接构建目录。点击“配置并验证”，程序实际配置、编译并解析 OpenOCD 脚本；多个 ELF 时选择固件，核对差异后确认写入 VS Code 任务。
4. 在 VS Code 的“终端 → 运行任务”中选择“一键编译”或“一键烧录(DAPLINK)”。烧录任务先编译，再通过 CMSIS-DAP/SWD 下载 ELF、校验和复位。

配置验证阶段只检查软件，不确认探针或芯片连接，不烧录硬件，也不生成 `.ps1` 文件。重新配置仅更新本工具管理的任务；同名用户任务保留，新任务加后缀区分。JSONC 注释及编码保留。

### 修改预览、恢复与问题定位

写入前查看各文件修改前后的内容，取消即可放弃写入。“恢复修改”显示该工程的修改记录，恢复前再次预览。文件已被手动改动时会列出冲突，需要明确确认覆盖；恢复操作也会留存记录。备份保存在用户数据目录，不添加到工程内。

编译输出中的错误和警告会整理到“问题与日志”，按 CMake 配置、编译、链接显示文件及行号、可能原因。双击有本地位置的问题可定位文件；处理建议需要结合工程核对，不会自动删改源码。

### 添加源文件

在左侧“添加源文件”选择当前工程内的文件夹和 CMake 目标，用目录树逐项或整组选取 `.c`、`.cpp`、`.cc`、`.cxx`、`.s`/`.S`、`.h`、`.hpp`、`.hh`、`.hxx`。可搜索路径、过滤已勾选/未勾选/已引用文件，过滤不丢失隐藏文件的选择。勾选头文件时自动添加所在目录，C++/汇编按需启用语言。

确认差异后修改根目录 `CMakeLists.txt` 中对应目标的维护块，保留手工配置并实际编译；再次扫描保留先前排除的选择。仅明确解析的直接引用用于避免重复添加；条件、函数、循环、include 或复杂表达式不能确定时会提示，文件仍可选，需要核对差异。

### 加入文件

在左侧“加入文件”输入工程根目录下的文件夹名称，选择或拖入文件，再点击“创建文件夹并加入”。同名文件夹弹出“是/否”确认，同名文件改为 `名称 (1).扩展名`，保留来源文件。完成后可前往“添加源文件”，选择需要参与构建的文件。

### 自动修复

点击“自动修复”仅补齐缺失或无法运行的工具，保留已有可用工具。默认安装到 `D:\CMake_Tools`，可在“工具管理”更换目录。安装使用 Windows x64 ZIP 完整包，下载前查询最新稳定发行版，下载后核对摘要，解压、检查版本，再编译临时 ARM ELF 并解析 DAP 配置。工具验证工程在安装目录内创建并清理，不写入用户工程。

安装目录选择、工具路径和镜像设置保存在 `%LOCALAPPDATA%\CMakeDapLink`；旧版安装目录中的配置会兼容读取。下载中显示大小和速度，可点击“停止下载”；已完成的安装保留。芯片型号、缺失源码和错误构建预设需要按弹窗说明处理。

### 工具管理

左侧“工具管理”列出已安装版本、路径和大小，可导入官方完整 ZIP 或指定外部工具。导入版本后通过“启用所选版本”验证并切换，其他版本保留，便于回退。“检查更新”先显示候选版本，由用户决定是否安装。清理页面仅允许删除本程序记录的缓存或未启用版本；外部手动工具不能删除。离线包来自用户选择，程序计算 SHA-256、检查内容和可执行性，不能凭本地摘要确认发行者身份。

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

可选检查项目位于 `tests`。正常软件流程：`dotnet run --project tests/CMakeDapLink.Tests -c Release -- --software-flow`；普通窗口流程：`dotnet run --project tests/CMakeDapLink.UiTests -c Release -- --software-ui`。这些流程需要已安装的嵌入式工具；界面检查需要交互式 Windows 桌面。临时工程不连接硬件。

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
