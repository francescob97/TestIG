// ============================================================================
//  Ricompone una Cubemap Unity dall'atlas 3x2 che arriva da Unreal.
//
//  PERCHE' L'ATLAS ESISTE:
//  le shared resource D3D11 richiedono ArraySize == 1; una TextureCube ne ha 6.
//  Quindi la cubemap non e' condivisibile direttamente e Unreal impacchetta le
//  facce in una texture 2D. Qui facciamo il percorso inverso.
//
//  Tutto avviene sulla GPU con Graphics.CopyTexture: nessun readback, nessuna
//  allocazione per frame. Sei copie di FaceSize x FaceSize.
//
//  Layout dell'atlas (identico a Gpu/CubeFaceAtlas.h lato Unreal):
//      +----+----+----+
//      | +X | -X | +Y |
//      +----+----+----+
//      | -Y | +Z | -Z |
//      +----+----+----+
//  L'ordine 0..5 e' quello di D3D11 ed e' anche quello di UnityEngine.CubemapFace
//  (PositiveX=0 ... NegativeZ=5), quindi la corrispondenza e' 1:1.
// ============================================================================

using System;
using UnityEngine;

namespace GpuShareSpike
{
    public sealed class CubemapAssembler : IDisposable
    {
        public const int AtlasCols = 3;
        public const int AtlasRows = 2;

        public Cubemap Cubemap { get; private set; }
        public bool ApplyAsReflectionProbe = true;
        public string LastError { get; private set; }

        private int _faceSize;

        /// <summary>Da chiamare ogni frame dopo il consume del gruppo CUBE.</summary>
        public void Update(Texture2D atlas)
        {
            if (atlas == null) return;

            int faceSize = atlas.width / AtlasCols;
            if (faceSize <= 0 || atlas.height / AtlasRows != faceSize)
            {
                LastError = $"atlas {atlas.width}x{atlas.height} non compatibile con una griglia {AtlasCols}x{AtlasRows}";
                return;
            }

            if (Cubemap == null || _faceSize != faceSize)
            {
                if (Cubemap != null) UnityEngine.Object.Destroy(Cubemap);
                Cubemap = new Cubemap(faceSize, TextureFormat.BGRA32, /*mipChain*/ false);
                _faceSize = faceSize;
            }

            // CopyTexture richiede il supporto della piattaforma: su D3D11 c'e'
            // sempre, ma un controllo esplicito evita un fallimento muto.
            if ((SystemInfo.copyTextureSupport & UnityEngine.Rendering.CopyTextureSupport.DifferentTypes) == 0)
            {
                LastError = "Graphics.CopyTexture non supporta la copia tra tipi diversi su questo device";
                return;
            }

            for (int face = 0; face < 6; ++face)
            {
                int col = face % AtlasCols;
                int row = face / AtlasCols;

                Graphics.CopyTexture(
                    atlas, /*srcElement*/ 0, /*srcMip*/ 0,
                    col * faceSize, row * faceSize, faceSize, faceSize,
                    Cubemap, /*dstElement (faccia)*/ face, /*dstMip*/ 0, 0, 0);
            }

            LastError = null;

            if (ApplyAsReflectionProbe)
            {
                // Ecco a cosa serve davvero il canale CUBE: gli oggetti disegnati
                // da Unity vengono illuminati e riflessi dal mondo di Unreal,
                // invece di sembrarci incollati sopra.
                RenderSettings.defaultReflectionMode =
                    UnityEngine.Rendering.DefaultReflectionMode.Custom;
                RenderSettings.customReflectionTexture = Cubemap;
            }
        }

        public void Dispose()
        {
            if (Cubemap != null)
            {
                UnityEngine.Object.Destroy(Cubemap);
                Cubemap = null;
            }
        }
    }
}
