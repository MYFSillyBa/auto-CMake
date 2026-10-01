# 贡献说明

欢迎反馈问题和提交改进。

## 报告问题

使用 Bug Report 模板，提供 Windows 版本、使用的发布版本、复现步骤、预期结果和实际结果。涉及工程检查时附上相关 CMake 配置及构建输出，日志中的个人目录可先替换为示例路径。

## 开发

1. Fork 或克隆仓库，建立用于修改的分支。
2. 安装 .NET 9 SDK，执行 `dotnet build CMakeDapLink.sln -c Release`。
3. 界面修改放 `src/CMakeDapLink.App`，可复用逻辑放 `src/CMakeDapLink.Core`，相应检查放 `tests`。
4. 保持修改集中，按实际影响选择验证范围，并在 PR 中写明验证命令和结果。
5. 功能或操作方式变化时同步更新根目录 README。

构建与发布命令见 [README](README.md)。`artifacts/`、`bin/`、`obj/` 和个人 IDE 设置不提交；遵循 `.editorconfig`，避免写死个人工作目录。

## Pull Request

说明修改的目的、主要变化和验证结果。界面变化可附截图；涉及文件修改时说明备份和已有配置的处理方式。

提交说明使用具体的中文，写明修改对象和结果，例如“修正 STM32 型号识别并匹配 OpenOCD Target”或“提供轻量版和极致轻量版发布配置”。不同功能尽量分开提交，避免使用“更新文件”“首次提交”等无法说明实际改动的描述。

提交的贡献遵循仓库的 [MIT 许可证](LICENSE)。
