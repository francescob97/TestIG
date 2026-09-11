// ============================================================================
//  Descrittori del modello a CANALI e GRUPPI.
//
//  CANALE = una superficie condivisa (colore, depth, atlas cubemap...).
//  GRUPPO = insieme di canali pubblicati ATOMICAMENTE.
//
//  Perche' il gruppo e non il canale e' l'unita' di pubblicazione:
//  COLOR e DEPTH devono provenire dallo STESSO frame. Se li pubblicassi in modo
//  indipendente, prima o poi Unity leggerebbe il colore del frame N col depth
//  del frame N-1 e il compositing sarebbe sbagliato in modo intermittente,
//  cioe' nel modo peggiore da diagnosticare. Pubblicandoli insieme il problema
//  non esiste per costruzione.
// ============================================================================

#pragma once

#include "CoreMinimal.h"
#include "ShareProtocol.h"   // da <repo>/shared, aggiunto agli include path dal Build.cs

/** Come il contenuto sorgente finisce dentro la superficie condivisa. */
enum class EGpuShareCopyMode : uint8
{
	/** CopyResource 1:1. Sorgente e destinazione devono avere stesse dimensioni e formato compatibile. */
	FullCopy,

	/**
	 * La sorgente e' una TextureCube (ArraySize = 6). Non e' condivisibile
	 * direttamente: le shared resource D3D11 richiedono ArraySize == 1.
	 * Copiamo le 6 facce in un atlas 2D 3x2 con 6 CopySubresourceRegion.
	 */
	CubeFacesToAtlas,
};

/** Descrizione statica di un canale, decisa all'avvio e mandata nell'handshake. */
struct FGpuShareChannelSetup
{
	uint32 ChannelId = GS_CH_COLOR;
	uint32 GroupId   = GS_GROUP_MAIN;

	uint32 Width  = 0;
	uint32 Height = 0;

	/** Valore numerico di DXGI_FORMAT (non includiamo dxgi.h in questo header). */
	uint32 DxgiFormat = 0;

	EGpuShareCopyMode CopyMode = EGpuShareCopyMode::FullCopy;

	/** Solo il canale COLOR porta gli 8 pixel di strumentazione. */
	bool bCarriesMarker = false;

	/** Usato solo con CubeFacesToAtlas. */
	uint32 CubeFaceSize = 0;
};
