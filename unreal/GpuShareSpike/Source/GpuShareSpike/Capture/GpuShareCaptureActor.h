// ============================================================================
//  AGpuShareCaptureActor  --  l'orchestratore lato Unreal.
//
//  Cosa fa, in ordine, a ogni frame:
//    1. legge l'ULTIMA pose arrivata da Unity (latest-wins)
//    2. la applica a CameraRoot, quindi alle SceneCapture che ne sono figlie
//    3. chiede la cattura delle scene
//    4. accoda sul render thread la copia verso le superfici condivise
//
//  ------------------------------------------------------------------------
//  GERARCHIA, ED E' IL PUNTO CHE CONTA
//
//      AGpuShareCaptureActor            <-- L'ANCORA (origine dello spazio Unity)
//        |                                  La sua transform NON e' toccata da
//        |                                  questo codice: muovila con quello che
//        |                                  vuoi (Blueprint, C++, un componente
//        |                                  esterno, un globe anchor di Cesium)
//        |                                  e tutto lo spazio di Unity ci va dietro.
//        +-- CameraRoot                 <-- guidato dalla pose che arriva da Unity,
//              |                            in coordinate RELATIVE all'ancora
//              +-- ColorCapture
//              +-- DepthCapture
//              +-- CubeCapture
//
//  Quindi: l'origine del mondo di Unity (o il suo OriginTransform, se ne
//  assegni uno) coincide con la transform di QUESTO ATTORE nel mondo Unreal.
//  Unity lavora in uno spazio locale piccolo e ben condizionato, in metri,
//  vicino all'origine; l'ancora lo colloca dove serve nel mondo enorme di
//  Unreal. E' lo stesso pattern del georeference di Cesium.
//
//  Se invece ti serve che la pose sia una transform di MONDO assoluta, spegni
//  bPoseRelativeToAnchor nei Project Settings.
//  ------------------------------------------------------------------------
//
//  NOTA UE PER CHI VIENE DA UNITY:
//  un "Actor" e' l'equivalente di un GameObject; i "Component" sono i
//  componenti. La differenza importante e' che qui non c'e' nessun Blueprint:
//  i componenti vengono creati nel COSTRUTTORE C++ con CreateDefaultSubobject,
//  che e' l'unico punto in cui e' lecito farlo.
//
//  Questo header NON include nulla di D3D11: e' processato da UnrealHeaderTool,
//  che si confonde con le intestazioni Windows. Le classi D3D vivono dietro
//  forward declaration e TSharedPtr.
// ============================================================================

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "Net/ControlChannel.h"
#include "GpuShareCaptureActor.generated.h"

class USceneCaptureComponent2D;
class USceneCaptureComponentCube;
class UTextureRenderTarget2D;
class UTextureRenderTargetCube;

// Definite nel .cpp: contengono tipi D3D11 che non possono comparire qui.
struct FGpuShareRenderResources;

UCLASS()
class GPUSHARESPIKE_API AGpuShareCaptureActor : public AActor
{
	GENERATED_BODY()

public:
	AGpuShareCaptureActor();

	virtual void Tick(float DeltaSeconds) override;

protected:
	virtual void BeginPlay() override;
	virtual void EndPlay(const EEndPlayReason::Type EndPlayReason) override;

private:
	// --- Componenti ---------------------------------------------------------

	/**
	 * Radice dell'attore = L'ANCORA. Questo codice non la muove mai: e' il
	 * punto che rappresenta l'origine dello spazio di Unity dentro il mondo
	 * di Unreal, ed e' pensata per essere comandata dall'esterno.
	 */
	UPROPERTY(VisibleAnywhere, Category = "GPU Share")
	TObjectPtr<USceneComponent> SceneRoot;

	/**
	 * Guidato dalla pose che arriva da Unity, in coordinate relative
	 * all'ancora. E' questo che si muove a ogni frame, non l'attore.
	 */
	UPROPERTY(VisibleAnywhere, Category = "GPU Share")
	TObjectPtr<USceneComponent> CameraRoot;

	/** Cattura il colore finale (post-process incluso) -> canale COLOR. */
	UPROPERTY(VisibleAnywhere, Category = "GPU Share")
	TObjectPtr<USceneCaptureComponent2D> ColorCapture;

	/** Cattura il depth lineare in centimetri -> canale DEPTH. */
	UPROPERTY(VisibleAnywhere, Category = "GPU Share")
	TObjectPtr<USceneCaptureComponent2D> DepthCapture;

	/** Cattura la cubemap per i riflessi -> canale CUBE (facoltativo, costoso). */
	UPROPERTY(VisibleAnywhere, Category = "GPU Share")
	TObjectPtr<USceneCaptureComponentCube> CubeCapture;

	// --- Render target ------------------------------------------------------
	// UPROPERTY non e' decorativo: senza, il garbage collector di Unreal
	// distruggerebbe questi oggetti appena creati, perche' nessuno li
	// referenzia dal grafo degli UObject.

	UPROPERTY(Transient)
	TObjectPtr<UTextureRenderTarget2D> ColorRenderTarget;

	UPROPERTY(Transient)
	TObjectPtr<UTextureRenderTarget2D> DepthRenderTarget;

	UPROPERTY(Transient)
	TObjectPtr<UTextureRenderTargetCube> CubeRenderTarget;

	// --- Stato non-UObject --------------------------------------------------
	// TSharedPtr thread-safe, non puntatori grezzi: questi oggetti vengono
	// catturati PER VALORE nelle lambda inviate al render thread. Se l'attore
	// venisse distrutto mentre una lambda e' ancora in coda, un puntatore
	// grezzo sarebbe dangling; una shared pointer no.

	TSharedPtr<FGpuShareControlChannel, ESPMode::ThreadSafe> ControlChannel;
	TSharedPtr<FGpuShareRenderResources, ESPMode::ThreadSafe> RenderResources;

	// --- Metodi interni -----------------------------------------------------

	void InitializeRenderTargets();
	void InitializeSharedSurfaces();
	void HandleHello(const FGpuShareClientInfo& ClientInfo);
	void ApplyPose(const FGpuSharePoseState& Pose);
	void EnqueuePublish(uint32 GroupId, const FGpuSharePoseState& Pose);
	bool ShouldCaptureNow(float Hz, double& InOutLastTime, double Now) const;

	// --- Stato di lavoro ----------------------------------------------------

	uint64 UeFrameCounter = 0;
	double LastMainCaptureTime = 0.0;
	double LastCubeCaptureTime = 0.0;
	double LastLogTime = 0.0;
	uint64 PublishedMainFrames = 0;

	/** Cache dei setting letti al BeginPlay (evita di rileggere il CDO a ogni frame). */
	int32 CachedColorWidth = 1920;
	int32 CachedColorHeight = 1080;
	int32 CachedCubeFaceSize = 512;
	bool  bCachedDepthEnabled = true;
	bool  bCachedCubeEnabled = false;
	float CachedMainCaptureHz = 0.0f;
	float CachedCubeCaptureHz = 5.0f;
	float CachedRenderFovMarginDeg = 0.0f;
	int32 CachedAcquireTimeoutMs = 2;
	int32 CachedLogEveryNFrames = 300;
	bool  bCachedPoseRelativeToAnchor = true;

	/** FOV verticale effettivamente applicato all'ultimo frame (per lo STATUS). */
	float AppliedFovYDeg = 60.0f;
	float RenderFovYDeg = 60.0f;
	float AppliedNearCm = 10.0f;

	bool bLoggedFirstPose = false;
	bool bSurfacesReady = false;
	bool bFatalError = false;
};
