// ============================================================================
//  AGpuShareCaptureActor  --  l'orchestratore lato Unreal.
//
//  Cosa fa, in ordine, a ogni frame:
//    1. legge l'ULTIMA pose arrivata da Unity (latest-wins)
//    2. applica ogni vista al suo CameraRoot (posizione, rotazione, frustum)
//    3. chiede la cattura delle scene
//    4. accoda sul render thread la copia verso le superfici condivise
//
//  ------------------------------------------------------------------------
//  GERARCHIA
//
//      AGpuShareCaptureActor            <-- L'ANCORA (origine dello spazio Unity)
//        |                                  La sua transform NON e' toccata da
//        |                                  questo codice: muovila con quello che
//        |                                  vuoi (Blueprint, C++, un componente
//        |                                  esterno, un globe anchor di Cesium)
//        |                                  e tutto lo spazio di Unity ci va dietro.
//        +-- CameraRoot                 <-- vista 0: mono, oppure occhio SINISTRO
//        |     +-- ColorCapture
//        |     +-- DepthCapture
//        |     +-- CubeCapture
//        +-- CameraRootRight            <-- vista 1: occhio DESTRO (solo in stereo)
//              +-- ColorCaptureRight
//              +-- DepthCaptureRight
//
//  Ogni CameraRoot riceve la SUA pose, in coordinate relative all'ancora.
//  Gli occhi non sono derivati l'uno dall'altro con un offset fisso: arrivano
//  da Unity cosi' come li fornisce il visore (posizione, rotazione e frustum
//  asimmetrico di ciascun occhio).
//
//  ------------------------------------------------------------------------
//  LA FORMA DELLO STREAM LA DECIDE UNITY
//
//  Quante viste, a che risoluzione, con o senza depth: arriva tutto nell'HELLO.
//  Le superfici condivise vengono create al primo HELLO e RICREATE se un HELLO
//  successivo chiede una forma diversa (per esempio Unity passa da desktop a
//  VR stereo). Un HELLO ripetuto con la stessa forma dallo stesso processo non
//  ricrea nulla: rimanda lo stesso handshake. Cosi' Unreal puo' restare acceso
//  mentre Unity viene riavviato in modalita' diverse.
//
//  Questo header NON include nulla di D3D11: e' processato da UnrealHeaderTool,
//  che si confonde con le intestazioni Windows.
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

// Definita nel .cpp: contiene tipi D3D11 che non possono comparire qui.
struct FGpuShareRenderResources;

/** Forma dello stream, decisa da Unity con l'HELLO. Struct C++ semplice, non una USTRUCT. */
struct FGpuShareStreamConfig
{
	int32 ViewCount = 1;
	int32 Width = 1920;
	int32 Height = 1080;
	bool  bDepth = true;
	bool  bCube = false;
	int32 CubeFaceSize = 512;

	bool operator==(const FGpuShareStreamConfig& Other) const
	{
		return ViewCount == Other.ViewCount && Width == Other.Width && Height == Other.Height
			&& bDepth == Other.bDepth && bCube == Other.bCube && CubeFaceSize == Other.CubeFaceSize;
	}
	bool operator!=(const FGpuShareStreamConfig& Other) const { return !(*this == Other); }

	FString ToString() const
	{
		return FString::Printf(TEXT("%d vist%s %dx%d, depth=%s, cube=%s"),
			ViewCount, ViewCount == 1 ? TEXT("a") : TEXT("e"), Width, Height,
			bDepth ? TEXT("si") : TEXT("no"), bCube ? TEXT("si") : TEXT("no"));
	}
};

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

	/** Vista 0 (mono, o occhio sinistro). Guidata dalla pose, relativa all'ancora. */
	UPROPERTY(VisibleAnywhere, Category = "GPU Share")
	TObjectPtr<USceneComponent> CameraRoot;

	/** Vista 1 (occhio destro). Usata solo in stereo. */
	UPROPERTY(VisibleAnywhere, Category = "GPU Share")
	TObjectPtr<USceneComponent> CameraRootRight;

	UPROPERTY(VisibleAnywhere, Category = "GPU Share")
	TObjectPtr<USceneCaptureComponent2D> ColorCapture;

	UPROPERTY(VisibleAnywhere, Category = "GPU Share")
	TObjectPtr<USceneCaptureComponent2D> DepthCapture;

	UPROPERTY(VisibleAnywhere, Category = "GPU Share")
	TObjectPtr<USceneCaptureComponent2D> ColorCaptureRight;

	UPROPERTY(VisibleAnywhere, Category = "GPU Share")
	TObjectPtr<USceneCaptureComponent2D> DepthCaptureRight;

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
	TObjectPtr<UTextureRenderTarget2D> ColorRenderTargetRight;

	UPROPERTY(Transient)
	TObjectPtr<UTextureRenderTarget2D> DepthRenderTargetRight;

	UPROPERTY(Transient)
	TObjectPtr<UTextureRenderTargetCube> CubeRenderTarget;

	/**
	 * Sorgenti del gruppo MAIN nello STESSO ORDINE dei canali creati.
	 * Non-owning: i render target sono tenuti vivi dalle UPROPERTY sopra.
	 */
	TArray<UTextureRenderTarget2D*> MainSourceTargets;

	// --- Stato non-UObject --------------------------------------------------
	// TSharedPtr thread-safe, non puntatori grezzi: questi oggetti vengono
	// catturati PER VALORE nelle lambda inviate al render thread. Se l'attore
	// venisse distrutto mentre una lambda e' ancora in coda, un puntatore
	// grezzo sarebbe dangling; una shared pointer no.

	TSharedPtr<FGpuShareControlChannel, ESPMode::ThreadSafe> ControlChannel;
	TSharedPtr<FGpuShareRenderResources, ESPMode::ThreadSafe> RenderResources;

	// --- Metodi interni -----------------------------------------------------

	FGpuShareStreamConfig ComputeDesiredConfig(const FGpuShareClientInfo& ClientInfo) const;
	bool ApplyStreamConfig(const FGpuShareStreamConfig& Config);
	void HandleHello(const FGpuShareClientInfo& ClientInfo);
	void ApplyPose(const FGpuSharePoseState& Pose);
	void ApplyProjection(int32 ViewIndex, const FGpuShareViewState& View);
	void EnqueuePublish(uint32 GroupId, const FGpuSharePoseState& Pose);
	bool ShouldCaptureNow(float Hz, double& InOutLastTime, double Now) const;

	USceneComponent* GetCameraRoot(int32 ViewIndex) const;
	USceneCaptureComponent2D* GetColorCapture(int32 ViewIndex) const;
	USceneCaptureComponent2D* GetDepthCapture(int32 ViewIndex) const;
	UTextureRenderTarget2D* CreateRenderTarget(int32 Width, int32 Height, bool bDepthFormat);

	// --- Stato di lavoro ----------------------------------------------------

	FGpuShareStreamConfig CurrentConfig;
	uint32 ConfigId = 0;          // +1 a ogni ApplyStreamConfig riuscita
	uint32 HandshakePid = 0;      // processo Unity in cui abbiamo duplicato gli handle

	uint64 UeFrameCounter = 0;
	double LastMainCaptureTime = 0.0;
	double LastCubeCaptureTime = 0.0;
	double LastLogTime = 0.0;
	uint64 PublishedMainFrames = 0;

	/** Cache dei setting letti al BeginPlay (evita di rileggere il CDO a ogni frame). */
	int32 DefaultViewWidth = 1920;
	int32 DefaultViewHeight = 1080;
	int32 MaxViewDimension = 4096;
	int32 CachedCubeFaceSize = 512;
	bool  bSettingsDepthEnabled = true;
	bool  bSettingsCubeEnabled = false;
	float CachedMainCaptureHz = 0.0f;
	float CachedCubeCaptureHz = 5.0f;
	int32 CachedAcquireTimeoutMs = 0;
	int32 CachedLogEveryNFrames = 300;
	bool  bCachedPoseRelativeToAnchor = true;
	bool  bCachedUseCustomProjection = true;

	/** Parametri della vista 0 applicati all'ultimo frame (per lo STATUS). */
	float AppliedFovYDeg = 60.0f;
	float AppliedNearCm = 10.0f;

	bool bLoggedFirstPose = false;
	bool bLoggedAsymmetricWithoutCustom = false;
	bool bSurfacesReady = false;
	bool bFatalError = false;
};
