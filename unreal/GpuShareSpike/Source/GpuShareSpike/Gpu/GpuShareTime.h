// ============================================================================
//  QPC = QueryPerformanceCounter.
//
//  PERCHE' NON FPlatformTime::Seconds():
//  su Windows FPlatformTime e' derivato da QPC ma applica un offset proprio,
//  quindi due processi diversi NON producono valori confrontabili. QPC grezzo
//  invece e' un contatore di sistema: coerente tra processi sulla stessa
//  macchina. E' esattamente cio' che serve per misurare un anello che
//  attraversa due eseguibili.
//
//  Lato Unity l'equivalente esatto e' System.Diagnostics.Stopwatch.GetTimestamp()
//  (su Windows chiama QueryPerformanceCounter) con Stopwatch.Frequency.
// ============================================================================

#pragma once

#include "CoreMinimal.h"

#if PLATFORM_WINDOWS
#include "Windows/AllowWindowsPlatformTypes.h"
#include "Windows/WindowsHWrapper.h"

FORCEINLINE int64 GpuShareQpcNow()
{
	LARGE_INTEGER Counter;
	::QueryPerformanceCounter(&Counter);
	return (int64)Counter.QuadPart;
}

FORCEINLINE int64 GpuShareQpcFrequency()
{
	// La frequenza e' fissa per tutta la vita del sistema: la leggiamo una volta.
	static int64 CachedFrequency = []()
	{
		LARGE_INTEGER Frequency;
		::QueryPerformanceFrequency(&Frequency);
		return (int64)Frequency.QuadPart;
	}();
	return CachedFrequency;
}

FORCEINLINE double GpuShareQpcToMilliseconds(int64 Ticks)
{
	return (double)Ticks * 1000.0 / (double)GpuShareQpcFrequency();
}

#include "Windows/HideWindowsPlatformTypes.h"
#else
FORCEINLINE int64  GpuShareQpcNow() { return 0; }
FORCEINLINE int64  GpuShareQpcFrequency() { return 1; }
FORCEINLINE double GpuShareQpcToMilliseconds(int64) { return 0.0; }
#endif
