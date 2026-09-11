// Target dell'EDITOR. Stesso modulo di gioco, ma linkato dentro l'editor.
using UnrealBuildTool;

public class GpuShareSpikeEditorTarget : TargetRules
{
	public GpuShareSpikeEditorTarget(TargetInfo Target) : base(Target)
	{
		Type = TargetType.Editor;

		DefaultBuildSettings = BuildSettingsVersion.Latest;
		IncludeOrderVersion  = EngineIncludeOrderVersion.Latest;

		ExtraModuleNames.Add("GpuShareSpike");
	}
}
