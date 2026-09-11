// ============================================================================
//  Impacchettamento di una TextureCube in un atlas 2D.
//
//  IL VINCOLO D3D11 CHE RENDE QUESTO FILE NECESSARIO:
//  una shared resource D3D11 deve avere ArraySize == 1 e MipLevels == 1.
//  Una TextureCube ha ArraySize == 6. Quindi NON puoi condividere una cubemap:
//  CreateSharedHandle fallisce e basta.
//
//  Soluzione: copiamo le 6 facce in una singola texture 2D disposta 3x2.
//
//      +----+----+----+
//      | +X | -X | +Y |     riga 0 : facce 0,1,2
//      +----+----+----+
//      | -Y | +Z | -Z |     riga 1 : facce 3,4,5
//      +----+----+----+
//
//  L'ordine delle facce e' quello di D3D11 / DXGI (ed e' anche quello degli
//  indici di subresource di una TextureCube):
//      0 = +X   1 = -X   2 = +Y   3 = -Y   4 = +Z   5 = -Z
//  Lato Unity, UnityEngine.CubemapFace ha lo stesso ordine
//  (PositiveX=0 ... NegativeZ=5), quindi la ricomposizione e' 1:1.
//
//  Cosi' l'atlas viaggia sul percorso di trasporto GENERICO: stessa shared
//  texture, stesso keyed mutex, stesso doppio buffer degli altri canali.
// ============================================================================

#pragma once

#include "CoreMinimal.h"
#include "Gpu/WindowsD3D11Includes.h"

#if PLATFORM_WINDOWS

/** Griglia dell'atlas: 3 colonne x 2 righe = 6 facce. */
static constexpr uint32 GpuShareCubeAtlasCols = 3;
static constexpr uint32 GpuShareCubeAtlasRows = 2;

FORCEINLINE uint32 GpuShareCubeAtlasWidth(uint32 FaceSize)  { return FaceSize * GpuShareCubeAtlasCols; }
FORCEINLINE uint32 GpuShareCubeAtlasHeight(uint32 FaceSize) { return FaceSize * GpuShareCubeAtlasRows; }

/**
 * Copia le 6 facce di CubeSource dentro AtlasDest.
 * Da chiamare sul render thread, MENTRE si tiene il keyed mutex di AtlasDest.
 */
void GpuShareCopyCubeFacesToAtlas(
	ID3D11DeviceContext* Context,
	ID3D11Texture2D* CubeSource,
	ID3D11Texture2D* AtlasDest,
	uint32 FaceSize);

#endif // PLATFORM_WINDOWS
