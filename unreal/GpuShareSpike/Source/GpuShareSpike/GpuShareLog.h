#pragma once
#include "CoreMinimal.h"
#include "Logging/LogMacros.h"

// In Unreal ogni sottosistema dichiara la propria "categoria di log", cosi' nel
// file Saved/Logs/*.log le righe sono prefissate da LogGpuShare: e le puoi
// filtrare. DECLARE nell'header, DEFINE una sola volta in un .cpp.
DECLARE_LOG_CATEGORY_EXTERN(LogGpuShare, Log, All);
