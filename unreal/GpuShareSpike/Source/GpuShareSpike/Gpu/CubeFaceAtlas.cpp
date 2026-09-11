#include "Gpu/CubeFaceAtlas.h"

#if PLATFORM_WINDOWS

void GpuShareCopyCubeFacesToAtlas(
	ID3D11DeviceContext* Context,
	ID3D11Texture2D* CubeSource,
	ID3D11Texture2D* AtlasDest,
	uint32 FaceSize)
{
	if (!Context || !CubeSource || !AtlasDest || FaceSize == 0)
	{
		return;
	}

	for (uint32 Face = 0; Face < 6; ++Face)
	{
		const uint32 Col = Face % GpuShareCubeAtlasCols;
		const uint32 Row = Face / GpuShareCubeAtlasCols;

		// Indice di subresource di una faccia di TextureCube con 1 solo mip:
		//   D3D11CalcSubresource(MipSlice=0, ArraySlice=Face, MipLevels=1) == Face
		const UINT SourceSubresource = Face;

		Context->CopySubresourceRegion(
			AtlasDest,
			/*DstSubresource*/ 0,
			/*DstX*/ Col * FaceSize,
			/*DstY*/ Row * FaceSize,
			/*DstZ*/ 0,
			CubeSource,
			SourceSubresource,
			/*pSrcBox*/ nullptr);   // nullptr = copia l'intera subresource
	}
}

#endif // PLATFORM_WINDOWS
