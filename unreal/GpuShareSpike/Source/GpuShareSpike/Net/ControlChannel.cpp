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

		// Magic giusto ma versione diversa = i due lati sono stati compilati da
		// commit diversi. E' un errore facilissimo da fare e il sintomo (Unity
		// che resta "in attesa dell'handshake") non lo spiegherebbe: lo diciamo.
		if (Header.magic == GPUSHARE_PROTOCOL_MAGIC && !bVersionMismatchLogged)
		{
			bVersionMismatchLogged = true;
			UE_LOG(LogGpuShare, Error,
				TEXT("Pacchetto con protocollo v%u, Unreal parla v%u: ricompila il lato rimasto indietro ")
				TEXT("(plugin nativo + script Unity, oppure il modulo Unreal)."),
				(uint32)Header.version, (uint32)GPUSHARE_PROTOCOL_VERSION);
		}
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
			PendingHello.ViewCount       = FMath::Clamp<uint32>(Hello.view_count, 1u, (uint32)GPUSHARE_MAX_VIEWS);
			PendingHello.ViewWidth       = Hello.view_width;
			PendingHello.ViewHeight      = Hello.view_height;
			PendingHello.RequestId       = Hello.request_id;
			bHelloPending = true;
		}

		bHasClient.store(true, std::memory_order_relaxed);

		UE_LOG(LogGpuShare, Log,
			TEXT("HELLO da Unity: pid=%u LUID=%u:%d flags=0x%X viste=%u risoluzione=%ux%u richiesta=%u"),
			Hello.unity_pid, Hello.adapter_luid_low, Hello.adapter_luid_high, Hello.flags,
			Hello.view_count, Hello.view_width, Hello.view_height, Hello.request_id);
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
		NewPose.FrameId   = Pose.frame_id;
		NewPose.QpcSend   = Pose.qpc;
		NewPose.ViewCount = (int32)FMath::Clamp<uint32>(Pose.view_count, 1u, (uint32)GPUSHARE_MAX_VIEWS);
		for (int32 ViewIndex = 0; ViewIndex < GPUSHARE_MAX_VIEWS; ++ViewIndex)
		{
			const GpuShareView& Src = Pose.views[ViewIndex];
			FGpuShareViewState& Dst = NewPose.Views[ViewIndex];
			Dst.UnityPosition = FVector(Src.position[0], Src.position[1], Src.position[2]);
			Dst.UnityRotation = FQuat(Src.rotation[0], Src.rotation[1], Src.rotation[2], Src.rotation[3]);

			// Tangenti degeneri (zero, o invertite) produrrebbero una matrice
			// singolare e una cattura nera senza errori: le rifiutiamo qui.
			if (Src.tan_right > Src.tan_left && Src.tan_up > Src.tan_down)
			{
				Dst.TanLeft  = Src.tan_left;
				Dst.TanRight = Src.tan_right;
				Dst.TanDown  = Src.tan_down;
				Dst.TanUp    = Src.tan_up;
			}
		}
		NewPose.NearM  = Pose.near_m;
		NewPose.FarM   = Pose.far_m;
		NewPose.bValid = true;

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
