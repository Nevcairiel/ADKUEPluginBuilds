# Using this setup for other editor plugins

This build system was written for BlueprintAssist, but nothing in the approach is specific to it. This document
collects everything needed to compile another UE editor plugin for the ARK Devkit (ShooterGameEditor, UE 5.8.3
licensee build), plus the lessons learned that are not obvious from the scripts.

See `README.md` for what the individual scripts do and for the recovered ARK ABI changes.

---

## 1. The idea in one paragraph

The devkit ships DLLs + full PDBs but no headers, import libraries or generated code. We take the vanilla UE 5.8
headers (`D:\ASA\UE_5.8`), patch them until every type the plugin touches has exactly the ARK layout / vtable /
enum values (recovered from the devkit PDBs and binaries), regenerate UHT output from the patched headers,
create import libraries from the export tables of the `ShooterGameEditor-*.dll` files, and compile with the
same flags stock UBT uses. Correctness is checked mechanically, not by hand:
`Test-ArkAbi.ps1` (PDB audit), `Test-LayoutIndependence.ps1` (probe build), `Test-DllLoad.ps1` (loader).

Everything shared lives at the root of `D:\Modding\ARK\PluginBuild` (see the layout in `README.md`): the SDK
(`SDK\` = vanilla headers + `ArkPatches`, UHT output, import libraries), the shared `Config\`
(`ArkForwardDeclarations.h`, `AbiExclusions.txt`, `SdkHeaders.Extra.txt`) and all scripts. The SDK is
**shared by all plugins**: a new plugin can only add patches, it never needs to redo the existing ones.
Each plugin is a folder below `Plugins\` and only contains its sources plus its own `Build\` data.

---

## 2. Recipe: building another plugin

All commands run from the build root (`D:\Modding\ARK\PluginBuild`) and take the plugin's folder name below
`Plugins\`. Paths of the engine, devkit and tools are configured in `Common.ps1` (see *Prerequisites* in README.md);
the paths in this document are the defaults.

### 2.1 Add the plugin folder

Copy the plugin as distributed (its `.uplugin`, `Source\`, `Resources\`, `Content\`, ...) to
`Plugins\<Folder>\`, e.g. `Plugins\AutoSizeComments\`. Plugin and module names are read from the `.uplugin`
(module name = DLL name `ShooterGameEditor-<Module>.dll`); nothing in the scripts needs to change for a
single-module plugin. If the source drop has no `.uplugin`, take the devkit's stub descriptor from
`<Devkit>\Engine\Plugins\Marketplace\<Plugin>\` (that also guarantees the names the devkit expects; hashed folder
names like `Blueprin0b23223502eaV17` are possible, search for the `.uplugin`).

### 2.2 Capture the build environment

```powershell
.\Tools\Capture-UbtBaseline.ps1 <Folder>
```

This builds the plugin once with stock UBT against vanilla UE 5.8 (it must compile there first!) and writes
`Plugins\<Folder>\Build\Config` (include paths, flags, definitions, link libraries, UHT module closure, list of
headers to copy). The shared `Config\` files are used as they are.

Only the modules declared in the `.uplugin` are linked into the UBT host project. Source drops sometimes contain
modules of other plugins (the BlueprintAssist update came with `Source\AutoSizeComments`, which belongs to the
separate AutoSizeComments plugin); UBT rejects undeclared modules inside a plugin, and `Build.ps1` ignores them.

Check the plugin's `Build\Config\EngineModules.txt` against the devkit: every module must exist as
`D:\ASA\ARKDevkitBeta\Engine\Binaries\Win64\ShooterGameEditor-<Module>.dll` (all plugin modules of the devkit are
in that one folder too). Missing modules (ue_mcp_bridge: `Fab`) are skipped at link time - make sure the
plugin only uses them through header-inline code guarded by `IsAvailable()`-style checks, otherwise the plugin
cannot work in the devkit at all.

Also check the plugin's `Build\Config\Definitions.h` contains the ARK block at the end (`LLM_ENABLED_IN_CONFIG 0`,
retargeted `UBT_MODULE_MANIFEST` etc.; the capture script adds it).

Two module variants are handled automatically (both first seen with GitSourceControl):

* **Shared PCH** (the default `PCHUsage` of a module): UBT force-includes an engine module's shared PCH header
  (e.g. `SharedPCH.UnrealEd.Cpp20.h`) and then a `Definitions.<Module>.h` that only adjusts the PCH module's
  definitions. `Build.ps1` uses no PCHs, so the capture splits this into `Definitions.h` (the PCH module's shared
  definitions + ARK block) and `SharedPch.h` (`#include "UnrealEdSharedPCH.h"` + the module's own definitions).
  `Build.ps1` force-includes `Definitions.h`, `ArkForwardDeclarations.h` and `SharedPch.h` in this order. That
  matches UBT's order, and the ARK definitions and forward declarations come before any engine header.
* **No reflected types** (no `UCLASS`/`USTRUCT`/...): the module is not in UBT's UHT manifest. The UHT closure then
  starts from the modules whose `Inc\<Module>\UHT` directories are on the include path, no `UhtPluginModule.json`
  is written, and `Build.ps1` skips the plugin's UHT step and the `.gen.cpp` unity file.

### 2.3 Extend the SDK and build

```powershell
.\New-ArkSdk.ps1              # copies newly needed headers (union of all plugins), applies ArkPatches, UHT, import libraries
.\Build.ps1 <Folder>
```

New headers are copied automatically (from every plugin's `Build\Config\SdkHeaders.txt`); headers that only ARK
additions need go to the shared `Config\SdkHeaders.Extra.txt`.

Typical first-build problems and their fix:

* **Unresolved externals of a devkit module no plugin linked before** → `Build.ps1` runs the full `New-ArkSdk.ps1`
  when an import library is missing; after a manual SDK reset run it yourself.
* **Unknown type in an ARK declaration** (`'FModInfo': undeclared identifier`) → add a forward declaration to
  `Config\ArkForwardDeclarations.h`. Use the class/struct key of the devkit PDB (it is part of the mangled name):
  `.?AV` = class, `.?AU` = struct (see `pdb-type-sizes.sh`). Types used by value need a definition (layout from
  `PdbLayout layout <devkit pdb> <Type>`), or the header that defines them in `SdkHeaders.Extra.txt`.
* **Unresolved external of an ARK virtual function** (`UObject::PostInterpChange` etc.) → ARK defines it inline
  in its headers. Recover the body, see 4.3.
* **Unresolved external of an engine function** → the ARK signature differs (extra parameter, changed access,
  const, ...). Compare with the devkit exports (`SDK\Lib\ShooterGameEditor-<Module>.def`), recover default
  arguments from a devkit call site (see 4.2).

### 2.4 Make the ABI match

```powershell
.\Tools\Test-ArkAbi.ps1 <Folder>           # audit: lists every type with unexplained ABI differences
.\Tools\Test-ArkAbi.ps1 <Folder> -Patch    # let PdbLayout patch the reported types in SDK\
bash .\Tools\Apply-ManualArkEdits.sh       # re-apply hand edits in headers the patcher also touched
.\Build.ps1 <Folder>
.\Tools\Test-ArkAbi.ps1 <Folder>           # repeat until "0 types with unexplained ABI differences"
.\Tools\Test-LayoutIndependence.ps1 <Folder> -Jobs 4
.\Tools\Test-DllLoad.ps1 <Folder>
.\Tools\Export-ArkPatches.ps1              # last: store the result as ArkPatches\*.patch
```

Export only at the very end, when every plugin passes the three tests. If you re-create the patches with
`Repatch-ArkSdk.ps1` (2.7), an export in between makes the next repatch apply the same changes twice.

`-Patch` uses the plugin's own PDB as reference, so only types that still differ are touched. Look at
`Plugins\<Folder>\Build\Intermediate\patch-report.txt` for `MANUAL` entries (templates, macro generated enums,
UHT generated `_Implementation` functions, ...) and do those by hand (section 4).

Because the SDK is shared, a patch added for a new plugin also changes the headers other plugins compile against:
rebuild and re-run the three tests for the existing plugins afterwards (`.\Build.ps1 BlueprintAssist` etc.).

Decide for every reported type whether it matters for the plugin:

* Types the plugin touches (members, inline functions, virtual calls, base classes of its own classes, Slate
  `FArguments` it constructs via `SNew`) **must** be patched.
* Gameplay / rendering types the plugin only uses as opaque pointers can go to `Config\AbiExclusions.txt`
  (with a reason). Then **prove** it: `Tools\Test-LayoutIndependence.ps1` must report identical code.
  If it does not, the type must be patched instead.

### 2.5 Multi-module plugins

Handled automatically (first used for ue_mcp_bridge). With several modules in the `.uplugin`, the capture writes
one configuration per module to `Build\Config\<Module>\` (single-module plugins keep `Build\Config\`),
`Invoke-Uht.ps1` adds one manifest entry per module, and `Build.ps1` compiles each module into `Obj\<Module>` and
links them in dependency order (`PluginModules.txt`: the import libraries of the plugin's own modules linked
before). `ShooterGameEditor.modules` lists all of them. The tests check every module (one ABI audit per module).

### 2.6 Adapting the plugin sources

Some plugins call engine API that ARK changed or removed. Do not edit `Source\`: put unified diffs (relative to
`Source\`, `git apply -p1`) into `Plugins\<Folder>\Build\SourcePatches\*.patch`. `Build.ps1` applies them in name
order to a copy of the sources in `Build\Intermediate\Source` and compiles that copy; the copy is refreshed
whenever a patch or a source file changes. Mark each change with `[ARK]` and the reason.

Definitions that UBT derives from the vanilla engine but that must differ in the devkit (ue_mcp_bridge:
`WITH_FAB_PLUGIN=0`, because the devkit has no Fab binaries) go to `Plugins\<Folder>\Build\DefinitionOverrides.txt`
(`NAME=VALUE` per line); rerun `Capture-UbtBaseline.ps1` after changing it.

ThirdParty libraries that UBT links (earlier ue_mcp_bridge versions: ICU) are captured to `ThirdPartyLibs.txt` and linked from the
vanilla engine copy in the SDK. They are static libraries, so the plugin gets its own copy next to the devkit's.

### 2.7 Re-creating the SDK patches from scratch

`Test-ArkAbi.ps1 -Patch` patches incrementally, on top of earlier patches. When a patcher change makes the
patches come out differently, `Tools\Repatch-ArkSdk.ps1` re-creates them all at once:

1. It resets the SDK to vanilla headers + `ArkPatches\` + `ArkHeaders\` (`New-ArkSdk.ps1 -Clean -HeadersOnly`).
2. It patches every type from the **baseline PDBs** in `Intermediate\BaselinePdb`: plugin PDBs built against that
   reset SDK. Currently the 15 modules of all 12 plugins.
3. It runs `Update-ArkTypes` and `Apply-ManualArkEdits.sh`.

Then run `New-ArkSdk.ps1`, build and test all plugins, and export (2.4).

The baseline PDBs only fit the `ArkPatches\` they were built against. `-SaveBaseline` records a hash of
`ArkPatches\` in `Intermediate\BaselinePdb\ArkPatches.stamp`, and the repatch refuses to run if the patches
changed since. After an export or an update of a baseline plugin, rebuild the baseline before the next repatch
(remove the old PDBs from `Intermediate\BaselinePdb` first if a module was dropped). The stamp covers the whole
folder, so a `-SaveBaseline` with only some plugins also marks the PDBs of the others as current: list every plugin
that has PDBs there, or remove the others first.

```powershell
.\New-ArkSdk.ps1 -Clean
.\Build.ps1 <Plugin> -Rebuild -Jobs 8             # every plugin; -ForceLink if it doesn't link yet: only the PDB is used
.\Tools\Repatch-ArkSdk.ps1 -SaveBaseline AutoSizeComments, BlueprintAssist, GitSourceControl, ElectronicNodes, JsonUtilitiesEditor, ToolsetRegistry, EditorToolset, NiagaraToolsets, UMGToolSet, SlateInspectorToolset, AnimationAssistantToolset, ue_mcp_bridge
```

### 2.8 Dependencies on other plugins and on engine modules the devkit lacks

* **Another plugin built here** (EditorToolset links ToolsetRegistry, which the devkit only has as a stub without
  binaries): `Build.ps1` finds the plugin folder that declares the module (`Get-OtherPluginModule`, Common.ps1) and
  links its import library (build that plugin first). `Test-DllLoad.ps1` loads its DLL first.
* **Vanilla engine modules the devkit does not ship** (ToolsetRegistry needs `JsonUtilitiesEditor`): the build links
  without them (`devkit has no ... module, not linked`), but a runtime `LoadModuleChecked` crashes the editor. Check
  every such module in the build log against the plugin's sources. If the plugin really loads it, add it to
  `Config\EngineModulePlugins.txt`: `Tools\New-EngineModulePlugins.ps1` (run by `Build.ps1`) creates
  `Plugins\<Module>` from the vanilla source with a generated `.uplugin`, and from then on it is a plugin like any
  other (capture, build, the three tests). Check that the devkit exports what the module builds on
  (`SDK\Lib\*.def`; for JsonUtilitiesEditor: `IJsonUtilitiesModularFeature` in JsonUtilities).
* In both cases the staged `.uplugin` gets the providing plugin added to its `Plugins` list (only the staged copy;
  the source descriptor stays as distributed), so the devkit enables it together with the plugin. This is computed on
  every build, so new plugin versions and rebuilds keep it.

### 2.9 Install / test in the devkit

Copy `Plugins\<Folder>\Build\Output\<Plugin>\Binaries` into the plugin folder below `<Devkit>\Engine\Plugins\...` (or a project
`Plugins` folder) and enable the plugin (`-EnablePlugins=<Plugin>` on the command line or the Plugins window).
The `.modules` file must carry the devkit's BuildId (taken from `Engine\Binaries\Win64\ShooterGameEditor.modules`
automatically); after a devkit update the plugin must be rebuilt (new BuildId, possibly new ABI, see 2.10).

### 2.10 After a devkit update

An update changes the BuildId of every module, so all plugins must be rebuilt even if no type changed.

1. Save `SDK\Lib\*.def` (the old export tables) before anything else; diff them afterwards to see which exports
   ARK removed or added. Removed ones are signature changes.
2. `.\Tools\New-ArkClassHeaders.ps1` regenerates `ArkHeaders\`; `git diff` shows ARK's vtable changes.
   `.\New-ArkSdk.ps1 -Clean` then rebuilds the SDK from the new import libraries.
3. Audit the PDBs of the old plugin builds against the new devkit (`Test-ArkAbi.ps1`, no rebuild needed): this lists
   the types ARK changed. PDB dumps are cached by file size and time, so a changed PDB is dumped again; the old dumps
   only take up disk space (`Intermediate\PdbCache`).
4. Build the plugins with `-Rebuild` (`Build.ps1` is incremental by file time, and refreshed plugin sources can keep
   their old times). Fix what comes up: `Test-ArkAbi.ps1 -Patch` for layouts (2.4), `Apply-ManualArkEdits.sh` for
   inline bodies (4.3) and changed signatures.
5. Re-read the values that are not in the type information (section 4): they can change without any layout changing.
   Everything in `Apply-ManualArkEdits.sh` that cites a disassembly belongs here, and so do runtime hot patches.
6. All plugins pass the three tests: `Export-ArkPatches.ps1`, then `.\New-ArkSdk.ps1 -Clean` and rebuild everything to
   prove that vanilla + patches reproduces the result, then `Repatch-ArkSdk.ps1 -SaveBaseline` (2.7).
7. Package the new builds with `Tools\Package-Dist.ps1` (README, *Distribution*): the old zips only work with the old
   BuildId. A new plugin also needs an entry in the script's tables.

---

## 3. What is ARK-specific (applies to every plugin)

* Target `ShooterGameEditor`, modular, Development editor, MSVC 14.50, same compiler flags as stock UBT.
* `LLM_ENABLED_IN_CONFIG=0` (the devkit exports no LLM symbols; without it every `operator new` fails to link).
* UHT output needs the `EPropertyFlags` remap (`Tools\Update-GeneratedCode.ps1`); all other reflection flag enums
  are unchanged.
* UE 5.8 modules register through a static `FModuleInitializerEntry`; there is no `InitializeModule` export.
* ARK changed many core types (see README). For a new plugin, everything in `ArkPatches` is already correct;
  only types the new plugin uses for the first time can show up in the audit.
* Gameplay framework re-based on ARK classes (`UPrimalActor`, `UPrimalActorComponent`, `UPrimalWorld`,
  `APrimalController`, `FPrimalViewportClient`, ...). `Tools\New-ArkClassHeaders.ps1` generates them from the PDBs
  into `ArkHeaders\` (list in `Config\ArkClasses.txt`): devkit bases, the data as opaque storage of the devkit's
  size, all virtual functions in vtable order. ARK data members are not named, so a plugin can only use the vanilla
  members of `AActor` & co. If ARK added a base class that is not listed yet, add a line to `ArkClasses.txt`,
  run the script and derive the engine class from it (patcher, `SetBases`).
* Members the devkit renamed at the same position (`ULevel::Actors2`) keep their vanilla name in the SDK
  (`Config\AbiRenames.txt`), so engine inline code and plugins compile unchanged.
* ARK changed some non-virtual signatures (extra parameters, e.g. `USceneComponent::SetVisibility`). These are
  unresolved externals, not audit findings; they are edited by hand in `Apply-ManualArkEdits.sh` with the mangled
  names from `SDK\Lib\*.def` and defaults that keep the vanilla behavior.

---

## 4. Recovering things that are not in the PDB type information

The REA MCP server with the Ghidra backend works on the devkit DLLs, with two caveats:

* REA refuses targets on non-NTFS volumes ("Backing volume must be NTFS with persistent ACLs"). Copy the DLL and
  its PDB to an NTFS location first (e.g. the session scratchpad on C:).
* Ghidra's first analysis of a large module takes minutes (Slate ~3 min). Engine/UnrealEd are huge - prefer to
  find the code in a small module (see 4.1).
* Ghidra recovers names from the exports/PDB, but not the PDB data types; use `Tools\PdbLayout` for types.

### 4.1 Finding inline code in the devkit

Inline functions of engine headers are compiled into every module that uses them, and the PDBs list them:

```bash
llvm-pdbutil dump --symbols ShooterGameEditor-<SmallModule>.pdb | grep -A1 'PROC32 .*`SButton::FArguments::FArguments`'
```

`addr = 0001:<offset>` → virtual address `0x180000000 + 0x1000 + offset` (`.text` at RVA 0x1000; check with
`dumpbin /headers`), file offset `0x400 + offset`. Small modules with many UObject/Slate users (EngineSettings,
InputBindingEditor, Slate itself) are good sources.

### 4.2 Default arguments and new FArguments members

* Default argument of a changed function: find a devkit call site of the vanilla-style call (e.g. a stock engine
  module calling `SetTimerForNextTick(Delegate)`) and read the argument register in the assembly
  (`procedure_assembly`; x64: rcx, rdx, r8, r9, then stack).
* New `SLATE_ARGUMENT`s: decompile `<Widget>::FArguments::FArguments` (4.1) and read the stores at the member
  offsets (`PdbLayout layout ShooterGameEditor-Slate.pdb "<Widget>::FArguments"`). Members the devkit does not
  initialize get `false`/0. Add them to the `SLATE_BEGIN_ARGS` initializer list (see `Apply-ManualArkEdits.sh`).

### 4.3 Inline virtual functions ARK added

Unresolved externals like `UObject::AllowSubobjectInstancing` mean ARK defined them inline. Their COMDAT copies
are tiny; read the bytes (REA `read_bytes`, or raw file bytes):

| bytes | body |
|---|---|
| `C2 00 00` | `{}` |
| `32 C0 C3` / `33 C0 C3` | `return false;` / `return 0 / nullptr;` |
| `B0 01 C3` | `return true;` |

Anything longer: decompile it. Add the bodies to `Apply-ManualArkEdits.sh` (`body` helper).

### 4.4 Comparing with a plugin ARK built itself

The devkit ships BlueprintAssist with its PDB, the same version we build. That makes it a reference for the SDK as a
whole, and it finds what the three tests cannot: changed logic in header-inline code, where layout and signatures
stay the same (`FProperty::HasAnyPropertyFlags`).

* **Layouts:** `PdbLayout audit <our.pdb> <report> <shipped.pdb>`, then `filter-audit.sh`. Only excluded types may differ.
* **Imports:** diff `dumpbin /imports` of both DLLs. Each difference needs an explanation: a function only the shipped
  DLL imports is either called by inline code our headers lack or a difference in the plugin's sources.
* **Call sites:** for such a function, list the callers in the shipped DLL (`dumpbin /disasm:nobytes` prints imported
  names; function ranges come from `llvm-pdbutil dump --symbols`).

The compilers differ (14.44 here, 14.50 in the devkit), so comparing whole functions is mostly noise from inlining and
register choice: function sizes, callees and member offsets per function produced thousands of differences, and those
read by hand turned out to be noise or the documented initializers of uninitialized `FArguments` members. Exports
differ too: the shipped DLL has 24 additional `TSharedFromThis::AsSharedPtr` instantiations, an inline ARK addition
without ABI impact.

---

## 5. PdbLayout cheat sheet

Easiest from PowerShell: `. .\Common.ps1; $T = Initialize-PdbLayout` builds the tool if needed and sets
`PDBLAYOUT_CACHE` / `LLVM_PDBUTIL` from the `Common.ps1` configuration. From bash, set them yourself:

```bash
export PDBLAYOUT_CACHE=Intermediate/PdbCache   # dumps are cached here (big!); Common.ps1: $PdbCacheDir
export LLVM_PDBUTIL=...                        # optional, else llvm-pdbutil on PATH
T=Tools/bin/PdbLayout/PdbLayout.exe
$T layout <pdb> SWidget "SButton::FArguments"          # members with offsets, bitfields, vtable slots, enumerators
$T diff <our.pdb> <devkit.pdb> UObject FArchive         # what changed
$T audit <our.pdb> <report.txt> <devkit pdbs...>        # all types (Test-ArkAbi.ps1 wraps this)
$T patch <our.pdb> <sdkRoot> <engineRootInPdb> <report> <devkit pdbs...> [--exclude AbiExclusions.txt]
$T probe <our.pdb> <sdkRoot> <engineRootInPdb> <exclusions> <devkit pdbs...> [--renames AbiRenames.txt]   # Test-LayoutIndependence.ps1
$T genclass <devkit.pdb> <out.h> <sdkRoot> <API macro> <includes> <types...>   # New-ArkClassHeaders.ps1
$T scan <header> <line> <class|struct> <Name> [sdkRoot]  # debug the header scanner on one type
```

The devkit PDB to use for a type is the one of the module that defines it (first match in the order
Core, CoreUObject, ApplicationCore, InputCore, Json, SlateCore, Slate, RenderCore, Engine, ...).
`<engineRootInPdb>` is the root path recorded in the reference PDB: `D:\ASA\UE_5.8` for a UBT baseline PDB,
the SDK root for PDBs built by `Build.ps1`.

---

## 6. Pitfalls that cost time (and their fixes, already in the scripts)

* **Stale PDB types:** `link.exe` merges into an existing PDB and keeps old type records - an audit then reports
  long-fixed differences. `Build.ps1` deletes the PDB before linking.
* **MSVC vtable order of overloads:** overloaded virtual functions are grouped at the first declaration and laid out
  in *reverse* declaration order. A new overload that must come first in the vtable is declared *after* the existing
  one (`UEdGraphSchema::CopyPinLinks`). The patcher knows this.
* **Access is part of the mangled name** (`MEAA` protected, `UEAA` public): an access change in ARK shows up as an
  unresolved external, not as a compile error. Data member access does not affect layout.
* **Methods ARK made virtual:** remove the old non-virtual declaration, keep the `*_API` macro on the new one.
* **UHT:** engine modules from an installed engine have `SaveExportedHeaders=false` - must be forced on;
  private headers of engine modules are needed for type resolution; `UFUNCTION` declarations need parameter
  names; `GENERATED_BODY()` of a UCLASS leaves `private:`, `GENERATED_UCLASS_BODY()` leaves `public:`;
  `-Log=` conflicts with UBT's own log file.
* **UHT line numbers:** `GENERATED_BODY` macros are keyed by line number; never shift lines in a header without
  rerunning UHT (the build always reruns it). Probes are inserted on existing lines for that reason.
* **Paths:** MSVC dependency files contain lower-cased paths (restore case before using them); engine source files
  are read-only (clear the attribute after copying); files mix CRLF/LF (patches are stored with LF, the line
  ending of the vanilla file is restored after applying).
* **Shells:** Git Bash rewrites `/nologo`-style switches into paths - use `-nologo` with `dumpbin`/`link` from bash.
  PowerShell 5.1 turns native stderr output into a terminating error under `$ErrorActionPreference='Stop'`.
  Windows Defender (AMSI) blocks PowerShell scripts that P/Invoke `LoadLibrary`; use the native
  `Tools\LoadTest\LoadTest.cpp` instead.
* **Plugins that compile engine-private code:** ElectronicNodes adds engine `Private\` folders to its include path
  and `#include`s engine `.cpp` files (`AnimGraphConnectionDrawingPolicy.cpp`, `SGraphPinPose.cpp`). Those are
  copied into the SDK like headers, and the engine code inside reads members of types that other plugins only
  passed around as pointers (`UAnimBlueprintGeneratedClass`). `Test-LayoutIndependence.ps1` catches this. Take
  such a type off `Config\AbiExclusions.txt` and patch it (`Test-ArkAbi.ps1 -Patch`).
* **Runtime hot patches:** a plugin that overwrites engine functions in memory (ElectronicNodes `FHotPatch`)
  replaces the *devkit's* function. Disassemble the devkit function (address from `llvm-pdbutil dump --symbols`,
  then `dumpbin /disasm /range:`) and compare it with the vanilla source before trusting the replacement.
* **Patches applied twice:** exporting before a `Repatch-ArkSdk.ps1` run puts the patcher's changes into
  `ArkPatches\`, and the repatch adds them again (redefinition errors). The stamp guard (2.7) now refuses that;
  export only at the end.
* **Members by value of new types:** an ARK member like `FWorldCachedViewInfo::FinalPostProcessSettings` needs the
  definition of its type. The patcher adds the include of the type's header (`// [ARK] member type`), taken from the
  type's PDB location; `.inl` locations (`FString`) are skipped because they cannot be included on their own.
* **Layout test noise:** the probe build has different paths, so the comparison normalizes MSVC anonymous
  namespace names (`?A0x<hash>@`), UHT file IDs and package hashes, drops `.bss` sizes and compares instructions
  without their bytes. Types declared outside the SDK (Windows SDK) are not probed; exclusions marked `[no probe]`
  in `AbiExclusions.txt` are skipped (e.g. a template the plugin instantiates with its own copy).
* **Engine plugins/modules with dependents:** the capture of a plugin or module that also exists in the engine
  (ToolsetRegistry, JsonUtilitiesEditor) fails ("should not reference module"): the engine's `UnrealEditor` target
  builds all engine modules (`bBuildAllModules`), and UBT rejects engine modules referencing a project plugin, even
  disabled ones. When an engine `.uplugin` names the plugin or an engine `.Build.cs` names one of its modules,
  `Capture-UbtBaseline.ps1` builds a project editor target (`HostEditor`, same shared build environment) instead,
  which only includes enabled plugins.
* **Inline generated code:** sources with `#include UE_INLINE_GENERATED_CPP_BY_NAME(X)` compile `X.gen.cpp`
  themselves; `Build.ps1` leaves those out of the `.gen.cpp` unity file (as UBT does), else LNK2005.
* **Headers new to the SDK:** a header no plugin compiled before contains only `ArkPatches` changes of its own; an
  override of a virtual ARK replaced elsewhere still has the vanilla signature (`LevelBounds.h`:
  `GetComponentsBoundingBox`, error C3668). Fix it in `Apply-ManualArkEdits.sh` (rules that match all overrides),
  re-run it, export at the end.
* **Templates ARK changed:** a template instantiation the plugin compiles itself (`SComboBox<TSharedPtr<FString>>`:
  ARK added `OnComboBoxClosing`) shows up in the audit as an unexplained difference of the instantiation and its
  `FArguments`. Compare `PdbLayout layout` of the devkit instantiation, find the uses in its member functions
  (disassemble the COMDAT copies, 4.1) and edit the template in `Apply-ManualArkEdits.sh`.
* **`-Patch` needs a rebuild in between:** the patcher works from the PDB, which still shows the old layout until the
  plugin is rebuilt. A second `-Patch` without a rebuild inserts the same members again. A multi-module plugin runs it
  once per module, and so does one call each for several plugins: patch from one PDB, rebuild, then audit again.
* **Check the diff after `-Patch`:** it also expands the opaque `ArkData` of the `Public\Ark\*.h` copies in the SDK into
  named members (`New-ArkSdk.ps1 -HeadersOnly` restores them from `ArkHeaders\`), re-inserts members of opaque
  types (`OnClosing`, `ActiveExcludedBoxesChangedDelegate`) and un-opaques `FRayTracingGeometry SourceRayTracingGeometry`.
* **Enums declared on one line:** `gentypes` (`Update-ArkTypes`) only recognizes an enum declaration on a line of its
  own. An engine enum declared inside a one-line `namespace X { enum class Y : T; }` looks like an ARK-only type and
  is declared in `ArkTypes.h` with the PDB's type (C3431/C3433 for `UE::Cook::ECookEvent`). Declare it across several
  lines in `Config\ArkForwardDeclarations.h`.
* **Memory:** big plugins (ue_mcp_bridge) exhaust memory with one compiler per CPU; use `-Jobs 4`.
* **Do not modify the devkit** (`D:\ASA\ARKDevkitBeta`): everything reads from it only; REA needs copies on NTFS.
* **Disk:** `Intermediate\PdbCache` grows to 30-50 GB (text dumps of the devkit PDBs); safe to delete.
