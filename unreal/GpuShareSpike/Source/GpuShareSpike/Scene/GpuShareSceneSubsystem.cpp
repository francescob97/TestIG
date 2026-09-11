#include "Scene/GpuShareSceneSubsystem.h"

#include "Capture/GpuShareCaptureActor.h"
#include "GpuShareLog.h"

#include "Components/DirectionalLightComponent.h"
#include "Components/SkyAtmosphereComponent.h"
#include "Components/SkyLightComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/DirectionalLight.h"
#include "Engine/SkyLight.h"
#include "Engine/StaticMesh.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/World.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Materials/MaterialInterface.h"

namespace
{
	// Asset di ENGINE, sempre presenti: non finiscono nel repository.
	const TCHAR* PlaneMeshPath    = TEXT("/Engine/BasicShapes/Plane.Plane");
	const TCHAR* CubeMeshPath     = TEXT("/Engine/BasicShapes/Cube.Cube");
	const TCHAR* BasicMaterialPath = TEXT("/Engine/BasicShapes/BasicShapeMaterial.BasicShapeMaterial");

	constexpr int32 NumCubes = 3;
	constexpr float CubeOrbitRadiusCm = 400.0f;
	constexpr float CubeOrbitSpeed = 0.6f;      // giri al secondo / 2pi
	constexpr float CubeBobHeightCm = 120.0f;
}

bool UGpuShareSceneSubsystem::ShouldCreateSubsystem(UObject* Outer) const
{
	if (!Super::ShouldCreateSubsystem(Outer))
	{
		return false;
	}

	// Senza questo controllo il subsystem verrebbe creato anche nei mondi di
	// anteprima dell'editor (thumbnail dei materiali, preview delle mesh...),
	// riempiendoli di cubi.
	const UWorld* World = Cast<UWorld>(Outer);
	return World != nullptr && World->IsGameWorld();
}

void UGpuShareSceneSubsystem::OnWorldBeginPlay(UWorld& InWorld)
{
	Super::OnWorldBeginPlay(InWorld);
	BuildScene(InWorld);
}

TStatId UGpuShareSceneSubsystem::GetStatId() const
{
	// Boilerplate obbligatorio di FTickableGameObject: registra il subsystem
	// nel sistema di profiling di Unreal ("stat tickables").
	RETURN_QUICK_DECLARE_CYCLE_STAT(UGpuShareSceneSubsystem, STATGROUP_Tickables);
}

AStaticMeshActor* UGpuShareSceneSubsystem::SpawnMeshActor(
	UWorld& World, const TCHAR* MeshPath, const FTransform& Transform,
	const FLinearColor& Color, const TCHAR* Name)
{
	UStaticMesh* Mesh = LoadObject<UStaticMesh>(nullptr, MeshPath);
	if (Mesh == nullptr)
	{
		UE_LOG(LogGpuShare, Error, TEXT("Mesh di engine non trovata: %s"), MeshPath);
		return nullptr;
	}

	// Nessun SpawnParams.Name esplicito: se il nome fosse gia' in uso SpawnActor
	// fallirebbe restituendo nullptr, e il motivo non sarebbe evidente.
	FActorSpawnParameters SpawnParams;
	SpawnParams.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;

	AStaticMeshActor* Actor = World.SpawnActor<AStaticMeshActor>(AStaticMeshActor::StaticClass(), Transform, SpawnParams);
	if (Actor == nullptr)
	{
		return nullptr;
	}

	UStaticMeshComponent* MeshComponent = Actor->GetStaticMeshComponent();

	// NOTA UE: gli AStaticMeshActor nascono con mobility "Static". Un attore
	// Static non puo' essere mosso a runtime: Unreal lo considera parte della
	// geometria precalcolata e ignora (o asserta su) i cambi di transform.
	// Per animarli servono Movable.
	MeshComponent->SetMobility(EComponentMobility::Movable);
	MeshComponent->SetStaticMesh(Mesh);

	if (UMaterialInterface* BaseMaterial = LoadObject<UMaterialInterface>(nullptr, BasicMaterialPath))
	{
		// Un Material Instance Dynamic e' l'unico modo di cambiare parametri di
		// materiale a runtime senza creare asset.
		UMaterialInstanceDynamic* DynamicMaterial = UMaterialInstanceDynamic::Create(BaseMaterial, Actor);
		// Se il parametro non esiste in quel materiale la chiamata e' un no-op
		// innocuo: nessun crash, semplicemente il colore resta quello base.
		DynamicMaterial->SetVectorParameterValue(TEXT("Color"), Color);
		MeshComponent->SetMaterial(0, DynamicMaterial);
	}

	return Actor;
}

void UGpuShareSceneSubsystem::BuildScene(UWorld& World)
{
	if (bSceneBuilt)
	{
		return;
	}
	bSceneBuilt = true;

	// --- Piano --------------------------------------------------------------
	// La mesh Plane di engine e' 100x100 unita' (1x1 metro): scalata 20x fa
	// 20x20 metri, abbastanza per avere un riferimento visivo.
	SpawnMeshActor(World, PlaneMeshPath,
		FTransform(FRotator::ZeroRotator, FVector(0.0, 0.0, 0.0), FVector(20.0, 20.0, 1.0)),
		FLinearColor(0.25f, 0.27f, 0.30f), TEXT("GpuShareGroundPlane"));

	// --- Tre cubi -----------------------------------------------------------
	static const FLinearColor CubeColors[NumCubes] = {
		FLinearColor(0.90f, 0.25f, 0.20f),
		FLinearColor(0.20f, 0.75f, 0.35f),
		FLinearColor(0.25f, 0.45f, 0.95f),
	};

	MovingCubes.Reset();
	for (int32 Index = 0; Index < NumCubes; ++Index)
	{
		const FString CubeName = FString::Printf(TEXT("GpuShareCube%d"), Index);
		AStaticMeshActor* Cube = SpawnMeshActor(World, CubeMeshPath,
			FTransform(FRotator::ZeroRotator, FVector(0.0, 0.0, 100.0), FVector(1.5, 1.5, 1.5)),
			CubeColors[Index], *CubeName);
		if (Cube != nullptr)
		{
			MovingCubes.Add(Cube);
		}
	}

	// --- Luce direzionale ---------------------------------------------------
	{
		ADirectionalLight* Sun = World.SpawnActor<ADirectionalLight>(
			ADirectionalLight::StaticClass(),
			FTransform(FRotator(-42.0, 35.0, 0.0)), FActorSpawnParameters());
		if (Sun != nullptr)
		{
			if (UDirectionalLightComponent* LightComponent = Cast<UDirectionalLightComponent>(Sun->GetLightComponent()))
			{
				// SetMobility va chiamata sul COMPONENTE: AActor non la espone.
				LightComponent->SetMobility(EComponentMobility::Movable);
				LightComponent->SetIntensity(6.0f);   // lux
			}
		}
	}

	// --- Atmosfera ----------------------------------------------------------
	// Da' un cielo vero. Serve a due cose: rende la scena leggibile senza
	// materiali emissivi, e soprattutto da' qualcosa di sensato da catturare
	// al canale CUBE quando lo accendi.
	{
		World.SpawnActor<ASkyAtmosphere>(ASkyAtmosphere::StaticClass(), FTransform::Identity, FActorSpawnParameters());
	}

	// --- Sky light ----------------------------------------------------------
	{
		ASkyLight* SkyLight = World.SpawnActor<ASkyLight>(ASkyLight::StaticClass(), FTransform::Identity, FActorSpawnParameters());
		if (SkyLight != nullptr)
		{
			if (USkyLightComponent* SkyComponent = SkyLight->GetLightComponent())
			{
				SkyComponent->SetMobility(EComponentMobility::Movable);
				SkyComponent->SourceType = ESkyLightSourceType::SLS_CapturedScene;
				// Ricattura il cielo a ogni frame invece che una volta sola.
				// Se la tua versione di engine non espone questa proprieta',
				// cancella la riga e chiama RecaptureSky() una volta: per una
				// scena statica come questa e' equivalente.
				SkyComponent->bRealTimeCapture = true;
				SkyComponent->SetIntensity(1.0f);
				SkyComponent->RecaptureSky();
			}
		}
	}

	// --- Attore di cattura --------------------------------------------------
	{
		CaptureActor = World.SpawnActor<AGpuShareCaptureActor>(
			AGpuShareCaptureActor::StaticClass(),
			FTransform(FRotator::ZeroRotator, FVector(-600.0, 0.0, 250.0)), FActorSpawnParameters());
	}

	UE_LOG(LogGpuShare, Log, TEXT("Scena costruita: piano + %d cubi + luce + cielo + attore di cattura."), MovingCubes.Num());
}

void UGpuShareSceneSubsystem::Tick(float DeltaTime)
{
	Super::Tick(DeltaTime);

	if (MovingCubes.Num() == 0)
	{
		return;
	}

	ElapsedTime += (double)DeltaTime;

	// Moto DETERMINISTICO: orbita circolare a velocita' costante piu' un
	// saliscendi sinusoidale. La determinatezza non e' un vezzo: rende la
	// latenza visibile a occhio come uno scostamento spaziale costante, e
	// riproducibile tra una misura e l'altra.
	for (int32 Index = 0; Index < MovingCubes.Num(); ++Index)
	{
		AStaticMeshActor* Cube = MovingCubes[Index];
		if (Cube == nullptr)
		{
			continue;
		}

		const double PhaseOffset = (2.0 * PI * (double)Index) / (double)MovingCubes.Num();
		const double Angle = ElapsedTime * (double)CubeOrbitSpeed * 2.0 * PI + PhaseOffset;

		const FVector Location(
			FMath::Cos(Angle) * CubeOrbitRadiusCm,
			FMath::Sin(Angle) * CubeOrbitRadiusCm,
			150.0 + FMath::Sin(ElapsedTime * 1.7 + PhaseOffset) * CubeBobHeightCm);

		const FRotator Rotation(0.0, FMath::RadiansToDegrees(Angle) * 2.0, 0.0);

		Cube->SetActorLocationAndRotation(Location, Rotation);
	}
}
