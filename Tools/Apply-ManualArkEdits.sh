#!/usr/bin/env bash
# Manual ARK edits in headers that are also modified by the automatic patcher (PdbLayout patch).
# Run after the patcher; idempotent. Values were recovered from the devkit PDBs / binaries:
#  - EAttachmentRule / EVisibilityBasedAnimTickOption: enumerators from ShooterGameEditor-Engine.pdb
#  - FArguments defaults: decompiled SButton/STextBlock/SEditableTextBox/SMenuAnchor/SRichTextBlock/SScrollBox
#    ::FArguments::FArguments in ShooterGameEditor-Slate.dll (REA/Ghidra). Members the devkit leaves
#    uninitialized are initialized to false.
set -e
# SDK root: argument, else ARK_SDK_ROOT (Common.ps1 setting, also set by Invoke-Bash), else the default <root>\SDK
SDK="${1:-${ARK_SDK_ROOT:-$(dirname "$0")/../SDK}}"
cd "$SDK/Engine/Source/Runtime"

ins() { # file, regex of anchor line, text inserted after it (line ending copied from the anchor)
  grep -qF -- "$3" "$1" && return 0
  perl -0777 -pi -e 'BEGIN{$re=shift; $add=shift} s/^((?:$re)[^\n]*?)(\r?\n)/$1$2$add$2/m or die "anchor not found\n"' "$2" "$3" "$1"; }

ins CoreUObject/Public/UObject/Script.h '	EX_FieldPathConst		= 0x6D,' '	EX_StringLocConst		= 0x6E, // [ARK]'
ins Engine/Classes/Engine/EngineBaseTypes.h '	VMI_StreamingMeshLODResidency = 48' '	VMI_GrassSliceColoration = 49 UMETA(DisplayName = "Grass Slice Coloration"), // [ARK]'
ins Engine/Classes/Engine/EngineTypes.h '\tSnapToTarget,' '	KeepWorldLegacy, // [ARK]'
grep -q LocalControlTickPoseAndRefreshBones Engine/Classes/Components/SkinnedMeshComponent.h || \
  perl -0777 -pi -e 's/(enum class EVisibilityBasedAnimTickOption : uint8(\r?\n)\{\r?\n)/$1\tLocalControlTickPoseAndRefreshBones, \/\/ [ARK]$2/ or die "EVisibilityBasedAnimTickOption\n"' Engine/Classes/Components/SkinnedMeshComponent.h

addinit() { # file, widget, initializers (inserted after the first entry of the SLATE_BEGIN_ARGS initializer list)
  grep -q "defaults recovered from ShooterGameEditor-Slate.dll" "$1" && return 0
  perl -0777 -pi -e 'BEGIN{$w=shift;$add=shift} s/(SLATE_BEGIN_ARGS\(\s*\Q$w\E\s*\)\r?\n[ \t]*:[^\n]*?)(\r?\n)/$1$2\t\t$add \/\/ [ARK] defaults recovered from ShooterGameEditor-Slate.dll$2/ or die "no SLATE_BEGIN_ARGS($w)\n"' "$2" "$3" "$1"; }
W=Slate/Public/Widgets
addinit $W/Input/SButton.h SButton ', _bUseCustomDisabledStyle(false)'
addinit $W/Text/STextBlock.h STextBlock ', _CenterVertically(false), _DisableInternalClipping(false), _NoDisabledEffect(false)'
addinit $W/Input/SEditableTextBox.h SEditableTextBox ', _bShouldHandleEscape(true)'
addinit $W/Input/SMenuAnchor.h SMenuAnchor ', _IgnoreClicksOutside(false)'
addinit $W/Text/SRichTextBlock.h SRichTextBlock ', _CenterVertically(false)'
addinit $W/Layout/SScrollBox.h SScrollBox ', _NumPreviewWidgets(0), _NumWidgetsBeforeWrapping(0), _WidgetSize(FVector2D::ZeroVector), _WidgetPadding(FVector2D::ZeroVector), _WidgetHorizontalAlignment(HAlign_Left), _WidgetVerticalAlignment(VAlign_Top), _UseWidgetPaddingAsMargin(false), _WidgetMargin(FMargin(0.f))'
echo "manual ARK edits applied"

# Inline bodies of ARK virtual functions that the devkit does not export (defined in ARK's headers).
# Recovered from the COMDAT copies in ShooterGameEditor-EngineSettings.dll / ShooterGameEditor-Slate.dll:
#   b0 01 c3 = return true;   32 c0 c3 / 33 c0 c3 = return false / nullptr;   c2 00 00 = {}
body() { # file, exact declaration (without ';'), body
  grep -qF -- "$2 $3" "$1" && return 0
  perl -0777 -pi -e 'BEGIN{$d=shift; $b=shift} s/\Q$d\E;/$d $b/ or die "declaration not found: $d\n"' "$2" "$3" "$1"; }
O=CoreUObject/Public/UObject/Object.h
body $O 'virtual void PostInterpChange(FProperty*)' '{}'
body $O 'virtual bool MoveTexturesToJITTextures()' '{ return false; }'
body $O 'virtual bool AllowSubobjectInstancing()' '{ return true; }'
body $O 'virtual bool Primal_AllowInstancingOfSubobjects(FName) const' '{ return true; }'
body $O 'virtual bool Primal_IsActor() const' '{ return false; }'
body $O 'virtual bool Primal_IsComponent() const' '{ return false; }'
body $O 'virtual bool IsMarkedForSeamlessTravel() const' '{ return false; }'
A=Core/Public/Serialization/Archive.h
body $A 'virtual bool ShouldSkipSerializingObject(UObject*) const' '{ return false; }'
body $A 'virtual bool UsingSaveOverride()' '{ return false; }'
body SlateCore/Public/Layout/ChildrenBase.h 'virtual bool IsChildValid(int) const' '{ return true; }'
body SlateCore/Public/Widgets/SWidget.h 'virtual bool IsObjectWidget() const' '{ return false; }'
body SlateCore/Public/Widgets/SWidget.h 'virtual UWidget* GetObjectWidget()' '{ return nullptr; }'
# ISourceControlState: the COMDAT copies in ShooterGameEditor-SubversionSourceControl.dll throw std::runtime_error
S=../Developer/SourceControl/Public/ISourceControlState.h
ins $S '#include "Textures/SlateIcon.h"' '#include <stdexcept> // [ARK] std::runtime_error in IsOurLock / IsOtherLock'
body $S 'virtual bool IsOurLock() const' '{ throw std::runtime_error("The source control provider currently being used has not implemented the ISourceControlState::IsOurLock() method!"); }'
body $S 'virtual bool IsOtherLock() const' '{ throw std::runtime_error("The source control provider currently being used has not implemented the ISourceControlState::IsOtherLock() method!"); }'
echo "inline ARK virtual bodies applied"
