// ============================================================================
//  FGpuShareControlChannel  --  canale di controllo UDP su localhost.
//
//  Gira su un thread proprio (FRunnable, il meccanismo standard di Unreal per
//  i thread lunghi) perche' una recv che blocca sul game thread bloccherebbe
//  il motore.
//
//  POLITICA "LATEST-WINS" (richiesta esplicita del design):
//  le pose NON vengono accodate. Esiste UN SOLO slot, che il thread di rete
//  sovrascrive a ogni pacchetto. Il game thread legge sempre l'ultima.
//  Una coda produrrebbe l'effetto opposto a quello voluto: se Unity manda piu'
//  velocemente di quanto Unreal renderizza, la coda cresce e la latenza
//  aumenta senza limite. Sovrascrivendo, la latenza resta limitata.
//
//  SINCRONIZZAZIONE: una semplice FCriticalSection.
//  Un seqlock sarebbe piu' "giusto" in teoria, ma qui abbiamo un solo scrittore
//  a ~120 Hz e un solo lettore a frame rate: la contesa e' inesistente e la
//  sezione critica e' lunga ~60 byte di memcpy. Codice ovviamente corretto
//  batte codice furbo, soprattutto in uno spike che deve MISURARE cose.
// ============================================================================

#pragma once

#include "CoreMinimal.h"
#include "HAL/Runnable.h"
#include "HAL/CriticalSection.h"
#include "ShareProtocol.h"
#include <atomic>

class FSocket;
class FInternetAddr;
class FRunnableThread;

/** Pose ricevuta da Unity, ancora in SPAZIO UNITY. */
struct FGpuSharePoseState
{
	uint64  FrameId = 0;
	int64   QpcSend = 0;

	// Convenzione Unity: metri, Y-up, Z-forward, left-handed.
	FVector UnityPosition = FVector::ZeroVector;
	FQuat   UnityRotation = FQuat::Identity;

	float FovYDeg = 60.0f;
	float Aspect  = 16.0f / 9.0f;
	float NearM   = 0.1f;
	float FarM    = 0.0f;

	bool bValid = false;

	/**
	 * CONVERSIONE DI COORDINATE - l'unico posto del progetto in cui avviene.
	 *
	 *   Unity : X destra,  Y alto,   Z avanti,  METRI
	 *   Unreal: X avanti,  Y destra, Z alto,    CENTIMETRI
	 *
	 * Quindi per un vettore:  UE = (U.z, U.x, U.y) * 100
	 *
	 * Per il quaternione: la mappa tra le due basi e' una permutazione ciclica
	 * degli assi, cioe' una rotazione propria (determinante +1), e entrambi i
	 * sistemi sono left-handed. Un cambio di base per rotazione propria
	 * trasforma l'ASSE del quaternione con la stessa permutazione e lascia
	 * invariato l'angolo, quindi la parte scalare w non cambia:
	 *     UE.q = (U.q.z, U.q.x, U.q.y, U.q.w)
	 */
	FTransform ToUnrealTransform() const
	{
		const FVector PositionCm(
			UnityPosition.Z * 100.0f,
			UnityPosition.X * 100.0f,
			UnityPosition.Y * 100.0f);

		const FQuat Rotation(
			UnityRotation.Z,
			UnityRotation.X,
			UnityRotation.Y,
			UnityRotation.W);

		return FTransform(Rotation.GetNormalized(), PositionCm, FVector::OneVector);
	}
};

/** Informazioni arrivate col pacchetto HELLO di Unity. */
struct FGpuShareClientInfo
{
	uint32 Pid = 0;
	uint32 AdapterLuidLow = 0;
	int32  AdapterLuidHigh = 0;
	uint32 Flags = 0;
};

class FGpuShareControlChannel : public FRunnable
{
public:
	FGpuShareControlChannel() = default;
	virtual ~FGpuShareControlChannel();

	/** Apre il socket e avvia il thread. */
	bool Start(int32 ListenPort, FString& OutError);
	void Shutdown();

	// --- FRunnable ---------------------------------------------------------
	virtual bool Init() override { return true; }
	virtual uint32 Run() override;
	virtual void Stop() override;
	virtual void Exit() override {}

	// --- chiamabili dal GAME THREAD ----------------------------------------

	/**
	 * Se e' arrivato un nuovo HELLO lo restituisce e azzera il flag.
	 * Il game thread reagisce (ri)creando le superfici e duplicando gli handle.
	 */
	bool ConsumePendingHello(FGpuShareClientInfo& OutInfo);

	/** Copia l'ultima pose ricevuta. Ritorna false se non ne e' mai arrivata una. */
	bool GetLatestPose(FGpuSharePoseState& OutPose) const;

	bool HasClient() const { return bHasClient.load(std::memory_order_relaxed); }

	/** Accoda l'handshake da spedire (il thread di rete lo manda entro ~1 ms). */
	void QueueHandshake(const GpuShareHandshake& Packet);

	// --- chiamabile dal RENDER THREAD --------------------------------------

	/** Accoda un pacchetto STATUS. Costo: una memcpy da 256 byte sotto lock. */
	void QueueStatus(const GpuShareStatus& Packet);

	// --- statistiche -------------------------------------------------------
	uint64 GetPosePacketCount() const { return PosePacketCount.load(std::memory_order_relaxed); }
	uint64 GetBadPacketCount() const  { return BadPacketCount.load(std::memory_order_relaxed); }

private:
	void HandlePacket(const uint8* Data, int32 Size, const TSharedRef<FInternetAddr>& Sender);
	void FlushOutgoing();

	FSocket* Socket = nullptr;
	FRunnableThread* Thread = nullptr;
	std::atomic<bool> bStopRequested{ false };
	std::atomic<bool> bHasClient{ false };

	/** Indirizzo da cui e' arrivato l'ultimo HELLO: e' li' che rispondiamo. */
	mutable FCriticalSection ClientAddrLock;
	TSharedPtr<FInternetAddr> ClientAddr;

	mutable FCriticalSection PoseLock;
	FGpuSharePoseState LatestPose;

	mutable FCriticalSection HelloLock;
	FGpuShareClientInfo PendingHello;
	bool bHelloPending = false;

	mutable FCriticalSection OutgoingLock;
	GpuShareHandshake PendingHandshake = {};
	bool bHandshakePending = false;
	GpuShareStatus PendingStatus = {};
	bool bStatusPending = false;

	std::atomic<uint64> PosePacketCount{ 0 };
	std::atomic<uint64> BadPacketCount{ 0 };
};
