namespace CMakeDapLink.TestFixtures;

/// <summary>An authored CubeMX-shaped software fixture; contains no vendor firmware code.</summary>
public static class CubeMxFixture
{
    public const string Target = "ConversionDemo";
    public static void Create(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Core", "Src"));
        Directory.CreateDirectory(Path.Combine(root, "Core", "Inc"));
        Directory.CreateDirectory(Path.Combine(root, "MDK-ARM"));
        File.WriteAllText(Path.Combine(root, "ConversionDemo.ioc"), "Mcu.CPN=STM32F103RCT6\nMcu.Name=STM32F103RCTx\nProjectManager.ProjectName=ConversionDemo\nProjectManager.FirmwarePackage=STM32Cube FW_F1 V1.8.6\nProjectManager.TargetToolchain=MDK-ARM V5\n");
        File.WriteAllText(Path.Combine(root, "Core", "Inc", "main.h"), "#pragma once\n#define FIXTURE_VALUE 7\n");
        File.WriteAllText(Path.Combine(root, "Core", "Src", "main.c"), "#include \"main.h\"\n#ifndef STM32F103xE\n#error Device macro is required\n#endif\n#ifndef USE_HAL_DRIVER\n#error HAL macro is required\n#endif\nextern int cpp_value(void);\nextern int rtti_value(void);\nvolatile int initialized_value = FIXTURE_VALUE;\nvolatile int zero_value;\n__attribute__((section(\".RamFunc\"), noinline)) int ram_value(void) { return initialized_value; }\nint main(void) { zero_value = ram_value() + cpp_value() + rtti_value(); for (;;) {} }\n");
        File.WriteAllText(Path.Combine(root, "Core", "Src", "support.cpp"), "#include \"main.h\"\nextern \"C\" volatile int initialized_value;\nstruct Setup { Setup() { initialized_value = FIXTURE_VALUE; } };\nstatic Setup setup;\nextern \"C\" int cpp_value(void) { static int value = FIXTURE_VALUE; return value; }\n");
        File.WriteAllText(Path.Combine(root, "Core", "Src", "rtti.cpp"), "class Base { public: virtual ~Base() {} };\nclass Derived : public Base {};\nextern \"C\" int rtti_value(void) { Derived item; Base *base = &item; return dynamic_cast<Derived *>(base) ? 7 : 0; }\n");
        File.WriteAllText(Path.Combine(root, "Core", "Src", "system_stm32f1xx.c"), "void SystemInit(void) {}\n");
        File.WriteAllText(Path.Combine(root, "MDK-ARM", "ConversionDemo.sct"), "LR_IROM1 0x08000000 0x00040000 {\n ER_IROM1 0x08000000 0x00040000 {\n  *.o (RESET, +First)\n  *(InRoot$$Sections)\n  .ANY (+RO)\n  .ANY (+XO)\n }\n RW_IRAM1 0x20000000 0x0000C000 {\n  *.o (.RamFunc, .RamFunc*)\n  .ANY (+RW +ZI)\n }\n}\n");
        var handlers = new[] { "NMI_Handler", "HardFault_Handler", "MemManage_Handler", "BusFault_Handler", "UsageFault_Handler", "0", "0", "0", "0", "SVC_Handler", "DebugMon_Handler", "0", "PendSV_Handler", "SysTick_Handler", "WWDG_IRQHandler", "PVD_IRQHandler", "TAMPER_IRQHandler", "RTC_IRQHandler", "FLASH_IRQHandler", "RCC_IRQHandler", "EXTI0_IRQHandler" };
        var assembly = "Stack_Size EQU 0x400\n AREA STACK, NOINIT, READWRITE, ALIGN=3\nStack_Mem SPACE Stack_Size\n__initial_sp\nHeap_Size EQU 0x200\n AREA HEAP, NOINIT, READWRITE, ALIGN=3\n__heap_base\nHeap_Mem SPACE Heap_Size\n__heap_limit\n PRESERVE8\n THUMB\n AREA RESET, DATA, READONLY\n EXPORT __Vectors\n EXPORT __Vectors_End\n EXPORT __Vectors_Size\n__Vectors DCD __initial_sp\n DCD Reset_Handler\n" +
            string.Join("\n", handlers.Select(x => " DCD " + x)) + "\n__Vectors_End\n__Vectors_Size EQU __Vectors_End - __Vectors\n AREA |.text|, CODE, READONLY\nReset_Handler PROC\n EXPORT Reset_Handler [WEAK]\n IMPORT SystemInit\n IMPORT __main\n LDR R0, =SystemInit\n BLX R0\n LDR R0, =__main\n BX R0\n ENDP\nHardFault_Handler\\\n PROC\n EXPORT HardFault_Handler [WEAK]\n B .\n ENDP\nDefault_Handler PROC\n" +
            string.Join("\n", handlers.Where(x => x != "0" && x != "HardFault_Handler").Select(x => " EXPORT " + x + " [WEAK]")) + "\n" + string.Join("\n", handlers.Where(x => x != "0" && x != "HardFault_Handler")) + "\n B .\n ENDP\n END\n";
        File.WriteAllText(Path.Combine(root, "MDK-ARM", "startup_stm32f103xe.s"), assembly);
        File.WriteAllText(Path.Combine(root, "MDK-ARM", "ConversionDemo.uvprojx"), """
<?xml version="1.0" encoding="UTF-8"?>
<Project><SchemaVersion>2.1</SchemaVersion><Header>### uVision Project</Header><Targets><Target>
<TargetName>ConversionDemo</TargetName><ToolsetNumber>0x4</ToolsetNumber><ToolsetName>ARM-ADS</ToolsetName><pArmCC/><pCCUsed/><uAC6>1</uAC6>
<TargetOption><TargetCommonOption><Device>STM32F103RC</Device><Vendor>STMicroelectronics</Vendor><Cpu>CPUTYPE("Cortex-M3")</Cpu><OutputName>ConversionDemo</OutputName><CreateExecutable>1</CreateExecutable><CreateLib>0</CreateLib></TargetCommonOption>
<CommonProperty><IncludeInBuild>1</IncludeInBuild></CommonProperty>
<TargetArmAds><ArmAdsMisc><AdsCpuType>"Cortex-M3"</AdsCpuType><RvdsVP>0</RvdsVP></ArmAdsMisc>
<Cads><uC99>1</uC99><VariousControls><Define>USE_HAL_DRIVER,STM32F103xE</Define><IncludePath>../Core/Inc</IncludePath><MiscControls/><Undefine/></VariousControls></Cads>
<Aads><VariousControls><Define/><IncludePath/><MiscControls/><Undefine/></VariousControls></Aads>
<LDads><umfTarg>0</umfTarg><useFile>1</useFile><ScatterFile>ConversionDemo.sct</ScatterFile></LDads></TargetArmAds></TargetOption>
<Groups><Group><GroupName>Application/User/Core</GroupName><Files>
<File><FileName>main.c</FileName><FileType>1</FileType><FilePath>../Core/Src/main.c</FilePath></File>
<File><FileName>system_stm32f1xx.c</FileName><FileType>1</FileType><FilePath>../Core/Src/system_stm32f1xx.c</FilePath></File>
<File><FileName>support.cpp</FileName><FileType>8</FileType><FilePath>../Core/Src/support.cpp</FilePath></File>
<File><FileName>rtti.cpp</FileName><FileType>8</FileType><FilePath>../Core/Src/rtti.cpp</FilePath></File>
</Files></Group><Group><GroupName>Application/MDK-ARM</GroupName><Files>
<File><FileName>startup_stm32f103xe.s</FileName><FileType>2</FileType><FilePath>startup_stm32f103xe.s</FilePath></File>
</Files></Group></Groups></Target></Targets></Project>

""");
    }
}
