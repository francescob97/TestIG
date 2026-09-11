#include "Gpu/D3D11ChannelGroup.h"

#if PLATFORM_WINDOWS

#include "Gpu/CubeFaceAtlas.h"
#include "Gpu/GpuShareTime.h"
#include "GpuShareLog.h"
#include "RenderingThread.h"   // IsInRenderingThread / IsInRHIThread

#include "Windows/AllowWindowsPlatformTypes.h"

namespace
{
	/** AcquireSync puo' tornare WAIT_ABANDONED: in quel caso il mutex E' nostro. */
	FORCEINLINE bool AcquireSucceeded(HRESULT Hr)
	{
		return Hr == S_OK || Hr == (HRESULT)WAIT_ABANDONED;
	}

	FString HrToString(HRESULT Hr)
	{
		return FString::Printf(TEXT("0x%08X"), (uint32)Hr);
	}
}

// ---------------------------------------------------------------------------

FGpuShareD3D11ChannelGroup::~FGpuShareD3D11ChannelGroup()
{
	Shutdown();
}

bool FGpuShareD3D11ChannelGroup::Initialize(
	ID3D11Device* Device,
	uint32 InGroupId,
	TArrayView<const FGpuShareChannelSetup> InChannels,
	bool bNamedHandles,
	const FString& NamePrefix,
	FString& OutError)
{
	check(IsInRenderingThread());

	Shutdown();

	if (Device == nullptr)
	{
		OutError = TEXT("ID3D11Device nullo: l'RHI attivo non e' D3D11?");
		return false;
	}
	if (InChannels.Num() == 0)
	{
		OutError = TEXT("Gruppo senza canali");
		return false;
	}

	D3DDevice = Device;
	// GetImmediateContext fa AddRef: TRefCountPtr lo rilascera' lui.
	D3DDevice->GetImmediateContext(ImmediateContext.GetInitReference());
	if (!ImmediateContext.IsValid())
	{
		OutError = TEXT("GetImmediateContext ha restituito null");
		return false;
	}

	GroupId = InGroupId;
	bUseNamedHandles = bNamedHandles;

	Channels.Reset();
	Channels.AddDefaulted(InChannels.Num());

	for (int32 ChannelIndex = 0; ChannelIndex < InChannels.Num(); ++ChannelIndex)
	{
		FChannel& Channel = Channels[ChannelIndex];
		Channel.Setup = InChannels[ChannelIndex];

		for (int32 BufferIndex = 0; BufferIndex < GPUSHARE_BUFFERS_PER_CHANNEL; ++BufferIndex)
		{
			if (bUseNamedHandles)
			{
				// Deve combaciare ESATTAMENTE con quello che ricostruisce Unity.
				// Vedi ShareProtocol.h, campo name_prefix.
				Channel.Buffers[BufferIndex].Name = FString::Printf(
					TEXT("%s_ch%u_b%d"), *NamePrefix, Channel.Setup.ChannelId, BufferIndex);
			}

			if (!CreateSurface(Channel, BufferIndex, OutError))
			{
				Shutdown();
				return false;
			}
		}

		UE_LOG(LogGpuShare, Log,
			TEXT("[gruppo %u] canale %u creato: %ux%u, DXGI_FORMAT=%u, marker=%s"),
			GroupId, Channel.Setup.ChannelId, Channel.Setup.Width, Channel.Setup.Height,
			Channel.Setup.DxgiFormat, Channel.Setup.bCarriesMarker ? TEXT("si") : TEXT("no"));
	}

	if (!CreateMarkerStaging(OutError))
	{
		Shutdown();
		return false;
	}

	LastPublishedIndex = GPUSHARE_BUFFERS_PER_CHANNEL - 1;
	PublishSequence = 0;
	SkippedFrames = 0;
	bInitialized = true;
	return true;
}

bool FGpuShareD3D11ChannelGroup::CreateSurface(FChannel& Channel, int32 BufferIndex, FString& OutError)
{
	FSurface& Surface = Channel.Buffers[BufferIndex];

	D3D11_TEXTURE2D_DESC Desc = {};
	Desc.Width  = Channel.Setup.Width;
	Desc.Height = Channel.Setup.Height;

	// VINCOLI NON NEGOZIABILI per una shared resource D3D11:
	//   MipLevels == 1, ArraySize == 1, niente MSAA, Usage DEFAULT, CPUAccessFlags == 0.
	// E' esattamente il motivo per cui la cubemap deve passare da un atlas 2D.
	Desc.MipLevels = 1;
	Desc.ArraySize = 1;
	Desc.Format = (DXGI_FORMAT)Channel.Setup.DxgiFormat;
	Desc.SampleDesc.Count = 1;
	Desc.SampleDesc.Quality = 0;
	Desc.Usage = D3D11_USAGE_DEFAULT;
	// SHADER_RESOURCE serve a Unity per campionarla; RENDER_TARGET lo mettono
	// tutte le implementazioni note di texture sharing perche' alcuni driver
	// rifiutano la condivisione senza.
	Desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
	Desc.CPUAccessFlags = 0;
	Desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;

	HRESULT Hr = D3DDevice->CreateTexture2D(&Desc, nullptr, Surface.Texture.GetInitReference());
	if (FAILED(Hr))
	{
		OutError = FString::Printf(
			TEXT("CreateTexture2D fallita per canale %u buffer %d (%ux%u fmt=%u): hr=%s"),
			Channel.Setup.ChannelId, BufferIndex, Desc.Width, Desc.Height,
			Channel.Setup.DxgiFormat, *HrToString(Hr));
		return false;
	}

	// IDXGIResource1 e' l'interfaccia che espone CreateSharedHandle (NT handle).
	// La vecchia IDXGIResource::GetSharedHandle da' handle NON-NT, legacy e
	// senza keyed mutex: non e' quello che vogliamo.
	TRefCountPtr<IDXGIResource1> DxgiResource;
	Hr = Surface.Texture->QueryInterface(__uuidof(IDXGIResource1), (void**)DxgiResource.GetInitReference());
	if (FAILED(Hr))
	{
		OutError = FString::Printf(TEXT("QueryInterface(IDXGIResource1) fallita: hr=%s"), *HrToString(Hr));
		return false;
	}

	const TCHAR* NamePtr = bUseNamedHandles ? *Surface.Name : nullptr;
	Hr = DxgiResource->CreateSharedHandle(
		/*pAttributes*/ nullptr,
		DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
		NamePtr,
		&Surface.LocalHandle);
	if (FAILED(Hr))
	{
		OutError = FString::Printf(TEXT("CreateSharedHandle fallita (nome='%s'): hr=%s"),
			bUseNamedHandles ? *Surface.Name : TEXT("<anonimo>"), *HrToString(Hr));
		return false;
	}

	Hr = Surface.Texture->QueryInterface(__uuidof(IDXGIKeyedMutex), (void**)Surface.KeyedMutex.GetInitReference());
	if (FAILED(Hr))
	{
		OutError = FString::Printf(TEXT("QueryInterface(IDXGIKeyedMutex) fallita: hr=%s"), *HrToString(Hr));
		return false;
	}

	return true;
}

bool FGpuShareD3D11ChannelGroup::CreateMarkerStaging(FString& OutError)
{
	// Texture 8x1 DYNAMIC: la mappiamo in CPU, ci scriviamo i 32 byte del
	// marker e poi la copiamo dentro la shared texture.
	//
	// Perche' non UpdateSubresource direttamente sulla shared texture:
	// funzionerebbe quasi certamente, ma su una risorsa
	// SHARED_NTHANDLE|KEYED_MUTEX e' territorio poco battuto e un
	// comportamento strano qui sarebbe difficile da isolare. Questa strada
	// usa solo operazioni ordinarie ed e' a prova di driver.
	D3D11_TEXTURE2D_DESC Desc = {};
	Desc.Width = GPUSHARE_MARKER_PIXELS;
	Desc.Height = 1;
	Desc.MipLevels = 1;
	Desc.ArraySize = 1;
	Desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
	Desc.SampleDesc.Count = 1;
	Desc.Usage = D3D11_USAGE_DYNAMIC;
	Desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;   // DYNAMIC esige almeno un bind flag
	Desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
	Desc.MiscFlags = 0;

	const HRESULT Hr = D3DDevice->CreateTexture2D(&Desc, nullptr, MarkerStaging.GetInitReference());
	if (FAILED(Hr))
	{
		OutError = FString::Printf(TEXT("CreateTexture2D (marker staging) fallita: hr=%s"), *HrToString(Hr));
		return false;
	}
	return true;
}

void FGpuShareD3D11ChannelGroup::Shutdown()
{
	ReleaseRemoteHandles();

	for (FChannel& Channel : Channels)
	{
		for (int32 BufferIndex = 0; BufferIndex < GPUSHARE_BUFFERS_PER_CHANNEL; ++BufferIndex)
		{
			FSurface& Surface = Channel.Buffers[BufferIndex];
			if (Surface.LocalHandle != nullptr)
			{
				::CloseHandle(Surface.LocalHandle);
				Surface.LocalHandle = nullptr;
			}
			Surface.KeyedMutex.SafeRelease();
			Surface.Texture.SafeRelease();
		}
	}
	Channels.Reset();

	MarkerStaging.SafeRelease();
	ImmediateContext.SafeRelease();
	D3DDevice.SafeRelease();

	bInitialized = false;
}

// ---------------------------------------------------------------------------
//  Passaggio degli handle al processo Unity
// ---------------------------------------------------------------------------

bool FGpuShareD3D11ChannelGroup::DuplicateHandlesInto(uint32 UnityPid, FString& OutError)
{
	if (!bInitialized)
	{
		OutError = TEXT("gruppo non inizializzato");
		return false;
	}

	// In modalita' handle NOMINATI non serve duplicare nulla: Unity apre per nome.
	if (bUseNamedHandles)
	{
		RemotePid = UnityPid;
		return true;
	}

	// Se lo stesso processo Unity rifa' l'handshake, chiudiamo i suoi handle
	// vecchi: altrimenti gliene accumuliamo uno per canale a ogni riconnessione.
	if (RemotePid != 0 && RemotePid != UnityPid)
	{
		ReleaseRemoteHandles();
	}

	// PROCESS_DUP_HANDLE su un processo dello stesso utente e dello stesso
	// integrity level non richiede privilegi speciali. Se questo fallisce con
	// ERROR_ACCESS_DENIED (5) quasi sempre significa che uno dei due processi
	// gira come amministratore e l'altro no.
	HANDLE UnityProcess = ::OpenProcess(PROCESS_DUP_HANDLE, FALSE, (DWORD)UnityPid);
	if (UnityProcess == nullptr)
	{
		const DWORD LastError = ::GetLastError();
		OutError = FString::Printf(
			TEXT("OpenProcess(PROCESS_DUP_HANDLE, pid=%u) fallita, GetLastError=%u. ")
			TEXT("Se e' 5 (ACCESS_DENIED): non lanciare uno dei due processi come amministratore, ")
			TEXT("oppure attiva bUseNamedSharedHandles nei Project Settings."),
			UnityPid, LastError);
		return false;
	}

	bool bAllOk = true;
	for (FChannel& Channel : Channels)
	{
		for (int32 BufferIndex = 0; BufferIndex < GPUSHARE_BUFFERS_PER_CHANNEL; ++BufferIndex)
		{
			FSurface& Surface = Channel.Buffers[BufferIndex];
			HANDLE Duplicated = nullptr;

			const BOOL bOk = ::DuplicateHandle(
				::GetCurrentProcess(), Surface.LocalHandle,
				UnityProcess,          &Duplicated,
				0, FALSE, DUPLICATE_SAME_ACCESS);

			if (!bOk)
			{
				OutError = FString::Printf(
					TEXT("DuplicateHandle fallita per canale %u buffer %d, GetLastError=%u"),
					Channel.Setup.ChannelId, BufferIndex, ::GetLastError());
				bAllOk = false;
				break;
			}

			// Il valore numerico dell'handle e' cio' che mandiamo nell'handshake:
			// e' valido SOLO dentro il processo Unity.
			Surface.RemoteHandle = (uint64)(UPTRINT)Duplicated;
		}
		if (!bAllOk) { break; }
	}

	::CloseHandle(UnityProcess);

	if (bAllOk)
	{
		RemotePid = UnityPid;
		UE_LOG(LogGpuShare, Log, TEXT("[gruppo %u] handle duplicati nel processo Unity pid=%u"), GroupId, UnityPid);
	}
	return bAllOk;
}

void FGpuShareD3D11ChannelGroup::ReleaseRemoteHandles()
{
	if (RemotePid == 0 || bUseNamedHandles)
	{
		RemotePid = 0;
		return;
	}

	// DuplicateHandle con DUPLICATE_CLOSE_SOURCE e destinazione nulla chiude
	// l'handle NEL PROCESSO SORGENTE. E' l'unico modo di chiudere un handle che
	// vive in un altro processo.
	HANDLE UnityProcess = ::OpenProcess(PROCESS_DUP_HANDLE, FALSE, (DWORD)RemotePid);
	if (UnityProcess != nullptr)
	{
		for (FChannel& Channel : Channels)
		{
			for (int32 BufferIndex = 0; BufferIndex < GPUSHARE_BUFFERS_PER_CHANNEL; ++BufferIndex)
			{
				FSurface& Surface = Channel.Buffers[BufferIndex];
				if (Surface.RemoteHandle != 0)
				{
					::DuplicateHandle(UnityProcess, (HANDLE)(UPTRINT)Surface.RemoteHandle,
						nullptr, nullptr, 0, FALSE, DUPLICATE_CLOSE_SOURCE);
					Surface.RemoteHandle = 0;
				}
			}
		}
		::CloseHandle(UnityProcess);
	}
	else
	{
		// Il processo Unity non c'e' piu': i suoi handle sono morti con lui.
		for (FChannel& Channel : Channels)
		{
			for (int32 BufferIndex = 0; BufferIndex < GPUSHARE_BUFFERS_PER_CHANNEL; ++BufferIndex)
			{
				Channel.Buffers[BufferIndex].RemoteHandle = 0;
			}
		}
	}

	RemotePid = 0;
}

void FGpuShareD3D11ChannelGroup::FillChannelDesc(int32 ChannelIndex, GpuShareChannelDesc& OutDesc) const
{
	FMemory::Memzero(&OutDesc, sizeof(OutDesc));
	if (!Channels.IsValidIndex(ChannelIndex))
	{
		return;
	}

	const FChannel& Channel = Channels[ChannelIndex];
	OutDesc.channel_id  = Channel.Setup.ChannelId;
	OutDesc.group_id    = GroupId;
	OutDesc.width       = Channel.Setup.Width;
	OutDesc.height      = Channel.Setup.Height;
	OutDesc.dxgi_format = Channel.Setup.DxgiFormat;
	OutDesc.flags       = Channel.Setup.bCarriesMarker ? GS_CHFLAG_HAS_MARKER : GS_CHFLAG_NONE;

	for (int32 BufferIndex = 0; BufferIndex < GPUSHARE_BUFFERS_PER_CHANNEL; ++BufferIndex)
	{
		OutDesc.shared_handles[BufferIndex] = Channel.Buffers[BufferIndex].RemoteHandle;
	}
}

// ---------------------------------------------------------------------------
//  Pubblicazione
// ---------------------------------------------------------------------------

bool FGpuShareD3D11ChannelGroup::TryAcquireAll(int32 BufferIndex, uint64 Key, int32 TimeoutMs)
{
	int32 AcquiredCount = 0;

	for (int32 ChannelIndex = 0; ChannelIndex < Channels.Num(); ++ChannelIndex)
	{
		IDXGIKeyedMutex* Mutex = Channels[ChannelIndex].Buffers[BufferIndex].KeyedMutex.GetReference();
		const HRESULT Hr = Mutex->AcquireSync(Key, (DWORD)FMath::Max(0, TimeoutMs));

		if (Hr == (HRESULT)WAIT_ABANDONED)
		{
			// L'altro processo e' morto tenendo il mutex. Ora e' nostro, ma il
			// contenuto del buffer e' da buttare.
			UE_LOG(LogGpuShare, Warning,
				TEXT("[gruppo %u] WAIT_ABANDONED sul canale %d buffer %d: il consumatore e' morto tenendo il mutex"),
				GroupId, ChannelIndex, BufferIndex);
		}

		if (!AcquireSucceeded(Hr))
		{
			// Rollback: rilasciamo con la STESSA chiave quelli gia' presi, cosi'
			// tornano esattamente nello stato di prima (disponibili al produttore).
			for (int32 Rollback = 0; Rollback < AcquiredCount; ++Rollback)
			{
				Channels[Rollback].Buffers[BufferIndex].KeyedMutex->ReleaseSync(Key);
			}
			return false;
		}

		++AcquiredCount;
	}

	return true;
}

void FGpuShareD3D11ChannelGroup::ReleaseAll(int32 BufferIndex, uint64 Key)
{
	for (FChannel& Channel : Channels)
	{
		Channel.Buffers[BufferIndex].KeyedMutex->ReleaseSync(Key);
	}
}

void FGpuShareD3D11ChannelGroup::WriteMarker(ID3D11Texture2D* Destination, uint64 FrameId, int64 QpcPoseSend, int64 QpcRenderEnd)
{
	if (!MarkerStaging.IsValid() || Destination == nullptr)
	{
		return;
	}

	GpuShareMarker Marker = {};
	Marker.frame_id       = FrameId;
	Marker.qpc_pose_send  = QpcPoseSend;
	Marker.qpc_render_end = QpcRenderEnd;
	GpuShareMarkerFinalize(&Marker);   // scrive magic + checksum

	D3D11_MAPPED_SUBRESOURCE Mapped = {};
	const HRESULT Hr = ImmediateContext->Map(MarkerStaging.GetReference(), 0, D3D11_MAP_WRITE_DISCARD, 0, &Mapped);
	if (FAILED(Hr))
	{
		return;
	}

	FMemory::Memcpy(Mapped.pData, &Marker, sizeof(Marker));
	ImmediateContext->Unmap(MarkerStaging.GetReference(), 0);

	// Gli 8 pixel finiscono in alto a sinistra (0,0). La copia e' ordinata DOPO
	// la CopyResource sull'immediate context, quindi non viene sovrascritta.
	D3D11_BOX Box = {};
	Box.left   = 0;
	Box.top    = 0;
	Box.front  = 0;
	Box.right  = GPUSHARE_MARKER_PIXELS;
	Box.bottom = 1;
	Box.back   = 1;

	ImmediateContext->CopySubresourceRegion(Destination, 0, 0, 0, 0, MarkerStaging.GetReference(), 0, &Box);
}

bool FGpuShareD3D11ChannelGroup::Publish(const FGpuSharePublishInput& Input, int32 TimeoutMs, FGpuSharePublishResult& OutResult)
{
	// Publish() gira dentro RHICmdList.EnqueueLambda. Su D3D11 non c'e' un RHI
	// thread separato, quindi siamo sul render thread; ma la check accetta anche
	// l'RHI thread perche' e' li' che finirebbe con altri RHI.
	check(IsInRenderingThread() || IsInRHIThread());

	OutResult = FGpuSharePublishResult();

	if (!bInitialized || Input.Sources.Num() != Channels.Num())
	{
		return false;
	}

	// Preferiamo il buffer diverso dall'ultimo pubblicato: e' quello che il
	// consumatore con ogni probabilita' non sta leggendo. Se e' occupato
	// proviamo l'altro. Se sono occupati entrambi, il consumatore e' indietro:
	// SALTIAMO il frame invece di bloccare il render thread di Unreal.
	const int32 Preferred = (LastPublishedIndex + 1) % GPUSHARE_BUFFERS_PER_CHANNEL;

	int32 ChosenIndex = INDEX_NONE;
	for (int32 Attempt = 0; Attempt < GPUSHARE_BUFFERS_PER_CHANNEL; ++Attempt)
	{
		const int32 Candidate = (Preferred + Attempt) % GPUSHARE_BUFFERS_PER_CHANNEL;
		if (TryAcquireAll(Candidate, GPUSHARE_KEY_PRODUCER, TimeoutMs))
		{
			ChosenIndex = Candidate;
			break;
		}
	}

	if (ChosenIndex == INDEX_NONE)
	{
		++SkippedFrames;
		return false;
	}

	// --- Da qui teniamo i mutex di TUTTI i canali del gruppo: le copie che
	//     seguono sono la parte "atomica" della pubblicazione. -----------------

	for (int32 ChannelIndex = 0; ChannelIndex < Channels.Num(); ++ChannelIndex)
	{
		FChannel& Channel = Channels[ChannelIndex];
		ID3D11Texture2D* Source = Input.Sources[ChannelIndex];
		ID3D11Texture2D* Destination = Channel.Buffers[ChosenIndex].Texture.GetReference();

		if (Source == nullptr || Destination == nullptr)
		{
			continue;
		}

		switch (Channel.Setup.CopyMode)
		{
		case EGpuShareCopyMode::FullCopy:
			// CopyResource richiede stesse dimensioni e formati "compatibili",
			// cioe' della stessa famiglia typeless. E' per questo che copiare
			// da un render target B8G8R8A8_UNORM_SRGB (quello che Unreal crea
			// di solito) verso il nostro B8G8R8A8_UNORM funziona: stessa
			// famiglia. Nessuna conversione di colore avviene: i bit passano
			// invariati, che e' esattamente cio' che serve al marker.
			ImmediateContext->CopyResource(Destination, Source);
			break;

		case EGpuShareCopyMode::CubeFacesToAtlas:
			GpuShareCopyCubeFacesToAtlas(ImmediateContext.GetReference(), Source, Destination, Channel.Setup.CubeFaceSize);
			break;
		}
	}

	// Timestamp preso DOPO aver accodato le copie. Attenzione: e' il momento
	// della SUBMIT lato CPU, non il completamento GPU. Il momento reale in cui
	// i pixel sono pronti e' quando l'AcquireSync di Unity ritorna, perche' il
	// rilascio del keyed mutex e' ordinato rispetto alle copie sulla GPU.
	const int64 QpcRenderEnd = GpuShareQpcNow();

	for (int32 ChannelIndex = 0; ChannelIndex < Channels.Num(); ++ChannelIndex)
	{
		if (Channels[ChannelIndex].Setup.bCarriesMarker)
		{
			WriteMarker(
				Channels[ChannelIndex].Buffers[ChosenIndex].Texture.GetReference(),
				Input.FrameId, Input.QpcPoseSend, QpcRenderEnd);
		}
	}

	// Rilasciando con KEY_CONSUMER rendiamo il buffer acquisibile da Unity.
	ReleaseAll(ChosenIndex, GPUSHARE_KEY_CONSUMER);

	LastPublishedIndex = ChosenIndex;
	++PublishSequence;

	OutResult.bPublished   = true;
	OutResult.ReadyIndex   = (uint32)ChosenIndex;
	OutResult.Sequence     = PublishSequence;
	OutResult.QpcRenderEnd = QpcRenderEnd;
	return true;
}

// ---------------------------------------------------------------------------

bool GpuShareGetAdapterLuid(ID3D11Device* Device, uint32& OutLuidLow, int32& OutLuidHigh)
{
	OutLuidLow = 0;
	OutLuidHigh = 0;
	if (Device == nullptr)
	{
		return false;
	}

	TRefCountPtr<IDXGIDevice> DxgiDevice;
	if (FAILED(Device->QueryInterface(__uuidof(IDXGIDevice), (void**)DxgiDevice.GetInitReference())))
	{
		return false;
	}

	TRefCountPtr<IDXGIAdapter> Adapter;
	if (FAILED(DxgiDevice->GetAdapter(Adapter.GetInitReference())))
	{
		return false;
	}

	DXGI_ADAPTER_DESC AdapterDesc = {};
	if (FAILED(Adapter->GetDesc(&AdapterDesc)))
	{
		return false;
	}

	OutLuidLow  = (uint32)AdapterDesc.AdapterLuid.LowPart;
	OutLuidHigh = (int32)AdapterDesc.AdapterLuid.HighPart;

	UE_LOG(LogGpuShare, Log, TEXT("Adapter D3D11 di Unreal: '%s' LUID=%u:%d"),
		AdapterDesc.Description, OutLuidLow, OutLuidHigh);
	return true;
}

#include "Windows/HideWindowsPlatformTypes.h"

#endif // PLATFORM_WINDOWS
