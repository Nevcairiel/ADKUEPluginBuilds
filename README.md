# ARK Devkit plugin build system

Builds Unreal Engine editor plugins for the ARK: Survival Ascended Devkit
(Unreal Engine 5.8.1 licensee build `++ARK1+Dev-UE5-Integration`, BuildId `ca365fbf-4765-4cb3-b720-c2ba301003d5`)
from unmodified plugin sources, with plain MSVC and without the devkit's engine sources. Plugins built with it so far:
BlueprintAssist, AutoSizeComments, GitSourceControl (Epic's Git plugin, which the devkit ships without binaries)
and ElectronicNodes.

The devkit only ships binaries + PDBs. The headers are reconstructed from a vanilla UE 5.8 source tree and
patched to match the ARK engine's ABI; every patch was recovered from the devkit's PDB type information and
binaries (see *How the headers were recovered*). The patched SDK is shared by all plugins.

To add **another** plugin, see [`EXTENDING.md`](EXTENDING.md).

## Layout

```
PluginBuild\
  Build.ps1              build one plugin: .\Build.ps1 <Plugin>   (UHT, compile, link, stage)
  New-ArkSdk.ps1         creates SDK\: engine headers + ARK patches, UHT output, devkit import libraries
  Invoke-Uht.ps1         runs UnrealHeaderTool (from the vanilla UBT) on the SDK and a plugin
  Common.ps1             paths, toolchain discovery, plugin folder handling
  README.md, EXTENDING.md
  ArkPatches\*.patch     ARK ABI patches for the engine headers (unified diffs against vanilla UE 5.8), shared
  Config\                shared configuration
    ArkForwardDeclarations.h  forward declarations of ARK-only types (force-included)
    AbiExclusions.txt         ARK types whose changes are not reproduced, with the reason
    SdkHeaders.Extra.txt      headers needed only by ARK additions
  Tools\
    Capture-UbtBaseline.ps1   captures a plugin's build environment from a stock UBT build against vanilla UE 5.8
    New-ImportLibs.ps1        import libraries from the export tables of ShooterGameEditor-*.dll
    Update-GeneratedCode.ps1  remaps reflection flag literals in UHT output to ARK values
    Export-ArkPatches.ps1     turns edits of SDK\ headers into ArkPatches\*.patch
    Apply-ManualArkEdits.sh   hand-recovered edits in headers that the patcher also touches (re-run after PdbLayout patch)
    Test-ArkAbi.ps1, Test-LayoutIndependence.ps1, Test-DllLoad.ps1 (+ LoadTest\)   verification, see below
    PdbLayout\                PDB layout tool: layout / diff / audit / patch / probe (llvm-pdbutil based)
    pdb-type-sizes.sh, compare-type-sizes.sh, filter-audit.sh   quick sizeof comparisons
  SDK\                   generated, shared by all plugins (union of their header needs)
  Intermediate\          generated, shared: PdbCache (PDB text dumps, ~12 GB, safe to delete), engine UHT run
  Plugins\
    BlueprintAssist\                 a plugin as distributed: BlueprintAssist.uplugin, Source\, Resources\
      Build\Config\                  captured build environment of this plugin (flags, defines, header list, UHT modules)
      Build\Intermediate\            generated: objects, UHT output, link output, UBT baseline host project
      Build\Output\BlueprintAssist\  staged plugin, ready to copy into the devkit
    AutoSizeComments\                same structure (sources only, no Resources: the devkit stub has them)
    GitSourceControl\                same structure (vanilla UE 5.8 sources, no Resources: the devkit stub has them)
    ElectronicNodes\                 same structure (complete plugin: the devkit has no stub)
```

Plugin and module names come from the plugin's `.uplugin`; the plugin folder name is what the scripts take as
argument (`BlueprintAssist`). Only single-module plugins are supported so far (see EXTENDING.md).

## Prerequisites

All machine specific paths are configured in `Common.ps1`: two default locations plus automatic tool discovery,
each overridable with an environment variable. No other script or tool contains absolute paths. The C# and bash
tools get the paths from `Common.ps1` through environment variables (`Initialize-PdbLayout`, `Invoke-Bash`).

| What | Default (`Common.ps1`) | Override (environment variable) |
|---|---|---|
| Vanilla UE 5.8 (source headers + prebuilt UnrealBuildTool, used as reference and for UHT) | `D:\ASA\UE_5.8` | `ARK_UE_ROOT` |
| ARK Devkit (read only, never modified) | `D:\ASA\ARKDevkitBeta` | `ARK_DEVKIT_ROOT` |
| Generated SDK / PDB dump cache | `<root>\SDK` / `<root>\Intermediate\PdbCache` | `ARK_SDK_ROOT` / `ARK_PDB_CACHE` |
| Visual Studio 2022 with the MSVC v143 x64 toolset, Windows 10/11 SDK | auto-detected via `vswhere` | `ARK_MSVC_VERSION`, `ARK_WINSDK_VERSION` |
| `llvm-pdbutil` (VS component *C++ Clang tools for Windows*) | VS installation, else `PATH` | `ARK_LLVM_PDBUTIL` |
| Git for Windows (`git apply` for the header patches, bash for some tools) | `git`/`bash` on `PATH`, else `%ProgramFiles%\Git` | `ARK_BASH` |
| .NET SDK 9 (builds `Tools\PdbLayout` on first use) | on `PATH` | |

## Building

```powershell
cd <root>                               # this folder (D:\Modding\ARK\PluginBuild)
.\Build.ps1 BlueprintAssist             # incremental build
.\Build.ps1 BlueprintAssist -Rebuild    # recompile everything
.\Build.ps1 BlueprintAssist -Clean      # remove the plugin's intermediates and output
.\New-ArkSdk.ps1 -Clean                 # regenerate the shared SDK (headers, UHT output, import libraries)
```

The result is a complete plugin folder in `Plugins\BlueprintAssist\Build\Output\BlueprintAssist`:

```
BlueprintAssist\
  BlueprintAssist.uplugin
  Resources\...
  Binaries\Win64\ShooterGameEditor-BlueprintAssist.dll
  Binaries\Win64\ShooterGameEditor-BlueprintAssist.pdb
  Binaries\Win64\ShooterGameEditor.modules        (BuildId of the devkit)
```

## Installing

The devkit already contains stubs of the first three plugins (descriptor + resources, no binaries):
`<Devkit>\Engine\Plugins\Marketplace\BlueprintAssist`, `...\Marketplace\AutoSizeComments` and
`...\Developer\GitSourceControl`. Copy the `Binaries` folder from the staged output
(`Plugins\<Plugin>\Build\Output\<Plugin>`) into that folder, together with the staged `.uplugin` (the newer
descriptor; identical for GitSourceControl). The build never writes into the devkit itself.

The stubs are disabled by default. BlueprintAssist and GitSourceControl are disabled through their entries in
`Projects\ShooterGame\ShooterGame.uproject`; AutoSizeComments has `"EnabledByDefault": false` in its descriptor
and no project entry. Enable them either in *Edit → Plugins* (this edits `ShooterGame.uproject`), or for one
session, without changing any file, by starting the devkit with
`-EnablePlugins=BlueprintAssist,AutoSizeComments,GitSourceControl,ElectronicNodes`. Then select Git under *Revision Control*.

The *Find in files* / *Open file* menus need the `AssetSearch` engine plugin, which the devkit does not ship;
BlueprintAssist detects this and shows a notification instead (no crash). Everything else is available.
BlueprintAssist and AutoSizeComments work independently; each integrates with the other when both are loaded.

ElectronicNodes has no stub in the devkit: copy the whole staged folder `Plugins\ElectronicNodes\Build\Output\ElectronicNodes`
to `<Devkit>\Engine\Plugins\Marketplace\ElectronicNodes` and enable it like the others (its descriptor has no
`EnabledByDefault`, so in the engine folder it starts disabled):
`-EnablePlugins=BlueprintAssist,AutoSizeComments,GitSourceControl,ElectronicNodes`. With its default setting `UseHotPatch`, the plugin overwrites
the start of `FNodeFactory::CreateConnectionPolicy` in `ShooterGameEditor-GraphEditor.dll` in memory, replacing it
with its own copy of that function. The devkit's version was checked against vanilla UE 5.8: they are the same,
so the replacement loses no ARK behaviour.

## Verification

| Script | What it checks |
|---|---|
| `Tools\Test-ArkAbi.ps1 <Plugin>` | Compares every class/struct/union/enum the plugin was compiled against (from the plugin's PDB; ~36 600 types for BlueprintAssist) with the devkit PDBs: data layout, vtable order of introduced virtual functions, enumerator values, method access. Expected result: `0 types with unexplained ABI differences`; the remaining differences are the types listed in `Config\AbiExclusions.txt`. |
| `Tools\Test-LayoutIndependence.ps1 <Plugin>` | Proves the excluded types do not matter for this plugin: injects a 64 byte member and a virtual function at the start of every excluded class, recompiles, and requires the machine code of all object files to be identical. |
| `Tools\Test-DllLoad.ps1 <Plugin>` | Loads the built DLL with the devkit's `Binaries\Win64` as DLL search path: all imports are bound against the real `ShooterGameEditor-*.dll` files and the module's static initialization (UObject and module registration) runs. |

## Updating a plugin

* New plugin version: replace the plugin's `Source\` (and `.uplugin`/`Resources\` if needed), then
  `.\Tools\Capture-UbtBaseline.ps1 <Plugin>` (the new sources must build with stock UBT against vanilla UE 5.8; this
  also refreshes the header list), `.\Build.ps1 <Plugin> -Rebuild` and the three verification scripts.
* If the audit reports new types: `.\Tools\Test-ArkAbi.ps1 <Plugin> -Patch`, check `patch-report.txt`, rebuild,
  repeat; then `.\Tools\Export-ArkPatches.ps1`. See EXTENDING.md.

## How the headers were recovered

The devkit is `ShooterGameEditor` built from ARK's UE 5.8.1 licensee branch with MSVC 14.50; every module has a
full PDB. The vanilla UE 5.8 headers are the starting point, everything ARK changed was recovered from the
devkit binaries:

1. **Build environment** – `LF_BUILDINFO` records in the PDBs give the compiler and flags (identical to stock
   UBT). The devkit exports no `FLowLevelMemTracker`/`FLLMScope` symbols: it is built with
   `LLM_ENABLED_IN_CONFIG=0` (added to each plugin's `Build\Config\Definitions.h` by the capture script).
2. **Signatures** – linking the vanilla-compiled plugin against import libraries generated from the devkit export
   tables lists every function whose signature differs (e.g. `FTimerManager::InternalSetTimerForNextTick` has an
   extra `bool bDoLoop`; `SButton::Press/Release` are public; `UObjectBaseUtility::MarkPackageDirty` has an extra
   `bool bPrimalForcePackageDirty`, default `false` as passed by all 11 call sites in stock engine modules).
   ARK's new inline virtual functions `ISourceControlState::IsOurLock/IsOtherLock` throw `std::runtime_error`
   unless a provider overrides them (bodies from `ShooterGameEditor-SubversionSourceControl.dll`).
3. **Layouts** – `Tools\PdbLayout` reads the type records of the PDBs (`llvm-pdbutil`) and diffs every type the
   plugin uses against the devkit: inserted/removed data members, vtable slots of introduced virtual functions,
   enumerator values, method access. `PdbLayout patch` applies the differences to the headers automatically,
   placing declarations by their neighbours (UDT source line records locate the class definitions).
4. **Values that are not in the type information** were recovered from code with REA/Ghidra and raw reads of the
   devkit DLLs: default values of new Slate arguments (decompiled `F*::FArguments` constructors in
   `ShooterGameEditor-Slate.dll`), the default of `bDoLoop` (call site in `ShooterGameEditor-InputBindingEditor.dll`),
   and the bodies of ARK's inline virtual functions that the devkit does not export (`UObject::AllowSubobjectInstancing`,
   `SWidget::IsObjectWidget`, `FArchive::UsingSaveOverride`, ...; COMDAT copies in `ShooterGameEditor-EngineSettings.dll`
   and `ShooterGameEditor-Slate.dll`).
5. **Reflection** – UnrealHeaderTool regenerates all `.generated.h` from the patched headers. ARK renumbered several
   `EPropertyFlags` bits (`CPF_TObjectPtr` 56→59, ...); UHT writes these as literals, so
   `Tools\Update-GeneratedCode.ps1` remaps them in the plugin's generated code.

Notable ARK changes reproduced in `ArkPatches` (58 headers):

* `SWidget`: 10 new members (highlighting, UMG back pointer, sounds) and 5 new virtual functions, two of them in the
  middle of the vtable (`IsObjectWidget`, `GetObjectWidget`) – every Slate widget's vtable is shifted.
* `UObject`: 9 new virtual functions (e.g. `PostInterpChange`, `HasWorld`, `AllowSubobjectInstancing`);
  `UStruct`, `UClass`, `FProperty`: new members (`Primal_bForceExposeToBlueprints`, sparse class data backup, ...).
* `UEdGraphSchema`: `CopyPinLinks(UEdGraphPin&, bool)`, `CutPinLinks`, `PastePinLinks` (the overload must be declared
  after the existing one: MSVC lays out overloaded virtuals in reverse declaration order); `UEdGraphNode::bIsSparseData`.
* `UEngine` (mods, guarded HTTP/WebSocket API), `UEditorEngine` (cook error log, Matinee), `UWidget` (custom tooltips,
  gamepad navigation), `FSlateFontInfo`, `FTextBlockStyle`, `SButton`, `STextBlock`, `SScrollBox`, `SListView`, ...
* Enums: `EName` (`NAME_BattlEye` inserted at 262), `EPropertyFlags`, `EFunctionFlags`, `ECollisionChannel`,
  `EViewModeIndex`, `EWidgetClipping`, show flags, `MAX_TEXCOORDS` = 6, ...

Not reproduced (`Config\AbiExclusions.txt`): ARK re-based the gameplay framework on its own classes (`AActor` derives
from `UPrimalActor`, `UActorComponent` from `UPrimalActorComponent`, `UWorld` from `UPrimalWorld`, `APlayerController`
from `APrimalController`) and changed rendering types deeply. BlueprintAssist only handles these as opaque pointers;
`Test-LayoutIndependence.ps1` proves that none of its code depends on their layout. Every new plugin must pass that
test too.
