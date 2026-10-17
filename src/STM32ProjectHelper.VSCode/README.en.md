# STM32 Project Helper · VS Code Extension

[English](README.en.md) · [简体中文](README.md)

STM32 Project Helper provides toolchain detection, CMake build and DAPLink flash task configuration, source management, STM32CubeMX MDK-ARM/CMake conversion, and change restoration inside VS Code. It operates on the current local workspace and does not upload projects.

## Local release: VS Code extension v1.3.0

All ten features below are included in the local v1.3.0 VSIX: `artifacts/vsix/stm32-project-helper-1.3.0-win32-x64.vsix`. Desktop v1.6.0 includes the other nine features through its Development and Analysis entry; PostHog is exclusive to the extension. Desktop Cortex-Debug configuration is used in VS Code. Earlier installers are retained; this release has not been uploaded to GitHub. Install the newer VSIX and reload the window to update.

| Feature | Workflow and limits |
| --- | --- |
| clangd setup | Development Setup → generate the real compilation database, then confirm `.clangd` and workspace settings. Preserves comments/custom arguments, merges compiler query drivers and disables C/C++ IntelliSense to avoid competing engines. Install the clangd extension through the provided search entry. |
| Cortex-Debug setup | Build/select the current actual ELF, then write or start the managed OpenOCD/GDB configuration. User configurations remain intact. Generated sessions use attach and load symbols without automatically flashing firmware. Cortex-Debug must be installed. |
| CubeMX regeneration sync | Watches IOC/MDK/CMake/linker configuration and offers a reviewed diff. Recomputes CMake conversion from a CubeMX MDK source or restores helper-managed source blocks after native CMake regeneration. Does not invoke CubeMX or regenerate peripheral code. Managed blocks are retained in local VS Code workspace state for recovery, without a recent-project list. |
| Hardware checks | Explicit probe/chip connection check; direct flashing checks first by default, with an option to disable. Script availability is separate from real probe/chip evidence. May halt/reset a target; checks do not flash. |
| ELF/MAP analysis | Ranked function/variable sizes, GNU MAP object contributions and regions. ELF and MAP can be combined when from the same build. Aliases are not summed as total usage; unknown layout stays unknown. |
| Diagnostic report | Locally previews redacted tool versions, results and recent output, then saves to a chosen file. Common paths, IPs, emails and tokens are replaced. Review project-specific secrets before sharing; no automatic upload. |
| Serial assistant | Explicit COM connection with baud rate, ASCII/HEX, LF/CRLF, timestamped RX/TX and log export. Currently 8N1 with DTR/RTS off. Bounded logs; closing the workbench or switching projects releases the port. |
| HardFault analysis | Register JSON or assignment text, core-aware valid-bit decoding, optional ELF PC/LR source mapping through the selected GCC's addr2line. M0/M0+, EXC_RETURN and imprecise faults retain uncertainty. No automatic runtime stack unwinding. |
| FreeRTOS task snapshots | Manual refresh through an existing local GDB server. Reads real TCB names/state/priority; reports stack high-water in StackType_t units only when bounds, growth and fill conditions are verified. Matching ELF/firmware and Python-enabled GDB required; unknown data stays unknown. May halt the target and attempts detach afterward; no polling. |
| PostHog statistics | Off by default; requires your HTTPS project host/token and native consent. Token stays in VS Code secret storage. Sends only random anonymous ID, version, language, feature and outcome, subject to VS Code telemetry settings. Excludes paths/code/logs/serial/device identifiers. The endpoint still sees the source IP. Disabling stops transmission and clears the local anonymous ID. |

One-click database generation uses the active CMake build directory; an alternative database directory selects an existing database. Avoid concurrent sessions competing for a target. Focused software fixtures were checked; real SWD, serial RX/TX, live FreeRTOS and real PostHog ingestion were not exercised.

## v1.2.1 local fixes

- CMake-only workspaces now activate the extension; automatic workbench opening still requires STM32 evidence.
- Operations reuse valid, explicitly selected tool paths; installation listing still performs discovery, and missing selections fall back to detection.
- Bounded parallel validation of bundled script caches shares ancestor checks within each validation pass while retaining per-file hashes, repair, link rejection and extra-file cleanup. Tool listings reuse the discovery result within a request.
- Backend cancellation acknowledgements and requests cancelled before startup retain the cancellation flag.

Local installer: `artifacts/vsix/stm32-project-helper-1.2.1-win32-x64.vsix`. This patch has not been uploaded to GitHub.

## v1.2.0 features

- **Build and flash controls:** Project Setup shows the configuration, build directory, actual firmware, progress and results. Task changes are confirmed before building. Building works without a probe or OpenOCD; flashing requires native confirmation and an explicit target when several ELF files exist.
- **Probe settings:** Choose DAPLink/CMSIS-DAP, ST-Link or J-Link. Advanced settings provide SWD speed, default reset, connect under reset and SYSRESETREQ. Actual support depends on the probe, OpenOCD and chip. Configuration does not automatically check hardware connectivity.
- **First setup wizard:** Review the project, tools, chip/probe and task changes in four steps. Continue manually or reopen the wizard from Project Setup or the Command Palette. Convert MDK projects to CMake first.
- **Custom configurations:** Suggestions come from presets, the build cache and matching File API metadata; safe custom names can also be entered. Fixed preset configurations are respected. MDK conversion still generates Debug/Release presets by default.
- **Error explanations:** GCC, CMake, Ninja and OpenOCD diagnostics retain raw messages and offer targeted suggestions. Connection issues that logs cannot determine require inspection of the actual device.
- **Static Flash/RAM analysis:** Uses the selected ELF's allocated sections, load images and linker regions to show occupancy, capacity, remaining space and section distribution. Unknown or complex layouts stay unknown. ELF file length is not memory usage, and reserved heap/stack is not a runtime measurement.
- **Large projects and editing:** Read-only inspection and previews use saved files without requiring source saves. Writes and builds retain dirty-file checks. Virtual source lists preserve search, selection and scroll. Comparisons are selected per file and released when closed. Webview messages omit file snapshots and raw build logs; history uses metadata only.
- **Environment and cleanup:** Automatic opening requires STM32 evidence and supports nested project selection. Missing saved tool paths are rediscovered. Cancellation, timeouts and extension closure use process-tree termination and Windows jobs to clean up child processes.

Building the extension and inspecting its UI does not validate hardware flashing, tool installation or every project environment. Confirm operation using your own project and device.

## Release integration status

All v1.2.0 features listed above are integrated into the published VSIX: Webview controls, backend requests, task generation and Chinese/English messages. The original six pages remain available. Desktop EXEs are not republished by this extension release.

This release does not add clangd setup, Cortex-Debug integration, serial monitoring, HardFault or live FreeRTOS debugging, hardware connectivity detection, recent-project memory or telemetry. Local Windows x64 only; WSL, Remote SSH and virtual workspaces are unsupported. SWD speeds accept 1–50000 kHz but depend on hardware. Unsupported reset/transport combinations are rejected. Unknown memory layouts remain unknown; static analysis does not measure runtime stack usage.

Packaging and Chinese/English UI viewing were completed before delivery. Functional regression and hardware tests were not run for this batch; no additional checks were performed for this documentation/publication update. Integrated functionality must not be read as validated across all projects or devices.

## Download and requirements

| Item | Requirement |
| --- | --- |
| Extension version | 1.2.0 |
| Operating system | Local Windows x64 |
| Editor | VS Code 1.95 or newer |
| Workspace | A trusted local folder |
| .NET | Included in the VSIX; no separate runtime installation |
| Build tools | CMake, Ninja and ARM GCC; OpenOCD for script checks and flash tasks |

Download the [v1.2.0 VSIX](https://github.com/MYFSillyBa/auto-CMake/releases/download/vscode-v1.2.0/stm32-project-helper-1.2.0-win32-x64.vsix) (about 8.6 MB) from the [release page](https://github.com/MYFSillyBa/auto-CMake/releases/tag/vscode-v1.2.0). The local build output is `artifacts/vsix/stm32-project-helper-1.2.0-win32-x64.vsix`. The package includes a trimmed, self-contained backend and the complete bundled OpenOCD scripts.

## Installation and updating

1. Download `stm32-project-helper-1.2.0-win32-x64.vsix`.
2. Press `Ctrl+Shift+X` in VS Code to open Extensions.
3. Open the Extensions panel's `…` menu and select **Install from VSIX…**.
4. Select the downloaded file and reload the window. If no prompt appears, run **Developer: Reload Window** from the Command Palette or restart VS Code.
5. Open and trust a local project folder containing `CMakeLists.txt`, `.ioc`, or an MDK `.uvprojx` file.

The workbench opens automatically by default. You can also click the blue chip icon in the Activity Bar or run **STM32 Project Helper: Open**.

To update, install the newer VSIX through the same menu and reload. Uninstalling the previous version is unnecessary. The identifier remains `myfsillyba.stm32-project-helper`; existing workspace tool settings and change records are retained.

The desktop application and VS Code extension have independent version numbers and releases. Desktop downloads are `.exe` files; extension downloads are `.vsix` files.

## Language

By default, the extension follows the VS Code display language: Chinese locales use Chinese, and other locales use English. The language selector at the top of the workbench offers **Follow VS Code / 中文 / English**. The same preference is available as `stm32ProjectHelper.language`.

Both languages cover all six pages, navigation, buttons, native confirmation dialogs, progress, software-generated inspection and compatibility messages, build results, and problem suggestions. Switching preserves form values and file selections and does not write project files. Language changes are deferred until an active operation finishes.

Newly written managed tasks use the selected language: **One-click Build / One-click Flash (DAPLINK)** in English and **一键编译 / 一键烧录(DAPLINK)** in Chinese. User tasks and comments are preserved. Switching the interface language alone does not rename existing tasks.

Project paths, chip and target names, user code, and original compiler output remain unchanged. Static Command Palette and extension settings titles follow the VS Code display language.

## Workflows

### Project Setup

1. Check the current project, chip evidence, configure/build presets, build directory, OpenOCD target script and tool versions.
2. If multiple installations are detected, choose **Select version…** and select by version and full path, or browse to the executable. Validated paths are saved in the current workspace.
3. Click **Confirm write…**. Fresh changes and affected files are listed in a native confirmation dialog. Confirm to save tasks and the required CMake helper file.
4. Run the software check to configure and compile the project and parse OpenOCD scripts. Results appear in Problems and the **STM32 Project Helper** output channel.
5. After a successful build, select an ELF from the current build targets and confirm the flash task binding.

Change previews and native file diffs are optional. If tasks are missing when a software check starts, the extension requests confirmation and writes them before building. Written tasks remain available if compilation does not pass.

Use the build, flash and Flash/RAM controls at the top of Project Setup; build and flash commands are also available in the Command Palette. Direct flashing builds first, selects an actual ELF and requests native confirmation. **Terminal → Run Task** remains available; managed task names follow the selected language and probe. Software checks do not connect hardware.

### Add Sources

Choose a folder inside the project and a CMake target. Scan, search and select individual C/C++, assembly and header files. Unselected files are excluded. Confirm the write to update source files and include directories in `CMakeLists.txt`. Return to Project Setup for a software check when needed.

### Import Files

Enter the project subfolder name, select local files, preview the copy plan and confirm copying. Existing destination folders require confirmation before appending. Same-name files receive a new name, and originals are retained. Register copied files for compilation through Add Sources.

### Project Conversion

Conversion supports single-core STM32CubeMX projects that retain `.ioc` metadata and generated sources. It does not install or invoke CubeMX.

- **MDK-ARM → CMake**: inspect the project, select the `.uvprojx` file and build target, review compatibility information, then confirm the write.
- **CMake → MDK-ARM**: confirm running CMake configuration to read actual targets, select a target and confirm the write. Inspection creates cache and build files in the build directory.

Blocking issues provide completion instructions. Generation and compilation are separate steps. Automatic startup and memory-layout conversion supports reliably parsed standard structures using one FLASH and one RAM region. Complex memory layouts, dual-core projects, unmatched libraries and unverified FreeRTOS ports require manual completion. See [support details](https://github.com/MYFSillyBa/auto-CMake#支持范围).

### Tool Management

Select local tools, repair missing tools, import offline ZIP packages and activate installed versions. Downloads begin only after an explicit confirmation. Multiple existing installations require a version selection before repair. Bundled OpenOCD script paths are displayed as read-only information.

Tool selection and repair results are saved to the current workspace. They preserve already selected valid tools and do not overwrite the desktop application's global tool paths.

### Change History

Select a record, preview restoration and confirm. Files changed after the recorded operation are listed as conflicts and require a separate overwrite confirmation. Restoration creates another record.

## Project protection

- Existing tasks, JSONC comments and original toolchain files are preserved. Only helper-managed tasks are updated.
- ELF files come from executable targets in the current build configuration, not obsolete files selected by modification time or folder-name guesses.
- Selected CMake, Ninja and ARM GCC use absolute paths. Stale caches from tool changes or project relocation are backed up before reconfiguration.
- Fixed preset configurations are respected; otherwise discovered or manually entered custom configurations are accepted.
- Writes, builds and other operations requiring disk content check unsaved workspace documents and request **Save and continue**. Read-only inspection and previews do not require saving. Import also checks explicitly selected external documents. Cancelling retains edits.
- Multiple workspaces can be selected from the sidebar. Opening another workspace's source file follows that project; automatic switching pauses during operations.
- Long operations support cancellation and display progress and output. No `.ps1` files are generated.

## Settings

| Setting | Purpose |
| --- | --- |
| `stm32ProjectHelper.autoOpen` | Open after project detection; enabled by default |
| `stm32ProjectHelper.language` | `auto`, `zh-CN` or `en`; global interface preference |
| `stm32ProjectHelper.tools.cMake` | CMake executable; empty enables detection |
| `stm32ProjectHelper.tools.ninja` | Ninja executable |
| `stm32ProjectHelper.tools.compiler` | `arm-none-eabi-gcc` executable |
| `stm32ProjectHelper.tools.openOcd` | OpenOCD executable |

## Common questions

**The workbench is not visible.** Use a local Windows x64 window, trust the workspace and open a supported project. Run **STM32 Project Helper: Open**. Automatic opening can be adjusted in settings.

**A tool needs selection.** Multiple installations were found. Select a version explicitly; downloading it again is unnecessary.

**The ELF name differs from the folder.** CMake targets and properties such as `OUTPUT_NAME` determine the filename. The flash task uses the actual firmware.

**Compilation did not pass after writing tasks.** Tasks remain available. Use Problems or the output channel to locate the issue, fix the project and rerun the check or build task.

**A target script is available.** This confirms software configuration only. Probe and chip connectivity are not checked by the extension.

## Building and checking

Requirements: Windows x64, .NET 9 SDK and Node.js 22 or newer. From the repository root:

```powershell
cd src/STM32ProjectHelper.VSCode
npm ci
npm run check
npm run package
```

VSIX output is written to `artifacts/vsix`. Packaging builds the backend and extension, not desktop EXEs. Generated artifacts, dependencies and backend binaries are excluded from Git.

With CMake, Ninja, ARM GCC and OpenOCD installed locally:

```powershell
npm test
npm run smoke
```

Tests use temporary projects for real ARM compilation, tasks, tool selection, file management, restoration and conversion. The smoke check uses an isolated ordinary VS Code window and confirms dialogs belonging to that window. Set `VSCODE_EXECUTABLE_PATH` to override the installed VS Code path. Checks do not connect hardware.

## Feedback and license

Use [GitHub Issues](https://github.com/MYFSillyBa/auto-CMake/issues) and include extension, VS Code and Windows versions, steps and output. For conversion, include the direction, chip and target.

Source is distributed under the [MIT License](https://github.com/MYFSillyBa/auto-CMake/blob/main/LICENSE). Bundled OpenOCD scripts retain original copyright and license notices. Third-party tools follow their respective licenses.
