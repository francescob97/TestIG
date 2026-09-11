// Target del GIOCO (eseguibile standalone / packaged).
// In Unreal ogni "Target" descrive un eseguibile. Ne servono due: uno per il
// gioco e uno per l'editor. Sono file C# compilati da UnrealBuildTool (UBT).
using UnrealBuildTool;

public class GpuShareSpikeTarget : TargetRules
{
	public GpuShareSpikeTarget(TargetInfo Target) : base(Target)
	{
		Type = TargetType.Game;

		// "Latest" invece di una versione fissa: evita di dover indovinare il
		// nome esatto del membro enum per la versione di engine installata.
		DefaultBuildSettings = BuildSettingsVersion.Latest;
		IncludeOrderVersion  = EngineIncludeOrderVersion.Latest;

		ExtraModuleNames.Add("GpuShareSpike");
	}
}
