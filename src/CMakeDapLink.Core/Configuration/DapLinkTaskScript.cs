using System.Text;

namespace CMakeDapLink.Core;

internal static class DapLinkTaskScript
{
    private const string Marker = "# STM32 Project Helper managed task runner v1\n";

    public static string AvailablePath(string root, bool buildOnly = false)
    {
        var directory = Path.Combine(root, ".vscode");
        for (var suffix = 1; ; suffix++)
        {
            var stem = buildOnly ? "stm32-build" : "stm32-daplink";
            var path = Path.Combine(directory, suffix == 1 ? stem + ".cmake" : $"{stem}-{suffix}.cmake");
            if (!File.Exists(path) || File.ReadAllText(path).Replace("\r\n", "\n").StartsWith(Marker, StringComparison.Ordinal)) return path;
        }
    }

    private static string Quote(string text)
    {
        var delimiter = "=";
        while (text.Contains("]" + delimiter + "]", StringComparison.Ordinal)) delimiter += "=";
        return "[" + delimiter + "[" + text.Replace('\\', '/') + "]" + delimiter + "]";
    }

    private static string Message(SetupOptions options, string english, string chinese) => options.TaskLanguage == "en" ? english : chinese;

    public static byte[] Create(SetupOptions options, CMakeBuildPlan plan, bool buildOnly = false)
    {
        var probe = buildOnly ? null : OpenOcdProbe.Resolve(options);
        var content = Marker + $$"""
cmake_minimum_required(VERSION 3.22)
get_filename_component(_root "${CMAKE_CURRENT_LIST_DIR}/.." ABSOLUTE)
set(_build {{Quote(plan.BuildDirectory)}})
set(_configuration {{Quote(plan.BuildConfiguration)}})
if(ACTION STREQUAL "configure")
  {{plan.ToolSelection?.PreparationScript()}}
  set(_query "${_build}/.cmake/api/v1/query/client-cmake-daplink")
  file(MAKE_DIRECTORY "${_query}")
  file(WRITE "${_query}/codemodel-v2" "")
  execute_process(COMMAND {{Quote(plan.Configure.Executable)}} {{string.Join(" ", plan.Configure.Arguments.Select(Quote))}}
    WORKING_DIRECTORY "${_root}" RESULT_VARIABLE _status COMMAND_ECHO STDOUT)
  if(NOT _status STREQUAL "0")
    message(FATAL_ERROR "{{Message(options, "CMake configure failed: ${_status}. Tasks remain available; correct the project and run the build task again.", "CMake 配置失败：${_status}。任务仍可使用；请修正工程后再次运行编译任务。")}}")
  endif()
  return()
endif()
if(NOT ACTION STREQUAL "flash" AND NOT ACTION STREQUAL "resolve")
  message(FATAL_ERROR "{{Message(options, "Unknown task action: ${ACTION}", "未知任务操作：${ACTION}")}}")
endif()

function(_read_reply _reference _output)
  string(JSON _name GET "${_reference}" jsonFile)
  get_filename_component(_leaf "${_name}" NAME)
  if(NOT _name STREQUAL _leaf OR _name MATCHES "[/\\\\]")
    message(FATAL_ERROR "{{Message(options, "Invalid CMake File API reference.", "CMake File API 引用无效。")}}")
  endif()
  file(READ "${_reply}/${_name}" _content)
  set(${_output} "${_content}" PARENT_SCOPE)
endfunction()

set(_reply "${_build}/.cmake/api/v1/reply")
file(GLOB _indices "${_reply}/index-*.json")
if(NOT _indices)
  message(FATAL_ERROR "{{Message(options, "No CMake File API data. Run the generated build task first.", "没有 CMake File API 数据，请先运行生成的编译任务。")}}")
endif()
list(SORT _indices ORDER DESCENDING)
list(GET _indices 0 _index_file)
file(READ "${_index_file}" _index)
string(JSON _object_count LENGTH "${_index}" objects)
set(_model "")
if(_object_count GREATER 0)
  math(EXPR _last "${_object_count}-1")
  foreach(_i RANGE 0 ${_last})
    string(JSON _reference GET "${_index}" objects ${_i})
    string(JSON _kind GET "${_reference}" kind)
    if(_kind STREQUAL "codemodel")
      string(JSON _major GET "${_reference}" version major)
      if(_major EQUAL 2)
        _read_reply("${_reference}" _model)
        break()
      endif()
    endif()
  endforeach()
endif()
if(NOT _model)
  message(FATAL_ERROR "{{Message(options, "Missing codemodel-v2. Run the generated build task again.", "缺少 codemodel-v2，请再次运行生成的编译任务。")}}")
endif()
string(JSON _model_source GET "${_model}" paths source)
string(JSON _model_build GET "${_model}" paths build)
foreach(_pair IN ITEMS source build)
  if(_pair STREQUAL "source")
    set(_expected "${_root}")
    set(_actual "${_model_source}")
  else()
    set(_expected "${_build}")
    set(_actual "${_model_build}")
  endif()
  cmake_path(SET _actual NORMALIZE "${_actual}")
  cmake_path(SET _expected NORMALIZE "${_expected}")
  string(TOLOWER "${_actual}" _actual)
  string(TOLOWER "${_expected}" _expected)
  string(REGEX REPLACE "/+$" "" _actual "${_actual}")
  string(REGEX REPLACE "/+$" "" _expected "${_expected}")
  if(NOT _actual STREQUAL _expected)
    message(FATAL_ERROR "{{Message(options, "CMake File API ${_pair} directory does not match this project. Reconfigure before flashing.", "CMake File API ${_pair} 目录与当前工程不一致，请先重新配置再烧录。")}}")
  endif()
endforeach()

string(JSON _config_count LENGTH "${_model}" configurations)
set(_selected "")
set(_wanted "${_configuration}")
if(_config_count GREATER 0)
  math(EXPR _last "${_config_count}-1")
  foreach(_i RANGE 0 ${_last})
    string(JSON _config GET "${_model}" configurations ${_i})
    string(JSON _name GET "${_config}" name)
    if(_name STREQUAL _wanted OR (_config_count EQUAL 1 AND _name STREQUAL ""))
      set(_selected "${_config}")
      break()
    endif()
  endforeach()
endif()
if(NOT _selected)
  message(FATAL_ERROR "{{Message(options, "No File API configuration ${_configuration}. Configure and build the selected configuration.", "没有 File API 配置 ${_configuration}，请配置并编译所选配置。")}}")
endif()
string(JSON _target_count LENGTH "${_selected}" targets)
set(_elfs "")
if(_target_count GREATER 0)
  math(EXPR _last "${_target_count}-1")
  foreach(_i RANGE 0 ${_last})
    string(JSON _reference GET "${_selected}" targets ${_i})
    _read_reply("${_reference}" _target)
    string(JSON _type GET "${_target}" type)
    if(NOT _type STREQUAL "EXECUTABLE")
      continue()
    endif()
    string(JSON _artifact_count ERROR_VARIABLE _error LENGTH "${_target}" artifacts)
    if(_error OR _artifact_count EQUAL 0)
      continue()
    endif()
    math(EXPR _artifact_last "${_artifact_count}-1")
    foreach(_a RANGE 0 ${_artifact_last})
      string(JSON _path GET "${_target}" artifacts ${_a} path)
      get_filename_component(_elf "${_path}" ABSOLUTE BASE_DIR "${_build}")
      get_filename_component(_extension "${_elf}" LAST_EXT)
      string(TOLOWER "${_extension}" _extension)
      file(RELATIVE_PATH _relative "${_build}" "${_elf}")
      if(_extension STREQUAL ".elf" AND EXISTS "${_elf}" AND NOT IS_DIRECTORY "${_elf}" AND NOT _relative MATCHES "^\\.\\.(/|$)" AND NOT IS_ABSOLUTE "${_relative}")
        string(REPLACE ";" "\\;" _list_elf "${_elf}")
        list(APPEND _elfs "${_list_elf}")
      endif()
    endforeach()
  endforeach()
endif()
list(REMOVE_DUPLICATES _elfs)
if(DEFINED FIRMWARE_ELF AND NOT FIRMWARE_ELF STREQUAL "")
  get_filename_component(_chosen "${FIRMWARE_ELF}" ABSOLUTE BASE_DIR "${_build}")
  string(TOLOWER "${_chosen}" _wanted_path)
  set(_found FALSE)
  foreach(_elf IN LISTS _elfs)
    string(TOLOWER "${_elf}" _path)
    if(_path STREQUAL _wanted_path)
      set(_chosen "${_elf}")
      set(_found TRUE)
      break()
    endif()
  endforeach()
  if(NOT _found)
    message(FATAL_ERROR "{{Message(options, "Selected firmware is not a current target artifact. Use the app to select it again.", "所选固件不是当前目标产物，请在应用中重新选择。")}}")
  endif()
else()
  list(LENGTH _elfs _count)
  if(NOT _count EQUAL 1)
    message(FATAL_ERROR "{{Message(options, "Found ${_count} current ELF artifacts. Use the app to select the firmware; no filename is guessed.", "找到 ${_count} 个当前 ELF 产物，请在应用中明确选择固件；不会猜测文件名。")}}")
  endif()
  list(GET _elfs 0 _chosen)
endif()
message(STATUS "FIRMWARE_ELF=${_chosen}")
if(ACTION STREQUAL "resolve")
  return()
endif()
# Escape Tcl double-quoted strings; hardware is touched only by ACTION=flash.
string(REPLACE "\\" "/" _tcl "${_chosen}")
foreach(_char IN ITEMS "$" "[" "]" "\"")
  string(REPLACE "${_char}" "\\${_char}" _tcl "${_tcl}")
endforeach()
set(_program "program \"${_tcl}\" verify reset exit")
execute_process(COMMAND {{Quote(options.OpenOcd)}} {{string.Join(" ", probe?.Arguments(options).Select(Quote) ?? [])}} -c "${_program}"
  WORKING_DIRECTORY "${_root}" RESULT_VARIABLE _status COMMAND_ECHO STDOUT)
if(NOT _status STREQUAL "0")
  message(FATAL_ERROR "{{Message(options, "OpenOCD flash failed: ${_status}", "OpenOCD 烧录失败：${_status}")}}")
endif()

""";
        if (buildOnly)
            content = content[..content.IndexOf("if(NOT ACTION STREQUAL \"flash\"", StringComparison.Ordinal)] +
                "message(FATAL_ERROR \"" + Message(options, "This runner supports configure only.", "此任务运行器仅支持配置操作。") + "\")\n";
        return new UTF8Encoding(false).GetBytes(content);
    }
}
