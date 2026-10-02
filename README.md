# STM32 工程助手 | STM32 Project Helper

<img src="docs/images/chip.png" width="64" height="64" alt="蓝色芯片图标">

面向 STM32 开发的 Windows 桌面工具，集中完成 **STM32CubeMX 工程的 Keil MDK-ARM ↔ CMake 转换**、**ARM GCC 构建环境检测与自动安装**、**VS Code 一键编译与 DAPLink 烧录任务配置**，以及 C/C++、头文件和汇编源文件管理。

**STM32 Project Helper** is a Windows GUI for STM32CubeMX project conversion between Keil MDK5 (`.uvprojx`) and ARM GCC CMake, embedded toolchain setup, VS Code build/flash tasks, CMSIS-DAP/DAPLink and source-file management. Project conversion does not invoke STM32CubeMX.

## 从需求找到入口

| 你想做什么 | 应用入口 | 得到什么 |
| --- | --- | --- |
| 把 CubeMX 生成的 Keil 工程放到 VS Code 中编译 | 工程转换 → MDK-ARM → CMake | CMakeLists、Debug/Release 预设、GCC 启动和链接配置 |
| 把 CubeMX CMake 工程转回 Keil MDK5 | 工程转换 → CMake → MDK-ARM | 按所选实际目标生成 `.uvprojx`、启动文件和 scatter |
| 解决 CMake、Ninja、ARM GCC 或 OpenOCD 缺失 | 工程配置 → 自动修复；工具管理 | 下载校验、安装验证、工具路径与版本切换 |
| 配置 VS Code 一键编译和 DAPLink 一键烧录 | 工程配置 → 配置并验证 | 保留原有任务的 `.vscode/tasks.json` |
| 把 BSP、驱动或业务模块加入 CMake 编译 | 添加源文件 | 勾选文件和目标，预览后维护源文件与包含目录 |
| 创建工程目录并复制文件 | 加入文件 | 文件夹创建、同名处理及后续参与构建的选择 |

所有配置写入前均可预览；受支持的修改会记录备份，可在应用中恢复。软件验证会区分“配置已生成”和“实际编译通过”。

## 功能

- 选择或拖入 CMake 工程，显示 STM32 型号的识别依据及冲突，选择构建预设和 Debug/Release 配置；多个 ELF 时自行选择固件。
- 检查 CMake、Ninja、ARM GCC、OpenOCD、CMSIS-DAP 脚本，提示待补全项，可展开具体处理说明。
- EXE 内置完整 OpenOCD Target 及配套脚本，识别 STM32 后自动选择配置并验证，无需查找或下载 Target；保留手动指定的 Target。
- 自动下载缺失工具，校验 SHA-256 后安装；支持自选安装目录、镜像源、离线 ZIP、版本切换和旧版本清理。
- 修改前显示文件差异，确认后写入 `.vscode/tasks.json` 或 `CMakeLists.txt`，保存可恢复的修改记录，保留用户任务和 JSONC 注释。
- 显示 CMake、编译和链接问题的位置与处理建议，可复制或导出完整日志。
- 通过目录树、搜索和过滤逐项选择 C/C++、汇编及头文件，添加到指定 CMake 目标。
- 创建工程文件夹并复制选定文件，同名文件夹询问是否追加，同名文件自动改名。
- 对 STM32CubeMX 生成的 STM32 工程进行 MDK5 与 ARM GCC CMake 双向转换，不调用本机 CubeMX；转换前预览、记录修改，生成后检查并按可用工具执行编译验证。
- 按钮显示悬停、按下、键盘焦点及运行状态，操作期间防止重复执行，禁用时说明原因。
- 显示当前操作步骤和用时，详细日志默认收起；完成后保留结果卡，展示实际固件路径、大小、时间和下一步入口。
- 文件列表显示已引用、待加入和未选择状态，统计过滤后隐藏的勾选项；复制完成后可直接预选新增文件参与构建。

## 下载与运行

前往 [GitHub Releases](https://github.com/MYFSillyBa/auto-CMake/releases/latest) 下载：

- **轻量版**：[STM32ProjectHelper-portable-win-x64.exe](https://github.com/MYFSillyBa/auto-CMake/releases/latest/download/STM32ProjectHelper-portable-win-x64.exe)，下载后直接运行，自带 .NET。
- **极致轻量版**：[STM32ProjectHelper-lite-win-x64.exe](https://github.com/MYFSillyBa/auto-CMake/releases/latest/download/STM32ProjectHelper-lite-win-x64.exe)，需要电脑已安装 .NET 9 Desktop Runtime。

| 版本 | 本地发布路径 | 大小 | 运行要求 |
| --- | --- | --- | --- |
| 轻量版 | `artifacts/publish/portable/STM32ProjectHelper.exe` | 约 49 MB | Windows x64，自带 .NET |
| 极致轻量版 | `artifacts/publish/lite/STM32ProjectHelper.exe` | 约 1.6 MB | Windows x64，.NET 9 Desktop Runtime |

两个版本功能相同，Release 附件直接提供 EXE。应用原名“CMake · DAPLink 配置助手”；新版本继续读取已有工具设置和修改记录，并识别旧版生成的 VS Code 任务。仓库名保留 `auto-CMake`，Git 仓库保存源码和文档，克隆后可以自行构建。GitHub Actions 构建成功后会生成两个版本的下载产物。

### 支持范围

- 工程转换：保留 `.ioc` 和生成源码的 STM32CubeMX 单核 STM32 MDK5 / ARM GCC CMake 工程；标准启动代码及实际使用一个 FLASH、一个 RAM 的布局。
- CMake 工作台：检测、配置和编译工程已有的构建目标，使用当前 CMake 配置声明的固件，避免选中旧 ELF。
- 软件要求：配置/编译需要对应 CMake、Ninja、ARM GCC；生成 MDK 工程不要求安装 Keil，实际 MDK 编译验证需要 Keil、对应芯片 Pack 和编译器。
- 烧录任务：DAPLink / CMSIS-DAP，OpenOCD SWD。应用验证阶段只检查软件和脚本；用户执行 VS Code 烧录任务时才连接并下载硬件。

### 常见查找方式

如果你在找“STM32 Keil 转 CMake”“MDK-ARM 转 VS Code”“CubeMX CMake 转 Keil”“CMake 自动安装配置”“DAPLink 一键烧录”或“CMake 添加源文件”，可先按上面的需求表选择入口。

Related terms: **STM32CubeMX · Keil uVision / MDK-ARM / MDK5 · uvprojx · CMakeLists.txt · CMakePresets.json · ARM GCC / arm-none-eabi · VS Code · OpenOCD · CMSIS-DAP · DAPLink · embedded firmware · project converter · toolchain setup**.

## 使用

1. 打开程序，在“工程配置”选择含 `CMakeLists.txt` 的工程根目录，也可以拖入文件夹。
2. 查看检测摘要。识别到支持的芯片后直接使用 EXE 内置 Target 及依赖脚本。构建工具或 OpenOCD 可执行程序缺失时点击“自动修复”；已有安装可使用“手动指定路径”。“镜像源”可以修改工具包的备用下载地址，“展开配置详情”可核对工具路径、内置脚本位置和 Target。
3. 查看“识别详情”，确认芯片型号及 Target，选择配置预设及对应编译预设；预设未固定构建类型时可另选 Debug/Release，也可选择直接构建目录。点击“配置并验证”，先核对差异并确认写入 VS Code 任务，再执行 CMake 配置、编译和 OpenOCD 脚本检查。编译或脚本检查未通过时任务保留，可根据问题提示修正源码后直接在 VS Code 重试。
4. 在 VS Code 的“终端 → 运行任务”中选择“一键编译”或“一键烧录(DAPLINK)”。烧录任务先编译，再通过 CMSIS-DAP/SWD 下载 ELF、校验和复位。

配置验证阶段只检查软件，不确认探针或芯片连接，不烧录硬件，也不生成 `.ps1` 文件。重新配置仅更新本工具管理的任务；同名用户任务保留，新任务加后缀区分。JSONC 注释及编码保留。

固件候选来自 CMake 当前构建配置中可执行目标声明的 ELF，不包含构建目录里旧目标遗留的文件。ELF 名称由工程的 CMake 目标及输出名称决定，可能与文件夹名称不同；无变化编译时文件时间保持不变，仍可作为有效固件。

任务先写入 `.vscode/tasks.json` 和必要的 `.vscode/stm32-daplink.cmake` 辅助文件，首次尚无 ELF 也可以生成。辅助文件由 CMake 执行，负责准备产物查询和读取当前构建目标；不调用 PowerShell，也不按文件夹名猜测 ELF。已有同名用户脚本时使用另一个文件名，不覆盖用户内容。多个 ELF 时，编译完成后选择固件并预览烧录任务的绑定修改；取消选择时保留已写入的任务，烧录任务不会自动选择目标。任务和辅助文件均可通过修改记录恢复。

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

**内置 Target**：EXE 包含完整的 OpenOCD `target` 目录（含子目录共 361 个文件），以及 `interface` 和共享 Tcl 等依赖。识别芯片后直接选用对应配置，不查找本机脚本，也不为 Target 下载工具包。首次使用时自动解压到 `%LOCALAPPDATA%\CMakeDapLink\OpenOcdScripts` 下按内容版本区分的缓存，后续检查缓存完整性。VS Code 任务直接使用该路径，不往用户工程中复制脚本。“内置脚本 → 打开”可以查看这些文件。

常见 STM32 C0、F0/F1/F2/F3/F4/F7、G0/G4、H7、L0/L1/L4/L5、U0/U3/U5、WB/WL 具有系列映射，WBA5 使用内置的 `stm32wbax.cfg`。H7R/S、WBA2/6 所需专用脚本不在本次内置库中，会显示具体原因；不会套用其他系列，也不会为了缺少 cfg 自动下载。用户手动填写的 Target 不会在重新检测、切换预设或确认芯片时被自动覆盖。

安装目录选择、工具路径和镜像设置保存在 `%LOCALAPPDATA%\CMakeDapLink`；旧版安装目录中的配置会兼容读取。下载中显示大小和速度，可点击“停止下载”；已完成的安装保留。芯片型号、缺失源码和错误构建预设需要按弹窗说明处理。

### 工程转换

在左侧“工程转换”选择含 `.ioc` 的 STM32CubeMX 工程根目录。仅支持 CubeMX 生成的 STM32 MDK5（`.uvprojx`）和 ARM GCC CMake 工程；不启动、不安装、不调用 CubeMX，也不重新生成外设初始化代码。

- **MDK-ARM → CMake**：选择 MDK 工程文件和构建目标，读取已启用源文件、宏、包含路径、启动文件及内存布局，预览后生成 CMake 配置。已有 CMake、Ninja、ARM GCC 时实际配置并编译，完成后可“转到工程配置”生成 VS Code 编译与 DAPLink 烧录任务。
- **CMake → MDK-ARM**：选择配置预设和构建类型，先“解析 CMake 构建目标”，再选择需要导出的实际可执行目标。预览后生成 MDK5 工程；检测到本机 Keil 时自动以隐藏命令行进行编译，缺少 Keil 时显示工程文件检查结果及待编译状态。

转换保留原有源文件和原格式工程。已有同名配置会显示替换差异，确认后保存到恢复记录，可通过“恢复转换修改”撤销。输出位置、编译状态和固件信息显示在结果卡中，日志默认收起。

转换兼容性：

- **遗留包含路径**：未使用模块留下的目录不存在时，保留原路径并提示，同一路径只提示一次；实际缺少头文件时由编译报告位置，不创建空目录或删除配置。
- **CMSIS DSP 库**：按 CPU/FPU 匹配工程内同名的 `ARM/arm_cortexM*_math.lib` 与 `GCC/libarm_cortexM*_math.a`，检查 ARM ELF 库容器，并显式链接；支持工程内路径及跨盘符的外部库路径。自动匹配限于已知 M0/M3/M4/M7 小端、soft 或 hard 浮点变体，两个编译器的库需来自相同 CMSIS DSP 版本，ABI/符号仍以实际编译结果为准；其他专用库会列出补全说明。
- **FreeRTOS 移植层**：检查 `port.c`、`portmacro.h` 的 GNU 语法和 `task.h` 内核版本。目录名为 RVDS 但内容已是 GNU 的文件可保留；需要替换时仅使用工程内同版本、同移植目录名称的 GCC 文件，并同步全局、组和文件级包含路径。不会下载其他内核版本替代，也不验证 RTOS 在硬件上的调度。
- **组/文件级设置**：保留各文件包含路径、宏、语言和支持的编译参数，路径生成兼容目录末尾斜杠及跨盘符位置。
- **转换说明**：分别显示“必须补全”和“提示”；转换页提供“编译问题与建议”，显示实际编译问题的文件、行号及处理方法。标准整数类型重复声明、浮点 ABI、短 wchar_t/枚举与预编译库不一致会提供具体建议，程序不会自动改写业务源码。

启动文件和链接脚本只转换能够可靠解析的标准结构，自动内存映射限于实际使用一个 FLASH、一个 RAM 的布局。复杂分区、多 RAM 分配、双核配置、无法匹配的专用库、自定义命令及缺少可确认语法/版本的 FreeRTOS 移植层会列出具体补全方法。生成工程成功和实际编译通过会分别显示；编译通过后仍需核对链接警告，整个流程只检查软件，不连接硬件。

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

转换正常流程：`dotnet run --project tests/CMakeDapLink.Tests -c Release -- --conversion-flow`；转换普通窗口流程：`dotnet run --project tests/CMakeDapLink.UiTests -c Release -- --conversion-ui`。

内置 Target 资源完整性、解压缓存、依赖检查与脚本解析：`dotnet run --project tests/CMakeDapLink.Tests -c Release -- --target-scripts-flow`。可选工具下载检查可追加 `--download-official`，安装在临时目录且不切换用户当前工具；日常使用内置 Target 无需下载。

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

欢迎通过 Issue 反馈问题或提交 Pull Request，见 [贡献说明](CONTRIBUTING.md)。应用源码使用 [MIT License](LICENSE)。内置 OpenOCD 脚本保留原始版权与 SPDX 声明，原始 OpenOCD 许可证随脚本资源保留；该第三方资源和运行时下载的工具遵循各自的许可证。
