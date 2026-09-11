// ============================================================================
//  Ponte verso UnityGpuShare.dll.
//
//  Rispecchia unity-native/src/Exports.h. Se cambi una firma di la', cambiala
//  anche qui: un mismatch di P/Invoke non da' errore di compilazione, da'
//  corruzione dello stack a runtime.
//
//  REGOLA DI THREAD: tutte queste chiamate si fanno dal MAIN THREAD di Unity.
//  Il lavoro D3D11 avviene solo dentro la callback restituita da
//  GetRenderEventFunc(), che Unity esegue sul render thread quando chiami
//  GL.IssuePluginEvent.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace GpuShareSpike
{
    public static class NativeBridge
    {
        public const string DllName = "UnityGpuShare";

        /// <summary>Versione della ABI attesa dal C#. Deve combaciare con GpuShare_GetApiVersion().</summary>
        public const int ExpectedApiVersion = 1;

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GpuShare_GetApiVersion();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GpuShare_IsDeviceReady();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GpuShare_GetAdapterLuid(out uint outLow, out int outHigh);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GpuShare_Configure(
            [In] NativeChannelDesc[] descs, int count, uint handleMode,
            [MarshalAs(UnmanagedType.LPStr)] string namePrefix);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GpuShare_IsConfigured();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr GpuShare_GetUnityTexturePtr(uint channelId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GpuShare_GetChannelSize(uint channelId, out uint outWidth, out uint outHeight);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void GpuShare_SetGroupReady(uint groupId, uint readyIndex, uint sequence);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void GpuShare_SetFrameContext(ulong unityFrameIndex);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void GpuShare_SetConsumeTimeoutMs(uint timeoutMs);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr GpuShare_GetRenderEventFunc();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GpuShare_GetLatestFrameInfo(out NativeFrameInfo outInfo);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GpuShare_GetStats(out NativeStats outStats);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int GpuShare_GetLastError(StringBuilder buffer, int bufferSize);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void GpuShare_Shutdown();

        public static string GetLastError()
        {
            var buffer = new StringBuilder(1024);
            GpuShare_GetLastError(buffer, buffer.Capacity);
            return buffer.ToString();
        }
    }
}
