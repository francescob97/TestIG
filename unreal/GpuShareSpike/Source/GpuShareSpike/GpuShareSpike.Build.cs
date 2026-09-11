// ============================================================================
//  Build.cs del modulo.
//
//  In Unreal ogni modulo C++ ha un file <Modulo>.Build.cs scritto in C# che
//  UnrealBuildTool esegue per sapere cosa linkare. Non e' un makefile: e'
//  codice, quindi puoi fare if/else sulla piattaforma.
//
//  Dipendenze che NON sono ovvie e perche' ci sono:
//   - RHI / RenderCore : servono per ENQUEUE_RENDER_COMMAND, FRHITexture,
//                        GDynamicRHI e in generale per parlare col render thread.
//   - d3d11.lib/dxgi.lib : parliamo direttamente con D3D11.
//
//  Dipendenza che NON serve, contro ogni aspettativa:
//   - il modulo "D3D11RHI" e i suoi header privati. Tutta la letteratura online
//     dice di aggiungere Source/Runtime/Windows/D3D11RHI/Private agli include
//     path per usare GetD3D11TextureFromRHITexture(). NON SERVE:
//       FRHITexture::GetNativeResource()   -> ID3D11Texture2D*
//       GDynamicRHI->RHIGetNativeDevice()  -> ID3D11Device*
//     sono API pubbliche e fanno esattamente la stessa cosa. Evitando gli
//     header privati il modulo non si rompe a ogni upgrade di engine.
// ============================================================================

using UnrealBuildTool;
using System.IO;

public class GpuShareSpike : ModuleRules
{
	public GpuShareSpike(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = ModuleRules.PCHUsageMode.UseExplicitOrSharedPCHs;

		PublicDependencyModuleNames.AddRange(new string[]
		{
			"Core",
			"CoreUObject",
			"Engine",
			"InputCore",
		});

		PrivateDependencyModuleNames.AddRange(new string[]
		{
			"RenderCore",         // ENQUEUE_RENDER_COMMAND
			"RHI",                // FRHITexture, GDynamicRHI
			"Projects",
			"Sockets",            // ISocketSubsystem, FSocket
			"Networking",         // FUdpSocketBuilder, FIPv4Endpoint
			"DeveloperSettings",  // UDeveloperSettings -> pagina in Project Settings
		});

		// Il protocollo binario vive fuori dal progetto Unreal perche' e'
		// condiviso letteralmente col plugin nativo di Unity.
		// ModuleDirectory = <repo>/unreal/GpuShareSpike/Source/GpuShareSpike
		PublicIncludePaths.Add(Path.GetFullPath(
			Path.Combine(ModuleDirectory, "..", "..", "..", "..", "shared")));

		if (Target.Platform == UnrealTargetPlatform.Win64)
		{
			PublicSystemLibraries.AddRange(new string[]
			{
				"d3d11.lib",
				"dxgi.lib",
				"dxguid.lib",   // per gli __uuidof / IID_* usati nelle QueryInterface
			});
		}
	}
}
