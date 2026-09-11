// ============================================================================
//  Specchio C# di shared/ShareProtocol.h e unity-native/src/NativeApi.h.
//
//  DUE STRATEGIE DIVERSE, E IL MOTIVO CONTA:
//
//  1) Pacchetti UDP -> lettura/scrittura MANUALE a offset espliciti.
//     Niente Marshal.PtrToStructure, niente [MarshalAs(ByValArray)]. Quelle
//     strade funzionano ma nascondono il layout dietro regole di marshalling
//     che cambiano tra runtime e piattaforme. Qui il layout e' scritto nero su
//     bianco, byte per byte, ed e' verificabile a occhio contro l'header C.
//     BinaryPrimitives rende anche esplicito il little endian invece di
//     affidarsi all'endianness della macchina.
//
//  2) Struct passate al plugin nativo -> struct blittabili [Pack=1].
//     Qui servono davvero, perche' attraversano la ABI. Sono tenute prive di
//     array a dimensione fissa (gli uint64[2] del C diventano due campi
//     separati con lo stesso identico layout) cosi' restano blittabili senza
//     bisogno di codice unsafe.
//
//  SelfTest() confronta le dimensioni calcolate con le costanti dell'header C:
//  se qualcuno cambia il protocollo da un lato solo, si accorge all'avvio e non
//  dopo mezza giornata di pixel sbagliati.
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace GpuShareSpike
{
    public static class Protocol
    {
        public const uint  Magic   = 0x47535031u;  // 'GSP1'
        public const ushort Version = 1;

        public const uint MarkerMagic  = 0x4D524B31u;  // 'MRK1'
        public const int  MarkerPixels = 8;
        public const int  MarkerBytes  = 32;

        public const int MaxChannels        = 4;
        public const int BuffersPerChannel  = 2;
        public const int GroupCount         = 2;

        public const ulong KeyProducer = 0;
        public const ulong KeyConsumer = 1;

        public const int DefaultUePort = 45001;

        // Dimensioni dei pacchetti, da tenere allineate con gli static_assert C.
        public const int HeaderSize      = 8;
        public const int HelloSize       = 24;
        public const int ChannelDescSize = 40;
        public const int HandshakeSize   = 256;
        public const int PoseSize        = 68;
        public const int GroupStatusSize = 40;
        public const int StatusSize      = 256;
        public const int ByeSize         = 16;

        public enum PacketType : ushort
        {
            Hello     = 1,
            Handshake = 2,
            Pose      = 3,
            Status    = 4,
            Bye       = 5,
        }

        public enum ChannelId : uint
        {
            Color = 0,
            Depth = 1,
            Cube  = 2,
        }

        public enum GroupId : uint
        {
            Main = 0,
            Cube = 1,
        }

        [Flags]
        public enum ChannelFlags : uint
        {
            None      = 0,
            HasMarker = 1u << 0,
        }

        [Flags]
        public enum HelloFlags : uint
        {
            None      = 0,
            WantDepth = 1u << 0,
            WantCube  = 1u << 1,
        }

        public enum HandleMode : uint
        {
            Duplicated = 0,
            Named      = 1,
        }

        public enum RenderEvent
        {
            Configure   = 1,
            ConsumeMain = 2,
            ConsumeCube = 3,
            Shutdown    = 4,
        }

        // --------------------------------------------------------------------
        //  Header
        // --------------------------------------------------------------------

        public static void WriteHeader(Span<byte> buffer, PacketType type)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(0, 4), Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(4, 2), Version);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(6, 2), (ushort)type);
        }

        public static bool TryReadHeader(ReadOnlySpan<byte> buffer, out PacketType type)
        {
            type = default;
            if (buffer.Length < HeaderSize) return false;

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(0, 4));
            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(4, 2));
            if (magic != Magic || version != Version) return false;

            type = (PacketType)BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(6, 2));
            return true;
        }

        // --------------------------------------------------------------------
        //  HELLO  (Unity -> Unreal)
        // --------------------------------------------------------------------

        public static int WriteHello(Span<byte> buffer, uint unityPid, uint luidLow, int luidHigh, HelloFlags flags)
        {
            WriteHeader(buffer, PacketType.Hello);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(8, 4),  unityPid);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(12, 4), luidLow);
            BinaryPrimitives.WriteInt32LittleEndian (buffer.Slice(16, 4), luidHigh);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(20, 4), (uint)flags);
            return HelloSize;
        }

        // --------------------------------------------------------------------
        //  BYE  (Unity -> Unreal)
        // --------------------------------------------------------------------

        public static int WriteBye(Span<byte> buffer, uint unityPid)
        {
            WriteHeader(buffer, PacketType.Bye);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(8, 4),  unityPid);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(12, 4), 0);
            return ByeSize;
        }

        // --------------------------------------------------------------------
        //  POSE  (Unity -> Unreal)
        //
        //  I valori sono in SPAZIO UNITY (metri, Y-up). E' Unreal a convertire,
        //  in un unico punto del suo codice.
        // --------------------------------------------------------------------

        public static int WritePose(
            Span<byte> buffer, ulong frameId, long qpc,
            float px, float py, float pz,
            float qx, float qy, float qz, float qw,
            float fovYDeg, float aspect, float nearM, float farM)
        {
            WriteHeader(buffer, PacketType.Pose);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(8, 8),  frameId);
            BinaryPrimitives.WriteInt64LittleEndian (buffer.Slice(16, 8), qpc);

            WriteSingle(buffer, 24, px);
            WriteSingle(buffer, 28, py);
            WriteSingle(buffer, 32, pz);

            WriteSingle(buffer, 36, qx);
            WriteSingle(buffer, 40, qy);
            WriteSingle(buffer, 44, qz);
            WriteSingle(buffer, 48, qw);

            WriteSingle(buffer, 52, fovYDeg);
            WriteSingle(buffer, 56, aspect);
            WriteSingle(buffer, 60, nearM);
            WriteSingle(buffer, 64, farM);
            return PoseSize;
        }

        // --------------------------------------------------------------------
        //  HANDSHAKE  (Unreal -> Unity)
        // --------------------------------------------------------------------

        public struct HandshakeInfo
        {
            public uint UePid;
            public uint AdapterLuidLow;
            public int  AdapterLuidHigh;
            public HandleMode Mode;
            public string NamePrefix;
            public NativeChannelDesc[] Channels;
        }

        public static bool TryReadHandshake(ReadOnlySpan<byte> buffer, out HandshakeInfo info)
        {
            info = default;
            if (buffer.Length < HandshakeSize) return false;

            info.UePid           = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8, 4));
            info.AdapterLuidLow  = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(12, 4));
            info.AdapterLuidHigh = BinaryPrimitives.ReadInt32LittleEndian (buffer.Slice(16, 4));

            uint channelCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(20, 4));
            info.Mode          = (HandleMode)BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(24, 4));
            // offset 28: reserved0

            info.NamePrefix = ReadFixedAscii(buffer.Slice(32, 64));

            if (channelCount > MaxChannels) channelCount = MaxChannels;
            info.Channels = new NativeChannelDesc[channelCount];

            for (int i = 0; i < channelCount; ++i)
            {
                int o = 96 + i * ChannelDescSize;
                info.Channels[i] = new NativeChannelDesc
                {
                    ChannelId  = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(o + 0, 4)),
                    GroupId    = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(o + 4, 4)),
                    Width      = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(o + 8, 4)),
                    Height     = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(o + 12, 4)),
                    DxgiFormat = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(o + 16, 4)),
                    Flags      = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(o + 20, 4)),
                    SharedHandle0 = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(o + 24, 8)),
                    SharedHandle1 = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(o + 32, 8)),
                };
            }

            return true;
        }

        // --------------------------------------------------------------------
        //  STATUS  (Unreal -> Unity)
        // --------------------------------------------------------------------

        public struct GroupStatus
        {
            public uint  GroupId;
            public uint  ReadyIndex;
            public ulong FrameId;
            public long  QpcPoseSend;
            public long  QpcRenderEnd;
            public uint  Sequence;
            public bool  Valid;
        }

        public struct StatusInfo
        {
            public ulong UeFrameCounter;
            public long  Qpc;
            public GroupStatus[] Groups;
            public float AppliedFovYDeg;
            public float AppliedNearCm;
            public float RenderFovYDeg;
            public float DepthScaleToMeters;
        }

        public static bool TryReadStatus(ReadOnlySpan<byte> buffer, out StatusInfo info)
        {
            info = default;
            if (buffer.Length < StatusSize) return false;

            info.UeFrameCounter = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(8, 8));
            info.Qpc            = BinaryPrimitives.ReadInt64LittleEndian (buffer.Slice(16, 8));
            // offset 24: group_count, 28: reserved0

            info.Groups = new GroupStatus[GroupCount];
            for (int i = 0; i < GroupCount; ++i)
            {
                int o = 32 + i * GroupStatusSize;
                info.Groups[i] = new GroupStatus
                {
                    GroupId      = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(o + 0, 4)),
                    ReadyIndex   = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(o + 4, 4)),
                    FrameId      = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(o + 8, 8)),
                    QpcPoseSend  = BinaryPrimitives.ReadInt64LittleEndian (buffer.Slice(o + 16, 8)),
                    QpcRenderEnd = BinaryPrimitives.ReadInt64LittleEndian (buffer.Slice(o + 24, 8)),
                    Sequence     = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(o + 32, 4)),
                    Valid        = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(o + 36, 4)) != 0,
                };
            }

            // offset 112: ue_view_matrix[16], 176: ue_proj_matrix[16].
            // Sono DIAGNOSTICHE, in convenzione Unreal: non le usiamo per
            // ricostruire la camera (vedi il commento in ShareProtocol.h).

            info.AppliedFovYDeg     = ReadSingle(buffer, 240);
            info.AppliedNearCm      = ReadSingle(buffer, 244);
            info.RenderFovYDeg      = ReadSingle(buffer, 248);
            info.DepthScaleToMeters = ReadSingle(buffer, 252);
            return true;
        }

        // --------------------------------------------------------------------
        //  Helper
        // --------------------------------------------------------------------

        // NOTA: si usano le varianti Int32 e non SingleToUInt32Bits perche'
        // quelle uint sono arrivate con .NET 6, mentre Unity 2022 e' su
        // .NET Standard 2.1. Il pattern di bit e' identico.
        private static void WriteSingle(Span<byte> buffer, int offset, float value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                buffer.Slice(offset, 4), BitConverter.SingleToInt32Bits(value));
        }

        private static float ReadSingle(ReadOnlySpan<byte> buffer, int offset)
        {
            return BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, 4)));
        }

        private static string ReadFixedAscii(ReadOnlySpan<byte> buffer)
        {
            int length = 0;
            while (length < buffer.Length && buffer[length] != 0) ++length;
            return System.Text.Encoding.UTF8.GetString(buffer.Slice(0, length).ToArray());
        }

        /// <summary>
        /// Verifica che le struct native abbiano la dimensione attesa.
        /// Chiamata all'avvio: un protocollo disallineato deve fallire subito e
        /// rumorosamente, non produrre pixel sbagliati in silenzio.
        /// </summary>
        public static bool SelfTest(out string error)
        {
            error = null;

            int descSize   = Marshal.SizeOf<NativeChannelDesc>();
            int markerSize = Marshal.SizeOf<NativeMarker>();
            int infoSize   = Marshal.SizeOf<NativeFrameInfo>();
            int statsSize  = Marshal.SizeOf<NativeStats>();

            if (descSize != ChannelDescSize)
                error = $"NativeChannelDesc e' {descSize} byte, atteso {ChannelDescSize}";
            else if (markerSize != MarkerBytes)
                error = $"NativeMarker e' {markerSize} byte, atteso {MarkerBytes}";
            else if (infoSize != 56)
                error = $"NativeFrameInfo e' {infoSize} byte, atteso 56";
            else if (statsSize != 48)
                error = $"NativeStats e' {statsSize} byte, atteso 48";
            else if (!System.Diagnostics.Stopwatch.IsHighResolution)
                error = "Stopwatch non e' ad alta risoluzione: i timestamp non sarebbero QPC";

            return error == null;
        }
    }

    // ------------------------------------------------------------------------
    //  Struct blittabili verso il plugin nativo.
    //  Pack = 1 per combaciare con #pragma pack(1) del lato C.
    // ------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NativeChannelDesc
    {
        public uint  ChannelId;
        public uint  GroupId;
        public uint  Width;
        public uint  Height;
        public uint  DxgiFormat;
        public uint  Flags;
        // uint64_t shared_handles[2] del C, espanso in due campi: stesso layout,
        // ma la struct resta blittabile senza codice unsafe.
        public ulong SharedHandle0;
        public ulong SharedHandle1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NativeMarker
    {
        public uint  Magic;
        public ulong FrameId;
        public long  QpcPoseSend;
        public long  QpcRenderEnd;
        public uint  Checksum;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NativeFrameInfo
    {
        public NativeMarker Marker;
        public long  QpcConsume;          // vedi NativeApi.h: e' IL numero che conta
        public ulong UnityFrameIndex;
        public uint  MarkerValid;
        public uint  ReadbackLagEvents;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NativeStats
    {
        public ulong ConsumeAttempts;
        public ulong ConsumeSuccess;
        public ulong AcquireTimeouts;
        public ulong MarkerReads;
        public ulong MarkerInvalid;
        public uint  DeviceReady;
        public uint  ChannelsOpen;
    }
}
