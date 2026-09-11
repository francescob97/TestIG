// ============================================================================
//  Superficie C esportata verso il C# di Unity.
//
//  Il C# la consuma con [DllImport("UnityGpuShare")] in NativeBridge.cs.
//  Tutte le funzioni sono a C ABI (extern "C") e non lanciano eccezioni.
//
//  CONVENZIONE DI THREAD:
//    - tutte queste funzioni si chiamano dal MAIN THREAD di Unity;
//    - il lavoro D3D11 avviene invece nella callback restituita da
//      GpuShare_GetRenderEventFunc(), che Unity esegue sul RENDER THREAD
//      quando il C# chiama GL.IssuePluginEvent.
//  Questa separazione non e' pedanteria: l'immediate context D3D11 di Unity
//  non e' thread-safe e usarlo dal main thread produce corruzione casuale.
// ============================================================================

#pragma once

#include "IUnityGraphics.h"
#include "IUnityInterface.h"
#include "NativeApi.h"

extern "C"
{
    /** Cambia quando la ABI di questo plugin cambia: il C# lo verifica all'avvio. */
    UNITY_INTERFACE_EXPORT int32_t UNITY_INTERFACE_API GpuShare_GetApiVersion();

    /** 1 quando Unity ha inizializzato il device D3D11 e il plugin l'ha agganciato. */
    UNITY_INTERFACE_EXPORT int32_t UNITY_INTERFACE_API GpuShare_IsDeviceReady();

    /**
     * LUID dell'adapter su cui gira Unity. Va spedito a Unreal nel pacchetto
     * HELLO: se i due processi sono su GPU diverse la condivisione non puo'
     * funzionare, e senza questo controllo il sintomo sarebbe solo un HRESULT
     * di errore da OpenSharedResource1.
     */
    UNITY_INTERFACE_EXPORT int32_t UNITY_INTERFACE_API GpuShare_GetAdapterLuid(uint32_t* outLow, int32_t* outHigh);

    /**
     * Registra la tabella dei canali ricevuta nell'handshake. Non apre nulla
     * subito: marca la configurazione come pendente. L'apertura vera avviene
     * al prossimo GS_EVENT_CONFIGURE sul render thread.
     */
    UNITY_INTERFACE_EXPORT int32_t UNITY_INTERFACE_API GpuShare_Configure(
        const GpuShareChannelDesc* descs, int32_t count, uint32_t handleMode, const char* namePrefix);

    /** 1 quando le shared texture sono aperte e le texture Unity esistono. */
    UNITY_INTERFACE_EXPORT int32_t UNITY_INTERFACE_API GpuShare_IsConfigured();

    /**
     * Puntatore nativo (ID3D11Texture2D*) della texture di proprieta' di Unity
     * per quel canale. E' cio' che si passa a Texture2D.CreateExternalTexture.
     * Ritorna null finche' la configurazione non e' completata sul render thread.
     */
    UNITY_INTERFACE_EXPORT void* UNITY_INTERFACE_API GpuShare_GetUnityTexturePtr(uint32_t channelId);

    UNITY_INTERFACE_EXPORT int32_t UNITY_INTERFACE_API GpuShare_GetChannelSize(
        uint32_t channelId, uint32_t* outWidth, uint32_t* outHeight);

    /** Indice di buffer pronto per un gruppo, preso dall'ultimo pacchetto STATUS. */
    UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API GpuShare_SetGroupReady(
        uint32_t groupId, uint32_t readyIndex, uint32_t sequence);

    /** Indice di frame di Unity, timbrato su ogni consume per calcolare l'eta' in frame. */
    UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API GpuShare_SetFrameContext(uint64_t unityFrameIndex);

    /**
     * Timeout dell'AcquireSync lato consumatore, in ms.
     * 0 e' il valore giusto per misurare: significa "se il frame non c'e',
     * non aspettarlo". Aspettare mascherebbe proprio le ripetizioni che
     * vogliamo contare.
     */
    UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API GpuShare_SetConsumeTimeoutMs(uint32_t timeoutMs);

    /** Funzione da passare a GL.IssuePluginEvent / CommandBuffer.IssuePluginEvent. */
    UNITY_INTERFACE_EXPORT UnityRenderingEvent UNITY_INTERFACE_API GpuShare_GetRenderEventFunc();

    /** Ultimo marker decodificato, con il suo qpc_consume. Vedi NativeApi.h. */
    UNITY_INTERFACE_EXPORT int32_t UNITY_INTERFACE_API GpuShare_GetLatestFrameInfo(GpuShareFrameInfo* outInfo);

    UNITY_INTERFACE_EXPORT int32_t UNITY_INTERFACE_API GpuShare_GetStats(GpuShareNativeStats* outStats);

    /** Ultimo errore in UTF-8. Ritorna il numero di byte scritti (terminatore escluso). */
    UNITY_INTERFACE_EXPORT int32_t UNITY_INTERFACE_API GpuShare_GetLastError(char* buffer, int32_t bufferSize);

    UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API GpuShare_Shutdown();
}
