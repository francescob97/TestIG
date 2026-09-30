// ============================================================================
//  Presentazione in VR.
//
//  PERCHE' NON UN QUAD COME IN DESKTOP:
//  un quad davanti alla camera funziona per UN occhio. In VR gli occhi sono
//  due, in posizioni diverse e con frustum asimmetrici diversi: un quad visto
//  dai due occhi insieme non puo' mostrare a ciascuno esattamente l'immagine
//  renderizzata per lui. Qualunque distanza scegli, uno dei due e' sbagliato.
//
//  COSA FA INVECE:
//  una mesh che copre tutto lo schermo, disegnata come sfondo. Per ogni pixel
//  di ogni occhio lo shader:
//    1. ricostruisce la DIREZIONE di vista di quel pixel nel mondo, con le
//       matrici che URP sta usando in quel momento per quell'occhio;
//    2. la proietta con la matrice con cui Unreal ha renderizzato quel frame;
//    3. campiona la texture di quell'occhio in quel punto.
//
//  Conseguenze, tutte volute:
//   - STEREO: se Unreal ha renderizzato dalla stessa pose, il risultato e'
//     pixel per pixel l'immagine di Unreal (la proiezione di una direzione da
//     una camera nella stessa posizione cade sullo stesso pixel).
//   - Se la testa si e' GIRATA nel frattempo, lo stesso calcolo diventa una
//     RIPROIEZIONE ROTAZIONALE (come l'ATW dei runtime VR): l'immagine resta
//     ferma nel mondo invece di girare con la testa. La latenza di rotazione
//     percepita scende a quella di Unity. La traslazione non viene corretta.
//   - MONO: un'unica immagine renderizzata dal centro della testa, proiettata
//     in entrambi gli occhi come se stesse all'infinito. Nessuna profondita'
//     stereo (per definizione), ma allineamento corretto in entrambi gli occhi.
//
//  Il parametro di riproiezione "Off" usa invece la pose del frame corrente:
//  l'immagine segue la testa rigidamente. Serve per vedere, a confronto, quanto
//  la riproiezione nasconde della latenza.
// ============================================================================

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GpuShareSpike
{
    public sealed class VrPresenter : IDisposable
    {
        private GameObject _object;
        private Mesh _mesh;
        private Material _material;

        private static readonly int EyeTex0Id     = Shader.PropertyToID("_EyeTex0");
        private static readonly int EyeTex1Id     = Shader.PropertyToID("_EyeTex1");
        private static readonly int EyeDepth0Id   = Shader.PropertyToID("_EyeDepth0");
        private static readonly int EyeDepth1Id   = Shader.PropertyToID("_EyeDepth1");
        private static readonly int RenderVP0Id   = Shader.PropertyToID("_RenderVP0");
        private static readonly int RenderVP1Id   = Shader.PropertyToID("_RenderVP1");
        private static readonly int SrgbId        = Shader.PropertyToID("_SrgbDecode");
        private static readonly int DebugId       = Shader.PropertyToID("_DebugMode");
        private static readonly int ShowEdgesId   = Shader.PropertyToID("_ShowEdges");
        private static readonly int DepthRangeId  = Shader.PropertyToID("_DepthRangeM");
        private static readonly int DepthScaleId  = Shader.PropertyToID("_DepthScaleToMeters");

        public bool IsReady => _material != null;

        public bool Create(Camera camera, Shader shader, out string error)
        {
            error = null;
            Dispose();

            if (camera == null) { error = "camera nulla"; return false; }
            if (shader == null)
            {
                error = "Shader 'GpuShare/VrPresent' non trovato: assegnalo a ShareClient.VrPresentShader.";
                return false;
            }
            if (!shader.isSupported)
            {
                error = "Shader 'GpuShare/VrPresent' non supportato su questa piattaforma (errori di compilazione?).";
                return false;
            }

            // Quattro vertici gia' in CLIP SPACE: il vertex shader li passa
            // cosi' come sono, ignorando le matrici. Coprono lo schermo di ogni
            // occhio indipendentemente da dove sia la camera.
            _mesh = new Mesh { name = "GpuShareVrFullscreen" };
            _mesh.vertices = new[]
            {
                new Vector3(-1.0f, -1.0f, 0.0f),
                new Vector3( 1.0f, -1.0f, 0.0f),
                new Vector3( 1.0f,  1.0f, 0.0f),
                new Vector3(-1.0f,  1.0f, 0.0f),
            };
            _mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            // Bounds enormi: la mesh non deve MAI essere scartata dal frustum
            // culling, che ragionerebbe sulle posizioni dei vertici come se
            // fossero coordinate di oggetto (non lo sono).
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1.0e5f);

            _material = new Material(shader) { name = "GpuShareVrPresent" };
            // Coda Background (1000): disegnata prima di tutto il resto, cosi'
            // la geometria di Unity (mani, controller, UI) finisce sopra.
            _material.renderQueue = (int)RenderQueue.Background;

            _object = new GameObject("GpuShareVrPresenter");
            _object.transform.SetParent(camera.transform, false);
            _object.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var renderer = _object.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            return true;
        }

        /// <summary>In mono si passa la stessa texture per entrambi gli occhi.</summary>
        public void SetTextures(Texture eye0, Texture eye1, Texture depth0, Texture depth1)
        {
            if (_material == null) return;
            _material.SetTexture(EyeTex0Id, eye0);
            _material.SetTexture(EyeTex1Id, eye1 != null ? eye1 : eye0);
            if (depth0 != null) _material.SetTexture(EyeDepth0Id, depth0);
            if (depth1 != null || depth0 != null) _material.SetTexture(EyeDepth1Id, depth1 != null ? depth1 : depth0);
        }

        /// <summary>
        /// Matrici mondo -> clip (convenzione GL di Unity) con cui Unreal ha
        /// renderizzato ciascun occhio del frame che si sta mostrando.
        /// </summary>
        public void SetRenderMatrices(Matrix4x4 viewProjection0, Matrix4x4 viewProjection1)
        {
            if (_material == null) return;
            _material.SetMatrix(RenderVP0Id, viewProjection0);
            _material.SetMatrix(RenderVP1Id, viewProjection1);
        }

        public void SetOptions(bool srgbDecode, int debugMode, bool showEdges, float depthRangeM, float depthScaleToMeters)
        {
            if (_material == null) return;
            _material.SetFloat(SrgbId, srgbDecode ? 1.0f : 0.0f);
            _material.SetFloat(DebugId, debugMode);
            _material.SetFloat(ShowEdgesId, showEdges ? 1.0f : 0.0f);
            _material.SetFloat(DepthRangeId, Mathf.Max(0.01f, depthRangeM));
            _material.SetFloat(DepthScaleId, depthScaleToMeters > 0.0f ? depthScaleToMeters : 0.01f);
        }

        public void Dispose()
        {
            if (_object != null) UnityEngine.Object.Destroy(_object);
            if (_material != null) UnityEngine.Object.Destroy(_material);
            if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
            _object = null;
            _material = null;
            _mesh = null;
        }
    }
}
