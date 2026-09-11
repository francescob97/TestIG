#include "Capture/GpuShareCaptureActor.h"

#include "GpuShareLog.h"
#include "GpuShareSettings.h"
#include "Gpu/GpuShareTime.h"
#include "Gpu/ShareChannel.h"

#include "Components/SceneCaptureComponent2D.h"
#include "Components/SceneCaptureComponentCube.h"
#include "Engine/Engine.h"
#include "Engine/TextureRenderTarget2D.h"
#include "Engine/TextureRenderTargetCube.h"
#include "Engine/World.h"
#include "Math/PerspectiveMatrix.h"
#include "RHI.h"
#include "RHICommandList.h"
#include "RenderingThread.h"
#include "TextureResource.h"

#if PLATFORM_WINDOWS
#include "Gpu/CubeFaceAtlas.h"
#include "Gpu/D3D11ChannelGroup.h"
#endif

// Se il tuo engine non espone CustomNearClippingPlane su USceneCaptureComponent2D
// (proprieta' aggiunta in UE5), metti 0 qui: il near plane restera' quello
// globale del motore, che per questo spike va benissimo.
#define GPUSHARE_USE_CUSTOM_NEAR_CLIP 1

// ---------------------------------------------------------------------------
//  Risorse di render, vive solo su Windows.
//  Sono in un oggetto separato tenuto da TSharedPtr perche' vengono catturate
//  per valore dalle lambda inviate al render thread: cosi' la loro vita non
//  dipende da quella dell'attore.
// ---------------------------------------------------------------------------

#if PLATFORM_WINDOWS

struct FGpuShareRenderResources
{
	FGpuShareD3D11ChannelGroup MainGroup;
	FGpuShareD3D11ChannelGroup CubeGroup;

	ID3D11Device* Device = nullptr;
	uint32 AdapterLuidLow = 0;
	int32  AdapterLuidHigh = 0;

	bool bCubeEnabled = false;
	bool bNamedHandles = false;
	FString NamePrefix;

	/** Toccato SOLO dal render thread: i render command sono serializzati tra loro. */
	GpuShareGroupStatus GroupStatus[GPUSHARE_GROUP_COUNT] = {};
};

namespace
{
	/**
	 * NOTA UE IMPORTANTE:
	 * per ottenere la ID3D11Texture2D sottostante a una texture di Unreal NON
	 * servono gli header privati del modulo D3D11RHI (che quasi tutti i
	 * tutorial dicono di includere). FRHITexture::GetNativeResource() e'
	 * pubblica e, su RHI D3D11, restituisce esattamente ID3D11Texture2D*.
	 */
	ID3D11Texture2D* GetNativeTexture2D(FTextureRenderTargetResource* Resource)
	{
		if (Resource == nullptr)
		{
			return nullptr;
		}

		FRHITexture* Texture = Resource->GetRenderTargetTexture();
		if (Texture == nullptr)
		{
			// Alcune risorse popolano solo TextureRHI (es. i render target cube).
			Texture = Resource->TextureRHI;
		}
		return Texture != nullptr ? static_cast<ID3D11Texture2D*>(Texture->GetNativeResource()) : nullptr;
	}

	ID3D11Texture2D* GetNativeTextureCube(FTextureRenderTargetResource* Resource)
	{
		if (Resource == nullptr || Resource->TextureRHI == nullptr)
		{
			return nullptr;
		}
		// Una TextureCube in D3D11 e' una ID3D11Texture2D con ArraySize == 6.
		return static_cast<ID3D11Texture2D*>(Resource->TextureRHI->GetNativeResource());
	}

	/**
	 * In UE5 FMatrix e FVector sono a DOPPIA precisione (Large World
	 * Coordinates). Il protocollo usa float, quindi la conversione va fatta a
	 * mano: una memcpy qui produrrebbe spazzatura.
	 */
	void MatrixToFloatArray(const FMatrix& Matrix, float* OutFloats)
	{
		for (int32 Row = 0; Row < 4; ++Row)
		{
			for (int32 Col = 0; Col < 4; ++Col)
			{
				OutFloats[Row * 4 + Col] = (float)Matrix.M[Row][Col];
			}
		}
	}
}

#else // !PLATFORM_WINDOWS

struct FGpuShareRenderResources { int32 Unused = 0; };

#endif // PLATFORM_WINDOWS

// ---------------------------------------------------------------------------
//  Costruzione
// ---------------------------------------------------------------------------

AGpuShareCaptureActor::AGpuShareCaptureActor()
{
	PrimaryActorTick.bCanEverTick = true;

	// TG_PostUpdateWork: tickiamo DOPO che il resto del mondo si e' aggiornato,
	// cosi' la scena che catturiamo e' quella definitiva del frame e non uno
	// stato intermedio.
	PrimaryActorTick.TickGroup = TG_PostUpdateWork;

	// CreateDefaultSubobject si puo' chiamare SOLO dal costruttore: e' il
	// meccanismo con cui Unreal costruisce la gerarchia di componenti di
	// default della classe. Chiamarlo altrove porta a crash o ad oggetti che
	// non sopravvivono alla serializzazione.
	SceneRoot = CreateDefaultSubobject<USceneComponent>(TEXT("SceneRoot"));
	RootComponent = SceneRoot;

	ColorCapture = CreateDefaultSubobject<USceneCaptureComponent2D>(TEXT("ColorCapture"));
	ColorCapture->SetupAttachment(SceneRoot);

	DepthCapture = CreateDefaultSubobject<USceneCaptureComponent2D>(TEXT("DepthCapture"));
	DepthCapture->SetupAttachment(SceneRoot);

	CubeCapture = CreateDefaultSubobject<USceneCaptureComponentCube>(TEXT("CubeCapture"));
	CubeCapture->SetupAttachment(SceneRoot);
}

// ---------------------------------------------------------------------------
//  BeginPlay
// ---------------------------------------------------------------------------

void AGpuShareCaptureActor::BeginPlay()
{
	Super::BeginPlay();

	const UGpuShareSettings& Settings = UGpuShareSettings::Get();
	CachedColorWidth         = Settings.ColorWidth;
	CachedColorHeight        = Settings.ColorHeight;
	CachedCubeFaceSize       = Settings.CubeFaceSize;
	bCachedDepthEnabled      = Settings.bEnableDepthChannel;
	bCachedCubeEnabled       = Settings.bEnableCubeChannel;
	CachedMainCaptureHz      = Settings.MainCaptureHz;
	CachedCubeCaptureHz      = Settings.CubeCaptureHz;
	CachedRenderFovMarginDeg = Settings.RenderFovMarginDeg;
	CachedAcquireTimeoutMs   = Settings.ProducerAcquireTimeoutMs;
	CachedLogEveryNFrames    = Settings.LogEveryNFrames;
	AppliedFovYDeg           = Settings.DefaultFovYDeg;
	RenderFovYDeg            = AppliedFovYDeg + CachedRenderFovMarginDeg;

	// --- Verifica dell'RHI -------------------------------------------------
	// Questo spike vive sul fatto che la texture RHI di Unreal SIA gia' una
	// ID3D11Texture2D. Se l'RHI attivo e' D3D12 o Vulkan, GetNativeResource()
	// restituisce tutt'altro e i sintomi sarebbero crash incomprensibili.
	// Meglio fermarsi qui con un messaggio chiaro.
	const FString RhiName = (GDynamicRHI != nullptr) ? FString(GDynamicRHI->GetName()) : FString(TEXT("<nessuno>"));
	const bool bIsD3D11 = RhiName.Contains(TEXT("D3D11"));

	UE_LOG(LogGpuShare, Log, TEXT("RHI attivo: '%s'  (feature level max: %d)"),
		*RhiName, (int32)GMaxRHIFeatureLevel);

	if (!bIsD3D11)
	{
		if (Settings.bRequireD3D11)
		{
			bFatalError = true;
			UE_LOG(LogGpuShare, Error,
				TEXT("RHI '%s' non supportato: serve D3D11. Avvia con -d3d11 oppure imposta ")
				TEXT("Project Settings > Platforms > Windows > Default RHI = DirectX 11 e riavvia l'editor."),
				*RhiName);
			return;
		}
		UE_LOG(LogGpuShare, Warning, TEXT("RHI non D3D11 ma bRequireD3D11=false: proseguo, aspettati guai."));
	}

	// --- Cap FPS del motore -------------------------------------------------
	// Per misurare la latenza serve il free-run: il vsync introdurrebbe
	// un'attesa che non c'entra nulla con l'anello che stiamo misurando.
	if (GEngine != nullptr)
	{
		GEngine->Exec(GetWorld(), *FString::Printf(TEXT("t.MaxFPS %.1f"), Settings.EngineMaxFPS));
		GEngine->Exec(GetWorld(), TEXT("r.VSync 0"));
	}

	InitializeRenderTargets();

	// --- Canale di controllo ------------------------------------------------
	ControlChannel = MakeShared<FGpuShareControlChannel, ESPMode::ThreadSafe>();
	FString NetError;
	if (!ControlChannel->Start(Settings.ListenPort, NetError))
	{
		bFatalError = true;
		UE_LOG(LogGpuShare, Error, TEXT("Canale di controllo non avviato: %s"), *NetError);
		return;
	}

	InitializeSharedSurfaces();
}

void AGpuShareCaptureActor::InitializeRenderTargets()
{
	// --- COLOR --------------------------------------------------------------
	ColorRenderTarget = NewObject<UTextureRenderTarget2D>(this, TEXT("GpuShareColorRT"));
	ColorRenderTarget->RenderTargetFormat = ETextureRenderTargetFormat::RTF_RGBA8;
	ColorRenderTarget->ClearColor = FLinearColor::Black;
	ColorRenderTarget->bAutoGenerateMips = false;
	// bForceLinearGamma = false -> il render target e' sRGB, che e' cio' che
	// vuoi per SCS_FinalColorLDR. La CopyResource verso la nostra shared
	// texture B8G8R8A8_UNORM resta valida (stessa famiglia typeless) e NON
	// applica conversioni: i bit passano intatti, compresi quelli del marker.
	ColorRenderTarget->InitCustomFormat(CachedColorWidth, CachedColorHeight, PF_B8G8R8A8, /*bInForceLinearGamma*/ false);
	ColorRenderTarget->UpdateResourceImmediate(true);

	ColorCapture->TextureTarget = ColorRenderTarget;
	ColorCapture->CaptureSource = ESceneCaptureSource::SCS_FinalColorLDR;
	// Catturiamo a mano con CaptureScene(): cosi' decidiamo NOI quando, e la
	// cadenza e' un parametro invece di essere legata al frame del motore.
	ColorCapture->bCaptureEveryFrame = false;
	ColorCapture->bCaptureOnMovement = false;
	// Senza questo, gli effetti temporali (TAA, motion blur, eye adaptation) si
	// resettano a ogni cattura e l'immagine "sfarfalla".
	ColorCapture->bAlwaysPersistRenderingState = true;

	// --- DEPTH --------------------------------------------------------------
	if (bCachedDepthEnabled)
	{
		DepthRenderTarget = NewObject<UTextureRenderTarget2D>(this, TEXT("GpuShareDepthRT"));
		DepthRenderTarget->RenderTargetFormat = ETextureRenderTargetFormat::RTF_R32f;
		DepthRenderTarget->ClearColor = FLinearColor::Black;
		DepthRenderTarget->bAutoGenerateMips = false;
		DepthRenderTarget->InitCustomFormat(CachedColorWidth, CachedColorHeight, PF_R32_FLOAT, /*bInForceLinearGamma*/ true);
		DepthRenderTarget->UpdateResourceImmediate(true);

		DepthCapture->TextureTarget = DepthRenderTarget;
		// SCS_SceneDepth scrive il depth LINEARE IN UNITA' UNREAL (centimetri)
		// dentro un render target COLORE R32_FLOAT.
		// Due vantaggi enormi rispetto a SCS_DeviceDepth:
		//  1. la sorgente e' gia' una texture colore, quindi la CopyResource
		//     verso la shared texture e' diretta: niente campo minato dei
		//     formati depth-stencil typeless;
		//  2. Unity non deve sapere nulla di reversed-Z, near/far o della
		//     matrice di proiezione: moltiplica per 0.01 e ha i metri.
		DepthCapture->CaptureSource = ESceneCaptureSource::SCS_SceneDepth;
		DepthCapture->bCaptureEveryFrame = false;
		DepthCapture->bCaptureOnMovement = false;
		DepthCapture->bAlwaysPersistRenderingState = true;
	}
	else
	{
		DepthCapture->SetComponentTickEnabled(false);
		DepthCapture->SetVisibility(false);
	}

	// --- CUBE ---------------------------------------------------------------
	if (bCachedCubeEnabled)
	{
		CubeRenderTarget = NewObject<UTextureRenderTargetCube>(this, TEXT("GpuShareCubeRT"));
		CubeRenderTarget->ClearColor = FLinearColor::Black;
		CubeRenderTarget->bAutoGenerateMips = false;
		CubeRenderTarget->Init(CachedCubeFaceSize, PF_B8G8R8A8);
		CubeRenderTarget->UpdateResourceImmediate(true);

		CubeCapture->TextureTarget = CubeRenderTarget;
		CubeCapture->CaptureSource = ESceneCaptureSource::SCS_FinalColorLDR;
		CubeCapture->bCaptureEveryFrame = false;
		CubeCapture->bCaptureOnMovement = false;

		UE_LOG(LogGpuShare, Warning,
			TEXT("Canale CUBE attivo: ogni cattura costa SEI render completi della scena. ")
			TEXT("Faccia=%d px, cadenza=%.1f Hz."), CachedCubeFaceSize, CachedCubeCaptureHz);
	}
	else
	{
		CubeCapture->SetComponentTickEnabled(false);
		CubeCapture->SetVisibility(false);
	}
}

void AGpuShareCaptureActor::InitializeSharedSurfaces()
{
#if PLATFORM_WINDOWS
	const UGpuShareSettings& Settings = UGpuShareSettings::Get();

	RenderResources = MakeShared<FGpuShareRenderResources, ESPMode::ThreadSafe>();
	RenderResources->bCubeEnabled  = bCachedCubeEnabled;
	RenderResources->bNamedHandles = Settings.bUseNamedSharedHandles;
	RenderResources->NamePrefix    = Settings.SharedHandleNamePrefix;

	// --- Descrizione dei canali (game thread) -------------------------------
	TArray<FGpuShareChannelSetup> MainChannels;
	{
		FGpuShareChannelSetup Color;
		Color.ChannelId      = GS_CH_COLOR;
		Color.GroupId        = GS_GROUP_MAIN;
		Color.Width          = (uint32)CachedColorWidth;
		Color.Height         = (uint32)CachedColorHeight;
		Color.DxgiFormat     = (uint32)DXGI_FORMAT_B8G8R8A8_UNORM;
		Color.CopyMode       = EGpuShareCopyMode::FullCopy;
		Color.bCarriesMarker = true;   // solo il colore porta gli 8 pixel
		MainChannels.Add(Color);

		if (bCachedDepthEnabled)
		{
			FGpuShareChannelSetup Depth;
			Depth.ChannelId  = GS_CH_DEPTH;
			Depth.GroupId    = GS_GROUP_MAIN;
			Depth.Width      = (uint32)CachedColorWidth;
			Depth.Height     = (uint32)CachedColorHeight;
			Depth.DxgiFormat = (uint32)DXGI_FORMAT_R32_FLOAT;
			Depth.CopyMode   = EGpuShareCopyMode::FullCopy;
			MainChannels.Add(Depth);
		}
	}

	TArray<FGpuShareChannelSetup> CubeChannels;
	if (bCachedCubeEnabled)
	{
		FGpuShareChannelSetup Cube;
		Cube.ChannelId    = GS_CH_CUBE;
		Cube.GroupId      = GS_GROUP_CUBE;
		Cube.Width        = GpuShareCubeAtlasWidth((uint32)CachedCubeFaceSize);
		Cube.Height       = GpuShareCubeAtlasHeight((uint32)CachedCubeFaceSize);
		Cube.DxgiFormat   = (uint32)DXGI_FORMAT_B8G8R8A8_UNORM;
		Cube.CopyMode     = EGpuShareCopyMode::CubeFacesToAtlas;
		Cube.CubeFaceSize = (uint32)CachedCubeFaceSize;
		CubeChannels.Add(Cube);
	}

	TSharedPtr<FGpuShareRenderResources, ESPMode::ThreadSafe> Resources = RenderResources;

	// ENQUEUE_RENDER_COMMAND e' la macro con cui il game thread manda lavoro al
	// render thread. Il corpo e' una lambda che riceve la command list RHI.
	// Tutto cio' che tocca D3D11 DEVE stare qui dentro.
	ENQUEUE_RENDER_COMMAND(GpuShareInitSurfaces)(
		[Resources, MainChannels, CubeChannels](FRHICommandListImmediate& RHICmdList)
		{
			// RHIGetNativeDevice() su RHI D3D11 restituisce l'ID3D11Device del
			// renderer di Unreal. E' lo STESSO device dei render target, quindi
			// la CopyResource verso le nostre texture e' una copia intra-device:
			// nessun trasferimento, nessuna sincronizzazione extra.
			ID3D11Device* Device = static_cast<ID3D11Device*>(GDynamicRHI->RHIGetNativeDevice());
			Resources->Device = Device;

			GpuShareGetAdapterLuid(Device, Resources->AdapterLuidLow, Resources->AdapterLuidHigh);

			FString Error;
			if (!Resources->MainGroup.Initialize(Device, GS_GROUP_MAIN, MainChannels,
				Resources->bNamedHandles, Resources->NamePrefix, Error))
			{
				UE_LOG(LogGpuShare, Error, TEXT("Gruppo MAIN non inizializzato: %s"), *Error);
				return;
			}

			if (CubeChannels.Num() > 0)
			{
				if (!Resources->CubeGroup.Initialize(Device, GS_GROUP_CUBE, CubeChannels,
					Resources->bNamedHandles, Resources->NamePrefix, Error))
				{
					UE_LOG(LogGpuShare, Error, TEXT("Gruppo CUBE non inizializzato: %s"), *Error);
				}
			}

			UE_LOG(LogGpuShare, Log, TEXT("Superfici condivise pronte."));
		});

	// Aspettiamo che il render thread abbia finito prima di dichiararci pronti:
	// succede una volta sola, al BeginPlay, quindi lo stallo non ci costa nulla.
	FlushRenderingCommands();

	bSurfacesReady = RenderResources->MainGroup.IsInitialized();
	if (!bSurfacesReady)
	{
		bFatalError = true;
		UE_LOG(LogGpuShare, Error, TEXT("Inizializzazione delle superfici condivise fallita, vedi sopra."));
	}
#else
	UE_LOG(LogGpuShare, Error, TEXT("Questo spike gira solo su Windows."));
	bFatalError = true;
#endif
}

// ---------------------------------------------------------------------------
//  Handshake
// ---------------------------------------------------------------------------

void AGpuShareCaptureActor::HandleHello(const FGpuShareClientInfo& ClientInfo)
{
#if PLATFORM_WINDOWS
	if (!RenderResources.IsValid() || !bSurfacesReady)
	{
		return;
	}

	// Controllo GPU: se i due processi finiscono su adapter diversi,
	// OpenSharedResource1 lato Unity fallisce con un HRESULT che non spiega
	// niente. Accorgersene qui fa risparmiare ore.
	if (ClientInfo.AdapterLuidLow != 0 || ClientInfo.AdapterLuidHigh != 0)
	{
		if (ClientInfo.AdapterLuidLow != RenderResources->AdapterLuidLow ||
			ClientInfo.AdapterLuidHigh != RenderResources->AdapterLuidHigh)
		{
			UE_LOG(LogGpuShare, Error,
				TEXT("MISMATCH DI ADAPTER: Unreal e' su LUID %u:%d, Unity su %u:%d. ")
				TEXT("La condivisione NON puo' funzionare tra GPU diverse. ")
				TEXT("Forza entrambi gli eseguibili sulla stessa GPU (Impostazioni Windows > Schermo > Grafica)."),
				RenderResources->AdapterLuidLow, RenderResources->AdapterLuidHigh,
				ClientInfo.AdapterLuidLow, ClientInfo.AdapterLuidHigh);
			// Mandiamo comunque l'handshake: Unity mostrera' l'errore in HUD.
		}
	}

	TSharedPtr<FGpuShareRenderResources, ESPMode::ThreadSafe> Resources = RenderResources;
	TSharedPtr<FGpuShareControlChannel, ESPMode::ThreadSafe> Channel = ControlChannel;
	const uint32 UnityPid = ClientInfo.Pid;
	const uint32 UePid = (uint32)FPlatformProcess::GetCurrentProcessId();

	ENQUEUE_RENDER_COMMAND(GpuShareHandshake)(
		[Resources, Channel, UnityPid, UePid](FRHICommandListImmediate& RHICmdList)
		{
			FString Error;
			if (!Resources->MainGroup.DuplicateHandlesInto(UnityPid, Error))
			{
				UE_LOG(LogGpuShare, Error, TEXT("Duplicazione handle (MAIN) fallita: %s"), *Error);
				return;
			}
			if (Resources->CubeGroup.IsInitialized() && !Resources->CubeGroup.DuplicateHandlesInto(UnityPid, Error))
			{
				UE_LOG(LogGpuShare, Error, TEXT("Duplicazione handle (CUBE) fallita: %s"), *Error);
			}

			GpuShareHandshake Packet = {};
			GpuShareInitHeader(&Packet.header, GS_PKT_HANDSHAKE);
			Packet.ue_pid           = UePid;
			Packet.adapter_luid_low = Resources->AdapterLuidLow;
			Packet.adapter_luid_high= Resources->AdapterLuidHigh;
			Packet.handle_mode      = Resources->bNamedHandles ? GS_HANDLEMODE_NAMED : GS_HANDLEMODE_DUPLICATED;

			FTCHARToUTF8 PrefixUtf8(*Resources->NamePrefix);
			FCStringAnsi::Strncpy(Packet.name_prefix, (const ANSICHAR*)PrefixUtf8.Get(), sizeof(Packet.name_prefix));

			uint32 ChannelCount = 0;
			for (int32 i = 0; i < Resources->MainGroup.GetChannelCount() && ChannelCount < GPUSHARE_MAX_CHANNELS; ++i)
			{
				Resources->MainGroup.FillChannelDesc(i, Packet.channels[ChannelCount++]);
			}
			for (int32 i = 0; i < Resources->CubeGroup.GetChannelCount() && ChannelCount < GPUSHARE_MAX_CHANNELS; ++i)
			{
				Resources->CubeGroup.FillChannelDesc(i, Packet.channels[ChannelCount++]);
			}
			Packet.channel_count = ChannelCount;

			Channel->QueueHandshake(Packet);
		});
#endif
}

// ---------------------------------------------------------------------------
//  Applicazione della pose
// ---------------------------------------------------------------------------

void AGpuShareCaptureActor::ApplyPose(const FGpuSharePoseState& Pose)
{
	// Un'unica conversione di coordinate, dentro FGpuSharePoseState.
	SetActorTransform(Pose.ToUnrealTransform());

	AppliedFovYDeg = Pose.FovYDeg;
	RenderFovYDeg  = FMath::Min(179.0f, AppliedFovYDeg + CachedRenderFovMarginDeg);
	AppliedNearCm  = FMath::Max(1.0f, Pose.NearM * 100.0f);

	// ATTENZIONE: USceneCaptureComponent2D::FOVAngle e' il FOV ORIZZONTALE in
	// gradi, mentre Unity (Camera.fieldOfView) usa il VERTICALE. Convertire:
	//     tan(h/2) = tan(v/2) * aspect
	const float Aspect = (Pose.Aspect > KINDA_SMALL_NUMBER) ? Pose.Aspect : ((float)CachedColorWidth / (float)CachedColorHeight);
	const float HalfVerticalRad = FMath::DegreesToRadians(RenderFovYDeg) * 0.5f;
	const float HorizontalFovDeg = FMath::RadiansToDegrees(2.0f * FMath::Atan(FMath::Tan(HalfVerticalRad) * Aspect));

	ColorCapture->FOVAngle = HorizontalFovDeg;
	if (bCachedDepthEnabled)
	{
		DepthCapture->FOVAngle = HorizontalFovDeg;
	}

#if GPUSHARE_USE_CUSTOM_NEAR_CLIP
	ColorCapture->bOverride_CustomNearClippingPlane = true;
	ColorCapture->CustomNearClippingPlane = AppliedNearCm;
	if (bCachedDepthEnabled)
	{
		DepthCapture->bOverride_CustomNearClippingPlane = true;
		DepthCapture->CustomNearClippingPlane = AppliedNearCm;
	}
#endif
}

// ---------------------------------------------------------------------------
//  Pubblicazione
// ---------------------------------------------------------------------------

bool AGpuShareCaptureActor::ShouldCaptureNow(float Hz, double& InOutLastTime, double Now) const
{
	if (Hz <= 0.0f)
	{
		return true;   // free-run puro: una cattura per frame del motore
	}
	const double Interval = 1.0 / (double)Hz;
	if (Now - InOutLastTime >= Interval)
	{
		InOutLastTime = Now;
		return true;
	}
	return false;
}

void AGpuShareCaptureActor::EnqueuePublish(uint32 GroupId, const FGpuSharePoseState& Pose)
{
#if PLATFORM_WINDOWS
	if (!RenderResources.IsValid() || !ControlChannel.IsValid())
	{
		return;
	}

	// GameThread_GetRenderTargetResource() va chiamata DAL GAME THREAD: e' il
	// punto in cui Unreal garantisce che la risorsa di render esista. Il
	// puntatore che restituisce e' invece valido sul render thread.
	TArray<FTextureRenderTargetResource*> Sources;
	bool bIsCube = false;

	if (GroupId == GS_GROUP_MAIN)
	{
		Sources.Add(ColorRenderTarget ? ColorRenderTarget->GameThread_GetRenderTargetResource() : nullptr);
		if (bCachedDepthEnabled)
		{
			Sources.Add(DepthRenderTarget ? DepthRenderTarget->GameThread_GetRenderTargetResource() : nullptr);
		}
	}
	else
	{
		if (CubeRenderTarget == nullptr)
		{
			return;
		}
		Sources.Add(CubeRenderTarget->GameThread_GetRenderTargetResource());
		bIsCube = true;
	}

	// Parte "game thread" del pacchetto STATUS. Il render thread aggiungera' i
	// dati dei gruppi e lo spedira'.
	GpuShareStatus StatusTemplate = {};
	GpuShareInitHeader(&StatusTemplate.header, GS_PKT_STATUS);
	StatusTemplate.ue_frame_counter      = UeFrameCounter;
	StatusTemplate.group_count           = GPUSHARE_GROUP_COUNT;
	StatusTemplate.applied_fov_y_deg     = AppliedFovYDeg;
	StatusTemplate.render_fov_y_deg      = RenderFovYDeg;
	StatusTemplate.applied_near_cm       = AppliedNearCm;
	StatusTemplate.depth_scale_to_meters = 0.01f;   // unita' Unreal (cm) -> metri

	{
		// Matrici DIAGNOSTICHE in convenzione Unreal. Vedi il commento in
		// ShareProtocol.h: Unity NON deve usarle direttamente, ricostruisce la
		// propria camera dall'eco della pose.
		const FTransform ActorTransform = GetActorTransform();
		const FMatrix ViewMatrix =
			FTranslationMatrix(-ActorTransform.GetLocation()) *
			FInverseRotationMatrix(ActorTransform.GetRotation().Rotator()) *
			FMatrix(FPlane(0, 0, 1, 0), FPlane(1, 0, 0, 0), FPlane(0, 1, 0, 0), FPlane(0, 0, 0, 1));

		const float HalfFovRad = FMath::DegreesToRadians(RenderFovYDeg) * 0.5f;
		const FMatrix ProjMatrix = FReversedZPerspectiveMatrix(
			HalfFovRad, (float)CachedColorWidth, (float)CachedColorHeight, AppliedNearCm);

		MatrixToFloatArray(ViewMatrix, StatusTemplate.ue_view_matrix);
		MatrixToFloatArray(ProjMatrix, StatusTemplate.ue_proj_matrix);
	}

	TSharedPtr<FGpuShareRenderResources, ESPMode::ThreadSafe> Resources = RenderResources;
	TSharedPtr<FGpuShareControlChannel, ESPMode::ThreadSafe> Channel = ControlChannel;
	const uint64 FrameId = Pose.FrameId;
	const int64 QpcPoseSend = Pose.QpcSend;
	const int32 TimeoutMs = CachedAcquireTimeoutMs;

	ENQUEUE_RENDER_COMMAND(GpuSharePublish)(
		[Resources, Channel, Sources, GroupId, bIsCube, FrameId, QpcPoseSend, TimeoutMs, StatusTemplate]
		(FRHICommandListImmediate& RHICmdList) mutable
		{
			// PERCHE' EnqueueLambda E NON IL CORPO DIRETTO DEL RENDER COMMAND:
			// il render thread REGISTRA comandi RHI che, quando esiste un RHI
			// thread separato, vengono eseguiti dopo. EnqueueLambda inserisce il
			// nostro codice NELLO STREAM di comandi RHI, cosi' si esegue nella
			// posizione giusta rispetto alla cattura appena accodata.
			// Su D3D11 Unreal non usa un RHI thread separato e le due forme
			// coincidono, ma questa e' corretta in entrambi i casi ed e' quella
			// che sopravvive a un'eventuale migrazione a D3D12.
			RHICmdList.EnqueueLambda(
				[Resources, Channel, Sources, GroupId, bIsCube, FrameId, QpcPoseSend, TimeoutMs, StatusTemplate]
				(FRHICommandListBase&) mutable
				{
					FGpuShareD3D11ChannelGroup& Group =
						(GroupId == GS_GROUP_MAIN) ? Resources->MainGroup : Resources->CubeGroup;

					if (!Group.IsInitialized())
					{
						return;
					}

					FGpuSharePublishInput Input;
					Input.FrameId = FrameId;
					Input.QpcPoseSend = QpcPoseSend;
					Input.Sources.Reserve(Sources.Num());
					for (FTextureRenderTargetResource* Resource : Sources)
					{
						Input.Sources.Add(bIsCube ? GetNativeTextureCube(Resource) : GetNativeTexture2D(Resource));
					}

					FGpuSharePublishResult Result;
					if (Group.Publish(Input, TimeoutMs, Result))
					{
						GpuShareGroupStatus& Slot = Resources->GroupStatus[GroupId];
						Slot.group_id       = GroupId;
						Slot.ready_index    = Result.ReadyIndex;
						Slot.frame_id       = FrameId;
						Slot.qpc_pose_send  = QpcPoseSend;
						Slot.qpc_render_end = Result.QpcRenderEnd;
						Slot.sequence       = Result.Sequence;
						Slot.valid          = 1;
					}

					// Lo STATUS lo mandiamo comunque, anche se la pubblicazione
					// e' stata saltata: Unity deve sapere che Unreal e' vivo.
					GpuShareStatus Status = StatusTemplate;
					Status.qpc = GpuShareQpcNow();
					for (int32 i = 0; i < GPUSHARE_GROUP_COUNT; ++i)
					{
						Status.groups[i] = Resources->GroupStatus[i];
					}
					Channel->QueueStatus(Status);
				});
		});
#endif
}

// ---------------------------------------------------------------------------
//  Tick
// ---------------------------------------------------------------------------

void AGpuShareCaptureActor::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);

	if (bFatalError || !ControlChannel.IsValid())
	{
		return;
	}

	// 1. Nuovo client? (ri)crea gli handle e mandagli la tabella dei canali.
	FGpuShareClientInfo ClientInfo;
	if (ControlChannel->ConsumePendingHello(ClientInfo))
	{
		HandleHello(ClientInfo);
	}

	// 2. Ultima pose ricevuta. LATEST-WINS: se ne sono arrivate cinque da
	//    quando abbiamo tickato l'ultima volta, usiamo la quinta e buttiamo le
	//    altre quattro. Accodarle aumenterebbe la latenza, che e' l'opposto di
	//    quello che vogliamo.
	FGpuSharePoseState Pose;
	const bool bHasPose = ControlChannel->GetLatestPose(Pose);
	if (bHasPose)
	{
		ApplyPose(Pose);
	}

	if (!bSurfacesReady || !ControlChannel->HasClient() || !bHasPose)
	{
		return;
	}

	const double Now = FPlatformTime::Seconds();

	// 3. Gruppo MAIN (colore + depth): stessa cattura, stesso frame.
	if (ShouldCaptureNow(CachedMainCaptureHz, LastMainCaptureTime, Now))
	{
		// CaptureScene() accoda IMMEDIATAMENTE i comandi di render della
		// cattura. Il nostro publish, accodato subito dopo, e' quindi ordinato
		// dopo di essa sul render thread: quando copiamo, il render target
		// contiene gia' il frame nuovo.
		ColorCapture->CaptureScene();
		if (bCachedDepthEnabled)
		{
			DepthCapture->CaptureScene();
		}
		EnqueuePublish(GS_GROUP_MAIN, Pose);
		++PublishedMainFrames;
	}

	// 4. Gruppo CUBE: cadenza indipendente e molto piu' bassa.
	if (bCachedCubeEnabled && ShouldCaptureNow(CachedCubeCaptureHz, LastCubeCaptureTime, Now))
	{
		CubeCapture->CaptureScene();
		EnqueuePublish(GS_GROUP_CUBE, Pose);
	}

	++UeFrameCounter;

	// 5. Log periodico.
	if (CachedLogEveryNFrames > 0 && (PublishedMainFrames % (uint64)CachedLogEveryNFrames) == 0 && PublishedMainFrames > 0)
	{
		const double Elapsed = Now - LastLogTime;
		if (Elapsed > 0.5)
		{
			UE_LOG(LogGpuShare, Log,
				TEXT("frame UE=%llu  pubblicati=%llu  pose ricevute=%llu  pacchetti scartati=%llu"),
				UeFrameCounter, PublishedMainFrames,
				ControlChannel->GetPosePacketCount(), ControlChannel->GetBadPacketCount());
			LastLogTime = Now;
		}
	}
}

// ---------------------------------------------------------------------------
//  Teardown
// ---------------------------------------------------------------------------

void AGpuShareCaptureActor::EndPlay(const EEndPlayReason::Type EndPlayReason)
{
	if (ControlChannel.IsValid())
	{
		ControlChannel->Shutdown();
	}

	// Aspettiamo che il render thread abbia svuotato la coda PRIMA di lasciar
	// morire le risorse: e' l'idioma Unreal per non distruggere oggetti che una
	// lambda in volo sta ancora per usare. (Le TSharedPtr ci proteggerebbero
	// comunque, ma cosi' il rilascio delle texture avviene in modo ordinato.)
	FlushRenderingCommands();

	if (RenderResources.IsValid())
	{
		TSharedPtr<FGpuShareRenderResources, ESPMode::ThreadSafe> Resources = RenderResources;
		ENQUEUE_RENDER_COMMAND(GpuShareShutdown)(
			[Resources](FRHICommandListImmediate&)
			{
#if PLATFORM_WINDOWS
				Resources->MainGroup.Shutdown();
				Resources->CubeGroup.Shutdown();
#endif
			});
		FlushRenderingCommands();
	}

	RenderResources.Reset();
	ControlChannel.Reset();
	bSurfacesReady = false;

	Super::EndPlay(EndPlayReason);
}
