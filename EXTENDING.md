# Using this setup for other editor plugins

This build system was written for BlueprintAssist, but nothing in the approach is specific to it. This document
collects everything needed to compile another UE editor plugin for the ARK Devkit (ShooterGameEditor, UE 5.8.1
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
`<Devkit>\Engine\Plugins\Marketplace\<Plugin>\` (that also guarantees the names the devkit expects).

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
in that one folder too). Missing modules (BlueprintAssist: `AssetSearch`) are skipped at link time - make sure the
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
.\Tools\Export-ArkPatches.ps1              # store the result as ArkPatches\*.patch
.\Tools\Test-LayoutIndependence.ps1 <Folder>
.\Tools\Test-DllLoad.ps1 <Folder>
```

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

`Build.ps1` builds exactly one module; `Initialize-Plugin` (Common.ps1) refuses `.uplugin` files with several
modules. To support them, make `$ModuleName` a list and loop over the modules in `Capture-UbtBaseline.ps1`
(one Config set per module), `Invoke-Uht.ps1` (one manifest entry per module, created from
`UhtPluginModule.json`) and `Build.ps1` (compile per module; link in dependency order, adding the import libraries
of the plugin's own modules); `ShooterGameEditor.modules` must list all of them.

### 2.6 Install / test in the devkit

Copy `Plugins\<Folder>\Build\Output\<Plugin>\Binaries` into the plugin folder below `<Devkit>\Engine\Plugins\...` (or a project
`Plugins` folder) and enable the plugin (`-EnablePlugins=<Plugin>` on the command line or the Plugins window).
The `.modules` file must carry the devkit's BuildId (taken from `Engine\Binaries\Win64\ShooterGameEditor.modules`
automatically); after a devkit update the plugin must be rebuilt (new BuildId, possibly new ABI - rerun the audit).

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
  `APrimalController`, `FPrimalViewportClient`). **A plugin that subclasses or deeply uses actors, components,
  worlds or player controllers needs these reconstructed** (full `UCLASS` definitions of the ARK base classes
  from the PDBs: members, vtable, UHT macros). This was out of scope for BlueprintAssist and is not done yet.

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
$T probe <our.pdb> <sdkRoot> <engineRootInPdb> <exclusions>   # Test-LayoutIndependence.ps1 uses this
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
* **Do not modify the devkit** (`D:\ASA\ARKDevkitBeta`): everything reads from it only; REA needs copies on NTFS.
* **Disk:** `Intermediate\PdbCache` grows to ~12 GB (text dumps of the devkit PDBs); safe to delete.
