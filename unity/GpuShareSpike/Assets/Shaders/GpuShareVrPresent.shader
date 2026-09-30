// ============================================================================
//  GpuShare/VrPresent
//
//  Presentazione a schermo intero per VR (mono e stereo), con riproiezione
//  rotazionale. Vedi VrPresenter.cs per il perche' non e' un quad.
//
//  PER OGNI PIXEL DI OGNI OCCHIO:
//    1. direzione di vista nel mondo, ricostruita con le matrici che URP sta
//       usando ADESSO per QUESTO occhio (UNITY_MATRIX_I_VP e posizione camera,
//       entrambe per-occhio in Single Pass Instanced);
//    2. proiezione di quella direzione con la matrice con cui Unreal ha
//       renderizzato il frame mostrato (_RenderVP0 / _RenderVP1);
//    3. campionamento della texture di quell'occhio.
//
//  PERCHE' TUTTO PASSA PER UNA DIREZIONE E NON PER COORDINATE SCHERMO:
//  cosi' il risultato non dipende da nessuno dei ribaltamenti verticali che
//  D3D, i render target intermedi di URP e le texture esterne introducono in
//  punti diversi. La direzione di un pixel e' un fatto geometrico; URP ce la
//  da' giusta qualunque cosa faccia internamente con le coordinate.
//
//  REQUISITO: Single Pass Instanced (default di OpenXR in Unity). In Multi Pass
//  lo shader non sa quale occhio sta disegnando e mostrerebbe l'occhio
//  sinistro in entrambi. L'HUD lo segnala.
// ============================================================================

Shader "GpuShare/VrPresent"
{
    Properties
    {
        _EyeTex0 ("Occhio 0 (sinistro / mono)", 2D) = "black" {}
        _EyeTex1 ("Occhio 1 (destro)", 2D) = "black" {}
        _EyeDepth0 ("Depth occhio 0", 2D) = "black" {}
        _EyeDepth1 ("Depth occhio 1", 2D) = "black" {}
        _SrgbDecode ("Decodifica sRGB", Float) = 0
        _DebugMode ("0=colore 1=depth", Float) = 0
        _ShowEdges ("Evidenzia fuori frustum", Float) = 1
        _DepthRangeM ("Fondo scala depth (m)", Float) = 50
        _DepthScaleToMeters ("Scala depth -> metri", Float) = 0.01
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Background"
        }

        Pass
        {
            Name "GpuShareVrPresent"
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            // Necessario per Single Pass Instanced: una draw call, due istanze,
            // una per occhio.
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_EyeTex0);   SAMPLER(sampler_EyeTex0);
            TEXTURE2D(_EyeTex1);   SAMPLER(sampler_EyeTex1);
            TEXTURE2D(_EyeDepth0); SAMPLER(sampler_EyeDepth0);
            TEXTURE2D(_EyeDepth1); SAMPLER(sampler_EyeDepth1);

            // Fuori da UnityPerMaterial di proposito: le matrici non possono
            // stare nel blocco Properties, e questo shader disegna UNA mesh,
            // quindi la compatibilita' con l'SRP Batcher non conta.
            float4x4 _RenderVP0;
            float4x4 _RenderVP1;
            float _SrgbDecode;
            float _DebugMode;
            float _ShowEdges;
            float _DepthRangeM;
            float _DepthScaleToMeters;

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // I vertici sono gia' in clip space: copertura dell'intero
                // schermo dell'occhio. Se su qualche piattaforma risultasse
                // ribaltata non importa: ogni pixel si calcola la sua direzione
                // da solo nel fragment shader.
                output.positionCS = float4(input.positionOS.xy, UNITY_RAW_FAR_CLIP_VALUE, 1.0);
                return output;
            }

            uint CurrentEye()
            {
            #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED) || defined(UNITY_SINGLE_PASS_STEREO)
                return (uint)unity_StereoEyeIndex;
            #else
                return 0;
            #endif
            }

            // EOTF sRGB esatta (non la scorciatoia pow(c, 2.2)).
            float3 SrgbToLinear(float3 c)
            {
                float3 low  = c / 12.92;
                float3 high = pow(max((c + 0.055) / 1.055, 0.0), 2.4);
                return lerp(low, high, step(0.04045, c));
            }

            float4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // 1. Direzione di vista di questo pixel, nel mondo.
                //    Qualunque profondita' tra near e far va bene: serve solo un
                //    punto sul raggio. 0.5 sta comodamente in mezzo sia con lo
                //    Z invertito (D3D) sia senza.
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float3 pointWS = ComputeWorldSpacePosition(screenUV, 0.5, UNITY_MATRIX_I_VP);
                float3 dirWS = pointWS - GetCameraPositionWS();

                // 2. Proiezione con la camera con cui Unreal ha renderizzato.
                //    w = 0: la traslazione della view sparisce, resta la sola
                //    rotazione. E' questo che rende il calcolo una riproiezione
                //    ROTAZIONALE e che lo rende esatto quando la pose coincide.
                uint eye = CurrentEye();
                float4x4 renderVP = (eye == 0) ? _RenderVP0 : _RenderVP1;
                float4 clip = mul(renderVP, float4(dirWS, 0.0));

                // Dietro la camera di Unreal: nessun pixel valido.
                if (clip.w <= 1e-5)
                {
                    return float4(0.0, 0.0, 0.0, 1.0);
                }

                float2 ndc = clip.xy / clip.w;
                // NDC GL (y verso l'alto) -> UV della texture esterna.
                // La texture viene da D3D: la riga 0 in memoria e' l'ALTO
                // dell'immagine di Unreal, ed e' v = 0.
                float2 uv = float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);

                // Fuori dal frustum renderizzato da Unreal (la testa si e' girata
                // piu' del margine): lo mostriamo invece di stirare il bordo,
                // cosi' si vede quanto margine serve davvero.
                if (any(uv < 0.0) || any(uv > 1.0))
                {
                    return _ShowEdges > 0.5 ? float4(0.10, 0.02, 0.12, 1.0) : float4(0.0, 0.0, 0.0, 1.0);
                }

                // 3. Campionamento. LOD esplicito: dentro un ramo dinamico le
                //    derivate non sono affidabili.
                if (_DebugMode > 0.5)
                {
                    float depthUnreal = (eye == 0)
                        ? SAMPLE_TEXTURE2D_LOD(_EyeDepth0, sampler_EyeDepth0, uv, 0).r
                        : SAMPLE_TEXTURE2D_LOD(_EyeDepth1, sampler_EyeDepth1, uv, 0).r;
                    float normalized = saturate(depthUnreal * _DepthScaleToMeters / max(_DepthRangeM, 0.01));
                    return float4(normalized.xxx, 1.0);
                }

                float3 color = (eye == 0)
                    ? SAMPLE_TEXTURE2D_LOD(_EyeTex0, sampler_EyeTex0, uv, 0).rgb
                    : SAMPLE_TEXTURE2D_LOD(_EyeTex1, sampler_EyeTex1, uv, 0).rgb;

                if (_SrgbDecode > 0.5)
                {
                    color = SrgbToLinear(color);
                }
                return float4(color, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
