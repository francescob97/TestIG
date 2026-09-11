#include "GpuShareSpike.h"
#include "GpuShareLog.h"
#include "Modules/ModuleManager.h"

DEFINE_LOG_CATEGORY(LogGpuShare);

// IMPLEMENT_PRIMARY_GAME_MODULE dichiara il modulo "principale" del gioco.
// Un progetto Unreal C++ ne ha esattamente uno; e' il punto in cui l'engine
// aggancia il codice del progetto. Il terzo argomento e' il nome del gioco.
IMPLEMENT_PRIMARY_GAME_MODULE(FDefaultGameModuleImpl, GpuShareSpike, "GpuShareSpike");
