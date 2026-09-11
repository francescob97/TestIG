// ============================================================================
//  MarkerReader  --  legge i 32 byte del marker dai pixel.
//
//  IL PUNTO CRITICO:
//  quei 32 byte devono arrivare BIT-ESATTI. Campionarli con uno shader e'
//  fuori discussione: in un progetto Linear color space il sampler applicherebbe
//  la conversione sRGB->lineare e i bit sarebbero distrutti. Li leggiamo quindi
//  come BYTE GREZZI da una staging texture mappata in CPU, dove nessuna
//  conversione di colore puo' avvenire.
//
//  PERCHE' UN RING DI STAGING E NON UNA MAP DIRETTA:
//  una Map() senza flag blocca finche' la GPU non ha completato la copia, cioe'
//  svuota la pipeline. Farlo ogni frame sul render thread aggiungerebbe
//  latenza proprio a cio' che stiamo misurando: lo strumento falserebbe la
//  misura. Quindi:
//     - a ogni consume copiamo 8x1 pixel nello slot corrente del ring
//     - proviamo a mappare lo slot PIU' VECCHIO con MAP_FLAG_DO_NOT_WAIT
//  Non si blocca mai. Il prezzo e' che veniamo a sapere il contenuto di un
//  frame due-tre eventi dopo; ma siccome il valore che conta (qpc_consume) e'
//  timbrato al momento giusto e trasportato insieme, il RITARDO DI LETTURA NON
//  ENTRA NELLA MISURA: cambia solo quando la leggiamo, non quanto vale.
// ============================================================================

#pragma once

#include <d3d11_1.h>
#include <wrl/client.h>
#include <string>

#include "NativeApi.h"

class MarkerReader
{
public:
    static constexpr int32_t RingSize = 3;

    bool Initialize(ID3D11Device* device, std::string& outError);
    void Shutdown();
    bool IsReady() const { return m_ready; }

    /**
     * Da chiamare sul render thread subito dopo un consume riuscito.
     * @param source          texture Unity del canale che porta il marker
     * @param qpcConsume      timbro preso all'AcquireSync di QUESTO frame
     * @param unityFrameIndex frame di Unity corrente
     */
    void Submit(ID3D11DeviceContext* context, ID3D11Texture2D* source,
                int64_t qpcConsume, uint64_t unityFrameIndex);

    /** Ultimo frame decodificato con successo. Chiamabile dal main thread. */
    bool GetLatest(GpuShareFrameInfo& outInfo) const;

    uint64_t GetReadCount() const { return m_readCount; }
    uint64_t GetInvalidCount() const { return m_invalidCount; }

private:
    struct Slot
    {
        Microsoft::WRL::ComPtr<ID3D11Texture2D> staging;
        int64_t  qpcConsume = 0;
        uint64_t unityFrameIndex = 0;
        uint64_t submitSerial = 0;
        bool     pending = false;
    };

    Slot m_slots[RingSize];
    int32_t m_writeIndex = 0;
    uint64_t m_submitSerial = 0;
    bool m_ready = false;

    // Scritto dal render thread, letto dal main thread. La struttura e'
    // piccola e il valore e' puramente diagnostico: una lettura mista in un
    // frame di transizione non cambia nulla di ciò che si misura. Il flag
    // volatile evita che il compilatore cachi il puntatore.
    mutable volatile int32_t m_latestValid = 0;
    GpuShareFrameInfo m_latest = {};

    uint64_t m_readCount = 0;
    uint64_t m_invalidCount = 0;
};
