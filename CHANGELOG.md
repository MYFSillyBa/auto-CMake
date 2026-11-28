# 更新记录

## VS Code v1.6.10 · 2026-11-28

### 实时波形

- 波形视图铺满当前 VS Code 编辑区，可从左上角返回工作台。默认深色主题改为石墨灰和淡紫色，连接选项集中在顶部，收发量和缓冲区信息收进「数据统计」。
- 右侧「实时通道」默认展开，显示通道名、当前数值和波形勾选项。控件库和数据终端默认收起，按需展开。
- 新增手动 CSV 录制。连接后点击「开始记录」，再次点击「正在记录，点击停止并保存」选择保存位置。文件只包含录制期间的样本。取消保存后可以重试。
- 「清除数据」直接放在录制按钮旁，用于清空当前波形采样。

### 工程兼容性

- STM32 芯片定义移入子目录的 `.cmake` 文件后，插件也能发现工程。
- 预设名称支持无歧义的大小写差异，构建目录支持等价路径。重新检测时会跟随预设更新构建目录，并清除旧固件选择。
- 单配置工程按 CMake 实际生成的配置识别 ELF，兼容 `CMakeLists.txt` 覆盖构建类型的情况。

### 安装

下载 Windows x64 VSIX。在 VS Code 中按 `F1`，执行「扩展：从 VSIX 安装」，选中文件后重新加载窗口。

**English** · Live Signals now fills the editor, keeps live channel values on the right, and provides explicit CSV recording and a toolbar action to clear samples. CMake project discovery and preset compatibility have also improved. Install the Windows x64 VSIX and reload VS Code.

[与上一公开版本对比](https://github.com/MYFSillyBa/auto-CMake/compare/vscode-v1.6.8...vscode-v1.6.10)

## VS Code v1.6.8 · 2026-11-28

新增实时变量查看侧栏和实时波形，支持设置变量刷新率，修复单配置工程的 ELF 识别和侧栏入口。[v1.6.8 发布说明](https://github.com/MYFSillyBa/auto-CMake/releases/tag/vscode-v1.6.8)

更早版本见 [GitHub 发布记录](https://github.com/MYFSillyBa/auto-CMake/releases)。
