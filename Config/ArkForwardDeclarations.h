// Forward declarations of ARK-only types (and of engine types used earlier than in vanilla) referenced by the
// ARK ABI patches of the engine headers. Force-included after Definitions.h.
// The class/struct keys match the devkit PDBs (they are part of the C++ name mangling).
#pragma once

#ifdef __cplusplus

// ARK gameplay classes
class AKillZVolume;
class AMatineeActor;
class APrimalPlayerController;
class UInterpTrackInstDirector;

// ARK structs/classes
class FModInfo;
struct FBlueprintHTTPRequestData;
struct FPrimalPropertyBag;
struct FClutterPropertyOverride;
struct FOverrideClutterLayer;
struct FPostProcessSettingsMaterialAdjuster;

// engine types referenced by ARK additions before their usual include
class IHttpRequest;
class IHttpResponse;
class ILevelPartitionInterface;
class ULevelSequenceDirector;
class UMaterialInterface;
class UWidget;

// ARK enums (opaque declarations with the devkit's underlying types)
namespace EPrimalEquipmentType { enum Type : int; }
namespace EAlignHorizontalUI { enum Type : int; }
namespace EAlignVerticalUI { enum Type : int; }

// generated: all other ARK-only types the patched SDK headers use (Update-ArkTypes in Common.ps1)
#if __has_include("Ark/ArkTypes.h")
#include "Ark/ArkTypes.h"
#endif

#endif
