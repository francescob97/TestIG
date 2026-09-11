// ============================================================================
//  UGpuShareSceneSubsystem
//
//  Costruisce l'intera scena in C++: un piano, tre cubi in movimento, luce,
//  cielo, e l'attore di cattura. Nessun Blueprint, nessun asset binario nel
//  repository: l'unica cosa che devi fare a mano nell'editor e' creare una
//  Empty Level e salvarla (vedi docs/01-build-unreal.md).
//
//  NOTA UE:
//  un "Subsystem" e' un singleton gestito dall'engine con un ciclo di vita
//  ben definito. Ne esistono di vari tipi (Engine, GameInstance, World,
//  LocalPlayer). UTickableWorldSubsystem vive quanto il World e riceve un Tick,
//  che e' esattamente cio' che serve per animare i cubi. E' il sostituto
//  pulito del vecchio pattern "metto tutto in un Actor manager".
// ============================================================================

#pragma once

#include "CoreMinimal.h"
#include "Subsystems/WorldSubsystem.h"
#include "GpuShareSceneSubsystem.generated.h"

class AStaticMeshActor;
class AGpuShareCaptureActor;

UCLASS()
class GPUSHARESPIKE_API UGpuShareSceneSubsystem : public UTickableWorldSubsystem
{
	GENERATED_BODY()

public:
	// --- UWorldSubsystem ---------------------------------------------------
	virtual bool ShouldCreateSubsystem(UObject* Outer) const override;
	virtual void OnWorldBeginPlay(UWorld& InWorld) override;

	// --- FTickableGameObject -----------------------------------------------
	virtual void Tick(float DeltaTime) override;
	virtual TStatId GetStatId() const override;

private:
	void BuildScene(UWorld& World);
	AStaticMeshActor* SpawnMeshActor(UWorld& World, const TCHAR* MeshPath, const FTransform& Transform,
	                                 const FLinearColor& Color, const TCHAR* Name);

	UPROPERTY(Transient)
	TArray<TObjectPtr<AStaticMeshActor>> MovingCubes;

	UPROPERTY(Transient)
	TObjectPtr<AGpuShareCaptureActor> CaptureActor;

	double ElapsedTime = 0.0;
	bool bSceneBuilt = false;
};
