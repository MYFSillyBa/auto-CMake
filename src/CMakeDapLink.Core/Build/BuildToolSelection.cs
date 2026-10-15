using System.Diagnostics;
using System.Text;

namespace CMakeDapLink.Core;

// One preparation script is shared by desktop builds and generated VS Code tasks.
// Moving the two known CMake cache entries preserves user output and works on CMake 3.22.
internal sealed class BuildToolSelection
{
    private const string Owner = "STM32 Project Helper tool selection v1";
    private readonly SetupOptions options;
    private readonly string buildDirectory;
    private readonly string stateDirectory;
    public string ToolchainPath => Path.Combine(stateDirectory, "toolchain.cmake");

    public BuildToolSelection(SetupOptions options, string buildDirectory)
    {
        this.options = options;
        this.buildDirectory = Path.GetFullPath(buildDirectory, options.Root);
        for (var suffix = 1; ; suffix++)
        {
            var candidate = Path.Combine(this.buildDirectory, suffix == 1 ? ".stm32-tools" : $".stm32-tools-{suffix}");
            var marker = Path.Combine(candidate, "owner.txt");
            if (!Directory.Exists(candidate) && !File.Exists(candidate) ||
                File.Exists(marker) && File.ReadAllText(marker) == Owner)
            {
                ToolVersionManager.EnsureOrdinaryPath(candidate);
                stateDirectory = candidate; break;
            }
        }
    }

    internal static string Quote(string text) => QuoteVerbatim(text.Replace('\\', '/'));

    private static string QuoteVerbatim(string text)
    {
        var delimiter = "=";
        while (text.Contains("]" + delimiter + "]", StringComparison.Ordinal)) delimiter += "=";
        return "[" + delimiter + "[" + text + "]" + delimiter + "]";
    }

    private string Compiler => Path.GetFullPath(options.Compiler, options.Root);
    private string CompilerDirectory => Path.GetDirectoryName(Compiler)!;
    private string ToolchainContent()
    {
        var text = new StringBuilder("# " + Owner + "\n");
        // Seed conventional find_program variables before reading the original chain.
        // Keep CPU, ABI, memory layout and other project-specific options in that file.
        var conventional = new Dictionary<string, string>
        {
            ["ARM_GCC"] = Compiler, ["ARM_GXX"] = Path.Combine(CompilerDirectory, "arm-none-eabi-g++.exe"),
            ["ARM_OBJCOPY"] = Path.Combine(CompilerDirectory, "arm-none-eabi-objcopy.exe"),
            ["ARM_SIZE"] = Path.Combine(CompilerDirectory, "arm-none-eabi-size.exe")
        };
        foreach (var pair in conventional) SetPath(text, pair.Key, pair.Value);
        if (options.ToolchainFile != null)
        {
            var original = Path.GetFullPath(options.ToolchainFile, options.Root);
            text.AppendLine($"include({Quote(original)})");
        }
        else
        {
            text.AppendLine("if(NOT CMAKE_SYSTEM_NAME)\n  set(CMAKE_SYSTEM_NAME Generic)\nendif()");
            text.AppendLine("if(NOT CMAKE_TRY_COMPILE_TARGET_TYPE)\n  set(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)\nendif()");
        }
        SetPath(text, "CMAKE_C_COMPILER", Compiler);
        SetPath(text, "CMAKE_CXX_COMPILER", Path.Combine(CompilerDirectory, "arm-none-eabi-g++.exe"));
        SetPath(text, "CMAKE_ASM_COMPILER", Compiler);
        foreach (var (variable, executable) in new[]
        {
            ("CMAKE_AR", "ar"), ("CMAKE_RANLIB", "ranlib"), ("CMAKE_OBJCOPY", "objcopy"),
            ("CMAKE_OBJDUMP", "objdump"), ("CMAKE_SIZE", "size"), ("CMAKE_NM", "nm"), ("CMAKE_STRIP", "strip"),
            ("CMAKE_C_COMPILER_AR", "gcc-ar"), ("CMAKE_CXX_COMPILER_AR", "gcc-ar"),
            ("CMAKE_C_COMPILER_RANLIB", "gcc-ranlib"), ("CMAKE_CXX_COMPILER_RANLIB", "gcc-ranlib")
        })
        {
            var path = Path.Combine(CompilerDirectory, "arm-none-eabi-" + executable + ".exe");
            text.AppendLine($"if(EXISTS {Quote(path)})");
            SetPath(text, variable, path);
            text.AppendLine("endif()");
        }
        // Some toolchains explicitly use GCC as the linker. Retain that choice.
        text.AppendLine("get_filename_component(_stm32_linker_name \"${CMAKE_LINKER}\" NAME)");
        text.AppendLine("if(_stm32_linker_name MATCHES \"^arm-none-eabi-g[+][+]([.]exe)?$\")");
        SetPath(text, "CMAKE_LINKER", Path.Combine(CompilerDirectory, "arm-none-eabi-g++.exe"));
        text.AppendLine("elseif(_stm32_linker_name MATCHES \"^arm-none-eabi-gcc([.]exe)?$\")");
        SetPath(text, "CMAKE_LINKER", Compiler);
        text.AppendLine("else()");
        SetPath(text, "CMAKE_LINKER", Path.Combine(CompilerDirectory, "arm-none-eabi-ld.exe"));
        text.AppendLine("endif()");
        return text.ToString();
    }

    private static void SetPath(StringBuilder text, string variable, string path)
    {
        text.AppendLine($"set({variable} {Quote(path)} CACHE FILEPATH \"Selected by STM32 Project Helper\" FORCE)");
        text.AppendLine($"set({variable} {Quote(path)})");
    }

    public string PreparationScript()
    {
        var signature = string.Join("\n", new[] { options.CMake, options.Ninja, Compiler, options.ToolchainFile ?? "" }.Select(x => x.Replace('\\', '/')));
        return $$$"""
set(_stm32_build {{{Quote(buildDirectory)}}})
set(_stm32_state {{{Quote(stateDirectory)}}})
set(_stm32_owner {{{Quote(Owner)}}})
set(_stm32_signature {{{Quote(signature)}}})
set(_stm32_chain {{{Quote(ToolchainPath)}}})
if(IS_SYMLINK "${_stm32_state}")
  message(FATAL_ERROR "Tool selection directory is a symbolic link: ${_stm32_state}")
endif()
if(EXISTS "${_stm32_state}")
  if(NOT EXISTS "${_stm32_state}/owner.txt")
    message(FATAL_ERROR "Tool selection directory is occupied. Regenerate tasks in the app.")
  endif()
  file(READ "${_stm32_state}/owner.txt" _stm32_existing_owner)
  if(NOT _stm32_existing_owner STREQUAL _stm32_owner)
    message(FATAL_ERROR "Tool selection directory belongs to another file. Regenerate tasks in the app.")
  endif()
endif()
set(_stm32_reset FALSE)
if(EXISTS "${_stm32_build}/CMakeCache.txt")
  file(STRINGS "${_stm32_build}/CMakeCache.txt" _stm32_cache_lines)
  foreach(_stm32_line IN LISTS _stm32_cache_lines)
    if(_stm32_line MATCHES "^(CMAKE_COMMAND|CMAKE_MAKE_PROGRAM|CMAKE_C_COMPILER|CMAKE_CXX_COMPILER|CMAKE_ASM_COMPILER|CMAKE_TOOLCHAIN_FILE):[^=]*=(.*)$")
      set(_stm32_cached_${CMAKE_MATCH_1} "${CMAKE_MATCH_2}")
    endif()
  endforeach()
  foreach(_stm32_pair IN ITEMS
      {{{Quote("CMAKE_COMMAND|" + Path.GetFullPath(options.CMake, options.Root))}}}
      {{{Quote("CMAKE_MAKE_PROGRAM|" + Path.GetFullPath(options.Ninja, options.Root))}}}
      {{{Quote("CMAKE_C_COMPILER|" + Compiler)}}}
      {{{Quote("CMAKE_CXX_COMPILER|" + Path.Combine(CompilerDirectory, "arm-none-eabi-g++.exe"))}}}
      {{{Quote("CMAKE_ASM_COMPILER|" + Compiler)}}}
      {{{Quote("CMAKE_TOOLCHAIN_FILE|" + ToolchainPath)}}})
    string(FIND "${_stm32_pair}" "|" _stm32_split)
    string(SUBSTRING "${_stm32_pair}" 0 ${_stm32_split} _stm32_key)
    math(EXPR _stm32_split "${_stm32_split}+1")
    string(SUBSTRING "${_stm32_pair}" ${_stm32_split} -1 _stm32_expected)
    set(_stm32_actual "${_stm32_cached_${_stm32_key}}")
    if((_stm32_key STREQUAL "CMAKE_CXX_COMPILER" OR _stm32_key STREQUAL "CMAKE_ASM_COMPILER") AND _stm32_actual STREQUAL "")
      continue()
    endif()
    string(REPLACE "\\" "/" _stm32_actual "${_stm32_actual}")
    string(TOLOWER "${_stm32_actual}" _stm32_actual)
    string(TOLOWER "${_stm32_expected}" _stm32_expected)
    if(NOT _stm32_actual STREQUAL _stm32_expected)
      set(_stm32_reset TRUE)
    endif()
  endforeach()
  if(EXISTS "${_stm32_state}/selection.txt")
    file(READ "${_stm32_state}/selection.txt" _stm32_previous)
    if(NOT _stm32_previous STREQUAL _stm32_signature)
      set(_stm32_reset TRUE)
    endif()
  else()
    set(_stm32_reset TRUE)
  endif()
elseif(EXISTS "${_stm32_build}/CMakeFiles")
  set(_stm32_reset TRUE)
endif()
file(MAKE_DIRECTORY "${_stm32_state}")
foreach(_stm32_file IN ITEMS owner.txt selection.txt toolchain.cmake cache-backups)
  if(IS_SYMLINK "${_stm32_state}/${_stm32_file}")
    message(FATAL_ERROR "Tool selection file is a symbolic link: ${_stm32_file}")
  endif()
endforeach()
file(WRITE "${_stm32_state}/owner.txt" "${_stm32_owner}")
if(_stm32_reset)
  string(TIMESTAMP _stm32_time "%Y%m%d-%H%M%S")
  string(RANDOM LENGTH 8 ALPHABET 0123456789abcdef _stm32_nonce)
  set(_stm32_backup "${_stm32_state}/cache-backups/${_stm32_time}-${_stm32_nonce}")
  file(MAKE_DIRECTORY "${_stm32_backup}")
  foreach(_stm32_entry IN ITEMS CMakeCache.txt CMakeFiles)
    if(EXISTS "${_stm32_build}/${_stm32_entry}" OR IS_SYMLINK "${_stm32_build}/${_stm32_entry}")
      file(RENAME "${_stm32_build}/${_stm32_entry}" "${_stm32_backup}/${_stm32_entry}" RESULT _stm32_move_error)
      if(NOT _stm32_move_error STREQUAL "0")
        message(FATAL_ERROR "Cannot back up ${_stm32_entry}: ${_stm32_move_error}")
      endif()
    endif()
  endforeach()
  message(STATUS "Tool paths changed; old CMake cache backed up to ${_stm32_backup}")
endif()
set(_stm32_chain_content {{{QuoteVerbatim(ToolchainContent())}}})
if(EXISTS "${_stm32_chain}")
  file(READ "${_stm32_chain}" _stm32_old_chain)
else()
  set(_stm32_old_chain "")
endif()
if(NOT _stm32_old_chain STREQUAL _stm32_chain_content)
  file(WRITE "${_stm32_chain}" "${_stm32_chain_content}")
endif()
file(WRITE "${_stm32_state}/selection.txt" "${_stm32_signature}")

""";
    }

    public ToolCommand PrepareCommand(ToolCommand configure)
    {
        ToolVersionManager.EnsureOrdinaryPath(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        var marker = Path.Combine(stateDirectory, "owner.txt");
        ToolVersionManager.EnsureOrdinaryPath(marker);
        if (File.Exists(marker) && File.ReadAllText(marker) != Owner)
            throw new IOException("工具选择目录已被其他文件占用，请重新生成配置。");
        File.WriteAllText(marker, Owner);
        var script = Path.Combine(stateDirectory, "prepare.cmake");
        ToolVersionManager.EnsureOrdinaryPath(script);
        File.WriteAllText(script, PreparationScript(), new UTF8Encoding(false));
        return configure with { Arguments = ["-P", script] };
    }

    public void Prepare(ToolCommand configure, Action<string>? report)
    {
        var command = PrepareCommand(configure);
        var info = new ProcessStartInfo(command.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = configure.WorkingDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("无法准备 CMake 工具选择。");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new TimeoutException("准备 CMake 工具选择超时。"); }
        var output = (stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()).Trim();
        if (output.Length > 0) report?.Invoke(output);
        if (process.ExitCode != 0) throw new IOException("准备 CMake 工具选择失败：" + output);
    }
}
