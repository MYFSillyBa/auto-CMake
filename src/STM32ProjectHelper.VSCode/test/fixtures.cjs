'use strict';
const fs = require('node:fs');
const path = require('node:path');

function createProject(root) {
  fs.mkdirSync(path.join(root, 'cmake'), { recursive: true });
  fs.mkdirSync(path.join(root, 'module', 'include'), { recursive: true });
  fs.mkdirSync(path.join(root, '.vscode'), { recursive: true });
  fs.writeFileSync(path.join(root, 'board.ioc'), 'Mcu.CPN=STM32H723VGT6\nMcu.Name=STM32H723VGTx\n');
  fs.writeFileSync(path.join(root, 'CMakeLists.txt'), `cmake_minimum_required(VERSION 3.22)
project(extension_probe C)
add_executable(extension_probe main.c)
set_target_properties(extension_probe PROPERTIES OUTPUT_NAME current_firmware SUFFIX .elf)
target_compile_options(extension_probe PRIVATE -mcpu=cortex-m7 -mthumb)
target_link_options(extension_probe PRIVATE -mcpu=cortex-m7 -mthumb -nostdlib -Wl,-e,main)
if(EXISTS "\${CMAKE_SOURCE_DIR}/.vscode/tasks.json")
 file(WRITE "\${CMAKE_BINARY_DIR}/tasks-first.txt" "tasks present")
endif()
`);
  fs.writeFileSync(path.join(root, 'main.c'), 'int main(void) { return 0; }\n');
  fs.writeFileSync(path.join(root, 'module', 'motor.c'), 'int motor_value(void) { return 7; }\n');
  fs.writeFileSync(path.join(root, 'module', 'unused.c'), 'int unused_value(void) { return 9; }\n');
  fs.writeFileSync(path.join(root, 'module', 'include', 'motor.h'), '#pragma once\nint motor_value(void);\n');
  fs.writeFileSync(path.join(root, 'cmake', 'gcc-arm-none-eabi.cmake'), `set(CMAKE_SYSTEM_NAME Generic)
set(CMAKE_SYSTEM_PROCESSOR arm)
set(CMAKE_C_COMPILER arm-none-eabi-gcc)
set(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)
`);
  fs.writeFileSync(path.join(root, 'CMakePresets.json'), JSON.stringify({ version: 3,
    configurePresets: [{ name: 'Debug', generator: 'Ninja', binaryDir: '${sourceDir}/build/Debug',
      toolchainFile: '${sourceDir}/cmake/gcc-arm-none-eabi.cmake', cacheVariables: { CMAKE_BUILD_TYPE: 'Debug' } }],
    buildPresets: [{ name: 'Debug', configurePreset: 'Debug' }] }, null, 2));
  fs.writeFileSync(path.join(root, '.vscode', 'tasks.json'), `{
  // User task remains unchanged.
  "version": "2.0.0",
  "tasks": [{ "label": "user-task", "type": "process", "command": "echo", "args": ["hello"] }]
}\n`);
  return { configurePreset: 'Debug', buildPreset: 'Debug', configuration: 'Debug', targetScript: 'target/stm32h7x.cfg' };
}
function createCubeMxProject(root) {
  for (const directory of ['Core/Src', 'Core/Inc', 'MDK-ARM']) fs.mkdirSync(path.join(root, directory), { recursive: true });
  fs.writeFileSync(path.join(root, 'ConversionDemo.ioc'), 'Mcu.CPN=STM32F103RCT6\nMcu.Name=STM32F103RCTx\nProjectManager.ProjectName=ConversionDemo\nProjectManager.FirmwarePackage=STM32Cube FW_F1 V1.8.6\nProjectManager.TargetToolchain=MDK-ARM V5\n');
  fs.writeFileSync(path.join(root, 'Core/Src/main.c'), 'int main(void) { for (;;) {} }\n');
  fs.writeFileSync(path.join(root, 'Core/Src/system_stm32f1xx.c'), 'void SystemInit(void) {}\n');
  fs.writeFileSync(path.join(root, 'MDK-ARM/ConversionDemo.sct'), `LR_IROM1 0x08000000 0x00040000 {
 ER_IROM1 0x08000000 0x00040000 {
  *.o (RESET, +First)
  *(InRoot$$Sections)
  .ANY (+RO)
  .ANY (+XO)
 }
 RW_IRAM1 0x20000000 0x0000C000 {
  .ANY (+RW +ZI)
 }
}
`);
  fs.writeFileSync(path.join(root, 'MDK-ARM/startup_stm32f103xe.s'), `Stack_Size EQU 0x400
 AREA STACK, NOINIT, READWRITE, ALIGN=3
Stack_Mem SPACE Stack_Size
__initial_sp
Heap_Size EQU 0x200
 AREA HEAP, NOINIT, READWRITE, ALIGN=3
__heap_base
Heap_Mem SPACE Heap_Size
__heap_limit
 PRESERVE8
 THUMB
 AREA RESET, DATA, READONLY
 EXPORT __Vectors
 EXPORT __Vectors_End
 EXPORT __Vectors_Size
__Vectors DCD __initial_sp
 DCD Reset_Handler
 DCD NMI_Handler
 DCD HardFault_Handler
 DCD MemManage_Handler
 DCD BusFault_Handler
 DCD UsageFault_Handler
 DCD 0
 DCD 0
 DCD 0
 DCD 0
 DCD SVC_Handler
 DCD DebugMon_Handler
 DCD 0
 DCD PendSV_Handler
 DCD SysTick_Handler
__Vectors_End
__Vectors_Size EQU __Vectors_End - __Vectors
 AREA |.text|, CODE, READONLY
Reset_Handler PROC
 EXPORT Reset_Handler [WEAK]
 IMPORT SystemInit
 IMPORT __main
 LDR R0, =SystemInit
 BLX R0
 LDR R0, =__main
 BX R0
 ENDP
Default_Handler PROC
 EXPORT Default_Handler [WEAK]
 EXPORT NMI_Handler [WEAK]
 EXPORT HardFault_Handler [WEAK]
 EXPORT MemManage_Handler [WEAK]
 EXPORT BusFault_Handler [WEAK]
 EXPORT UsageFault_Handler [WEAK]
 EXPORT SVC_Handler [WEAK]
 EXPORT DebugMon_Handler [WEAK]
 EXPORT PendSV_Handler [WEAK]
 EXPORT SysTick_Handler [WEAK]
NMI_Handler
HardFault_Handler
MemManage_Handler
BusFault_Handler
UsageFault_Handler
SVC_Handler
DebugMon_Handler
PendSV_Handler
SysTick_Handler
 B .
 ENDP
 END
`);
  fs.writeFileSync(path.join(root, 'MDK-ARM/ConversionDemo.uvprojx'), `<?xml version="1.0" encoding="UTF-8"?>
<Project><SchemaVersion>2.1</SchemaVersion><Header>### uVision Project</Header><Targets><Target>
<TargetName>ConversionDemo</TargetName><ToolsetNumber>0x4</ToolsetNumber><ToolsetName>ARM-ADS</ToolsetName><uAC6>1</uAC6>
<TargetOption><TargetCommonOption><Device>STM32F103RC</Device><Vendor>STMicroelectronics</Vendor><Cpu>CPUTYPE("Cortex-M3")</Cpu><OutputName>ConversionDemo</OutputName><CreateExecutable>1</CreateExecutable><CreateLib>0</CreateLib></TargetCommonOption>
<CommonProperty><IncludeInBuild>1</IncludeInBuild></CommonProperty>
<TargetArmAds><ArmAdsMisc><AdsCpuType>"Cortex-M3"</AdsCpuType><RvdsVP>0</RvdsVP></ArmAdsMisc>
<Cads><uC99>1</uC99><VariousControls><Define>USE_HAL_DRIVER,STM32F103xE</Define><IncludePath>../Core/Inc</IncludePath><MiscControls/><Undefine/></VariousControls></Cads>
<Aads><VariousControls><Define/><IncludePath/><MiscControls/><Undefine/></VariousControls></Aads>
<LDads><umfTarg>0</umfTarg><useFile>1</useFile><ScatterFile>ConversionDemo.sct</ScatterFile></LDads></TargetArmAds></TargetOption>
<Groups><Group><GroupName>Application/User/Core</GroupName><Files>
<File><FileName>main.c</FileName><FileType>1</FileType><FilePath>../Core/Src/main.c</FilePath></File>
<File><FileName>system_stm32f1xx.c</FileName><FileType>1</FileType><FilePath>../Core/Src/system_stm32f1xx.c</FilePath></File>
</Files></Group><Group><GroupName>Application/MDK-ARM</GroupName><Files>
<File><FileName>startup_stm32f103xe.s</FileName><FileType>2</FileType><FilePath>startup_stm32f103xe.s</FilePath></File>
</Files></Group></Groups></Target></Targets></Project>
`);
}
module.exports = { createProject, createCubeMxProject };
