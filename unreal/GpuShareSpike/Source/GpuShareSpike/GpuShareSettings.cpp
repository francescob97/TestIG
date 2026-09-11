#include "GpuShareSettings.h"

UGpuShareSettings::UGpuShareSettings()
{
	// CategoryName controlla sotto quale sezione della finestra Project Settings
	// compare questa pagina.
	CategoryName = TEXT("Plugins");
	SectionName = TEXT("GPU Share Spike");
}

const UGpuShareSettings& UGpuShareSettings::Get()
{
	// GetDefault<T>() restituisce il "Class Default Object" (CDO): in Unreal i
	// UDeveloperSettings vivono come singleton sul CDO, popolato dall'ini.
	const UGpuShareSettings* Settings = GetDefault<UGpuShareSettings>();
	check(Settings);
	return *Settings;
}
