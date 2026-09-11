// ============================================================================
//  ChannelConsumer  --  lato consumatore del trasporto.
//
//  Apre le texture condivise create da Unreal, e a ogni evento di consume:
//     AcquireSync(KEY_CONSUMER) -> CopyResource -> ReleaseSync(KEY_PRODUCER)
//
//  PERCHE' COPIA INVECE DI USARE DIRETTAMENTE LA SHARED TEXTURE:
//  finche' tieni il keyed mutex, il produttore non puo' scrivere. Se Unity
//  tenesse il mutex per tutta la durata del proprio frame, bloccherebbe
//  Unreal, e al primo frame perso i due processi si incastrerebbero a vicenda.
//  Copiando (~8 MB, molto meno di 0.1 ms su qualunque GPU moderna) il mutex si
//  tiene per pochi microsecondi e i due processi restano disaccoppiati.
//
//  TUTTI i metodi vanno chiamati dal RENDER THREAD di Unity, cioe' dentro la
//  callback registrata con GL.IssuePluginEvent: l'immediate context D3D11 di
//  Unity non e' thread-safe.
// ============================================================================

#pragma once

#include <d3d11_1.h>
#include <wrl/client.h>
#include <string>
#include <vector>

#include "NativeApi.h"

class MarkerReader;

class ChannelConsumer
{
public:
    bool Configure(ID3D11Device* device,
                   const GpuShareChannelDesc* descs, int32_t count,
                   uint32_t handleMode, const char* namePrefix,
                   std::string& outError);

    void Shutdown();

    bool IsConfigured() const { return m_configured; }
    int32_t GetChannelCount() const { return (int32_t)m_channels.size(); }

    /** Puntatore nativo della texture di PROPRIETA' di Unity per quel canale. */
    void* GetUnityTexture(uint32_t channelId) const;

    bool GetChannelSize(uint32_t channelId, uint32_t& outWidth, uint32_t& outHeight) const;

    /**
     * Consuma un gruppo. Ritorna false senza bloccare se il produttore non ha
     * ancora pubblicato (AcquireSync in timeout): in quel caso il chiamante
     * ripresenta il frame precedente e conta una ripetizione.
     */
    bool ConsumeGroup(uint32_t groupId, uint32_t readyIndex, uint32_t timeoutMs,
                      int64_t& outQpcConsume);

    /** Texture Unity del canale che porta il marker (serve al MarkerReader). */
    ID3D11Texture2D* GetMarkerSourceTexture() const { return m_markerSource; }

    /** Immediate context di Unity. Usabile SOLO dal render thread. */
    ID3D11DeviceContext* GetContext() const { return m_context.Get(); }

private:
    struct SharedBuffer
    {
        Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;
        Microsoft::WRL::ComPtr<IDXGIKeyedMutex> keyedMutex;
    };

    struct Channel
    {
        GpuShareChannelDesc desc = {};
        SharedBuffer buffers[GPUSHARE_BUFFERS_PER_CHANNEL];
        Microsoft::WRL::ComPtr<ID3D11Texture2D> unityTexture;
    };

    bool OpenSharedBuffer(Channel& channel, int32_t bufferIndex,
                          uint32_t handleMode, const char* namePrefix,
                          std::string& outError);
    bool CreateUnityTexture(Channel& channel, std::string& outError);

    Microsoft::WRL::ComPtr<ID3D11Device>        m_device;
    Microsoft::WRL::ComPtr<ID3D11Device1>       m_device1;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> m_context;

    std::vector<Channel> m_channels;
    ID3D11Texture2D* m_markerSource = nullptr;   // non-owning, punta in m_channels
    bool m_configured = false;
};
