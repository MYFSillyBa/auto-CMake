# STM32 工程助手 · VS Code 扩展

STM32 Project Helper 为本地 Windows 工程提供 STM32 工具链检测、VS Code 编译与 DAPLink 烧录任务配置、源文件管理、STM32CubeMX MDK-ARM/CMake 双向转换和修改恢复。扩展直接使用当前工作区，无需启动桌面程序，不上传工程。

## 下载与运行要求

| 项目 | 要求 |
| --- | --- |
| 扩展版本 | 1.0.3 |
| 操作系统 | 本地 Windows x64 |
| 编辑器 | VS Code 1.95 或更新版本 |
| 工作区 | 本地文件夹，已授予工作区信任 |
| .NET | VSIX 自带后台运行时，无需另行安装 |
| 构建工具 | CMake、Ninja、ARM GCC；OpenOCD 用于脚本检查和烧录任务 |

下载 [VSIX 安装包](https://github.com/MYFSillyBa/auto-CMake/releases/download/vscode-v1.0.3/stm32-project-helper-1.0.3-win32-x64.vsix)，版本更新内容见 [发布说明](https://github.com/MYFSillyBa/auto-CMake/releases/tag/vscode-v1.0.3)。安装包约 8.5 MB，使用经过裁剪的后台运行库，保留全部功能代码、JSON 协议模型与内置 OpenOCD 脚本。

## 安装

1. 下载 `stm32-project-helper-1.0.3-win32-x64.vsix`。
2. 在 VS Code 中按 `Ctrl+Shift+X` 打开扩展面板。
3. 点击扩展面板右上角的 `…`，选择“从 VSIX 安装…”，选中下载的文件。
4. 安装完成后重新加载 VS Code 窗口。若未出现提示，可通过命令面板执行“Developer: Reload Window”，或关闭并重新打开 VS Code。
5. 打开包含 `CMakeLists.txt`、`.ioc` 或 MDK `.uvprojx` 的工程文件夹，并信任该工作区。

默认自动打开工程工作台。也可点击活动栏的蓝色芯片图标，或在命令面板执行“STM32 工程助手：打开”。

## 更新

下载新版 VSIX，再次执行“从 VSIX 安装…”，覆盖已有扩展后重新加载窗口，无需先卸载。扩展标识保持 `myfsillyba.stm32-project-helper`；已有工作区工具设置和修改记录继续使用。

桌面程序与 VS Code 扩展分别编号、分别发布。扩展安装文件为 `.vsix`，桌面运行文件为 `.exe`。

## 操作流程

### 工程配置

1. 核对当前工程、芯片识别依据、配置预设、构建预设、构建目录和 OpenOCD 目标脚本。
2. 检测到同一种工具的多个安装时，点击“选择版本…”；按版本号和完整路径选择，也可手动指定可执行文件。工具通过验证后保存到当前工作区。
3. 点击“确认写入…”。扩展重新计算当前改动，在原生弹窗中列出工程、操作摘要及受影响文件；确认后保存任务和必要的 CMake 辅助文件。
4. 点击“运行软件检查”，执行 CMake 配置、实际编译与 OpenOCD 脚本解析。问题显示在 VS Code 的问题面板和“STM32 工程助手”输出频道。
5. 编译成功后，从当前构建目标的实际 ELF 列表选择固件，确认烧录任务绑定。

“预览改动”是可选入口，可以查看 VS Code 原生文件差异。直接写入不要求先打开差异页。运行软件检查时若任务尚未配置，扩展会先请求确认并写入任务，再执行构建；编译未通过时保留已写入任务。

任务入口为“终端 → 运行任务 → 一键编译 / 一键烧录(DAPLINK)”。烧录由使用者明确执行；扩展检查过程不确认探针连接，也不烧录硬件。

### 添加源文件

选择当前工程内的文件夹和 CMake 目标，扫描后搜索并逐项选择需要注册的 C/C++、汇编及头文件。未勾选文件不加入构建。确认写入后维护 `CMakeLists.txt` 中对应目标的源文件与包含目录；需要验证时返回工程配置执行软件检查。

### 加入文件

输入工程内的目标文件夹名称，通过本地文件选择器选择来源文件，预览复制计划后确认复制。目标文件夹已存在时需确认追加；同名文件自动改名，保留来源文件。复制完成后可在“添加源文件”中选择参与构建的文件。

### 工程转换

仅支持保留 `.ioc` 与生成源码的 STM32CubeMX 单核 STM32 工程，不安装或调用 CubeMX。

- **MDK-ARM → CMake**：检查工程，选择 `.uvprojx` 和构建目标，查看兼容性问题后确认写入。
- **CMake → MDK-ARM**：选择配置，确认运行 CMake 配置以读取实际构建目标，再选择目标并确认写入 MDK 工程。该检查会在构建目录生成缓存和构建文件。

存在阻止转换的问题时会列出补全说明。转换生成工程与工程编译是两个步骤；生成文件后可运行软件检查。支持范围、CMSIS DSP 库匹配、FreeRTOS 移植层及内存布局限制见 [项目说明](https://github.com/MYFSillyBa/auto-CMake#支持范围)。

### 工具管理

支持选择本地工具、修复缺失工具、导入离线 ZIP 包和激活已有安装。下载仅在明确点击并确认后执行。检测到多个已有安装时，先提示选择版本，再按需修复缺失工具。OpenOCD 使用扩展内置的完整 scripts 目录，其路径作为只读信息展示。

### 修改记录

查看当前工程的修改记录，预览后恢复配置。若文件在备份后已被再次修改，会列出冲突并要求单独确认。恢复也会保存记录。

## 工程与配置保护

- 保留原有任务、JSONC 注释及工程工具链文件。任务只更新本工具管理的内容。
- ELF 来自当前 CMake 构建配置的可执行目标，不按工程文件夹名称猜测，也不从旧文件中选择最近修改者。
- 所选 CMake、Ninja 与 ARM GCC 使用绝对路径。工具路径或工程位置与旧缓存不一致时，先备份缓存，再重新配置。
- 配置预设固定 Debug/Release 时，界面跟随预设并禁用冲突的构建类型选择。
- 写入前检查当前工作区未保存文件并要求“保存后继续”。导入流程还检查明确选中的外部来源文件；取消时保留编辑内容。
- 多工作区通过侧栏的文件夹按钮切换，打开另一工作区的源文件也会切换当前工程。耗时操作期间暂停自动跟随编辑器切换。
- 耗时操作支持取消，显示步骤和输出，不生成 `.ps1` 文件。

## 扩展设置

| 设置 | 用途 |
| --- | --- |
| `stm32ProjectHelper.autoOpen` | 工程检测后的自动打开，默认启用 |
| `stm32ProjectHelper.tools.cMake` | CMake 可执行文件路径，留空自动检测 |
| `stm32ProjectHelper.tools.ninja` | Ninja 可执行文件路径 |
| `stm32ProjectHelper.tools.compiler` | `arm-none-eabi-gcc` 可执行文件路径 |
| `stm32ProjectHelper.tools.openOcd` | OpenOCD 可执行文件路径 |

工具选择与修复结果写入当前工作区的设置，不替换工作区已选的有效工具，也不改写桌面程序保存的全局工具路径。

## 常见问题

**安装后没有显示工作台**：确认使用本地 Windows x64 窗口，工程包含受支持的文件且工作区已信任，然后执行“STM32 工程助手：打开”。可通过扩展设置关闭或开启自动打开。

**工具显示“待选择”**：检测到多个安装，需要明确选择版本。已有工具并不需要因此重新下载。

**生成的 ELF 名称与工程文件夹不同**：名称由 CMake 构建目标和 `OUTPUT_NAME` 等配置决定。烧录任务绑定实际生成的固件，无需重命名文件夹。

**任务已写入但编译未通过**：已写入任务保留。打开问题面板或输出频道定位编译问题，修正后重新运行软件检查或“一键编译”。

**Target 脚本可用是否代表硬件已连接**：脚本检查只确认软件配置，硬件烧录由“一键烧录(DAPLINK)”任务执行。

## 从源码打包与检查

需要 Windows x64、.NET 9 SDK 和 Node.js 22 或更新版本。在仓库根目录执行：

```powershell
cd src/STM32ProjectHelper.VSCode
npm ci
npm run check
npm run package
```

VSIX 写入 `artifacts/vsix`。打包只发布独立后台和扩展，不重新发布桌面 EXE。构建产物、依赖和后台二进制不提交到 Git。

本机已安装 CMake、Ninja、ARM GCC 与 OpenOCD 时，可执行正常软件流程：

```powershell
npm test
npm run smoke
```

`npm test` 使用临时工程检查任务写入、真实 ARM 编译、文件管理、恢复、工具选择与双向转换。`npm run smoke` 使用隔离的普通 VS Code 窗口检查扩展，并确认该检查窗口内的测试写入弹窗；可用 `VSCODE_EXECUTABLE_PATH` 指定本机 VS Code。检查不连接硬件。

## 问题反馈与许可证

通过 [GitHub Issues](https://github.com/MYFSillyBa/auto-CMake/issues) 提供扩展版本、VS Code 版本、Windows 版本、操作步骤与相关输出。涉及转换时补充方向、芯片型号及所选目标。

应用源码使用 [MIT License](https://github.com/MYFSillyBa/auto-CMake/blob/main/LICENSE)。内置 OpenOCD 脚本保留原始版权及许可证声明；第三方工具遵循各自许可证。
