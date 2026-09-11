// ============================================================================
//  UGpuShareSettings
//
//  UDeveloperSettings e' il modo idiomatico in Unreal per esporre parametri di
//  progetto: ereditando da questa classe, la UCLASS compare AUTOMATICAMENTE in
//  Edit -> Project Settings (categoria "Plugins" -> "GPU Share Spike") e i
//  valori vengono serializzati in Config/DefaultGame.ini senza scrivere una
//  riga di codice di UI.
//
//  config=Game      -> i valori finiscono in DefaultGame.ini
//  defaultconfig    -> l'editor puo' SCRIVERE su DefaultGame.ini (non solo leggere)
//  meta=(DisplayName=...) -> il nome mostrato nella UI
// ============================================================================

#pragma once

#include "CoreMinimal.h"
#include "Engine/DeveloperSettings.h"
#include "GpuShareSettings.generated.h"
// ^ NOTA UE: ogni header che contiene una UCLASS/USTRUCT deve includere per
//   ULTIMO il proprio file ".generated.h". E' generato da UnrealHeaderTool
//   prima della compilazione vera e propria. Se lo metti non per ultimo, o se
//   lo dimentichi, ottieni errori criptici tipo "Unknown class specifier".

UCLASS(config = Game, defaultconfig, meta = (DisplayName = "GPU Share Spike"))
class GPUSHARESPIKE_API UGpuShareSettings : public UDeveloperSettings
{
	GENERATED_BODY()
	// ^ NOTA UE: macro obbligatoria dentro ogni UCLASS. Espande il boilerplate
	//   generato da UnrealHeaderTool (reflection, costruttori, ecc.).

public:
	UGpuShareSettings();

	/** Scorciatoia per leggere i settings da qualunque punto del codice. */
	static const UGpuShareSettings& Get();

	// ---- Canale COLOR + DEPTH (gruppo MAIN) --------------------------------

	UPROPERTY(config, EditAnywhere, Category = "Main Group", meta = (ClampMin = "16", ClampMax = "7680"))
	int32 ColorWidth = 1920;

	UPROPERTY(config, EditAnywhere, Category = "Main Group", meta = (ClampMin = "16", ClampMax = "4320"))
	int32 ColorHeight = 1080;

	/** Cadenza di cattura del gruppo MAIN. 0 = a ogni frame del motore (free-run puro). */
	UPROPERTY(config, EditAnywhere, Category = "Main Group", meta = (ClampMin = "0.0", ClampMax = "480.0"))
	float MainCaptureHz = 0.0f;

	/** Canale DEPTH: depth lineare in centimetri, R32_FLOAT, stesso frame del colore. */
	UPROPERTY(config, EditAnywhere, Category = "Main Group")
	bool bEnableDepthChannel = true;

	// ---- Canale CUBE (gruppo CUBE) -----------------------------------------

	/**
	 * ATTENZIONE AL COSTO: una SceneCaptureComponentCube costa SEI render
	 * completi della scena. Su un mondo streamato scala-Cesium e' pesante.
	 * Tienila a risoluzione e cadenza basse.
	 */
	UPROPERTY(config, EditAnywhere, Category = "Cube Group")
	bool bEnableCubeChannel = false;

	UPROPERTY(config, EditAnywhere, Category = "Cube Group", meta = (ClampMin = "32", ClampMax = "2048", EditCondition = "bEnableCubeChannel"))
	int32 CubeFaceSize = 512;

	UPROPERTY(config, EditAnywhere, Category = "Cube Group", meta = (ClampMin = "0.1", ClampMax = "60.0", EditCondition = "bEnableCubeChannel"))
	float CubeCaptureHz = 5.0f;

	// ---- Camera ------------------------------------------------------------

	/**
	 * Margine di FOV renderizzato in piu' rispetto a quello richiesto da Unity.
	 * Serve al re-crop tardivo: Unreal renderizza piu' largo, Unity puo'
	 * ritagliare per compensare una rotazione arrivata dopo, senza bordi neri.
	 * 0 = disattivato.
	 */
	UPROPERTY(config, EditAnywhere, Category = "Camera", meta = (ClampMin = "0.0", ClampMax = "40.0"))
	float RenderFovMarginDeg = 0.0f;

	/** Usato finche' Unity non ha mandato la prima pose. */
	UPROPERTY(config, EditAnywhere, Category = "Camera")
	float DefaultFovYDeg = 60.0f;

	// ---- Rete --------------------------------------------------------------

	UPROPERTY(config, EditAnywhere, Category = "Network", meta = (ClampMin = "1024", ClampMax = "65535"))
	int32 ListenPort = 45001;

	// ---- Trasporto GPU -----------------------------------------------------

	/**
	 * Timeout dell'AcquireSync del produttore, in millisecondi.
	 * Se scade, Unreal SALTA la pubblicazione di quel frame invece di
	 * bloccarsi: senza questo, un Unity lento o morto congelerebbe il render
	 * thread di Unreal.
	 */
	UPROPERTY(config, EditAnywhere, Category = "GPU Transport", meta = (ClampMin = "0", ClampMax = "100"))
	int32 ProducerAcquireTimeoutMs = 2;

	/**
	 * false = DuplicateHandle verso il PID di Unity (percorso primario).
	 * true  = handle NOMINATI, aperti con OpenSharedResourceByName.
	 * Usa true se OpenProcess fallisce con ACCESS_DENIED (tipico se uno dei
	 * due processi gira elevato e l'altro no). Vedi docs/05-troubleshooting.md.
	 */
	UPROPERTY(config, EditAnywhere, Category = "GPU Transport")
	bool bUseNamedSharedHandles = false;

	UPROPERTY(config, EditAnywhere, Category = "GPU Transport", meta = (EditCondition = "bUseNamedSharedHandles"))
	FString SharedHandleNamePrefix = TEXT("Local\\GpuShareSpike");

	// ---- Diagnostica -------------------------------------------------------

	/** Se true e l'RHI attivo non e' D3D11, il trasporto si rifiuta di partire con un errore esplicito. */
	UPROPERTY(config, EditAnywhere, Category = "Diagnostics")
	bool bRequireD3D11 = true;

	/** Cap FPS del motore applicato al BeginPlay (t.MaxFPS). 0 = illimitato. */
	UPROPERTY(config, EditAnywhere, Category = "Diagnostics", meta = (ClampMin = "0.0", ClampMax = "1000.0"))
	float EngineMaxFPS = 0.0f;

	/** Log di una riga ogni N frame pubblicati. 0 = silenzio. */
	UPROPERTY(config, EditAnywhere, Category = "Diagnostics", meta = (ClampMin = "0"))
	int32 LogEveryNFrames = 300;
};
