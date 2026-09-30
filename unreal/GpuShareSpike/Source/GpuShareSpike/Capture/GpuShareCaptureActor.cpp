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
#include "HAL/PlatformProcess.h"
#include "Math/RotationMatrix.h"       // FInverseRotationMatrix
#include "Math/TranslationMatrix.h"    // FTranslationMatrix
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

namespace
{
	/**
	 * Matrice di proiezione OFF-CENTER a partire dalle quattro tangenti del
	 * frustum, nella convenzione di Unreal: row-vector, reversed-Z, far
	 * infinito.
	 *
	 * E' la stessa identica forma che usa il plugin OpenXR di Unreal per gli
	 * occhi di un visore (FOpenXRHMD::GetStereoProjectionMatrix): un frustum
	 * asimmetrico non si descrive con un FOV e un aspect, servono i quattro
	 * bordi. Per un frustum simmetrico si riduce alla proiezione prospettica
	 * normale.
	 *
	 *   riga 0 : 2/(R-L)        0             0     0
	 *   riga 1 : 0              2/(U-D)       0     0
	 *   riga 2 : -(R+L)/(R-L)   -(U+D)/(U-D)  0     1      <- il termine off-center
	 *   riga 3 : 0              0             Near  0      <- reversed-Z, far infinito
	 */
	FMatrix GpuShareBuildOffCenterProjection(const FGpuShareViewState& View, float NearCm)
	{
		const float L = View.TanLeft;
		const float R = View.TanRight;
		const float D = View.TanDown;
		const float U = View.TanUp;
		const float InvRL = 1.0f / (R - L);
		const float InvUD = 1.0f / (U - D);

		return FMatrix(
			FPlane(2.0f * InvRL,      0.0f,              0.0f,   0.0f),
			FPlane(0.0f,              2.0f * InvUD,      0.0f,   0.0f),
			FPlane(-(R + L) * InvRL,  -(U + D) * InvUD,  0.0f,   1.0f),
			FPlane(0.0f,              0.0f,              NearCm, 0.0f));
	}

	void SetupColorCapture(USceneCaptureComponent2D* Capture, UTextureRenderTarget2D* Target)
	{
		Capture->TextureTarget = Target;
		Capture->CaptureSource = ESceneCaptureSource::SCS_FinalColorLDR;
		// Catturiamo a mano con CaptureScene(): cosi' decidiamo NOI quando, e la
		// cadenza e' un parametro invece di essere legata al frame del motore.
		Capture->bCaptureEveryFrame = false;
		Capture->bCaptureOnMovement = false;
		// Senza questo, gli effetti temporali (TAA, motion blur, eye adaptation)
		// si resettano a ogni cattura e l'immagine "sfarfalla".
		Capture->bAlwaysPersistRenderingState = true;
	}

	void SetupDepthCapture(USceneCaptureComponent2D* Capture, UTextureRenderTarget2D* Target)
	{
		Capture->TextureTarget = Target;
		// SCS_SceneDepth scrive il depth LINEARE IN UNITA' UNREAL (centimetri)
		// dentro un render target COLORE R32_FLOAT.
		// Due vantaggi enormi rispetto a SCS_DeviceDepth:
		//  1. la sorgente e' gia' una texture colore, quindi la CopyResource
		//     verso la shared texture e' diretta: niente campo minato dei
		//     formati depth-stencil typeless;
		//  2. Unity non deve sapere nulla di reversed-Z, near/far o della
		//     matrice di proiezione: moltiplica per 0.01 e ha i metri.
		Capture->CaptureSource = ESceneCaptureSource::SCS_SceneDepth;
		Capture->bCaptureEveryFrame = false;
		Capture->bCaptureOnMovement = false;
		Capture->bAlwaysPersistRenderingState = true;
	}

	void DisableCapture(USceneCaptureComponent2D* Capture)
	{
		Capture->TextureTarget = nullptr;
		Capture->bCaptureEveryFrame = false;
		Capture->bCaptureOnMovement = false;
	}
}

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
	//
	// NOTA: ogni volta che cambia questo elenco di componenti, il modulo va
	// ricompilato A EDITOR CHIUSO. L'hot-reload lascia il Class Default Object
	// indietro e a runtime i componenti nuovi arrivano nulli.

	// SceneRoot e' L'ANCORA: la transform dell'attore. Questo codice non la
	// tocca mai, cosi' un componente esterno puo' muoverla liberamente.
	SceneRoot = CreateDefaultSubobject<USceneComponent>(TEXT("SceneRoot"));
	RootComponent = SceneRoot;

	// Una radice per vista, entrambe figlie dell'ancora. La pose di ciascun
	// occhio arriva da Unity e viene applicata IN RELATIVO all'ancora.
	CameraRoot = CreateDefaultSubobject<USceneComponent>(TEXT("CameraRoot"));
	CameraRoot->SetupAttachment(SceneRoot);

	CameraRootRight = CreateDefaultSubobject<USceneComponent>(TEXT("CameraRootRight"));
	CameraRootRight->SetupAttachment(SceneRoot);

	ColorCapture = CreateDefaultSubobject<USceneCaptureComponent2D>(TEXT("ColorCapture"));
	ColorCapture->SetupAttachment(CameraRoot);

	DepthCapture = CreateDefaultSubobject<USceneCaptureComponent2D>(TEXT("DepthCapture"));
	DepthCapture->SetupAttachment(CameraRoot);

	ColorCaptureRight = CreateDefaultSubobject<USceneCaptureComponent2D>(TEXT("ColorCaptureRight"));
	ColorCaptureRight->SetupAttachment(CameraRootRight);

	DepthCaptureRight = CreateDefaultSubobject<USceneCaptureComponent2D>(TEXT("DepthCaptureRight"));
	DepthCaptureRight->SetupAttachment(CameraRootRight);

	CubeCapture = CreateDefaultSubobject<USceneCaptureComponentCube>(TEXT("CubeCapture"));
	CubeCapture->SetupAttachment(CameraRoot);

	// Nessuna cattura parte da sola: le comandiamo tutte da Tick.
	USceneCaptureComponent2D* const AllCaptures[] = { ColorCapture.Get(), DepthCapture.Get(), ColorCaptureRight.Get(), DepthCaptureRight.Get() };
	for (USceneCaptureComponent2D* Capture : AllCaptures)
	{
		Capture->bCaptureEveryFrame = false;
		Capture->bCaptureOnMovement = false;
	}
	CubeCapture->bCaptureEveryFrame = false;
	CubeCapture->bCaptureOnMovement = false;
}

USceneComponent* AGpuShareCaptureActor::GetCameraRoot(int32 ViewIndex) const
{
	return ViewIndex == 0 ? CameraRoot.Get() : CameraRootRight.Get();
}

USceneCaptureComponent2D* AGpuShareCaptureActor::GetColorCapture(int32 ViewIndex) const
{
	return ViewIndex == 0 ? ColorCapture.Get() : ColorCaptureRight.Get();
}

USceneCaptureComponent2D* AGpuShareCaptureActor::GetDepthCapture(int32 ViewIndex) const
{
	return ViewIndex == 0 ? DepthCapture.Get() : DepthCaptureRight.Get();
}

// ---------------------------------------------------------------------------
//  BeginPlay
// ---------------------------------------------------------------------------

void AGpuShareCaptureActor::BeginPlay()
{
	Super::BeginPlay();

	const UGpuShareSettings& Settings = UGpuShareSettings::Get();
	DefaultViewWidth         = Settings.ColorWidth;
	DefaultViewHeight        = Settings.ColorHeight;
	MaxViewDimension         = Settings.MaxViewDimension;
	CachedCubeFaceSize       = Settings.CubeFaceSize;
	bSettingsDepthEnabled    = Settings.bEnableDepthChannel;
	bSettingsCubeEnabled     = Settings.bEnableCubeChannel;
	CachedMainCaptureHz      = Settings.MainCaptureHz;
	CachedCubeCaptureHz      = Settings.CubeCaptureHz;
	CachedAcquireTimeoutMs   = Settings.ProducerAcquireTimeoutMs;
	CachedLogEveryNFrames    = Settings.LogEveryNFrames;
	bCachedPoseRelativeToAnchor = Settings.bPoseRelativeToAnchor;
	bCachedUseCustomProjection  = Settings.bUseCustomProjectionMatrix;

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

#if PLATFORM_WINDOWS
	// Le risorse esistono da subito, ma i gruppi restano vuoti finche' Unity
	// non dice con l'HELLO che forma deve avere lo stream.
	RenderResources = MakeShared<FGpuShareRenderResources, ESPMode::ThreadSafe>();
	RenderResources->bNamedHandles = Settings.bUseNamedSharedHandles;
	RenderResources->NamePrefix    = Settings.SharedHandleNamePrefix;

	{
		TSharedPtr<FGpuShareRenderResources, ESPMode::ThreadSafe> Resources = RenderResources;
		ENQUEUE_RENDER_COMMAND(GpuShareQueryDevice)(
			[Resources](FRHICommandListImmediate&)
			{
				// RHIGetNativeDevice() su RHI D3D11 restituisce l'ID3D11Device del
				// renderer di Unreal. E' lo STESSO device dei render target, quindi
				// la CopyResource verso le nostre texture e' una copia intra-device:
				// nessun trasferimento, nessuna sincronizzazione extra.
				Resources->Device = static_cast<ID3D11Device*>(GDynamicRHI->RHIGetNativeDevice());
				GpuShareGetAdapterLuid(Resources->Device, Resources->AdapterLuidLow, Resources->AdapterLuidHigh);
			});
		FlushRenderingCommands();
	}
#else
	UE_LOG(LogGpuShare, Error, TEXT("Questo spike gira solo su Windows."));
	bFatalError = true;
	return;
#endif

	// --- Canale di controllo ------------------------------------------------
	ControlChannel = MakeShared<FGpuShareControlChannel, ESPMode::ThreadSafe>();
	FString NetError;
	if (!ControlChannel->Start(Settings.ListenPort, NetError))
	{
		bFatalError = true;
		UE_LOG(LogGpuShare, Error, TEXT("Canale di controllo non avviato: %s"), *NetError);
		return;
	}

	UE_LOG(LogGpuShare, Log, TEXT("In attesa dell'HELLO di Unity: le superfici vengono create su sua richiesta."));
}

// ---------------------------------------------------------------------------
//  Configurazione dello stream
// ---------------------------------------------------------------------------

FGpuShareStreamConfig AGpuShareCaptureActor::ComputeDesiredConfig(const FGpuShareClientInfo& ClientInfo) const
{
	FGpuShareStreamConfig Config;
	Config.ViewCount = FMath::Clamp((int32)ClientInfo.ViewCount, 1, GPUSHARE_MAX_VIEWS);

	const int32 RequestedWidth  = ClientInfo.ViewWidth  > 0 ? (int32)ClientInfo.ViewWidth  : DefaultViewWidth;
	const int32 RequestedHeight = ClientInfo.ViewHeight > 0 ? (int32)ClientInfo.ViewHeight : DefaultViewHeight;

	// Un tetto esplicito: in VR le risoluzioni per occhio possono essere alte,
	// e due viste x (colore + depth) x 2 buffer fanno otto superfici.
	Config.Width  = FMath::Clamp(RequestedWidth,  16, MaxViewDimension);
	Config.Height = FMath::Clamp(RequestedHeight, 16, MaxViewDimension);
	if (Config.Width != RequestedWidth || Config.Height != RequestedHeight)
	{
		UE_LOG(LogGpuShare, Warning, TEXT("Risoluzione richiesta %dx%d limitata a %dx%d (MaxViewDimension=%d)."),
			RequestedWidth, RequestedHeight, Config.Width, Config.Height, MaxViewDimension);
	}

	// Un canale opzionale esiste solo se lo vogliono ENTRAMBI i lati: Unreal
	// (Project Settings, per il costo) e Unity (HELLO, per l'uso).
	Config.bDepth = bSettingsDepthEnabled && (ClientInfo.Flags & GS_HELLOFLAG_WANT_DEPTH) != 0;
	Config.bCube  = bSettingsCubeEnabled  && (ClientInfo.Flags & GS_HELLOFLAG_WANT_CUBE)  != 0;
	Config.CubeFaceSize = CachedCubeFaceSize;
	return Config;
}

UTextureRenderTarget2D* AGpuShareCaptureActor::CreateRenderTarget(int32 Width, int32 Height, bool bDepthFormat)
{
	// Colore: B8G8R8A8 sRGB. Depth: R32_FLOAT lineare.
	const EPixelFormat Format = bDepthFormat ? PF_R32_FLOAT : PF_B8G8R8A8;
	const bool bForceLinearGamma = bDepthFormat;

	// Niente nome esplicito: NewObject con un nome gia' usato nello stesso
	// Outer SOSTITUIREBBE l'oggetto vecchio sul posto, mentre il render thread
	// potrebbe ancora riferirlo. Con NAME_None Unreal ne genera uno univoco.
	UTextureRenderTarget2D* Target = NewObject<UTextureRenderTarget2D>(this);
	Target->ClearColor = FLinearColor::Black;
	Target->bAutoGenerateMips = false;
	// Colore: bForceLinearGamma = false -> render target sRGB, che e' cio' che
	// vuoi per SCS_FinalColorLDR. La CopyResource verso la shared texture
	// B8G8R8A8_UNORM resta valida (stessa famiglia typeless) e NON applica
	// conversioni: i bit passano intatti, compresi quelli del marker.
	Target->InitCustomFormat(Width, Height, Format, bForceLinearGamma);
	Target->UpdateResourceImmediate(true);
	return Target;
}

bool AGpuShareCaptureActor::ApplyStreamConfig(const FGpuShareStreamConfig& Config)
{
#if PLATFORM_WINDOWS
	if (!RenderResources.IsValid())
	{
		return false;
	}

	UE_LOG(LogGpuShare, Log, TEXT("Configurazione dello stream: %s"), *Config.ToString());

	bSurfacesReady = false;

	// 1. Nessuna pubblicazione in volo deve usare le risorse che stiamo per
	//    buttare: svuotiamo la coda del render thread.
	FlushRenderingCommands();

	// 2. Via le superfici vecchie. Shutdown chiude anche gli handle che avevamo
	//    duplicato dentro il processo Unity: le texture che Unity ha GIA' aperto
	//    restano valide (hanno un riferimento loro), smette solo di arrivarci
	//    roba nuova.
	{
		TSharedPtr<FGpuShareRenderResources, ESPMode::ThreadSafe> Resources = RenderResources;
		ENQUEUE_RENDER_COMMAND(GpuShareShutdownGroups)(
			[Resources](FRHICommandListImmediate&)
			{
				Resources->MainGroup.Shutdown();
				Resources->CubeGroup.Shutdown();
				// Le sequenze ripartono da 1 con i gruppi nuovi: lo stato vecchio
				// finirebbe negli STATUS e Unity lo scambierebbe per quello nuovo.
				for (GpuShareGroupStatus& Status : Resources->GroupStatus)
				{
					Status = GpuShareGroupStatus{};
				}
			});
		FlushRenderingCommands();
	}

	// 3. Render target e catture, una coppia per vista.
	TArray<FGpuShareChannelSetup> MainChannels;
	MainSourceTargets.Reset();

	for (int32 ViewIndex = 0; ViewIndex < GPUSHARE_MAX_VIEWS; ++ViewIndex)
	{
		const bool bViewActive = ViewIndex < Config.ViewCount;
		const bool bDepthActive = bViewActive && Config.bDepth;

		UTextureRenderTarget2D* ColorTarget = bViewActive ? CreateRenderTarget(Config.Width, Config.Height, false) : nullptr;
		UTextureRenderTarget2D* DepthTarget = bDepthActive ? CreateRenderTarget(Config.Width, Config.Height, true) : nullptr;

		if (ViewIndex == 0)
		{
			ColorRenderTarget = ColorTarget;
			DepthRenderTarget = DepthTarget;
		}
		else
		{
			ColorRenderTargetRight = ColorTarget;
			DepthRenderTargetRight = DepthTarget;
		}

		if (ColorTarget != nullptr)
		{
			SetupColorCapture(GetColorCapture(ViewIndex), ColorTarget);

			FGpuShareChannelSetup Color;
			Color.ChannelId      = GpuShareColorChannelForView((uint32)ViewIndex);
			Color.GroupId        = GS_GROUP_MAIN;
			Color.Width          = (uint32)Config.Width;
			Color.Height         = (uint32)Config.Height;
			Color.DxgiFormat     = (uint32)DXGI_FORMAT_B8G8R8A8_UNORM;
			Color.CopyMode       = EGpuShareCopyMode::FullCopy;
			// Il marker sta solo nella vista 0: il gruppo e' atomico, quindi
			// identifica anche tutti gli altri canali dello stesso frame.
			Color.bCarriesMarker = (ViewIndex == 0);
			MainChannels.Add(Color);
			MainSourceTargets.Add(ColorTarget);
		}
		else
		{
			DisableCapture(GetColorCapture(ViewIndex));
		}

		if (DepthTarget != nullptr)
		{
			SetupDepthCapture(GetDepthCapture(ViewIndex), DepthTarget);

			FGpuShareChannelSetup Depth;
			Depth.ChannelId  = GpuShareDepthChannelForView((uint32)ViewIndex);
			Depth.GroupId    = GS_GROUP_MAIN;
			Depth.Width      = (uint32)Config.Width;
			Depth.Height     = (uint32)Config.Height;
			Depth.DxgiFormat = (uint32)DXGI_FORMAT_R32_FLOAT;
			Depth.CopyMode   = EGpuShareCopyMode::FullCopy;
			MainChannels.Add(Depth);
			MainSourceTargets.Add(DepthTarget);
		}
		else
		{
			DisableCapture(GetDepthCapture(ViewIndex));
		}
	}

	// 4. Cube (facoltativo, costoso).
	TArray<FGpuShareChannelSetup> CubeChannels;
	if (Config.bCube)
	{
		CubeRenderTarget = NewObject<UTextureRenderTargetCube>(this);
		CubeRenderTarget->ClearColor = FLinearColor::Black;
		CubeRenderTarget->bAutoGenerateMips = false;
		CubeRenderTarget->Init(Config.CubeFaceSize, PF_B8G8R8A8);
		CubeRenderTarget->UpdateResourceImmediate(true);

		CubeCapture->TextureTarget = CubeRenderTarget;
		CubeCapture->CaptureSource = ESceneCaptureSource::SCS_FinalColorLDR;

		FGpuShareChannelSetup Cube;
		Cube.ChannelId    = GS_CH_CUBE;
		Cube.GroupId      = GS_GROUP_CUBE;
		Cube.Width        = GpuShareCubeAtlasWidth((uint32)Config.CubeFaceSize);
		Cube.Height       = GpuShareCubeAtlasHeight((uint32)Config.CubeFaceSize);
		Cube.DxgiFormat   = (uint32)DXGI_FORMAT_B8G8R8A8_UNORM;
		Cube.CopyMode     = EGpuShareCopyMode::CubeFacesToAtlas;
		Cube.CubeFaceSize = (uint32)Config.CubeFaceSize;
		CubeChannels.Add(Cube);

		UE_LOG(LogGpuShare, Warning,
			TEXT("Canale CUBE attivo: ogni cattura costa SEI render completi della scena. ")
			TEXT("Faccia=%d px, cadenza=%.1f Hz."), Config.CubeFaceSize, CachedCubeCaptureHz);
	}
	else
	{
		CubeRenderTarget = nullptr;
		CubeCapture->TextureTarget = nullptr;
	}

	if (Config.ViewCount > 1)
	{
		UE_LOG(LogGpuShare, Log,
			TEXT("Stereo: %d catture di scena per frame (%s). Ogni occhio e' un render completo."),
			Config.bDepth ? 4 : 2, Config.bDepth ? TEXT("colore + depth per occhio") : TEXT("solo colore"));
	}

	// 5. Superfici condivise.
	TSharedPtr<FGpuShareRenderResources, ESPMode::ThreadSafe> Resources = RenderResources;
	ENQUEUE_RENDER_COMMAND(GpuShareInitSurfaces)(
		[Resources, MainChannels, CubeChannels](FRHICommandListImmediate&)
		{
			FString Error;
			if (!Resources->MainGroup.Initialize(Resources->Device, GS_GROUP_MAIN, MainChannels,
				Resources->bNamedHandles, Resources->NamePrefix, Error))
			{
				UE_LOG(LogGpuShare, Error, TEXT("Gruppo MAIN non inizializzato: %s"), *Error);
				return;
			}

			if (CubeChannels.Num() > 0 &&
				!Resources->CubeGroup.Initialize(Resources->Device, GS_GROUP_CUBE, CubeChannels,
					Resources->bNamedHandles, Resources->NamePrefix, Error))
			{
				UE_LOG(LogGpuShare, Error, TEXT("Gruppo CUBE non inizializzato: %s"), *Error);
			}
		});

	// Succede solo a ogni cambio di forma dello stream: lo stallo non conta.
	FlushRenderingCommands();

	bSurfacesReady = RenderResources->MainGroup.IsInitialized();
	if (!bSurfacesReady)
	{
		UE_LOG(LogGpuShare, Error, TEXT("Creazione delle superfici condivise fallita, vedi sopra."));
		return false;
	}

	CurrentConfig = Config;
	++ConfigId;
	HandshakePid = 0;   // i gruppi sono nuovi: gli handle vanno duplicati di nuovo
	bLoggedFirstPose = false;

	UE_LOG(LogGpuShare, Log, TEXT("Superfici pronte (config %u): %d canali MAIN%s."),
		ConfigId, MainChannels.Num(), CubeChannels.Num() > 0 ? TEXT(" + CUBE") : TEXT(""));
	return true;
#else
	return false;
#endif
}

// ---------------------------------------------------------------------------
//  Handshake
// ---------------------------------------------------------------------------

void AGpuShareCaptureActor::HandleHello(const FGpuShareClientInfo& ClientInfo)
{
#if PLATFORM_WINDOWS
	if (!RenderResources.IsValid())
	{
		return;
	}

	const FGpuShareStreamConfig Desired = ComputeDesiredConfig(ClientInfo);
	const bool bReconfigure = !bSurfacesReady || Desired != CurrentConfig;

	if (bReconfigure && !ApplyStreamConfig(Desired))
	{
		return;
	}

	// Un HELLO ripetuto dallo stesso processo con la stessa forma e' la norma
	// (Unity lo ripete finche' non riceve risposta): NON riduplichiamo gli
	// handle. Se lo facessimo chiuderemmo quelli vecchi dentro Unity proprio
	// mentre Unity potrebbe starli aprendo, e l'apertura fallirebbe.
	const bool bDuplicate = bReconfigure || ClientInfo.Pid != HandshakePid;
	HandshakePid = ClientInfo.Pid;

	// Controllo GPU: se i due processi finiscono su adapter diversi,
	// OpenSharedResource1 lato Unity fallisce con un HRESULT che non spiega
	// niente. Accorgersene qui fa risparmiare ore.
	if ((ClientInfo.AdapterLuidLow != 0 || ClientInfo.AdapterLuidHigh != 0) &&
		(ClientInfo.AdapterLuidLow != RenderResources->AdapterLuidLow ||
		 ClientInfo.AdapterLuidHigh != RenderResources->AdapterLuidHigh))
	{
		UE_LOG(LogGpuShare, Error,
			TEXT("MISMATCH DI ADAPTER: Unreal e' su LUID %u:%d, Unity su %u:%d. ")
			TEXT("La condivisione NON puo' funzionare tra GPU diverse. ")
			TEXT("Forza entrambi gli eseguibili sulla stessa GPU (Impostazioni Windows > Schermo > Grafica)."),
			RenderResources->AdapterLuidLow, RenderResources->AdapterLuidHigh,
			ClientInfo.AdapterLuidLow, ClientInfo.AdapterLuidHigh);
		// Mandiamo comunque l'handshake: Unity mostrera' l'errore in HUD.
	}

	TSharedPtr<FGpuShareRenderResources, ESPMode::ThreadSafe> Resources = RenderResources;
	TSharedPtr<FGpuShareControlChannel, ESPMode::ThreadSafe> Channel = ControlChannel;
	const uint32 UnityPid  = ClientInfo.Pid;
	const uint32 UePid     = (uint32)FPlatformProcess::GetCurrentProcessId();
	const uint32 Views     = (uint32)CurrentConfig.ViewCount;
	const uint32 ConfigIdCopy = ConfigId;
	const uint32 RequestId = ClientInfo.RequestId;

	ENQUEUE_RENDER_COMMAND(GpuShareHandshake)(
		[Resources, Channel, UnityPid, UePid, bDuplicate, Views, ConfigIdCopy, RequestId](FRHICommandListImmediate&)
		{
			if (bDuplicate)
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
			}

			GpuShareHandshake Packet = {};
			GpuShareInitHeader(&Packet.header, GS_PKT_HANDSHAKE);
			Packet.ue_pid            = UePid;
			Packet.adapter_luid_low  = Resources->AdapterLuidLow;
			Packet.adapter_luid_high = Resources->AdapterLuidHigh;
			Packet.handle_mode       = Resources->bNamedHandles ? GS_HANDLEMODE_NAMED : GS_HANDLEMODE_DUPLICATED;
			Packet.view_count        = Views;
			Packet.config_id         = ConfigIdCopy;
			Packet.request_id        = RequestId;

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
	AppliedNearCm = FMath::Max(1.0f, Pose.NearM * 100.0f);

	for (int32 ViewIndex = 0; ViewIndex < CurrentConfig.ViewCount; ++ViewIndex)
	{
		USceneComponent* Root = GetCameraRoot(ViewIndex);
		if (Root == nullptr)
		{
			// Succede solo se il modulo C++ e' stato hot-reloadato male: i
			// componenti sono stati aggiunti di recente e il CDO puo' restare
			// indietro. Chiudi l'editor, ricompila da zero, riapri.
			UE_LOG(LogGpuShare, Error, TEXT("CameraRoot della vista %d nullo: ricompila il modulo C++ da editor chiuso."), ViewIndex);
			return;
		}

		// Transitorio durante un cambio di modalita': Unity puo' mandare ancora
		// una pose mono mentre lo stream e' gia' stereo. Usiamo la vista 0.
		const FGpuShareViewState& View = Pose.Views[FMath::Min(ViewIndex, Pose.ViewCount - 1)];

		// Un'unica conversione di coordinate, dentro FGpuShareViewState.
		const FTransform PoseTransform = View.ToUnrealTransform();

		if (bCachedPoseRelativeToAnchor)
		{
			// La pose e' RELATIVA all'ancora: la transform dell'attore resta
			// intoccata e continua a essere comandata da chi la comanda.
			Root->SetRelativeTransform(PoseTransform);
		}
		else
		{
			// Modalita' assoluta: la pose e' una transform di MONDO. Anche qui
			// non tocchiamo l'attore: posizioniamo direttamente la vista.
			Root->SetWorldTransform(PoseTransform);
		}

		ApplyProjection(ViewIndex, View);
	}

	AppliedFovYDeg = Pose.Views[0].VerticalFovDeg();

	if (!bLoggedFirstPose)
	{
		bLoggedFirstPose = true;
		for (int32 ViewIndex = 0; ViewIndex < CurrentConfig.ViewCount; ++ViewIndex)
		{
			const FGpuShareViewState& View = Pose.Views[FMath::Min(ViewIndex, Pose.ViewCount - 1)];
			UE_LOG(LogGpuShare, Log,
				TEXT("Prima pose applicata, vista %d.\n")
				TEXT("  ancora (attore) : %s\n")
				TEXT("  pose relativa   : %s\n")
				TEXT("  camera nel mondo: %s\n")
				TEXT("  frustum         : sx %.3f dx %.3f giu %.3f su %.3f (%.1f x %.1f gradi%s), near %.1f cm"),
				ViewIndex,
				*GetActorTransform().GetLocation().ToString(),
				*View.ToUnrealTransform().GetLocation().ToString(),
				*GetCameraRoot(ViewIndex)->GetComponentLocation().ToString(),
				View.TanLeft, View.TanRight, View.TanDown, View.TanUp,
				View.HorizontalFovDeg(), View.VerticalFovDeg(),
				View.IsSymmetric() ? TEXT(", simmetrico") : TEXT(", ASIMMETRICO"),
				AppliedNearCm);
		}
	}
}

void AGpuShareCaptureActor::ApplyProjection(int32 ViewIndex, const FGpuShareViewState& View)
{
	USceneCaptureComponent2D* Captures[2] =
	{
		GetColorCapture(ViewIndex),
		CurrentConfig.bDepth ? GetDepthCapture(ViewIndex) : nullptr,
	};

	const FMatrix Projection = GpuShareBuildOffCenterProjection(View, AppliedNearCm);

	if (!bCachedUseCustomProjection && !View.IsSymmetric() && !bLoggedAsymmetricWithoutCustom)
	{
		bLoggedAsymmetricWithoutCustom = true;
		UE_LOG(LogGpuShare, Warning,
			TEXT("Frustum asimmetrico (VR) con bUseCustomProjectionMatrix=false: approssimo con un FOV ")
			TEXT("simmetrico e l'immagine NON combacera' con l'occhio. Riattiva la proiezione custom."));
	}

	for (USceneCaptureComponent2D* Capture : Captures)
	{
		if (Capture == nullptr)
		{
			continue;
		}

		// FOVAngle e' ORIZZONTALE in gradi. Con la proiezione custom non
		// definisce l'immagine, ma lo impostiamo comunque: Unreal lo usa per
		// alcune euristiche (LOD, screen size) indipendenti dalla matrice.
		Capture->FOVAngle = View.HorizontalFovDeg();

		// La proiezione custom e' l'unico modo di ottenere il frustum ESATTO,
		// asimmetrico per un occhio di un visore, simmetrico in desktop. Con la
		// matrice il frustum copre tutto il render target a prescindere dal suo
		// aspect, quindi l'immagine coincide con cio' che Unity si aspetta.
		Capture->bUseCustomProjectionMatrix = bCachedUseCustomProjection;
		if (bCachedUseCustomProjection)
		{
			Capture->CustomProjectionMatrix = Projection;
		}

#if GPUSHARE_USE_CUSTOM_NEAR_CLIP
		Capture->bOverride_CustomNearClippingPlane = true;
		Capture->CustomNearClippingPlane = AppliedNearCm;
#endif
	}
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
		// Stesso ordine dei canali creati in ApplyStreamConfig.
		for (UTextureRenderTarget2D* Target : MainSourceTargets)
		{
			Sources.Add(Target != nullptr ? Target->GameThread_GetRenderTargetResource() : nullptr);
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
	StatusTemplate.config_id             = ConfigId;
	StatusTemplate.applied_fov_y_deg     = AppliedFovYDeg;
	StatusTemplate.render_fov_y_deg      = AppliedFovYDeg;
	StatusTemplate.applied_near_cm       = AppliedNearCm;
	StatusTemplate.depth_scale_to_meters = 0.01f;   // unita' Unreal (cm) -> metri

	{
		// Matrici DIAGNOSTICHE della vista 0, in convenzione Unreal. Vedi il
		// commento in ShareProtocol.h: Unity NON le usa per ricostruire la
		// camera, usa la pose che ha spedito lei stessa.
		const FTransform CameraTransform = CameraRoot->GetComponentTransform();
		const FMatrix ViewMatrix =
			FTranslationMatrix(-CameraTransform.GetLocation()) *
			FInverseRotationMatrix(CameraTransform.GetRotation().Rotator()) *
			FMatrix(FPlane(0, 0, 1, 0), FPlane(1, 0, 0, 0), FPlane(0, 1, 0, 0), FPlane(0, 0, 0, 1));

		MatrixToFloatArray(ViewMatrix, StatusTemplate.ue_view_matrix);
		MatrixToFloatArray(GpuShareBuildOffCenterProjection(Pose.Views[0], AppliedNearCm), StatusTemplate.ue_proj_matrix);
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
			//
			// Il parametro e' dichiarato come FRHICommandListBase&: a seconda
			// della versione di engine la lambda viene invocata con la classe
			// base o con la derivata, e la base lega in entrambi i casi.
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

	// 1. Nuovo HELLO? (ri)crea le superfici se serve e manda la tabella dei canali.
	FGpuShareClientInfo ClientInfo;
	if (ControlChannel->ConsumePendingHello(ClientInfo))
	{
		HandleHello(ClientInfo);
	}

	if (!bSurfacesReady)
	{
		return;
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

	if (!ControlChannel->HasClient() || !bHasPose)
	{
		return;
	}

	const double Now = FPlatformTime::Seconds();

	// 3. Gruppo MAIN: colore (+ depth) di ogni vista, stesso frame.
	if (ShouldCaptureNow(CachedMainCaptureHz, LastMainCaptureTime, Now))
	{
		// CaptureScene() accoda IMMEDIATAMENTE i comandi di render della
		// cattura. Il nostro publish, accodato subito dopo, e' quindi ordinato
		// dopo di esse sul render thread: quando copiamo, i render target
		// contengono gia' il frame nuovo, per tutte le viste.
		for (int32 ViewIndex = 0; ViewIndex < CurrentConfig.ViewCount; ++ViewIndex)
		{
			GetColorCapture(ViewIndex)->CaptureScene();
			if (CurrentConfig.bDepth)
			{
				GetDepthCapture(ViewIndex)->CaptureScene();
			}
		}
		EnqueuePublish(GS_GROUP_MAIN, Pose);
		++PublishedMainFrames;
	}

	// 4. Gruppo CUBE: cadenza indipendente e molto piu' bassa.
	if (CurrentConfig.bCube && ShouldCaptureNow(CachedCubeCaptureHz, LastCubeCaptureTime, Now))
	{
		CubeCapture->CaptureScene();
		EnqueuePublish(GS_GROUP_CUBE, Pose);
	}

	++UeFrameCounter;

	// 5. Log periodico.
	if (CachedLogEveryNFrames > 0 && PublishedMainFrames > 0 && (PublishedMainFrames % (uint64)CachedLogEveryNFrames) == 0)
	{
		const double Elapsed = Now - LastLogTime;
		if (Elapsed > 0.5)
		{
			UE_LOG(LogGpuShare, Log,
				TEXT("frame UE=%llu  pubblicati=%llu  pose ricevute=%llu  pacchetti scartati=%llu  config=%u (%s)"),
				UeFrameCounter, PublishedMainFrames,
				ControlChannel->GetPosePacketCount(), ControlChannel->GetBadPacketCount(),
				ConfigId, *CurrentConfig.ToString());
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
	MainSourceTargets.Reset();
	bSurfacesReady = false;

	Super::EndPlay(EndPlayReason);
}
