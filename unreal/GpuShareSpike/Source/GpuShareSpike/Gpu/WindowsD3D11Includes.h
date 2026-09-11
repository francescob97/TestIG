// ============================================================================
//  Includere <d3d11.h> dentro Unreal non e' banale. Questo file incapsula la
//  ricetta corretta cosi' la scrivi una volta sola.
//
//  IL PROBLEMA:
//  Unreal ridefinisce un sacco di simboli che collidono con le intestazioni
//  Windows. I casi classici:
//    - TEXT()            : Unreal lo vuole wide, windows.h lo rende dipendente da UNICODE
//    - TCHAR, DWORD, ...  : Unreal ha i propri typedef
//    - GetObject, CreateFile, DrawText, ... : windows.h ne fa MACRO che
//      sostituiscono silenziosamente i nomi delle tue funzioni con la variante
//      A/W, producendo errori di link incomprensibili
//    - InterlockedXxx     : collidono con gli atomics di Unreal
//
//  LA RICETTA:
//    AllowWindowsPlatformTypes.h    -> sospende i typedef di Unreal
//    AllowWindowsPlatformAtomics.h  -> sospende gli atomics di Unreal
//    THIRD_PARTY_INCLUDES_START/END -> spegne i warning-as-error sul codice di terzi
//    ...include...
//    Hide* nell'ordine INVERSO      -> ripristina tutto
//
//  Se salti il blocco Hide*, il file successivo che include questo si trova
//  windows.h "aperto" e ti esplode in faccia da tutt'altra parte.
//
//  REGOLA D'USO IN QUESTO PROGETTO:
//  questo header NON va incluso da nessun header che contenga una UCLASS o una
//  USTRUCT: UnrealHeaderTool analizza quei file con un parser suo e si confonde
//  con le intestazioni Windows. Nei file UCLASS usa una forward declaration
//  (es. "class FGpuShareD3D11ChannelGroup;") e includi qui solo dai .cpp.
// ============================================================================

#pragma once

#include "CoreMinimal.h"

#if PLATFORM_WINDOWS

// Modo UE-safe di tirare dentro windows.h (serve per OpenProcess/DuplicateHandle).
#include "Windows/WindowsHWrapper.h"

#include "Windows/AllowWindowsPlatformTypes.h"
#include "Windows/AllowWindowsPlatformAtomics.h"
THIRD_PARTY_INCLUDES_START

	#include <d3d11_1.h>    // ID3D11Device1, IDXGIResource1::CreateSharedHandle
	#include <dxgi1_2.h>    // IDXGIKeyedMutex, DXGI_SHARED_RESOURCE_*

THIRD_PARTY_INCLUDES_END
#include "Windows/HideWindowsPlatformAtomics.h"
#include "Windows/HideWindowsPlatformTypes.h"

#endif // PLATFORM_WINDOWS
