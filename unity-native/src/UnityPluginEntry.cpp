// ============================================================================
//  Punto di ingresso del plugin nativo e implementazione della superficie C.
//
//  CICLO DI VITA DI UN PLUGIN NATIVO UNITY:
//    UnityPluginLoad(IUnityInterfaces*)  -> Unity ci passa le sue interfacce
//    RegisterDeviceEventCallback         -> ci notifica init/shutdown del device
//    kUnityGfxDeviceEventInitialize      -> qui prendiamo l'ID3D11Device
//    UnityPluginUnload                   -> teardown
//
//  ATTENZIONE (costa mezz'ora la prima volta che capita):
//  l'EDITOR DI UNITY TIENE LA DLL APERTA per tutta la sessione. Se ricompili
//  il plugin mentre l'editor e' aperto, il file e' bloccato e la copia
//  fallisce, oppure resta in memoria la versione vecchia. Vedi
//  unity-native/README.md per il ciclo di iterazione corretto.
// ============================================================================

#include "Exports.h"

#include "ChannelConsumer.h"
#include "MarkerReader.h"

#include <d3d11_1.h>
#include <dxgi.h>
#include <wrl/client.h>

#include <atomic>
#include <cstring>
#include <mutex>
#include <string>

#include "IUnityGraphicsD3D11.h"

using Microsoft::WRL::ComPtr;

namespace
{
    struct PluginState
    {
        IUnityInterfaces* interfaces = nullptr;
        IUnityGraphics*   graphics = nullptr;
        ID3D11Device*     device = nullptr;   // di proprieta' di Unity: non lo rilasciamo

        ChannelConsumer consumer;
        MarkerReader    markerReader;

        // --- configurazione in attesa (main thread -> render thread) --------
        std::mutex configureMutex;
        GpuShareChannelDesc pendingDescs[GPUSHARE_MAX_CHANNELS] = {};
        int32_t  pendingCount = 0;
        uint32_t pendingHandleMode = GS_HANDLEMODE_DUPLICATED;
        char     pendingNamePrefix[64] = {};
        std::atomic<bool> configurePending{ false };
        std::atomic<bool> configured{ false };

        // --- stato per frame ------------------------------------------------
        std::atomic<uint32_t> groupReadyIndex[GPUSHARE_GROUP_COUNT];
        std::atomic<uint32_t> groupSequence[GPUSHARE_GROUP_COUNT];
        std::atomic<uint64_t> unityFrameIndex{ 0 };
        std::atomic<uint32_t> consumeTimeoutMs{ 0 };

        // --- statistiche ----------------------------------------------------
        std::atomic<uint64_t> consumeAttempts{ 0 };
        std::atomic<uint64_t> consumeSuccess{ 0 };
        std::atomic<uint64_t> acquireTimeouts{ 0 };

        std::mutex errorMutex;
        std::string lastError;

        void SetError(const std::string& message)
        {
            std::lock_guard<std::mutex> lock(errorMutex);
            lastError = message;
        }
    };

    PluginState g;

    // ------------------------------------------------------------------------
    //  Lavoro sul render thread
    // ------------------------------------------------------------------------

    void DoConfigure_RenderThread()
    {
        if (!g.configurePending.exchange(false))
        {
            return;
        }
        if (g.device == nullptr)
        {
            g.SetError("device D3D11 non ancora disponibile");
            return;
        }

        GpuShareChannelDesc descs[GPUSHARE_MAX_CHANNELS];
        int32_t count = 0;
        uint32_t handleMode = GS_HANDLEMODE_DUPLICATED;
        char namePrefix[64];
        {
            std::lock_guard<std::mutex> lock(g.configureMutex);
            memcpy(descs, g.pendingDescs, sizeof(descs));
            count = g.pendingCount;
            handleMode = g.pendingHandleMode;
            memcpy(namePrefix, g.pendingNamePrefix, sizeof(namePrefix));
        }

        std::string error;
        if (!g.consumer.Configure(g.device, descs, count, handleMode, namePrefix, error))
        {
            g.SetError(error);
            g.configured.store(false);
            return;
        }

        if (!g.markerReader.Initialize(g.device, error))
        {
            g.SetError(error);
            // Non fatale: senza marker reader il trasporto funziona ancora,
            // perdi solo la strumentazione.
        }

        g.SetError("");
        g.configured.store(true);
    }

    void DoConsume_RenderThread(uint32_t groupId)
    {
        if (!g.configured.load() || groupId >= GPUSHARE_GROUP_COUNT)
        {
            return;
        }

        g.consumeAttempts.fetch_add(1);

        const uint32_t readyIndex = g.groupReadyIndex[groupId].load();
        const uint32_t timeoutMs = g.consumeTimeoutMs.load();

        int64_t qpcConsume = 0;
        if (!g.consumer.ConsumeGroup(groupId, readyIndex, timeoutMs, qpcConsume))
        {
            // Nessun frame nuovo: Unity ripresentera' quello precedente.
            // NON e' un errore, e' il dato che vogliamo contare.
            g.acquireTimeouts.fetch_add(1);
            return;
        }

        g.consumeSuccess.fetch_add(1);

        // Solo il gruppo MAIN porta il marker.
        if (groupId == GS_GROUP_MAIN)
        {
            g.markerReader.Submit(
                g.consumer.GetContext(),
                g.consumer.GetMarkerSourceTexture(),
                qpcConsume,
                g.unityFrameIndex.load());
        }
    }

    void DoShutdown_RenderThread()
    {
        g.markerReader.Shutdown();
        g.consumer.Shutdown();
        g.configured.store(false);
    }

    void UNITY_INTERFACE_API OnRenderEvent(int eventId)
    {
        switch (eventId)
        {
        case GS_EVENT_CONFIGURE:    DoConfigure_RenderThread();              break;
        case GS_EVENT_CONSUME_MAIN: DoConsume_RenderThread(GS_GROUP_MAIN);   break;
        case GS_EVENT_CONSUME_CUBE: DoConsume_RenderThread(GS_GROUP_CUBE);   break;
        case GS_EVENT_SHUTDOWN:     DoShutdown_RenderThread();               break;
        default: break;
        }
    }

    // ------------------------------------------------------------------------
    //  Eventi di device
    // ------------------------------------------------------------------------

    void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType)
    {
        switch (eventType)
        {
        case kUnityGfxDeviceEventInitialize:
        {
            if (g.graphics == nullptr)
            {
                break;
            }
            if (g.graphics->GetRenderer() != kUnityGfxRendererD3D11)
            {
                g.SetError("Unity non sta girando su Direct3D 11. "
                           "Player Settings > Other Settings: togli 'Auto Graphics API for Windows' "
                           "e lascia solo Direct3D11.");
                break;
            }
            if (IUnityGraphicsD3D11* d3d11 = g.interfaces->Get<IUnityGraphicsD3D11>())
            {
                g.device = d3d11->GetDevice();
            }
            break;
        }

        case kUnityGfxDeviceEventShutdown:
            DoShutdown_RenderThread();
            g.device = nullptr;
            break;

        default:
            break;
        }
    }
}

// ============================================================================
//  Hook di Unity
// ============================================================================

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API
UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
    g.interfaces = unityInterfaces;
    g.graphics = unityInterfaces->Get<IUnityGraphics>();

    for (int32_t i = 0; i < GPUSHARE_GROUP_COUNT; ++i)
    {
        g.groupReadyIndex[i].store(0);
        g.groupSequence[i].store(0);
    }

    g.graphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);

    // Il device potrebbe essere gia' inizializzato quando il plugin viene
    // caricato: l'evento non arriverebbe mai, quindi lo simuliamo.
    OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
    if (g.graphics != nullptr)
    {
        g.graphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    }
    DoShutdown_RenderThread();
    g.device = nullptr;
    g.graphics = nullptr;
    g.interfaces = nullptr;
}

// ============================================================================
//  Superficie C (vedi Exports.h)
// ============================================================================

extern "C"
{

int32_t UNITY_INTERFACE_API GpuShare_GetApiVersion()
{
    return 1;
}

int32_t UNITY_INTERFACE_API GpuShare_IsDeviceReady()
{
    return g.device != nullptr ? 1 : 0;
}

int32_t UNITY_INTERFACE_API GpuShare_GetAdapterLuid(uint32_t* outLow, int32_t* outHigh)
{
    if (outLow == nullptr || outHigh == nullptr)
    {
        return 0;
    }
    *outLow = 0;
    *outHigh = 0;

    if (g.device == nullptr)
    {
        return 0;
    }

    ComPtr<IDXGIDevice> dxgiDevice;
    if (FAILED(g.device->QueryInterface(IID_PPV_ARGS(dxgiDevice.GetAddressOf()))))
    {
        return 0;
    }

    ComPtr<IDXGIAdapter> adapter;
    if (FAILED(dxgiDevice->GetAdapter(adapter.GetAddressOf())))
    {
        return 0;
    }

    DXGI_ADAPTER_DESC desc = {};
    if (FAILED(adapter->GetDesc(&desc)))
    {
        return 0;
    }

    *outLow = (uint32_t)desc.AdapterLuid.LowPart;
    *outHigh = (int32_t)desc.AdapterLuid.HighPart;
    return 1;
}

int32_t UNITY_INTERFACE_API GpuShare_Configure(
    const GpuShareChannelDesc* descs, int32_t count, uint32_t handleMode, const char* namePrefix)
{
    if (descs == nullptr || count <= 0 || count > GPUSHARE_MAX_CHANNELS)
    {
        g.SetError("GpuShare_Configure: parametri non validi");
        return 0;
    }

    {
        std::lock_guard<std::mutex> lock(g.configureMutex);
        memset(g.pendingDescs, 0, sizeof(g.pendingDescs));
        memcpy(g.pendingDescs, descs, sizeof(GpuShareChannelDesc) * (size_t)count);
        g.pendingCount = count;
        g.pendingHandleMode = handleMode;
        memset(g.pendingNamePrefix, 0, sizeof(g.pendingNamePrefix));
        if (namePrefix != nullptr)
        {
            strncpy_s(g.pendingNamePrefix, sizeof(g.pendingNamePrefix), namePrefix, _TRUNCATE);
        }
    }

    g.configured.store(false);
    g.configurePending.store(true);
    return 1;
}

int32_t UNITY_INTERFACE_API GpuShare_IsConfigured()
{
    return g.configured.load() ? 1 : 0;
}

void* UNITY_INTERFACE_API GpuShare_GetUnityTexturePtr(uint32_t channelId)
{
    if (!g.configured.load())
    {
        return nullptr;
    }
    return g.consumer.GetUnityTexture(channelId);
}

int32_t UNITY_INTERFACE_API GpuShare_GetChannelSize(uint32_t channelId, uint32_t* outWidth, uint32_t* outHeight)
{
    if (outWidth == nullptr || outHeight == nullptr || !g.configured.load())
    {
        return 0;
    }
    return g.consumer.GetChannelSize(channelId, *outWidth, *outHeight) ? 1 : 0;
}

void UNITY_INTERFACE_API GpuShare_SetGroupReady(uint32_t groupId, uint32_t readyIndex, uint32_t sequence)
{
    if (groupId >= GPUSHARE_GROUP_COUNT || readyIndex >= GPUSHARE_BUFFERS_PER_CHANNEL)
    {
        return;
    }
    g.groupReadyIndex[groupId].store(readyIndex);
    g.groupSequence[groupId].store(sequence);
}

void UNITY_INTERFACE_API GpuShare_SetFrameContext(uint64_t unityFrameIndex)
{
    g.unityFrameIndex.store(unityFrameIndex);
}

void UNITY_INTERFACE_API GpuShare_SetConsumeTimeoutMs(uint32_t timeoutMs)
{
    g.consumeTimeoutMs.store(timeoutMs);
}

UnityRenderingEvent UNITY_INTERFACE_API GpuShare_GetRenderEventFunc()
{
    return OnRenderEvent;
}

int32_t UNITY_INTERFACE_API GpuShare_GetLatestFrameInfo(GpuShareFrameInfo* outInfo)
{
    if (outInfo == nullptr)
    {
        return 0;
    }
    return g.markerReader.GetLatest(*outInfo) ? 1 : 0;
}

int32_t UNITY_INTERFACE_API GpuShare_GetStats(GpuShareNativeStats* outStats)
{
    if (outStats == nullptr)
    {
        return 0;
    }
    outStats->consume_attempts = g.consumeAttempts.load();
    outStats->consume_success  = g.consumeSuccess.load();
    outStats->acquire_timeouts = g.acquireTimeouts.load();
    outStats->marker_reads     = g.markerReader.GetReadCount();
    outStats->marker_invalid   = g.markerReader.GetInvalidCount();
    outStats->device_ready     = (g.device != nullptr) ? 1u : 0u;
    outStats->channels_open    = (uint32_t)g.consumer.GetChannelCount();
    return 1;
}

int32_t UNITY_INTERFACE_API GpuShare_GetLastError(char* buffer, int32_t bufferSize)
{
    if (buffer == nullptr || bufferSize <= 0)
    {
        return 0;
    }

    std::lock_guard<std::mutex> lock(g.errorMutex);
    const int32_t length = (int32_t)g.lastError.size();
    const int32_t toCopy = (length < bufferSize - 1) ? length : (bufferSize - 1);
    memcpy(buffer, g.lastError.c_str(), (size_t)toCopy);
    buffer[toCopy] = '\0';
    return toCopy;
}

void UNITY_INTERFACE_API GpuShare_Shutdown()
{
    // Il vero teardown avviene sul render thread via GS_EVENT_SHUTDOWN: qui
    // marchiamo solo lo stato, cosi' il C# smette di usare le texture.
    g.configured.store(false);
}

} // extern "C"
