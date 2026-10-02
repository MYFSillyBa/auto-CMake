namespace CMakeDapLink.Core;

internal static class MdkProjectTemplate
{
    // Required MDK5 XML fields in schema order; no installed Keil files are needed at runtime.
    public const string TargetOptions = """
<TargetOption>
  <TargetCommonOption>
    <Device />
    <Vendor />
    <Cpu />
    <FlashUtilSpec />
    <StartupFile />
    <FlashDriverDll />
    <DeviceId />
    <RegisterFile />
    <MemoryEnv />
    <Cmp />
    <Asm />
    <Linker />
    <OHString />
    <InfinionOptionDll />
    <SLE66CMisc />
    <SLE66AMisc />
    <SLE66LinkerMisc />
    <UseEnv>0</UseEnv>
    <BinPath />
    <IncludePath />
    <LibPath />
    <RegisterFilePath />
    <DBRegisterFilePath />
    <TargetStatus>
      <Error>0</Error>
      <ExitCodeStop>0</ExitCodeStop>
      <ButtonStop>0</ButtonStop>
      <NotGenerated>0</NotGenerated>
      <InvalidFlash>0</InvalidFlash>
    </TargetStatus>
    <OutputDirectory />
    <OutputName />
    <CreateExecutable>0</CreateExecutable>
    <CreateLib>0</CreateLib>
    <CreateHexFile>0</CreateHexFile>
    <DebugInformation>0</DebugInformation>
    <BrowseInformation>0</BrowseInformation>
    <ListingPath />
    <HexFormatSelection>0</HexFormatSelection>
    <Merge32K>0</Merge32K>
    <CreateBatchFile>0</CreateBatchFile>
    <BeforeCompile>
      <RunUserProg1>0</RunUserProg1>
      <RunUserProg2>0</RunUserProg2>
      <UserProg1Name />
      <UserProg2Name />
      <UserProg1Dos16Mode>0</UserProg1Dos16Mode>
      <UserProg2Dos16Mode>0</UserProg2Dos16Mode>
    </BeforeCompile>
    <BeforeMake>
      <RunUserProg1>0</RunUserProg1>
      <RunUserProg2>0</RunUserProg2>
      <UserProg1Name />
      <UserProg2Name />
      <UserProg1Dos16Mode>0</UserProg1Dos16Mode>
      <UserProg2Dos16Mode>0</UserProg2Dos16Mode>
      <nStopB1X>0</nStopB1X>
      <nStopB2X>0</nStopB2X>
    </BeforeMake>
    <AfterMake>
      <RunUserProg1>0</RunUserProg1>
      <RunUserProg2>0</RunUserProg2>
      <UserProg1Name />
      <UserProg2Name />
      <UserProg1Dos16Mode>0</UserProg1Dos16Mode>
      <UserProg2Dos16Mode>0</UserProg2Dos16Mode>
      <nStopA1X>0</nStopA1X>
      <nStopA2X>0</nStopA2X>
    </AfterMake>
    <SelectedForBatchBuild>0</SelectedForBatchBuild>
    <SVCSIdString />
  </TargetCommonOption>
  <CommonProperty>
    <UseCPPCompiler>0</UseCPPCompiler>
    <RVCTCodeConst>0</RVCTCodeConst>
    <RVCTZI>0</RVCTZI>
    <RVCTOtherData>0</RVCTOtherData>
    <ModuleSelection>0</ModuleSelection>
    <IncludeInBuild>0</IncludeInBuild>
    <AlwaysBuild>0</AlwaysBuild>
    <GenerateAssemblyFile>0</GenerateAssemblyFile>
    <AssembleAssemblyFile>0</AssembleAssemblyFile>
    <PublicsOnly>0</PublicsOnly>
    <StopOnExitCode>0</StopOnExitCode>
    <CustomArgument />
    <IncludeLibraryModules />
  </CommonProperty>
  <DllOption>
    <SimDllName />
    <SimDllArguments />
    <SimDlgDll />
    <SimDlgDllArguments />
    <TargetDllName />
    <TargetDllArguments />
    <TargetDlgDll />
    <TargetDlgDllArguments />
  </DllOption>
  <DebugOption>
    <OPTHX>
      <HexSelection>0</HexSelection>
      <HexRangeLowAddress>0</HexRangeLowAddress>
      <HexRangeHighAddress>0</HexRangeHighAddress>
      <HexOffset>0</HexOffset>
      <Oh166RecLen>0</Oh166RecLen>
    </OPTHX>
    <Simulator>
      <UseSimulator>0</UseSimulator>
      <LoadApplicationAtStartup>0</LoadApplicationAtStartup>
      <RunToMain>0</RunToMain>
      <RestoreBreakpoints>0</RestoreBreakpoints>
      <RestoreWatchpoints>0</RestoreWatchpoints>
      <RestoreMemoryDisplay>0</RestoreMemoryDisplay>
      <RestoreFunctions>0</RestoreFunctions>
      <RestoreToolbox>0</RestoreToolbox>
      <LimitSpeedToRealTime>0</LimitSpeedToRealTime>
    </Simulator>
    <Target>
      <UseTarget>0</UseTarget>
      <LoadApplicationAtStartup>0</LoadApplicationAtStartup>
      <RunToMain>0</RunToMain>
      <RestoreBreakpoints>0</RestoreBreakpoints>
      <RestoreWatchpoints>0</RestoreWatchpoints>
      <RestoreMemoryDisplay>0</RestoreMemoryDisplay>
      <RestoreFunctions>0</RestoreFunctions>
      <RestoreToolbox>0</RestoreToolbox>
    </Target>
    <RunDebugAfterBuild>0</RunDebugAfterBuild>
    <TargetSelection>0</TargetSelection>
    <SimDlls>
      <CpuDll />
      <CpuDllArguments />
      <PeripheralDll />
      <PeripheralDllArguments />
      <InitializationFile />
    </SimDlls>
    <TargetDlls>
      <CpuDll />
      <CpuDllArguments />
      <PeripheralDll />
      <PeripheralDllArguments />
      <InitializationFile />
      <Driver />
    </TargetDlls>
  </DebugOption>
  <Utilities>
    <Flash1>
      <UseTargetDll>0</UseTargetDll>
      <UseExternalTool>0</UseExternalTool>
      <RunIndependent>0</RunIndependent>
      <UpdateFlashBeforeDebugging>0</UpdateFlashBeforeDebugging>
      <Capability>0</Capability>
      <DriverSelection>0</DriverSelection>
    </Flash1>
    <bUseTDR>0</bUseTDR>
    <Flash2 />
    <Flash3 />
    <Flash4 />
  </Utilities>
  <TargetArmAds>
    <ArmAdsMisc>
      <GenerateListings>0</GenerateListings>
      <asHll>0</asHll>
      <asAsm>0</asAsm>
      <asMacX>0</asMacX>
      <asSyms>0</asSyms>
      <asFals>0</asFals>
      <asDbgD>0</asDbgD>
      <asForm>0</asForm>
      <ldLst>0</ldLst>
      <ldmm>0</ldmm>
      <ldXref>0</ldXref>
      <BigEnd>0</BigEnd>
      <AdsALst>0</AdsALst>
      <AdsACrf>0</AdsACrf>
      <AdsANop>0</AdsANop>
      <AdsANot>0</AdsANot>
      <AdsLLst>0</AdsLLst>
      <AdsLmap>0</AdsLmap>
      <AdsLcgr>0</AdsLcgr>
      <AdsLsym>0</AdsLsym>
      <AdsLszi>0</AdsLszi>
      <AdsLtoi>0</AdsLtoi>
      <AdsLsun>0</AdsLsun>
      <AdsLven>0</AdsLven>
      <AdsLsxf>0</AdsLsxf>
      <RvctClst>0</RvctClst>
      <GenPPlst>0</GenPPlst>
      <AdsCpuType />
      <RvctDeviceName />
      <mOS>0</mOS>
      <uocRom>0</uocRom>
      <uocRam>0</uocRam>
      <hadIROM>0</hadIROM>
      <hadIRAM>0</hadIRAM>
      <hadXRAM>0</hadXRAM>
      <uocXRam>0</uocXRam>
      <RvdsVP>0</RvdsVP>
      <RvdsCdeCp>0</RvdsCdeCp>
      <hadIRAM2>0</hadIRAM2>
      <hadIROM2>0</hadIROM2>
      <StupSel>0</StupSel>
      <useUlib>0</useUlib>
      <EndSel>0</EndSel>
      <uLtcg>0</uLtcg>
      <nSecure>0</nSecure>
      <RoSelD>0</RoSelD>
      <RwSelD>0</RwSelD>
      <CodeSel>0</CodeSel>
      <OptFeed>0</OptFeed>
      <NoZi1>0</NoZi1>
      <NoZi2>0</NoZi2>
      <NoZi3>0</NoZi3>
      <NoZi4>0</NoZi4>
      <NoZi5>0</NoZi5>
      <Ro1Chk>0</Ro1Chk>
      <Ro2Chk>0</Ro2Chk>
      <Ro3Chk>0</Ro3Chk>
      <Ir1Chk>0</Ir1Chk>
      <Ir2Chk>0</Ir2Chk>
      <Ra1Chk>0</Ra1Chk>
      <Ra2Chk>0</Ra2Chk>
      <Ra3Chk>0</Ra3Chk>
      <Im1Chk>0</Im1Chk>
      <Im2Chk>0</Im2Chk>
      <OnChipMemories>
        <Ocm1>
          <Type>0</Type>
          <StartAddress>0x0</StartAddress>
          <Size>0x0</Size>
        </Ocm1>
        <Ocm2>
          <Type>0</Type>
          <StartAddress>0x0</StartAddress>
          <Size>0x0</Size>
        </Ocm2>
        <Ocm3>
          <Type>0</Type>
          <StartAddress>0x0</StartAddress>
          <Size>0x0</Size>
        </Ocm3>
        <Ocm4>
          <Type>0</Type>
          <StartAddress>0x0</StartAddress>
          <Size>0x0</Size>
        </Ocm4>
        <Ocm5>
          <Type>0</Type>
          <StartAddress>0x0</StartAddress>
          <Size>0x0</Size>
        </Ocm5>
        <Ocm6>
          <Type>0</Type>
          <StartAddress>0x0</StartAddress>
          <Size>0x0</Size>
        </Ocm6>
        <IRAM>
          <Type>0</Type>
          <StartAddress>0x0</StartAddress>
          <Size>0x0</Size>
        </IRAM>
        <IROM>
          <Type>0</Type>
          <StartAddress>0x0</StartAddress>
          <Size>0x0</Size>
        </IROM>
        <XRAM>
          <Type>0</Type>
          <StartAddress>0x0</StartAddress>
          <Size>0x0</Size>
        </XRAM>
        <OCR_RVCT1>
          <Type>0</Type>
          <StartAddress>0x0</StartAddress>
          <Size>0x0</Size>
        </OCR_RVCT1>
        <OCR_RVCT2>
          <Type>0</Type>
          <StartAddress>0x0</StartAddress>
          <Size>0x0</Size>
        </OCR_RVCT2>
        <OCR_RVCT3>
          <Type>0</Type>
          <StartAddress>0x0</StartAddress>
          <Size>0x0</Size>
        </OCR_RVCT3>
        <OCR_RVCT4>
          <Type>0</Type>
          <StartAddress>0x0</StartAddress>
          <Size>0x0</Size>
        </OCR_RVCT4>
        <OCR_RVCT5>
          <Type>0</Type>
          <StartAddress>0x0</StartAddress>
          <Size>0x0</Size>
        </OCR_RVCT5>
        <OCR_RVCT6>
          <Type>0</Type>
          <StartAddress>0x0</StartAddress>
          <Size>0x0</Size>
        </OCR_RVCT6>
        <OCR_RVCT7>
          <Type>0</Type>
          <StartAddress>0x0</StartAddress>
          <Size>0x0</Size>
        </OCR_RVCT7>
        <OCR_RVCT8>
          <Type>0</Type>
          <StartAddress>0x0</StartAddress>
          <Size>0x0</Size>
        </OCR_RVCT8>
        <OCR_RVCT9>
          <Type>0</Type>
          <StartAddress>0x0</StartAddress>
          <Size>0x0</Size>
        </OCR_RVCT9>
        <OCR_RVCT10>
          <Type>0</Type>
          <StartAddress>0x0</StartAddress>
          <Size>0x0</Size>
        </OCR_RVCT10>
      </OnChipMemories>
      <RvctStartVector />
    </ArmAdsMisc>
    <Cads>
      <interw>0</interw>
      <Optim>0</Optim>
      <oTime>0</oTime>
      <SplitLS>0</SplitLS>
      <OneElfS>0</OneElfS>
      <Strict>0</Strict>
      <EnumInt>0</EnumInt>
      <PlainCh>0</PlainCh>
      <Ropi>0</Ropi>
      <Rwpi>0</Rwpi>
      <wLevel>0</wLevel>
      <uThumb>0</uThumb>
      <uSurpInc>0</uSurpInc>
      <uC99>0</uC99>
      <uGnu>0</uGnu>
      <useXO>0</useXO>
      <v6Lang>0</v6Lang>
      <v6LangP>0</v6LangP>
      <vShortEn>0</vShortEn>
      <vShortWch>0</vShortWch>
      <v6Lto>0</v6Lto>
      <v6WtE>0</v6WtE>
      <v6Rtti>0</v6Rtti>
      <VariousControls>
        <MiscControls />
        <Define />
        <Undefine />
        <IncludePath />
      </VariousControls>
    </Cads>
    <Aads>
      <interw>0</interw>
      <Ropi>0</Ropi>
      <Rwpi>0</Rwpi>
      <thumb>0</thumb>
      <SplitLS>0</SplitLS>
      <SwStkChk>0</SwStkChk>
      <NoWarn>0</NoWarn>
      <uSurpInc>0</uSurpInc>
      <uClangAs>0</uClangAs>
      <ClangAsOpt>0</ClangAsOpt>
      <VariousControls>
        <MiscControls />
        <Define />
        <Undefine />
        <IncludePath />
      </VariousControls>
    </Aads>
    <LDads>
      <umfTarg>0</umfTarg>
      <Ropi>0</Ropi>
      <Rwpi>0</Rwpi>
      <noStLib>0</noStLib>
      <RepFail>0</RepFail>
      <useFile>0</useFile>
      <TextAddressRange />
      <DataAddressRange />
      <ScatterFile />
      <IncludeLibs />
      <IncludeLibsPath />
      <Misc />
      <LinkerInputFile />
      <DisabledWarnings />
    </LDads>
  </TargetArmAds>
</TargetOption>
""";
}
