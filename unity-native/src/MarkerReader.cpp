#include "MarkerReader.h"

#include <windows.h>
#include <cstdio>
#include <cstring>

using Microsoft::WRL::ComPtr;

bool MarkerReader::Initialize(ID3D11Device* device, std::string& outError)
{
    Shutdown();

    if (device == nullptr)
    {
        outError = "MarkerReader: device nullo";
        return false;
    }

    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = GPUSHARE_MARKER_PIXELS;
    desc.Height = 1;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    // STAGING + CPU_ACCESS_READ: e' l'unica combinazione da cui la CPU puo'
    // leggere. Una texture STAGING non puo' essere bindata a nessuno stadio
    // della pipeline, ed e' esattamente cio' che vogliamo: nessuno shader la
    // tocchera' mai, quindi nessuna conversione di colore.
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    desc.MiscFlags = 0;

    for (int32_t i = 0; i < RingSize; ++i)
    {
        const HRESULT hr = device->CreateTexture2D(&desc, nullptr, m_slots[i].staging.GetAddressOf());
        if (FAILED(hr))
        {
            char buffer[256];
            snprintf(buffer, sizeof(buffer), "MarkerReader: CreateTexture2D staging %d fallita: hr=0x%08X", i, (unsigned)hr);
            outError = buffer;
            Shutdown();
            return false;
        }
        m_slots[i].pending = false;
    }

    m_writeIndex = 0;
    m_submitSerial = 0;
    m_ready = true;
    return true;
}

void MarkerReader::Shutdown()
{
    for (int32_t i = 0; i < RingSize; ++i)
    {
        m_slots[i].staging.Reset();
        m_slots[i].pending = false;
    }
    m_ready = false;
    m_latestValid = 0;
}

void MarkerReader::Submit(ID3D11DeviceContext* context, ID3D11Texture2D* source,
                          int64_t qpcConsume, uint64_t unityFrameIndex)
{
    if (!m_ready || context == nullptr || source == nullptr)
    {
        return;
    }

    // 1. Accoda la copia degli 8 pixel nello slot corrente.
    D3D11_BOX box = {};
    box.left = 0;
    box.top = 0;
    box.front = 0;
    box.right = GPUSHARE_MARKER_PIXELS;
    box.bottom = 1;
    box.back = 1;

    Slot& writeSlot = m_slots[m_writeIndex];
    context->CopySubresourceRegion(writeSlot.staging.Get(), 0, 0, 0, 0, source, 0, &box);
    writeSlot.qpcConsume = qpcConsume;
    writeSlot.unityFrameIndex = unityFrameIndex;
    writeSlot.submitSerial = ++m_submitSerial;
    writeSlot.pending = true;

    // 2. Prova a leggere lo slot piu' vecchio, SENZA MAI BLOCCARE.
    const int32_t readIndex = (m_writeIndex + 1) % RingSize;
    Slot& readSlot = m_slots[readIndex];

    if (readSlot.pending)
    {
        D3D11_MAPPED_SUBRESOURCE mapped = {};
        const HRESULT hr = context->Map(readSlot.staging.Get(), 0, D3D11_MAP_READ,
                                        D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
        if (hr == S_OK)
        {
            GpuShareMarker marker = {};
            memcpy(&marker, mapped.pData, sizeof(marker));
            context->Unmap(readSlot.staging.Get(), 0);

            readSlot.pending = false;
            ++m_readCount;

            GpuShareFrameInfo info = {};
            info.marker = marker;
            info.qpc_consume = readSlot.qpcConsume;
            info.unity_frame_index = readSlot.unityFrameIndex;
            info.marker_valid = GpuShareMarkerIsValid(&marker) ? 1u : 0u;
            info.readback_lag_events = (uint32_t)(m_submitSerial - readSlot.submitSerial);

            if (info.marker_valid == 0)
            {
                ++m_invalidCount;
            }

            m_latest = info;
            m_latestValid = 1;
        }
        // hr == DXGI_ERROR_WAS_STILL_DRAWING: la GPU non ha ancora finito.
        // Nessun problema: riproviamo al prossimo giro, lo slot resta pending.
    }

    m_writeIndex = readIndex;
}

bool MarkerReader::GetLatest(GpuShareFrameInfo& outInfo) const
{
    if (m_latestValid == 0)
    {
        return false;
    }
    outInfo = m_latest;
    return true;
}
