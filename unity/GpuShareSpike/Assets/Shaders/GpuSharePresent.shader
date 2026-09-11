// ============================================================================
//  GpuShare/Present
//
//  Unlit: l'immagine e' gia' illuminata e post-processata da Unreal, qui la si
//  mostra e basta. Qualunque cosa in piu' falserebbe cio' che stiamo guardando.
//
//  GESTIONE DEL COLORE, spiegata perche' e' il punto in cui ci si perde:
//  la texture esterna e' creata con linear:true, quindi Unity NON applica la
//  conversione sRGB al campionamento e allo shader arrivano i valori GREZZI.
//  Ma il canale COLOR contiene il final color LDR di Unreal, che E' codificato
//  sRGB. In un progetto Linear color space bisogna quindi decodificarlo a mano:
//  e' cio' che fa _SrgbDecode.
//  Farlo qui, con un interruttore, invece di affidarsi al flag di
//  CreateExternalTexture, significa poter verificare a occhio quale delle due
//  interpretazioni e' giusta, invece di indovinare.
//
//  NOTA: questo non tocca in alcun modo la strumentazione. Gli 8 pixel del
//  marker vengono letti in CPU dal plugin nativo, come byte grezzi, senza mai
//  passare da un sampler.
// ============================================================================

Shader "GpuShare/Present"
{
    Properties
    {
        _MainTex ("Canale COLOR", 2D) = "black" {}
        _DepthTex ("Canale DEPTH", 2D) = "black" {}
        _SrgbDecode ("Decodifica sRGB", Float) = 1
        _DebugMode ("0=colore 1=depth 2=marker", Float) = 0
        _DepthRangeM ("Fondo scala depth (m)", Float) = 50
        _DepthScaleToMeters ("Scala depth -> metri", Float) = 0.01
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "GpuSharePresent"
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_DepthTex);
            SAMPLER(sampler_DepthTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _MainTex_TexelSize;
                float  _SrgbDecode;
                float  _DebugMode;
                float  _DepthRangeM;
                float  _DepthScaleToMeters;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            // EOTF sRGB esatta (non la scorciatoia pow(c, 2.2)): il segmento
            // lineare sotto 0.04045 conta sui toni scuri.
            float3 SrgbToLinear(float3 c)
            {
                float3 low  = c / 12.92;
                float3 high = pow(max((c + 0.055) / 1.055, 0.0), 2.4);
                return lerp(low, high, step(0.04045, c));
            }

            float4 Fragment(Varyings input) : SV_Target
            {
                // La texture arriva con origine in alto a sinistra (convenzione
                // D3D), le UV di Unity hanno origine in basso a sinistra.
                float2 uv = float2(input.uv.x, 1.0 - input.uv.y);

                // --- modo 2: zoom sugli 8 pixel del marker ----------------------
                // Ingrandisce a schermo intero la sola striscia in alto a
                // sinistra. Serve a confermare a occhio che i pixel ci sono e
                // che cambiano a ogni frame; il valore vero lo legge la CPU.
                if (_DebugMode > 1.5)
                {
                    // _MainTex_TexelSize = (1/w, 1/h, w, h)
                    float2 markerUv = float2(uv.x * 8.0 * _MainTex_TexelSize.x,
                                             uv.y * 1.0 * _MainTex_TexelSize.y);
                    float3 marker = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, markerUv).rgb;
                    return float4(marker, 1.0);
                }

                // --- modo 1: depth -------------------------------------------
                if (_DebugMode > 0.5)
                {
                    // Il canale DEPTH e' depth LINEARE in unita' Unreal
                    // (centimetri). _DepthScaleToMeters arriva dal pacchetto
                    // STATUS ed e' 0.01: nessuna costante magica sparsa nel codice.
                    float depthUnreal = SAMPLE_TEXTURE2D(_DepthTex, sampler_DepthTex, uv).r;
                    float depthMeters = depthUnreal * _DepthScaleToMeters;
                    float normalized = saturate(depthMeters / max(_DepthRangeM, 0.01));
                    return float4(normalized.xxx, 1.0);
                }

                // --- modo 0: colore ------------------------------------------
                float3 color = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).rgb;
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
