#include "Net/ControlChannel.h"

#include "GpuShareLog.h"
#include "HAL/RunnableThread.h"
#include "Sockets.h"
#include "SocketSubsystem.h"
#include "IPAddress.h"
#include "Common/UdpSocketBuilder.h"
#include "Interfaces/IPv4/IPv4Endpoint.h"

FGpuShareControlChannel::~FGpuShareControlChannel()
{
	Shutdown();
}

bool FGpuShareControlChannel::Start(int32 ListenPort, FString& OutError)
{
	Shutdown();

	// FUdpSocketBuilder e' l'helper del modulo Networking: incapsula la
	// creazione del socket UDP e le opzioni. AsNonBlocking + Wait() esplicito
	// ci fa controllare il tempo di attesa senza mai bloccare per sempre.
	const FIPv4Endpoint Endpoint(FIPv4Address::Any, (uint16)ListenPort);

	Socket = FUdpSocketBuilder(TEXT("GpuShareControlChannel"))
		.AsNonBlocking()
		.AsReusable()
		.BoundToEndpoint(Endpoint)
		.WithReceiveBufferSize(1 * 1024 * 1024)
		.WithSendBufferSize(1 * 1024 * 1024)
		.Build();

	if (Socket == nullptr)
	{
		OutError = FString::Printf(TEXT("Impossibile aprire il socket UDP sulla porta %d (gia' occupata?)"), ListenPort);
		return false;
	}

	bStopRequested.store(false, std::memory_order_relaxed);

	// TPri_AboveNormal: il canale di controllo e' a bassissimo carico ma deve
	// reagire in fretta, altrimenti aggiunge latenza proprio a cio' che misura.
	Thread = FRunnableThread::Create(this, TEXT("GpuShareControlChannel"), 128 * 1024, TPri_AboveNormal);
	if (Thread == nullptr)
	{
		OutError = TEXT("FRunnableThread::Create fallita");
		Shutdown();
		return false;
	}

	UE_LOG(LogGpuShare, Log, TEXT("Canale di controllo in ascolto su UDP 0.0.0.0:%d"), ListenPort);
	return true;
}

void FGpuShareControlChannel::Stop()
{
	bStopRequested.store(true, std::memory_order_relaxed);
}

void FGpuShareControlChannel::Shutdown()
{
	bStopRequested.store(true, std::memory_order_relaxed);

	if (Thread != nullptr)
	{
		Thread->WaitForCompletion();
		delete Thread;
		Thread = nullptr;
	}

	if (Socket != nullptr)
	{
		if (ISocketSubsystem* SocketSubsystem = ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM))
		{
			Socket->Close();
			SocketSubsystem->DestroySocket(Socket);
		}
		Socket = nullptr;
	}

	bHasClient.store(false, std::memory_order_relaxed);
}

uint32 FGpuShareControlChannel::Run()
{
	ISocketSubsystem* SocketSubsystem = ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM);
	if (SocketSubsystem == nullptr)
	{
		UE_LOG(LogGpuShare, Error, TEXT("ISocketSubsystem non disponibile"));
		return 1;
	}

	TSharedRef<FInternetAddr> Sender = SocketSubsystem->CreateInternetAddr();

	// Il pacchetto piu' grande del protocollo e' 256 byte (handshake/status).
	uint8 Buffer[1024];

	while (!bStopRequested.load(std::memory_order_relaxed))
	{
		// Attesa di 1 ms: fa anche da cadenza del loop, quindi controlliamo la
		// coda di invio a 1 kHz. La latenza aggiunta dal canale di controllo
		// resta cosi' sotto il millisecondo, trascurabile rispetto a un frame.
		Socket->Wait(ESocketWaitConditions::WaitForRead, FTimespan::FromMilliseconds(1.0));

		int32 BytesRead = 0;
		while (Socket->RecvFrom(Buffer, sizeof(Buffer), BytesRead, *Sender))
		{
			if (BytesRead <= 0)
			{
				break;
			}
			HandlePacket(Buffer, BytesRead, Sender);
		}

		FlushOutgoing();
	}

	return 0;
}

void FGpuShareControlChannel::HandlePacket(const uint8* Data, int32 Size, const TSharedRef<FInternetAddr>& Sender)
{
	if (Size < (int32)sizeof(GpuShareHeader))
	{
		BadPacketCount.fetch_add(1, std::memory_order_relaxed);
		return;
	}

	GpuShareHeader Header;
	FMemory::Memcpy(&Header, Data, sizeof(Header));

	if (!GpuShareHeaderIsValid(&Header))
	{
		BadPacketCount.fetch_add(1, std::memory_order_relaxed);
		return;
	}

	switch (Header.type)
	{
	case GS_PKT_HELLO:
	{
		if (Size < (int32)sizeof(GpuShareHello))
		{
			BadPacketCount.fetch_add(1, std::memory_order_relaxed);
			return;
		}

		GpuShareHello Hello;
		FMemory::Memcpy(&Hello, Data, sizeof(Hello));

		{
			// Rispondiamo all'indirizzo/porta da cui e' arrivato l'HELLO: cosi'
			// Unity non deve prenotarsi una porta fissa.
			FScopeLock Lock(&ClientAddrLock);
			ClientAddr = Sender->Clone();
		}
		{
			FScopeLock Lock(&HelloLock);
			PendingHello.Pid             = Hello.unity_pid;
			PendingHello.AdapterLuidLow  = Hello.adapter_luid_low;
			PendingHello.AdapterLuidHigh = Hello.adapter_luid_high;
			PendingHello.Flags           = Hello.flags;
			bHelloPending = true;
		}

		bHasClient.store(true, std::memory_order_relaxed);

		UE_LOG(LogGpuShare, Log,
			TEXT("HELLO da Unity: pid=%u LUID=%u:%d flags=0x%X"),
			Hello.unity_pid, Hello.adapter_luid_low, Hello.adapter_luid_high, Hello.flags);
		break;
	}

	case GS_PKT_POSE:
	{
		if (Size < (int32)sizeof(GpuSharePose))
		{
			BadPacketCount.fetch_add(1, std::memory_order_relaxed);
			return;
		}

		GpuSharePose Pose;
		FMemory::Memcpy(&Pose, Data, sizeof(Pose));

		FGpuSharePoseState NewPose;
		NewPose.FrameId       = Pose.frame_id;
		NewPose.QpcSend       = Pose.qpc;
		NewPose.UnityPosition = FVector(Pose.position[0], Pose.position[1], Pose.position[2]);
		NewPose.UnityRotation = FQuat(Pose.rotation[0], Pose.rotation[1], Pose.rotation[2], Pose.rotation[3]);
		NewPose.FovYDeg       = Pose.fov_y_deg;
		NewPose.Aspect        = Pose.aspect;
		NewPose.NearM         = Pose.near_m;
		NewPose.FarM          = Pose.far_m;
		NewPose.bValid        = true;

		{
			// LATEST-WINS: sovrascrittura pura, nessuna coda.
			FScopeLock Lock(&PoseLock);
			LatestPose = NewPose;
		}

		PosePacketCount.fetch_add(1, std::memory_order_relaxed);
		break;
	}

	case GS_PKT_BYE:
	{
		UE_LOG(LogGpuShare, Log, TEXT("BYE da Unity"));
		bHasClient.store(false, std::memory_order_relaxed);
		break;
	}

	default:
		BadPacketCount.fetch_add(1, std::memory_order_relaxed);
		break;
	}
}

void FGpuShareControlChannel::FlushOutgoing()
{
	TSharedPtr<FInternetAddr> Destination;
	{
		FScopeLock Lock(&ClientAddrLock);
		Destination = ClientAddr;
	}

	if (!Destination.IsValid() || Socket == nullptr)
	{
		return;
	}

	GpuShareHandshake HandshakeCopy;
	GpuShareStatus StatusCopy;
	bool bSendHandshake = false;
	bool bSendStatus = false;

	{
		FScopeLock Lock(&OutgoingLock);
		if (bHandshakePending)
		{
			HandshakeCopy = PendingHandshake;
			bHandshakePending = false;
			bSendHandshake = true;
		}
		if (bStatusPending)
		{
			StatusCopy = PendingStatus;
			bStatusPending = false;
			bSendStatus = true;
		}
	}

	int32 BytesSent = 0;
	if (bSendHandshake)
	{
		Socket->SendTo((const uint8*)&HandshakeCopy, sizeof(HandshakeCopy), BytesSent, *Destination);
		UE_LOG(LogGpuShare, Log, TEXT("HANDSHAKE spedito (%d byte, %u canali)"),
			BytesSent, HandshakeCopy.channel_count);
	}
	if (bSendStatus)
	{
		Socket->SendTo((const uint8*)&StatusCopy, sizeof(StatusCopy), BytesSent, *Destination);
	}
}

bool FGpuShareControlChannel::ConsumePendingHello(FGpuShareClientInfo& OutInfo)
{
	FScopeLock Lock(&HelloLock);
	if (!bHelloPending)
	{
		return false;
	}
	OutInfo = PendingHello;
	bHelloPending = false;
	return true;
}

bool FGpuShareControlChannel::GetLatestPose(FGpuSharePoseState& OutPose) const
{
	FScopeLock Lock(&PoseLock);
	if (!LatestPose.bValid)
	{
		return false;
	}
	OutPose = LatestPose;
	return true;
}

void FGpuShareControlChannel::QueueHandshake(const GpuShareHandshake& Packet)
{
	FScopeLock Lock(&OutgoingLock);
	PendingHandshake = Packet;
	bHandshakePending = true;
}

void FGpuShareControlChannel::QueueStatus(const GpuShareStatus& Packet)
{
	// Chiamata dal RENDER THREAD. Deve restare economica: una memcpy da 256 byte
	// sotto un lock non conteso. Mandare direttamente l'UDP da qui sarebbe una
	// syscall sul render thread, ed e' esattamente cio' che non vogliamo.
	FScopeLock Lock(&OutgoingLock);
	PendingStatus = Packet;
	bStatusPending = true;
}
