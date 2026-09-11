#include "ChannelConsumer.h"

#include <windows.h>
#include <cstdio>
#include <cstdarg>

using Microsoft::WRL::ComPtr;

namespace
{
    int64_t QpcNow()
    {
        LARGE_INTEGER counter;
        QueryPerformanceCounter(&counter);
        return (int64_t)counter.QuadPart;
    }

    std::wstring Widen(const char* utf8)
    {
        if (utf8 == nullptr || *utf8 == '\0')
        {
            return std::wstring();
        }
        const int needed = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, nullptr, 0);
        std::wstring result((size_t)(needed > 0 ? needed - 1 : 0), L'\0');
        if (needed > 1)
        {
            MultiByteToWideChar(CP_UTF8, 0, utf8, -1, &result[0], needed);
        }
        return result;
    }

    std::string Format(const char* fmt, ...)
    {
        char buffer[1024];
        va_list args;
        va_start(args, fmt);
        vsnprintf(buffer, sizeof(buffer), fmt, args);
        va_end(args);
        return std::string(buffer);
    }
}

bool ChannelConsumer::Configure(ID3D11Device* device,
                                const GpuShareChannelDesc* descs, int32_t count,
                                uint32_t handleMode, const char* namePrefix,
                                std::string& outError)
{
    Shutdown();

    if (device == nullptr)
    {
        outError = "ID3D11Device nullo: Unity non sta girando su D3D11?";
        return false;
    }
    if (descs == nullptr || count <= 0 || count > GPUSHARE_MAX_CHANNELS)
    {
        outError = Format("numero di canali non valido: %d", count);
        return false;
    }

    m_device = device;
    m_device->GetImmediateContext(&m_context);

    // OpenSharedResource1 sta su ID3D11Device1 (Windows 8+). E' il metodo per
    // gli NT handle; il vecchio OpenSharedResource lavora sugli handle legacy,
    // che non supportano il keyed mutex.
    if (FAILED(m_device.As(&m_device1)))
    {
        outError = "QueryInterface(ID3D11Device1) fallita: serve Windows 8 o superiore";
        return false;
    }

    m_channels.resize((size_t)count);
    for (int32_t i = 0; i < count; ++i)
    {
        m_channels[(size_t)i].desc = descs[i];

        for (int32_t b = 0; b < GPUSHARE_BUFFERS_PER_CHANNEL; ++b)
        {
            if (!OpenSharedBuffer(m_channels[(size_t)i], b, handleMode, namePrefix, outError))
            {
                Shutdown();
                return false;
            }
        }

        if (!CreateUnityTexture(m_channels[(size_t)i], outError))
        {
            Shutdown();
            return false;
        }

        if ((m_channels[(size_t)i].desc.flags & GS_CHFLAG_HAS_MARKER) != 0)
        {
            m_markerSource = m_channels[(size_t)i].unityTexture.Get();
        }
    }

    m_configured = true;
    return true;
}

bool ChannelConsumer::OpenSharedBuffer(Channel& channel, int32_t bufferIndex,
                                       uint32_t handleMode, const char* namePrefix,
                                       std::string& outError)
{
    SharedBuffer& buffer = channel.buffers[bufferIndex];
    HRESULT hr = E_FAIL;

    if (handleMode == GS_HANDLEMODE_NAMED)
    {
        // Il nome deve combaciare ESATTAMENTE con quello costruito da Unreal in
        // FGpuShareD3D11ChannelGroup::Initialize.
        char nameUtf8[160];
        snprintf(nameUtf8, sizeof(nameUtf8), "%s_ch%u_b%d", namePrefix, channel.desc.channel_id, bufferIndex);
        const std::wstring nameWide = Widen(nameUtf8);

        hr = m_device1->OpenSharedResourceByName(
            nameWide.c_str(),
            DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
            IID_PPV_ARGS(buffer.texture.GetAddressOf()));

        if (FAILED(hr))
        {
            outError = Format("OpenSharedResourceByName('%s') fallita: hr=0x%08X", nameUtf8, (unsigned)hr);
            return false;
        }
    }
    else
    {
        const HANDLE handle = (HANDLE)(uintptr_t)channel.desc.shared_handles[bufferIndex];
        if (handle == nullptr)
        {
            outError = Format("handle nullo per canale %u buffer %d: Unreal non ha duplicato nulla",
                              channel.desc.channel_id, bufferIndex);
            return false;
        }

        hr = m_device1->OpenSharedResource1(handle, IID_PPV_ARGS(buffer.texture.GetAddressOf()));
        if (FAILED(hr))
        {
            outError = Format(
                "OpenSharedResource1 fallita per canale %u buffer %d: hr=0x%08X. "
                "Causa piu' probabile: i due processi sono su GPU DIVERSE (controlla il LUID in HUD).",
                channel.desc.channel_id, bufferIndex, (unsigned)hr);
            return false;
        }
    }

    if (FAILED(buffer.texture.As(&buffer.keyedMutex)))
    {
        outError = Format("la risorsa condivisa del canale %u non espone IDXGIKeyedMutex",
                          channel.desc.channel_id);
        return false;
    }

    return true;
}

bool ChannelConsumer::CreateUnityTexture(Channel& channel, std::string& outError)
{
    // Texture di proprieta' di Unity: stesso formato e stessa dimensione della
    // condivisa, ma SENZA i flag di sharing. E' questa che avvolgiamo con
    // Texture2D.CreateExternalTexture lato C#.
    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = channel.desc.width;
    desc.Height = channel.desc.height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = (DXGI_FORMAT)channel.desc.dxgi_format;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    desc.CPUAccessFlags = 0;
    desc.MiscFlags = 0;

    const HRESULT hr = m_device->CreateTexture2D(&desc, nullptr, channel.unityTexture.GetAddressOf());
    if (FAILED(hr))
    {
        outError = Format("CreateTexture2D (lato Unity) fallita per canale %u: hr=0x%08X",
                          channel.desc.channel_id, (unsigned)hr);
        return false;
    }
    return true;
}

void ChannelConsumer::Shutdown()
{
    m_channels.clear();
    m_markerSource = nullptr;
    m_context.Reset();
    m_device1.Reset();
    m_device.Reset();
    m_configured = false;
}

void* ChannelConsumer::GetUnityTexture(uint32_t channelId) const
{
    for (const Channel& channel : m_channels)
    {
        if (channel.desc.channel_id == channelId)
        {
            return channel.unityTexture.Get();
        }
    }
    return nullptr;
}

bool ChannelConsumer::GetChannelSize(uint32_t channelId, uint32_t& outWidth, uint32_t& outHeight) const
{
    for (const Channel& channel : m_channels)
    {
        if (channel.desc.channel_id == channelId)
        {
            outWidth = channel.desc.width;
            outHeight = channel.desc.height;
            return true;
        }
    }
    return false;
}

bool ChannelConsumer::ConsumeGroup(uint32_t groupId, uint32_t readyIndex, uint32_t timeoutMs,
                                   int64_t& outQpcConsume)
{
    outQpcConsume = 0;

    if (!m_configured || readyIndex >= GPUSHARE_BUFFERS_PER_CHANNEL)
    {
        return false;
    }

    // Indici dei canali che appartengono a questo gruppo.
    int32_t members[GPUSHARE_MAX_CHANNELS];
    int32_t memberCount = 0;
    for (size_t i = 0; i < m_channels.size(); ++i)
    {
        if (m_channels[i].desc.group_id == groupId)
        {
            members[memberCount++] = (int32_t)i;
        }
    }
    if (memberCount == 0)
    {
        return false;
    }

    // Acquisizione nello STESSO ORDINE usato dal produttore (indice di canale
    // crescente). Con chiavi diverse tra produttore e consumatore un deadlock
    // da ordine di lock non sarebbe comunque possibile, ma tenere un ordine
    // fisso e' igiene che costa zero.
    int32_t acquired = 0;
    for (int32_t k = 0; k < memberCount; ++k)
    {
        IDXGIKeyedMutex* mutex = m_channels[(size_t)members[k]].buffers[readyIndex].keyedMutex.Get();
        const HRESULT hr = mutex->AcquireSync(GPUSHARE_KEY_CONSUMER, timeoutMs);

        if (hr != S_OK && hr != (HRESULT)WAIT_ABANDONED)
        {
            // Timeout: il produttore non ha ancora pubblicato su questo buffer.
            // Rollback e ritorno immediato: il chiamante ripresentera' il frame
            // precedente e contera' una ripetizione.
            for (int32_t r = 0; r < acquired; ++r)
            {
                m_channels[(size_t)members[r]].buffers[readyIndex].keyedMutex->ReleaseSync(GPUSHARE_KEY_CONSUMER);
            }
            return false;
        }
        ++acquired;
    }

    // Timbro preso appena TUTTI i mutex del gruppo sono nostri: e' il momento
    // esatto in cui i pixel di questo frame sono disponibili a Unity.
    outQpcConsume = QpcNow();

    for (int32_t k = 0; k < memberCount; ++k)
    {
        Channel& channel = m_channels[(size_t)members[k]];
        m_context->CopyResource(channel.unityTexture.Get(), channel.buffers[readyIndex].texture.Get());
    }

    // Rilasciando con KEY_PRODUCER restituiamo il buffer a Unreal.
    for (int32_t k = 0; k < memberCount; ++k)
    {
        m_channels[(size_t)members[k]].buffers[readyIndex].keyedMutex->ReleaseSync(GPUSHARE_KEY_PRODUCER);
    }

    return true;
}
