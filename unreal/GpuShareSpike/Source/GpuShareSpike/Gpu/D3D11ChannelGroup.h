// ============================================================================
//  FGpuShareD3D11ChannelGroup
//
//  Possiede le superfici D3D11 condivise di UN gruppo e ne gestisce la
//  pubblicazione atomica.
//
//  MODELLO DI THREADING (leggere prima di toccare questo file):
//  TUTTI i metodi, Initialize compreso, vanno chiamati dal RENDER THREAD di
//  Unreal. Motivo: l'ID3D11DeviceContext "immediate" NON e' thread-safe, ed e'
//  lo stesso context che usa il renderer di Unreal. Usarlo dal game thread
//  produce corruzione casuale o crash dentro CopyResource, tipicamente non
//  subito e non in modo riproducibile.
//  (Le CreateXxx di ID3D11Device sarebbero free-threaded, ma teniamo tutto sul
//  render thread per non dover ragionare caso per caso.)
//
//  PROTOCOLLO DEL KEYED MUTEX:
//    produttore : AcquireSync(KEY_PRODUCER) -> scrive -> ReleaseSync(KEY_CONSUMER)
//    consumatore: AcquireSync(KEY_CONSUMER) -> copia  -> ReleaseSync(KEY_PRODUCER)
//  Un keyed mutex appena creato e' "rilasciato con chiave 0", quindi la prima
//  AcquireSync(KEY_PRODUCER) riesce sempre.
//
//  Nota importante: il consumatore deve COPIARE il contenuto e rilasciare
//  subito, non tenere il mutex per tutta la durata del proprio frame. Tenerlo
//  bloccherebbe il produttore e basta un frame perso perche' i due processi si
//  incastrino a vicenda.
// ============================================================================

#pragma once

#include "CoreMinimal.h"
#include "Gpu/ShareChannel.h"
#include "Gpu/WindowsD3D11Includes.h"

#if PLATFORM_WINDOWS

/** Dati che il render thread passa a una pubblicazione. */
struct FGpuSharePublishInput
{
	/** Una texture sorgente per canale, NELLO STESSO ORDINE dei setup dati a Initialize. */
	TArray<ID3D11Texture2D*> Sources;

	/** frame_id della pose effettivamente applicata a questo frame. */
	uint64 FrameId = 0;

	/** Eco del QPC che Unity aveva messo nel pacchetto POSE. E' la base della misura. */
	int64 QpcPoseSend = 0;
};

struct FGpuSharePublishResult
{
	bool   bPublished   = false;   // false = frame saltato (mutex occupato)
	uint32 ReadyIndex   = 0;
	uint32 Sequence     = 0;
	int64  QpcRenderEnd = 0;
};

class FGpuShareD3D11ChannelGroup
{
public:
	FGpuShareD3D11ChannelGroup() = default;
	~FGpuShareD3D11ChannelGroup();

	FGpuShareD3D11ChannelGroup(const FGpuShareD3D11ChannelGroup&) = delete;
	FGpuShareD3D11ChannelGroup& operator=(const FGpuShareD3D11ChannelGroup&) = delete;

	/**
	 * Crea le superfici condivise (2 per canale) e i relativi keyed mutex.
	 * @param bNamedHandles  true = handle NOMINATI (fallback), false = DuplicateHandle
	 * @param NamePrefix     usato solo se bNamedHandles
	 */
	bool Initialize(
		ID3D11Device* Device,
		uint32 InGroupId,
		TArrayView<const FGpuShareChannelSetup> InChannels,
		bool bNamedHandles,
		const FString& NamePrefix,
		FString& OutError);

	void Shutdown();

	bool IsInitialized() const { return bInitialized; }
	uint32 GetGroupId() const { return GroupId; }
	int32 GetChannelCount() const { return Channels.Num(); }

	/**
	 * Duplica tutti gli NT handle nello spazio del processo Unity.
	 * Da richiamare a ogni nuovo HELLO: se Unity riparte cambia PID e i vecchi
	 * handle non valgono piu'.
	 */
	bool DuplicateHandlesInto(uint32 UnityPid, FString& OutError);

	/**
	 * Chiude gli handle precedentemente duplicati DENTRO il processo Unity
	 * (trucco: DuplicateHandle con DUPLICATE_CLOSE_SOURCE). Serve solo se lo
	 * stesso processo Unity rifa' l'handshake: altrimenti gli handle sono gia'
	 * morti col processo.
	 */
	void ReleaseRemoteHandles();

	/** Riempie il descrittore da spedire nell'handshake. */
	void FillChannelDesc(int32 ChannelIndex, GpuShareChannelDesc& OutDesc) const;

	/**
	 * Copia le sorgenti nel buffer libero, scrive il marker, rende il buffer
	 * visibile al consumatore.
	 * Ritorna false (senza bloccare) se nessuno dei due buffer e' acquisibile
	 * entro TimeoutMs: in quel caso il frame e' semplicemente saltato.
	 */
	bool Publish(const FGpuSharePublishInput& Input, int32 TimeoutMs, FGpuSharePublishResult& OutResult);

	/** Quante volte abbiamo saltato una pubblicazione perche' il consumatore era indietro. */
	uint64 GetSkippedFrameCount() const { return SkippedFrames; }

private:
	struct FSurface
	{
		TRefCountPtr<ID3D11Texture2D> Texture;
		TRefCountPtr<IDXGIKeyedMutex> KeyedMutex;

		/** NT handle valido NEL NOSTRO processo. */
		HANDLE LocalHandle = nullptr;

		/** Valore dell'handle valido nel processo UNITY (dopo DuplicateHandle). */
		uint64 RemoteHandle = 0;

		/** Nome dell'oggetto, solo in modalita' handle nominati. */
		FString Name;
	};

	struct FChannel
	{
		FGpuShareChannelSetup Setup;
		FSurface Buffers[GPUSHARE_BUFFERS_PER_CHANNEL];
	};

	bool CreateSurface(FChannel& Channel, int32 BufferIndex, FString& OutError);
	bool CreateMarkerStaging(FString& OutError);

	/** Acquisisce il buffer BufferIndex su TUTTI i canali, con rollback se uno fallisce. */
	bool TryAcquireAll(int32 BufferIndex, uint64 Key, int32 TimeoutMs);
	void ReleaseAll(int32 BufferIndex, uint64 Key);

	void WriteMarker(ID3D11Texture2D* Destination, uint64 FrameId, int64 QpcPoseSend, int64 QpcRenderEnd);

	TRefCountPtr<ID3D11Device>        D3DDevice;
	TRefCountPtr<ID3D11DeviceContext> ImmediateContext;

	/** Texture DYNAMIC 8x1 usata come sorgente per il marker. */
	TRefCountPtr<ID3D11Texture2D> MarkerStaging;

	TArray<FChannel> Channels;

	uint32 GroupId = GS_GROUP_MAIN;
	bool   bInitialized = false;
	bool   bUseNamedHandles = false;

	int32  LastPublishedIndex = GPUSHARE_BUFFERS_PER_CHANNEL - 1;
	uint32 PublishSequence = 0;
	uint64 SkippedFrames = 0;

	uint32 RemotePid = 0;
};

/** Legge il LUID dell'adapter su cui gira il device. Serve a diagnosticare il mismatch di GPU. */
bool GpuShareGetAdapterLuid(ID3D11Device* Device, uint32& OutLuidLow, int32& OutLuidHigh);

#endif // PLATFORM_WINDOWS
