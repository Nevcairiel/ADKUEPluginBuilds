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
# FMaterialCompilationOutput declares its members with LAYOUT_FIELD macros, which the patcher cannot anchor on
# (members and offsets from ShooterGameEditor-Engine.pdb: +0x1D8 / +0x1DC, bits 3 and 4 of +0xCD3)
M=Engine/Public/MaterialShared.h
ins $M '\tLAYOUT_FIELD\(uint8, UsedPathTracingBufferTextures\);' '	LAYOUT_FIELD(uint32, AuxTexturesRead); // [ARK]
	LAYOUT_FIELD(uint32, AuxTexturesWrite); // [ARK]'
ins $M '\tLAYOUT_BITFIELD\(uint8, bUsesPerInstanceCustomData, 1\);' '	LAYOUT_BITFIELD(uint8, bUsesPerInstanceDynamicCustomData, 1); // [ARK]
	LAYOUT_BITFIELD(uint8, bUsesComputeMaterialSplatData, 1); // [ARK]'
# APlayerController RPCs: UHT declares their _Implementation/_Validate virtuals in GENERATED_BODY, in reverse order of
# the UFUNCTION declarations, so ARK's vtable changes are edits of the UFUNCTIONs (vtable from
# ShooterGameEditor-Engine.pdb, parameter names from its S_LOCAL records; names marked "assumed" were optimized out,
# defaults of new parameters are assumed - they only matter to callers)
P=Engine/Classes/GameFramework/PlayerController.h
sub() { # file, perl regex, replacement, marker that shows the edit is done
  grep -qF -- "$4" "$1" && return 0
  perl -0777 -pi -e 'BEGIN{$re=shift; $rep=shift} s/$re/$rep/ee or die "pattern not found: $re\n"' "$2" "$3" "$1"; }
sub $P '(FForceFeedbackParameters Params = FForceFeedbackParameters\(\))\);' '"$1, float IntensityMultiplier = 1.f); // [ARK] third parameter (name assumed)"' 'IntensityMultiplier = 1.f'
sub $P '(FRotator UserPlaySpaceRot = FRotator::ZeroRotator)\);' '"$1, float Speed = 1.f, bool bArkUnknown = false); // [ARK] Speed, bool (name assumed)"' 'float Speed = 1.f'
sub $P 'UFUNCTION\(reliable, server, WithValidation, SealedEvent\)(\r?\n\tENGINE_API void ServerUpdateLevelVisibility\()' '"UFUNCTION(reliable, server, WithValidation) // [ARK] not sealed: virtual _Implementation$1"' 'not sealed: virtual _Implementation'
# BPPostRender is a BlueprintNativeEvent in the devkit whose _Implementation is the first virtual of APlayerController:
# UHT emits the wrapper of the last declared UFUNCTION first, so it is declared at the end of the class (and the
# explicit declaration the patcher inserted for it is dropped)
sub $P '\r?\n\t(?:\w+_API )?virtual void BPPostRender_Implementation\(UCanvas\*\); // \[ARK\]' '""' 'UFUNCTION(BlueprintNativeEvent) // [ARK]'
sub $P '(ClientAckTimeDilation\(float TimeDilation, int32 ServerStep\);[\s\S]*?)(\r?\n)(\};)' '"$1$2public:$2\tUFUNCTION(BlueprintNativeEvent) // [ARK] _Implementation is the first virtual of the class: declared last$2\tENGINE_API void BPPostRender(UCanvas* Canvas);$2$3"' 'UFUNCTION(BlueprintNativeEvent) // [ARK]'
# new RPCs (UE4 camera anims, clipboard): declared before the RPC they follow in the vtable
sub $P '(\tUFUNCTION\([^\n]*\)\r?\n\tENGINE_API void ClientStartCameraShake\()' '"\tUFUNCTION(reliable, client) // [ARK]\r\n\tENGINE_API void CopyStringToClipboard(const FString& S); // [ARK]\r\n\tUFUNCTION(unreliable, client) // [ARK]\r\n\tENGINE_API void ClientPlayCameraAnim(class UCameraAnim* AnimToPlay, float Scale = 1.f, float Rate = 1.f, float BlendInTime = 0.f, float BlendOutTime = 0.f, bool bLoop = false, bool bRandomStartTime = false, ECameraShakePlaySpace Space = ECameraShakePlaySpace::CameraLocal, FRotator CustomPlaySpace = FRotator::ZeroRotator); // [ARK]\r\n$1"' 'void CopyStringToClipboard('
sub $P '(\tUFUNCTION\([^\n]*\)\r?\n\tENGINE_API void ClientStopCameraShake\()' '"\tUFUNCTION(reliable, client) // [ARK]\r\n\tENGINE_API void ClientStopCameraAnim(class UCameraAnim* AnimToStop); // [ARK]\r\n$1"' 'void ClientStopCameraAnim('
ins $P '#include "GameFramework/Controller.h"' '#include "Ark/CameraAnim.h" // [ARK] UCameraAnim (RPC parameters)'
# ARK replaced AActor::GetVelocity() by GetVelocity(bool); the classes using the transform-hiding macro do not override it
sub Engine/Classes/GameFramework/Actor.h '\tvirtual FVector GetVelocity\(\) const override \{ return Super::GetVelocity\(\); \} \\\r?\n' '"\t/* [ARK] no GetVelocity override */ \\\r\n"' '[ARK] no GetVelocity override'
# USceneComponent::InternalSetVisibility (ARK virtual, at its vtable position) uses the nested enum that is defined
# further down: declare it early, with the same access. Protected (private in vanilla): ARK's ULightComponentBase
# overrides InternalSetVisibility
sub Engine/Classes/Components/SceneComponent.h '(\r?\n)private:(\r?\n\t/\*\* \r?\n\t \* Enum that dictates what propagation policy[\s\S]*?\r?\n\t\};\r?\n)' '"$1protected: // [ARK] enum protected$2private:$1"' '[ARK] enum protected'
sub Engine/Classes/Components/SceneComponent.h '(\r?\n)(\t(?:\w+_API )?virtual void InternalSetVisibility\(bool, USceneComponent::EVisibilityPropagation, bool\);)' '"$1protected:$1\tenum class EVisibilityPropagation : uint8; // [ARK] declared early for InternalSetVisibility$1protected:$1$2"' 'declared early for InternalSetVisibility'
# ARK's USkeletalMeshComponent overrides UpdateComponentToWorld: not final in the devkit
sub Engine/Classes/Components/SceneComponent.h '(virtual void UpdateComponentToWorld\([^)]*\)) override final' '"$1 override /* [ARK] not final */"' '[ARK] not final'
# members by value of engine types the vanilla header does not include
ins Engine/Classes/Components/PrimitiveComponent.h '#include "CoreMinimal.h"' '#include "Math/GenericOctreePublic.h" // [ARK] FOctreeElementId2 members'
# FCollisionResponseContainer: the named channel bytes of the C++ union follow ARK's ECollisionChannel
# (ShooterGameEditor-Engine.pdb: WorldStatic WorldDynamic Visibility PhysicsBody Destructible GameTraceChannel1..50 + EnumArray[64])
CH="\t\t\t// [ARK] channel names of the devkit (ShooterGameEditor-Engine.pdb)"
for n in WorldStatic WorldDynamic Visibility PhysicsBody Destructible $(seq -f 'GameTraceChannel%g' 1 50); do CH="$CH\r\n\t\t\tuint8 $n;"; done
sub Engine/Classes/Engine/EngineTypes.h '(\tunion\r?\n\t\{\r?\n\t\tstruct\r?\n\t\t\{)[\s\S]*?(\r?\n\t\t\};\r?\n\t\tuint8 EnumArray\[64\];)' "\"\$1\\r\\n$CH\$2\"" 'channel names of the devkit'
# FSlateUser::Create(int32, ...) is public in the devkit; it sits under the SLATE_SCOPE: label the patcher does not see
sub Slate/Public/Framework/Application/SlateUser.h 'SLATE_SCOPE:(\r?\n)(\tstatic TSharedRef<FSlateUser> Create\(int32)' '"SLATE_SCOPE:$1public: // [ARK] public in the devkit$1$2"' 'public in the devkit'
# AActor virtuals whose signature ARK changed: parameter names from the devkit's S_LOCAL records, defaults of the
# vanilla declaration (Destroy, the first two GetComponentsBoundingBox flags) and false for the new opt-in flags
# (bIsForRagdoll, bForLevelBounds). Default arguments are not inherited, so every override in the SDK gets them too.
for f in $(grep -rlE --include=*.h 'GetVelocity\(bool\) const|Destroy\(bool, bool\)|GetComponentsBoundingBox\(bool, bool, bool\) const' ..); do
  perl -pi -e 's/GetVelocity\(bool\) const/GetVelocity(bool bIsForRagdoll = false) const/g; s/\bDestroy\(bool, bool\)/Destroy(bool bNetForce = false, bool bShouldModifyLevel = true)/g; s/GetComponentsBoundingBox\(bool, bool, bool\) const/GetComponentsBoundingBox(bool bNonColliding = false, bool bIncludeFromChildActors = false, bool bForLevelBounds = false) const/g' "$f"
done
# enums whose underlying type ARK changed but that have no source location in the PDBs (nested, unused by name)
sub Engine/Public/Engine/ScopedMovementUpdate.h '(\tenum class EOverlapState)(\r?\n)' '"$1 : uint8 // [ARK] 1 byte in the devkit$2"' '1 byte in the devkit'
# FActorLastRenderTime: ARK stores the times as double and AActor holds it through a TSharedPtr
A=Engine/Classes/GameFramework/Actor.h
sub $A '(\r?\n\t\treturn )(LastRenderTime\.load\(std::memory_order_relaxed\);)' '"$1(float)$2 // [ARK] double"' '(float)LastRenderTime.load'
sub $A 'InActor->LastRenderTime\.SetLastRenderTime\(LastRenderTime\);' '"if (InActor->LastRenderTime) { InActor->LastRenderTime->SetLastRenderTime(LastRenderTime); } // [ARK] TSharedPtr"' 'InActor->LastRenderTime->SetLastRenderTime'
sub $A 'return \(InActor \? &InActor->LastRenderTime : nullptr\);' '"return (InActor ? InActor->LastRenderTime.Get() : nullptr); // [ARK] TSharedPtr"' 'InActor->LastRenderTime.Get()'
S=Engine/Public/PrimitiveSceneInfoData.h
sub $S '(\r?\n\t\treturn )(LastRenderTime\.load\(std::memory_order_relaxed\);)' '"$1(float)$2 // [ARK] double"' '(float)LastRenderTime.load'
sub $S '(\r?\n\t\treturn )(LastRenderTimeOnScreen\.load\(std::memory_order_relaxed\);)' '"$1(float)$2 // [ARK] double"' '(float)LastRenderTimeOnScreen.load'
# FMeshBuildSettings: ARK replaced bGenerateDistanceFieldAsIfTwoSided by MeshDistanceFieldTwoSidedSetting
E=Engine/Classes/Engine/EngineTypes.h
sub $E '\r?\n\t\t, bGenerateDistanceFieldAsIfTwoSided\(false\)' '"\r\n\t\t/* [ARK] no bGenerateDistanceFieldAsIfTwoSided */"' 'no bGenerateDistanceFieldAsIfTwoSided */'
sub $E '\r?\n\t\t\t&& bGenerateDistanceFieldAsIfTwoSided == Other\.bGenerateDistanceFieldAsIfTwoSided' '"\r\n\t\t\t/* [ARK] no bGenerateDistanceFieldAsIfTwoSided == */"' 'no bGenerateDistanceFieldAsIfTwoSided == */'
echo "manual ARK edits applied"

# Inline bodies of ARK virtual functions that the devkit does not export (defined in ARK's headers).
# Recovered from the COMDAT copies in ShooterGameEditor-EngineSettings.dll / ShooterGameEditor-Slate.dll:
#   b0 01 c3 = return true;   32 c0 c3 / 33 c0 c3 = return false / nullptr;   c2 00 00 = {}
body() { # file, exact declaration (without ';'), body
  grep -qF -- "$2 $3" "$1" && return 0
  # inline: without the export macro (the devkit does not export functions defined in its headers)
  perl -0777 -pi -e 'BEGIN{$d=shift; $b=shift} s/(?:\b\w+_API )?\Q$d\E;/$d $b/ or die "declaration not found: $d\n"' "$2" "$3" "$1"; }
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
# UActorComponent: ARK virtuals defined in ARK's header (COMDAT copies in ShooterGameEditor-Engine.dll)
C=Engine/Classes/Components/ActorComponent.h
body $C 'virtual bool CanSkipInstancingWhenPrevented() const' '{ return true; }'
body $C 'virtual void ReceiveComponentDamage(float, const FDamageEvent&, AController*, AActor*)' '{}'
body $C 'virtual bool AllowOctreeUsing()' '{ return true; }'
body $C 'virtual bool IsSceneComponent() const' '{ return false; }'
body $C 'virtual bool IsSkeletalMeshComponent() const' '{ return false; }'
body $C 'virtual bool IsCharacterMovementComponent() const' '{ return false; }'
body $C 'virtual bool IsPrimitiveComponent() const' '{ return false; }'
body $C 'virtual bool IsMeshComponent() const' '{ return false; }'
body $C 'virtual TArray<TWeakObjectPtr<USkinnedMeshComponent>>* GetFollowersSkinnedMeshComponentsForEndOfFrameUpdate()' '{ return nullptr; }'
body $C 'virtual bool HasLeaderForEndOfFrameUpdate() const' '{ return false; }'
body $C 'virtual bool HasFollowersForEndOfFrameUpdate() const' '{ return false; }'
sub $C 'ENGINE_API virtual bool Primal_IsComponent\(\) const;' '"virtual bool Primal_IsComponent() const override { return true; }"' 'Primal_IsComponent() const override { return true; }'
# ISourceControlState: the COMDAT copies in ShooterGameEditor-SubversionSourceControl.dll throw std::runtime_error
S=../Developer/SourceControl/Public/ISourceControlState.h
ins $S '#include "Textures/SlateIcon.h"' '#include <stdexcept> // [ARK] std::runtime_error in IsOurLock / IsOtherLock'
body $S 'virtual bool IsOurLock() const' '{ throw std::runtime_error("The source control provider currently being used has not implemented the ISourceControlState::IsOurLock() method!"); }'
body $S 'virtual bool IsOtherLock() const' '{ throw std::runtime_error("The source control provider currently being used has not implemented the ISourceControlState::IsOtherLock() method!"); }'
# TPanelChildren<T> overrides IsChildValid in the devkit (template: its vtable is emitted in every plugin). Body from
# the COMDAT TPanelChildren<SBoxPanel::FSlot>::IsChildValid in ShooterGameEditor-SlateCore.dll: index check, slot not
# null, widget of the slot not null.
sub SlateCore/Public/Layout/Children.h '(\tTArray<TUniquePtr<SlotType>> Children;)(\r?\n)' '"$1$2protected:$2\tvirtual bool IsChildValid(int32 Index) const override { return Children.IsValidIndex(Index) && Children[Index].IsValid() && Children[Index]->GetWidget().ToSharedPtr().IsValid(); } // [ARK] TPanelChildren::IsChildValid$2private:$2"' '[ARK] TPanelChildren::IsChildValid'
# ARK's FAnimNode_ModifyBone overrides FAnimNode_SkeletalControlBase::EvaluateComponentSpace_AnyThread: not final in the devkit
sub AnimGraphRuntime/Public/BoneControllers/AnimNode_SkeletalControlBase.h '(virtual void EvaluateComponentSpace_AnyThread\(FComponentSpacePoseContext& Output\)) final;' '"$1 /* [ARK] not final */;"' '[ARK] not final'
# members ARK removed that inline code of the vanilla header uses
sub AIModule/Classes/BehaviorTree/BehaviorTreeComponent.h '\{ CSVTickStatName = InCSVTickStatName; \}' '"{ /* [ARK] no CSVTickStatName */ }"' '[ARK] no CSVTickStatName'
for n in RepActiveDataLayerNames RepLoadedDataLayerNames RepEffectiveActiveDataLayerNames RepEffectiveLoadedDataLayerNames; do
  sub Engine/Public/WorldPartition/DataLayer/WorldDataLayers.h "MARK_PROPERTY_DIRTY_FROM_NAME\\(AWorldDataLayers, $n, this\\);\\r?\\n\\t*$n = [^;]*;" "\"/* [ARK] no $n */\"" "[ARK] no $n */"
done
# ACharacter::ReplicatedBasedMovement is ARK's FReplicatedBasedMovementInfo
sub Engine/Classes/GameFramework/Character.h 'inline const FBasedMovementInfo& GetReplicatedBasedMovement\(\)' '"inline const FReplicatedBasedMovementInfo& GetReplicatedBasedMovement() /* [ARK] type */"' 'FReplicatedBasedMovementInfo& GetReplicatedBasedMovement()'
# Non-virtual functions with ARK parameters (mangled names in SDK\Lib\*.def). Names marked "assumed" are not in the
# PDBs; defaults keep the vanilla behavior where the disassembly shows it:
#  - USceneComponent: SetVisibility/SetHiddenInGame take a third bool (execSetVisibility / execSetHiddenInGame);
#    ARK has no private SetVisibility(bool, EVisibilityPropagation), its public one calls the ARK virtual
#    InternalSetVisibility(bNew, Propagate|DirtyOnly, bool)
#  - UAnimInstance::Montage_IsPlaying: the float is ignored when 0
#  - UProgressBar::SetPercent: the int is stored in the ARK member CurrentInterpolationKey
SC=Engine/Classes/Components/SceneComponent.h
sub $SC 'ENGINE_API void SetVisibility\(bool bNewVisibility, EVisibilityPropagation PropagateToChildren\);' '"void SetVisibility(bool bNewVisibility, EVisibilityPropagation PropagateToChildren, bool bArkUnknown = false) { InternalSetVisibility(bNewVisibility, PropagateToChildren, bArkUnknown); } // [ARK] not exported: calls InternalSetVisibility"' '[ARK] not exported: calls InternalSetVisibility'
sub $SC 'ENGINE_API void SetHiddenInGame\(bool bNewHiddenInGame, EVisibilityPropagation PropagateToChildren\);' '"ENGINE_API void SetHiddenInGame(bool bNewHiddenInGame, EVisibilityPropagation PropagateToChildren, bool bArkUnknown = false); // [ARK] bool (name assumed)"' 'EVisibilityPropagation PropagateToChildren, bool bArkUnknown = false); // [ARK] bool'
sub $SC 'void SetVisibility\(bool bNewVisibility, bool bPropagateToChildren=false\)(\s*\{\s*SetVisibility\(bNewVisibility, bPropagateToChildren \? EVisibilityPropagation::Propagate : EVisibilityPropagation::DirtyOnly)\);' '"void SetVisibility(bool bNewVisibility, bool bPropagateToChildren=false, bool bArkUnknown=false) /* [ARK] bool (name assumed) */$1, bArkUnknown);"' 'bool bPropagateToChildren=false, bool bArkUnknown=false) /* [ARK]'
sub $SC 'void SetHiddenInGame\(bool NewHidden, bool bPropagateToChildren=false\)(\s*\{\s*SetHiddenInGame\(NewHidden, bPropagateToChildren \? EVisibilityPropagation::Propagate : EVisibilityPropagation::DirtyOnly)\);' '"void SetHiddenInGame(bool NewHidden, bool bPropagateToChildren=false, bool bArkUnknown=false) /* [ARK] bool (name assumed) */$1, bArkUnknown);"' 'void SetHiddenInGame(bool NewHidden, bool bPropagateToChildren=false, bool bArkUnknown=false)'
sub Engine/Classes/Kismet/GameplayStatics.h '(const AActor\* OwningActor = nullptr, const UInitialActiveSoundParams\* InitialParams = nullptr)\);' '"$1, bool bArkUnknown = false); // [ARK] bool (name assumed)"' 'InitialParams = nullptr, bool bArkUnknown = false); // [ARK]'
sub Core/Public/Misc/ConfigCacheIni.h 'CORE_API bool Flush\(bool bRemoveFromCache, const FString& Filename=TEXT\(""\)\);' '"CORE_API bool Flush(bool bRemoveFromCache, const FString& Filename=TEXT(\"\"), bool bArkUnknown=false); // [ARK] bool (name assumed)"' 'bool bArkUnknown=false); // [ARK] bool'
sub Engine/Classes/Kismet/KismetRenderingLibrary.h '(CreateRenderTarget2D\([^;]*bool bSupportUAVs = false)\);' '"$1, bool bArkUnknown = false); // [ARK] bool (name assumed)"' 'bSupportUAVs = false, bool bArkUnknown = false); // [ARK]'
sub Landscape/Classes/LandscapeInfo.h 'LANDSCAPE_API void FixupProxiesTransform\(bool bDirty = false\);' '"LANDSCAPE_API void FixupProxiesTransform(bool bDirty = false, bool bArkUnknown = false); // [ARK] bool (name assumed)"' 'FixupProxiesTransform(bool bDirty = false, bool bArkUnknown = false)'
sub Engine/Classes/Animation/AnimInstance.h 'ENGINE_API bool Montage_IsPlaying\(const UAnimMontage\* Montage\) const;' '"ENGINE_API bool Montage_IsPlaying(const UAnimMontage* Montage, float ArkUnknown = 0.f) const; // [ARK] float, ignored when 0 (name assumed)"' 'float ArkUnknown = 0.f) const; // [ARK]'
sub UMG/Public/Components/ProgressBar.h 'FieldNotify, Getter, Setter, BlueprintSetter="SetPercent",' '"FieldNotify, Getter, /* [ARK] SetPercent has two parameters: no Setter */"' '[ARK] SetPercent has two parameters'
sub UMG/Public/Components/ProgressBar.h 'UMG_API void SetPercent\(float InPercent\);' '"UMG_API void SetPercent(float InPercent, int32 InterpolationKey = 0); // [ARK] stored in CurrentInterpolationKey (name assumed)"' 'int32 InterpolationKey = 0); // [ARK]'
# FRayTracingGeometry is excluded (Config\AbiExclusions.txt: ARK shrank it to 0x60 bytes and removed members its
# vanilla inline code uses); FSkeletalMeshLODRenderData embeds it by value (no inline code uses the member):
# opaque storage of the devkit's size keeps the following members at the devkit's offsets
sub Engine/Public/Rendering/SkeletalMeshLODRenderData.h '\tFRayTracingGeometry SourceRayTracingGeometry;' '"\talignas(8) uint8 SourceRayTracingGeometry[0x60]; // [ARK] FRayTracingGeometry of the devkit (excluded type), opaque"' '[ARK] FRayTracingGeometry of the devkit'
# TOctree2<> (template, every instantiation in the devkit PDBs): ARK appended uint32 UserData (also a constructor and
# SetElementId parameter; the template code is compiled into each plugin, only the layout matters)
sub Core/Public/Math/GenericOctree.h '(\tTArray<FNodeIndex> FreeList;\r?\n\t/\*\* The extent of a leaf at the maximum allowed depth of the tree. \*/\r?\n\tFVector::FReal MinLeafExtent;)(\r?\n)' '"$1$2\tuint32 UserData = 0; // [ARK]$2"' 'uint32 UserData = 0; // [ARK]'
# UCheckBox::K2_Gate_CheckedState (PROPERTY_BINDING_IMPLEMENTATION macro) is public in the devkit (access is part of
# the mangled name)
sub UMG/Public/Components/CheckBox.h '(\r?\n)(PRAGMA_DISABLE_DEPRECATION_WARNINGS\r?\n\tPROPERTY_BINDING_IMPLEMENTATION\(ECheckBoxState, CheckedState\))' '"$1public: // [ARK] K2_Gate_CheckedState public$1$2"' '[ARK] K2_Gate_CheckedState public'
# EFunctionInputType (no source location in the reference PDB): ARK enumerator from ShooterGameEditor-Engine.pdb
sub Engine/Public/Materials/MaterialExpressionFunctionInput.h '(\tFunctionInput_Substrate UMETA\(DisplayName = "Substrate"\),)(\r?\n)' '"$1$2\tFunctionInput_AuxSceneTexture, // [ARK]$2"' 'FunctionInput_AuxSceneTexture, // [ARK]'
sub Engine/Public/Materials/MaterialExpressionFunctionInput.h '#define EXPECTED_NUM_INPUT_TYPES 13' '"#define EXPECTED_NUM_INPUT_TYPES 14 // [ARK]"' 'EXPECTED_NUM_INPUT_TYPES 14'
echo "inline ARK virtual bodies applied"
