// license:BSD-3-Clause
// copyright-holders:Edward Fast
// Skeleton Heavy Smash Havoc registration for Euther Drive MCS bring-up.

using System;
using System.Collections.Generic;
using System.IO;

using device_type = mame.emu.detail.device_type_impl_base;
using MemoryU8 = mame.MemoryContainer<System.Byte>;
using offs_t = System.UInt32;
using u8 = System.Byte;
using u16 = System.UInt16;
using u32 = System.UInt32;
using uint32_t = System.UInt32;

using static mame.diexec_global;
using static mame.emucore_global;
using static mame.emumem_global;
using static mame.gamedrv_global;
using static mame.hash_global;
using static mame.ioport_global;
using static mame.ioport_ioport_type_helper;
using static mame.m68000_global;
using static mame.romentry_global;
using static mame.screen_global;


namespace mame
{
    class hshavoc_state : driver_device
    {
        const int InterleavedSize = 0x100000;
        const int BaseDecodeEnd = 0x0e8000;
        const int VramSize = 0x10000;
        const uint BoardRamStart = 0x00200000;
        const uint BoardRamEnd = 0x002023ff;
        const uint WorkRamStart = 0x00ff0000;
        const uint WorkRamEnd = 0x00ffffff;
        const uint AckWordAddress = 0x00fff906;
        const uint VdpStatusWordAddress = 0x00fffe00;
        const uint LatchedVdpQueueBlock = 0x00ffe91a;
        const uint VdpCommandBlockStart = 0x00ffe900;
        const uint VdpCommandBlockEnd = 0x00ffea80;
        const uint MdPixelOpaque = 0x80000000U;
        const uint MdPixelPriority = 0x40000000U;
        const uint MdPixelColorMask = 0x00ffffffU;
        static readonly bool TraceMcsVdp =
            IsEnvEnabled("EUTHERDRIVE_HSHAVOC_MCS_TRACE_VDP");
        static readonly bool TraceMcsVdpQueue =
            IsEnvEnabled("EUTHERDRIVE_HSHAVOC_MCS_TRACE_VDP_QUEUE");
        static readonly int TraceMcsVdpStartFrame =
            ParseEnvInt("EUTHERDRIVE_HSHAVOC_MCS_TRACE_VDP_START_FRAME", -1);
        static readonly int TraceMcsVdpEndFrame =
            ParseEnvInt("EUTHERDRIVE_HSHAVOC_MCS_TRACE_VDP_END_FRAME", int.MaxValue);
        static readonly int TraceMcsVdpQueueStartFrame =
            ParseEnvInt("EUTHERDRIVE_HSHAVOC_MCS_TRACE_VDP_QUEUE_START_FRAME", TraceMcsVdpStartFrame);
        static readonly int TraceMcsVdpQueueEndFrame =
            ParseEnvInt("EUTHERDRIVE_HSHAVOC_MCS_TRACE_VDP_QUEUE_END_FRAME", TraceMcsVdpEndFrame);
        static readonly bool TraceMcsIo =
            IsEnvEnabled("EUTHERDRIVE_HSHAVOC_MCS_TRACE_IO");
        static readonly int TraceMcsIoStartFrame =
            ParseEnvInt("EUTHERDRIVE_HSHAVOC_MCS_TRACE_IO_START_FRAME", -1);
        static readonly int TraceMcsIoEndFrame =
            ParseEnvInt("EUTHERDRIVE_HSHAVOC_MCS_TRACE_IO_END_FRAME", int.MaxValue);
        static readonly bool TraceMcsRam =
            IsEnvEnabled("EUTHERDRIVE_HSHAVOC_MCS_TRACE_RAM");
        static readonly int TraceMcsRamStart =
            ParseEnvInt("EUTHERDRIVE_HSHAVOC_MCS_TRACE_RAM_START", (int)WorkRamStart);
        static readonly int TraceMcsRamEnd =
            ParseEnvInt("EUTHERDRIVE_HSHAVOC_MCS_TRACE_RAM_END", (int)WorkRamEnd);
        static readonly int TraceMcsRamStartFrame =
            ParseEnvInt("EUTHERDRIVE_HSHAVOC_MCS_TRACE_RAM_START_FRAME", -1);
        static readonly int TraceMcsRamEndFrame =
            ParseEnvInt("EUTHERDRIVE_HSHAVOC_MCS_TRACE_RAM_END_FRAME", int.MaxValue);
        static readonly bool LowPatternRamProbe =
            IsEnvEnabled("EUTHERDRIVE_HSHAVOC_MCS_LOW_PATTERN_PROBE");
        static readonly int LowPatternRamProbeWords =
            ParseEnvInt("EUTHERDRIVE_HSHAVOC_MCS_LOW_PATTERN_PROBE_WORDS", 0x400);
        static readonly bool AdditiveHscroll =
            IsEnvEnabled("EUTHERDRIVE_HSHAVOC_MCS_SCROLL_ADD");
        static readonly bool SwapVramBytes =
            IsEnvEnabled("EUTHERDRIVE_HSHAVOC_MCS_VRAM_SWAP_BYTES");
        static readonly bool ForceH40 =
            IsEnvEnabled("EUTHERDRIVE_HSHAVOC_MCS_FORCE_H40");
        static readonly int SnapshotFrame =
            ParseEnvInt("EUTHERDRIVE_HSHAVOC_MCS_SNAPSHOT_FRAME", -1);
        static readonly string SnapshotDir =
            Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_MCS_SNAPSHOT_DIR") ?? "/tmp";

        static readonly int [] DataBitswap =
        {
            7, 15, 6, 14, 5, 2, 1, 10, 13, 4, 12, 3, 11, 0, 8, 9
        };

        static readonly int [] TailBitswap =
        {
            7, 15, 6, 14, 5, 2, 1, 0, 13, 4, 12, 3, 11, 10, 9, 8
        };

        static readonly int [] Typedat =
        {
            1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 1, 1, 0, 1, 1
        };

        static readonly (int Address, u16 Value) [] BestStartupPatch =
        {
            (0x0c42, 0x007c), (0x0c44, 0x0700), (0x0c46, 0x4eb9), (0x0c48, 0x0000),
            (0x0c4a, 0x109c), (0x0c4c, 0x4e71), (0x0c4e, 0x4e71), (0x0c50, 0x4e71),
            (0x0c52, 0x4e71), (0x0c54, 0x4e71), (0x0c56, 0x4e71), (0x0c58, 0x4e71),
            (0x0c5a, 0x4e71), (0x0c5c, 0x4e71), (0x0c5e, 0x4e71), (0x0c60, 0x4e71),
            (0x0c62, 0x4e71), (0x0c64, 0x4eb9), (0x0c66, 0x0000), (0x0c68, 0x10f8),
            (0x0c6a, 0x4eb9), (0x0c6c, 0x0000), (0x0c6e, 0x10f8), (0x0c70, 0x4eb9),
            (0x0c72, 0x0000), (0x0c74, 0x1332), (0x0c76, 0x4e71), (0x0c78, 0x4e71),
            (0x0c7a, 0x4e71), (0x0c7c, 0x4e71), (0x0c7e, 0x4e71), (0x0c80, 0x4e71),
            (0x0c82, 0x4e71), (0x0c84, 0x4e71), (0x0c86, 0x4e71), (0x0c88, 0x4e71),
            (0x0c8a, 0x4e71), (0x0c8c, 0x4e71), (0x0c8e, 0x4e71), (0x0c90, 0x4e71),
            (0x0c92, 0x4e71), (0x0c94, 0x4eb9), (0x0c96, 0x0000), (0x0c98, 0x0a1c),
            (0x0c9a, 0x4eb9), (0x0c9c, 0x000d), (0x0c9e, 0x0000), (0x0ca0, 0x4eb9),
            (0x0ca2, 0x000d), (0x0ca4, 0x0682), (0x0ca6, 0x4eb9), (0x0ca8, 0x000d),
            (0x0caa, 0x0692), (0x0cac, 0x4eb9), (0x0cae, 0x000d), (0x0cb0, 0x06d6),
            (0x0cb2, 0x027c), (0x0cb4, 0xf8ff), (0x0cb6, 0x4ef9), (0x0cb8, 0x0000),
            (0x0cba, 0x1126), (0x065e, 0x4e71), (0x0660, 0x4e71), (0x0662, 0x4e71),
            (0x0664, 0x4e71), (0x0666, 0x4e71), (0xd05ca, 0x4e71), (0xd05cc, 0x4e71),
            (0xd05ce, 0x4e71), (0xd05d0, 0x4e71), (0xd05d2, 0x4e71), (0x0a30, 0x4e71),
            (0x0a32, 0x4e71), (0x0a34, 0x4e71), (0x0a36, 0x4e71)
        };

        static readonly (int Address, u16 Value) [] InputIllegalBridgePatch =
        {
            (0x03d004, 0x6100), (0x03d00c, 0x6100), (0x03d024, 0x6100), (0x03d044, 0x6100),
            (0x03d008, 0x4e75), (0x03d010, 0x4e75), (0x03d01e, 0x4e75), (0x03d02e, 0x4e75),
            (0x03d040, 0x4e75), (0x03d046, 0x4e75), (0x03d04a, 0x4e75), (0x03d056, 0x4e75),
            (0x03d054, 0x4e75), (0x03d094, 0x4e75), (0x03d304, 0x6100), (0x03d30c, 0x6100),
            (0x03d314, 0x6100), (0x03d324, 0x6100), (0x03d334, 0x6100), (0x03d344, 0x6100),
            (0x03d308, 0x4e75), (0x03d310, 0x4e75), (0x03d320, 0x4e75), (0x03d330, 0x4e75),
            (0x03d340, 0x4e75), (0x03d346, 0x4e75), (0x03d306, 0x6100), (0x03d30e, 0x6100),
            (0x03d316, 0x6100), (0x03d326, 0x6100), (0x03d336, 0x6100)
        };

        readonly required_device<m68000_device> m_maincpu;
        readonly u8 [] m_vram = new u8[VramSize];
        readonly u32 [] m_vram_last_source = new u32[VramSize / 2];
        readonly u32 [] m_vram_last_pc = new u32[VramSize / 2];
        readonly int [] m_vram_last_frame = new int[VramSize / 2];
        readonly u16 [] m_cram = new u16[0x40];
        readonly u16 [] m_vsram = new u16[0x40];
        readonly u8 [] m_vdp_reg = new u8[0x20];
        readonly u8 [] m_board_ram = new u8[BoardRamEnd - BoardRamStart + 1];
        readonly u8 [] m_work_ram = new u8[WorkRamEnd - WorkRamStart + 1];
        readonly u8 [] m_io_data = { 0x7f, 0x7f, 0x7f };
        readonly u8 [] m_io_ctrl = { 0x00, 0x00, 0x00 };
        readonly Dictionary<uint, ulong> m_flushed_vdp_command_blocks = new Dictionary<uint, ulong>();
        bool m_z80_bus_requested;
        bool m_z80_reset_asserted = true;
        uint m_latched_slot0_source;
        u16 m_latched_slot0_destination;
        u16 m_latched_slot0_length;
        bool m_latched_slot0_active;
        int m_vdp_code;
        u16 m_vdp_address;
        bool m_vdp_command_select;
        u16 m_vdp_command_word;
        bool m_vdp_dma_fill_pending;
        int m_frame_counter;
        int m_ctrl_writes_this_frame;
        int m_data_writes_this_frame;
        int m_vram_writes_this_frame;
        int m_cram_writes_this_frame;
        int m_vsram_writes_this_frame;
        int m_dma_memory_this_frame;
        int m_dma_fill_this_frame;
        int m_dma_copy_this_frame;
        ulong m_low_pattern_probe_signature;
        bool m_snapshot_dumped;
        int m_external_start_frames;
        int m_external_coin_frames;
        u32 m_current_vram_write_source = 0xffffffff;


        public hshavoc_state(machine_config mconfig, device_type type, string tag)
            : base(mconfig, type, tag)
        {
            m_maincpu = new required_device<m68000_device>(this, "maincpu");
        }


        public void SetExternalInputState(bool start, bool coin)
        {
            if (start)
                m_external_start_frames = Math.Max(m_external_start_frames, 12);
            if (coin)
                m_external_coin_frames = Math.Max(m_external_coin_frames, 12);
        }


        void main_map(address_map map, device_t device)
        {
            map.op(0x000000, 0x0fffff).rom();
            map.op(BoardRamStart, BoardRamEnd).rw((read16_delegate)board_ram_r, (write16_delegate)board_ram_w);
            map.op(0xa10000, 0xa1001f).rw((read16_delegate)io_r, (write16_delegate)io_w);
            map.op(0xa11100, 0xa11201).rw((read16_delegate)z80_control_r, (write16_delegate)z80_control_w);
            map.op(0xc00000, 0xc0001f).rw((read16_delegate)vdp_r, (write16_delegate)vdp_w);
            map.op(WorkRamStart, WorkRamEnd).rw((read16_delegate)work_ram_r, (write16_delegate)work_ram_w);
        }


        void vblank_irq(int state)
        {
            if (state != 0)
                m_maincpu.op0.set_input_line(6, HOLD_LINE);
        }


        public void hshavoc(machine_config config)
        {
            M68000(config, m_maincpu, new XTAL(53_693_175) / 7);
            m_maincpu.op0.memory().set_addrmap(AS_PROGRAM, main_map);

            screen_device screen = SCREEN(config, "screen", SCREEN_TYPE_RASTER);
            screen.set_screen_update(screen_update);
            screen.set_refresh_hz(60);
            screen.set_size(320, 224);
            screen.set_visarea(0, 319, 0, 223);
            screen.screen_vblank().set((write_line_delegate)vblank_irq).reg();
        }


        uint32_t screen_update(screen_device screen, bitmap_rgb32 bitmap, rectangle cliprect)
        {
            FlushVdpCommandBlocks();
            ReplayLowPatternRamProbeIfRequested();
            RenderMegadrivePlanes(bitmap, cliprect);
            TraceVdpFrame();
            DumpSnapshotIfRequested();
            ResetVdpFrameCounters();
            DecayExternalInputLatch();
            m_frame_counter++;
            return 0;
        }


        protected override void machine_reset()
        {
            Array.Clear(m_vram, 0, m_vram.Length);
            Array.Clear(m_cram, 0, m_cram.Length);
            Array.Clear(m_vsram, 0, m_vsram.Length);
            Array.Clear(m_vdp_reg, 0, m_vdp_reg.Length);
            Array.Clear(m_board_ram, 0, m_board_ram.Length);
            Array.Clear(m_work_ram, 0, m_work_ram.Length);
            m_vdp_reg[15] = 2;
            Array.Fill(m_io_data, (u8)0x7f);
            Array.Clear(m_io_ctrl, 0, m_io_ctrl.Length);
            m_flushed_vdp_command_blocks.Clear();
            m_z80_bus_requested = false;
            m_z80_reset_asserted = true;
            m_latched_slot0_source = 0;
            m_latched_slot0_destination = 0;
            m_latched_slot0_length = 0;
            m_latched_slot0_active = false;
            m_vdp_code = 0;
            m_vdp_address = 0;
            m_vdp_command_select = false;
            m_vdp_command_word = 0;
            m_vdp_dma_fill_pending = false;
            m_low_pattern_probe_signature = 0;
            m_frame_counter = 0;
            ResetVdpFrameCounters();

            m_maincpu.op0.reset_from_bus();
        }


        u16 work_ram_r(address_space space, offs_t offset, u16 mem_mask)
        {
            uint byteOffset = offset << 1;
            if (byteOffset >= m_work_ram.Length)
                return 0xffff;

            uint address = WorkRamStart + byteOffset;
            if (address == AckWordAddress && (m_maincpu.op0.Pc & 0x00ffffff) == 0x000ac2)
                return 0x0001;

            u16 value = (u16)((m_work_ram[byteOffset] << 8) | m_work_ram[(byteOffset + 1) & 0xffff]);
            if (address == VdpStatusWordAddress && value == 0)
                return 0x8164;

            if (ShouldTraceMcsRam(address))
            {
                var state = m_maincpu.op0.GetState();
                Console.Error.WriteLine(
                    $"[HSH-MCS-RAM-R] frame={m_frame_counter} pc=0x{m_maincpu.op0.Pc:X6} " +
                    $"addr=0x{address:X6} value=0x{value:X4} mask=0x{mem_mask:X4} " +
                    $"a0=0x{state.Address[0]:X6} a1=0x{state.Address[1]:X6} a2=0x{state.Address[2]:X6} " +
                    $"d0=0x{state.Data[0]:X8} d7=0x{state.Data[7]:X8}");
            }

            return value;
        }


        void work_ram_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            uint byteOffset = offset << 1;
            if (byteOffset >= m_work_ram.Length)
                return;

            if ((mem_mask & 0xff00) != 0)
                m_work_ram[byteOffset] = (u8)(data >> 8);
            if ((mem_mask & 0x00ff) != 0)
                m_work_ram[(byteOffset + 1) & 0xffff] = (u8)data;

            uint address = WorkRamStart + byteOffset;
            if (ShouldTraceMcsRam(address))
            {
                u16 value = (u16)((m_work_ram[byteOffset] << 8) | m_work_ram[(byteOffset + 1) & 0xffff]);
                var state = m_maincpu.op0.GetState();
                Console.Error.WriteLine(
                    $"[HSH-MCS-RAM-W] frame={m_frame_counter} pc=0x{m_maincpu.op0.Pc:X6} " +
                    $"addr=0x{address:X6} data=0x{data:X4} value=0x{value:X4} mask=0x{mem_mask:X4} " +
                    $"a0=0x{state.Address[0]:X6} a1=0x{state.Address[1]:X6} a2=0x{state.Address[2]:X6} " +
                    $"d0=0x{state.Data[0]:X8} d7=0x{state.Data[7]:X8}");
            }

            if (address != AckWordAddress)
            {
                LatchVdpQueueParameter(address, data, m_maincpu.op0.Pc & 0x00ffffff);
                if (ShouldTraceMcsVdpQueue() && address >= 0x00ffe800 && address <= 0x00ffe820 && data != 0)
                    Console.Error.WriteLine($"[HSH-MCS-RAM-W] pc=0x{m_maincpu.op0.Pc:X6} addr=0x{address:X6} data=0x{data:X4} mask=0x{mem_mask:X4}");
            }
            else if (IsQueueAckWritePc(m_maincpu.op0.Pc & 0x00ffffff))
            {
                FlushLatchedVdpQueueOnAckIfRequested();
                FlushVdpCommandBlocks();
            }
            else if (ShouldTraceMcsVdpQueue())
            {
                Console.Error.WriteLine(
                    $"[HSH-MCS-ACK-W] frame={m_frame_counter} pc=0x{m_maincpu.op0.Pc:X6} " +
                    $"data=0x{data:X4} mask=0x{mem_mask:X4} active={(m_latched_slot0_active ? 1 : 0)} " +
                    $"source=0x{m_latched_slot0_source:X6} dest=0x{m_latched_slot0_destination:X4} len=0x{m_latched_slot0_length:X4}");
            }
        }


        u16 board_ram_r(address_space space, offs_t offset, u16 mem_mask)
        {
            uint byteOffset = offset << 1;
            return (u16)((m_board_ram[byteOffset] << 8) | m_board_ram[(byteOffset + 1) % m_board_ram.Length]);
        }


        void board_ram_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            uint byteOffset = offset << 1;
            if (byteOffset >= m_board_ram.Length)
                return;

            if ((mem_mask & 0xff00) != 0)
                m_board_ram[byteOffset] = (u8)(data >> 8);
            if ((mem_mask & 0x00ff) != 0 && byteOffset + 1 < m_board_ram.Length)
                m_board_ram[byteOffset + 1] = (u8)data;
        }


        u16 io_r(address_space space, offs_t offset, u16 mem_mask)
        {
            uint port = (offset << 1) & 0x1f;
            u8 value = port switch
            {
                0x00 => 0xa0,
                0x02 => MegadrivePadByte(0),
                0x04 => MegadrivePadByte(1),
                0x06 => SystemPortByte(),
                0x08 => m_io_ctrl[0],
                0x0a => m_io_ctrl[1],
                0x0c => m_io_ctrl[2],
                0x0e => (u8)(ioport("SYSTEM").read() & 0xff),
                _ => 0xff
            };

            if (ShouldTraceMcsIo(port))
                Console.Error.WriteLine(
                    $"[HSH-MCS-IO-R] frame={m_frame_counter} pc=0x{m_maincpu.op0.Pc:X6} port=0x{port:X2} value=0x{value:X2} " +
                    $"ext_start={m_external_start_frames} ext_coin={m_external_coin_frames}");

            return (u16)(0xff00 | value);
        }


        void io_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            uint port = (offset << 1) & 0x1f;
            u8 value = (u8)data;
            switch (port)
            {
                case 0x02:
                    m_io_data[0] = value;
                    break;
                case 0x04:
                    m_io_data[1] = value;
                    // The arcade input routine toggles this latch before sampling P1.
                    // P2 is currently unused in this skeleton, so mirror it to P1.
                    m_io_data[0] = value;
                    break;
                case 0x06:
                    m_io_data[2] = value;
                    break;
                case 0x08:
                    m_io_ctrl[0] = value;
                    break;
                case 0x0a:
                    m_io_ctrl[1] = value;
                    break;
                case 0x0c:
                    m_io_ctrl[2] = value;
                    break;
            }

            if (ShouldTraceMcsIo(port))
                Console.Error.WriteLine($"[HSH-MCS-IO-W] frame={m_frame_counter} pc=0x{m_maincpu.op0.Pc:X6} port=0x{port:X2} data=0x{value:X2}");
        }


        bool ShouldTraceMcsIo(uint port)
        {
            if (port > 0x0e)
                return false;
            if (ShouldTraceMcsVdpQueue())
                return true;
            if (!TraceMcsIo)
                return false;
            int frame = m_frame_counter;
            if (TraceMcsIoStartFrame >= 0 && frame < TraceMcsIoStartFrame)
                return false;
            return frame <= TraceMcsIoEndFrame;
        }


        bool ShouldTraceMcsVdp()
            => TraceMcsVdp && IsFrameInTraceRange(TraceMcsVdpStartFrame, TraceMcsVdpEndFrame);


        bool ShouldTraceMcsVdpQueue()
            => TraceMcsVdpQueue && IsFrameInTraceRange(TraceMcsVdpQueueStartFrame, TraceMcsVdpQueueEndFrame);


        bool ShouldTraceMcsRam(uint address)
        {
            if (!TraceMcsRam)
                return false;
            if (!IsFrameInTraceRange(TraceMcsRamStartFrame, TraceMcsRamEndFrame))
                return false;
            return address >= (uint)TraceMcsRamStart && address <= (uint)TraceMcsRamEnd;
        }


        bool IsFrameInTraceRange(int startFrame, int endFrame)
        {
            int frame = m_frame_counter;
            if (startFrame >= 0 && frame < startFrame)
                return false;
            return frame <= endFrame;
        }


        u8 MegadrivePadByte(int player)
        {
            u8 p = (u8)(ioport(player == 0 ? "P1" : "P2").read() & 0xff);
            if (player == 0 && m_external_start_frames > 0)
                p &= 0x7f;
            u8 value = 0x7f;
            bool thHigh = (m_io_data[player] & 0x40) != 0;

            if ((p & 0x01) == 0) value &= 0xfe;
            if ((p & 0x02) == 0) value &= 0xfd;
            if (thHigh)
            {
                if ((p & 0x04) == 0) value &= 0xfb;
                if ((p & 0x08) == 0) value &= 0xf7;
                if ((p & 0x10) == 0) value &= 0xef;
                if ((p & 0x20) == 0) value &= 0xdf;
                value |= 0x40;
            }
            else
            {
                value &= 0xf3;
                if ((p & 0x40) == 0) value &= 0xef;
                if ((p & 0x80) == 0) value &= 0xdf;
            }

            return value;
        }


        u8 SystemPortByte()
        {
            u8 value = 0x7f;
            if (m_external_coin_frames > 0 || (ioport("SYSTEM").read() & 0x01) == 0)
                value &= 0xfe;
            return value;
        }


        void DecayExternalInputLatch()
        {
            if (m_external_start_frames > 0)
                m_external_start_frames--;
            if (m_external_coin_frames > 0)
                m_external_coin_frames--;
        }


        u16 z80_control_r(address_space space, offs_t offset, u16 mem_mask)
        {
            uint port = (offset << 1) & 0x1ff;
            if (port == 0x000)
                return m_z80_bus_requested ? (u16)0x0000 : (u16)0x0100;
            if (port == 0x100)
                return m_z80_reset_asserted ? (u16)0x0000 : (u16)0x0100;
            return 0xffff;
        }


        void z80_control_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            uint port = (offset << 1) & 0x1ff;
            if (port == 0x000)
                m_z80_bus_requested = (data & 0x0100) != 0;
            else if (port == 0x100)
                m_z80_reset_asserted = (data & 0x0100) == 0;
        }


        u16 vdp_r(address_space space, offs_t offset, u16 mem_mask)
        {
            uint port = (offset << 1) & 0x1f;
            if (port < 0x04)
                return VdpReadData();
            if (port < 0x08)
            {
                m_vdp_command_select = false;
                return 0x3408;
            }
            if (port < 0x0c)
                return 0;

            return 0xffff;
        }


        void vdp_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            uint port = (offset << 1) & 0x1f;
            if (port < 0x04)
            {
                m_data_writes_this_frame++;
                VdpWriteData(data);
                return;
            }

            if (port < 0x08)
            {
                m_ctrl_writes_this_frame++;
                VdpWriteControl(data);
                return;
            }
        }


        u16 VdpReadData()
        {
            m_vdp_command_select = false;
            u16 result = 0xffff;
            switch (m_vdp_code & 0x0f)
            {
                case 0:
                    result = VramReadWord(m_vdp_address);
                    break;
                case 4:
                    result = m_vsram[(m_vdp_address >> 1) & 0x3f];
                    break;
                case 8:
                    result = m_cram[(m_vdp_address >> 1) & 0x3f];
                    break;
            }

            m_vdp_address = (u16)((m_vdp_address + VdpAutoIncrement()) & 0xffff);
            return result;
        }


        void VdpWriteData(u16 data)
        {
            m_vdp_command_select = false;
            if (m_vdp_dma_fill_pending)
            {
                m_vdp_dma_fill_pending = false;
                VdpRunFill(data);
                return;
            }

            VdpWriteDataToSelectedTarget(data);
        }


        void VdpWriteControl(u16 data)
        {
            if (m_vdp_command_select && (data & 0xc000) == 0x8000)
                m_vdp_command_select = false;

            if (!m_vdp_command_select)
            {
                m_vdp_command_word = data;
                m_vdp_address = (u16)((data & 0x3fff) | (m_vdp_address & 0xc000));
                m_vdp_code = ((data >> 14) & 0x03) | (m_vdp_code & 0x3c);

                if ((data & 0xc000) == 0x8000)
                {
                    VdpSetRegister((data >> 8) & 0x1f, (u8)data);
                    return;
                }

                m_vdp_command_select = true;
                return;
            }

            m_vdp_command_select = false;
            m_vdp_address = (u16)((m_vdp_address & 0x3fff) | ((data & 0x0007) << 14));
            m_vdp_code = (m_vdp_code & ~0x3c) | ((data >> 2) & 0x3c);
            if ((m_vdp_code & 0x20) != 0 && ((m_vdp_reg[1] & 0x10) != 0 || HshavocAllowsDmaWithoutReg1Enable()))
            {
                int mode = VdpDmaMode();
                if (mode == 1)
                    VdpRunMemoryDma();
                else if (mode == 2)
                    m_vdp_dma_fill_pending = true;
                else if (mode == 3)
                    VdpRunVramCopy();
            }
        }


        static bool IsQueueAckWritePc(uint pc)
            => pc == 0x000b1a || pc == 0x002a0e || pc == 0x002a16 || pc == 0x009538 ||
               pc == 0x019338 || pc == 0x019386 || pc == 0x0193d4 || pc == 0x019436 ||
               pc == 0x01948c;


        static bool HshavocAllowsDmaWithoutReg1Enable()
            => !string.Equals(
                Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_MCS_STRICT_VDP_DMA_ENABLE"),
                "1",
                StringComparison.Ordinal);


        static bool IsEnvEnabled(string name)
            => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));


        static int ParseEnvInt(string name, int fallback)
        {
            string value = Environment.GetEnvironmentVariable(name) ?? string.Empty;
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out int hex))
            {
                return hex;
            }

            return int.TryParse(value, out int parsed) ? parsed : fallback;
        }


        void VdpWriteDataToSelectedTarget(u16 data)
        {
            switch (m_vdp_code & 0x0f)
            {
                case 1:
                    m_vram_writes_this_frame++;
                    VramWriteWord(m_vdp_address, data);
                    break;
                case 3:
                    m_cram_writes_this_frame++;
                    m_cram[(m_vdp_address >> 1) & 0x3f] = (u16)(data & 0x0eee);
                    break;
                case 5:
                    m_vsram_writes_this_frame++;
                    m_vsram[(m_vdp_address >> 1) & 0x3f] = data;
                    break;
            }

            m_vdp_address = (u16)((m_vdp_address + VdpAutoIncrement()) & 0xffff);
        }


        void VdpSetRegister(int index, u8 data)
        {
            if ((uint)index >= m_vdp_reg.Length)
                return;

            m_vdp_reg[index] = data;
            if (ShouldTraceMcsVdpQueue() && (index == 1 || index == 2 || index == 4 || index == 12 || index == 13 || index == 15 || index == 16 || index >= 19))
                Console.Error.WriteLine($"[HSH-MCS-VDP-REG] frame={m_frame_counter} reg={index} data=0x{data:X2}");
        }


        int VdpAutoIncrement()
        {
            return m_vdp_reg[15] == 0 ? 2 : m_vdp_reg[15];
        }


        int VdpDmaLength()
        {
            int length = m_vdp_reg[19] | (m_vdp_reg[20] << 8);
            return length == 0 ? 0x10000 : length;
        }


        int VdpDmaMode()
        {
            u8 reg23 = m_vdp_reg[23];
            if ((reg23 & 0x80) == 0)
                return 1;
            return (reg23 & 0x40) != 0 ? 3 : 2;
        }


        uint VdpDmaSourceAddress()
        {
            uint high = (uint)(m_vdp_reg[23] & (((m_vdp_reg[23] & 0x80) == 0) ? 0x7f : 0x3f));
            uint low = (uint)((m_vdp_reg[22] << 8) | m_vdp_reg[21]);
            return (high << 17) | (low << 1);
        }


        void VdpRunMemoryDma()
        {
            m_dma_memory_this_frame++;
            int length = VdpDmaLength();
            uint source = VdpDmaSourceAddress();
            address_space program = m_maincpu.op0.memory().space(AS_PROGRAM);
            if (ShouldTraceMcsVdpQueue())
            {
                int sourceNonZero = CountNonZeroMemoryWords(program, source, Math.Min(length, 0x400));
                Console.Error.WriteLine(
                    $"[HSH-MCS-VDP-DMA] frame={m_frame_counter} mode=mem src=0x{source:X6} dest=0x{m_vdp_address:X4} " +
                    $"len=0x{length:X} code=0x{m_vdp_code:X2} inc=0x{VdpAutoIncrement():X2} srcnz={sourceNonZero}");
            }
            for (int i = 0; i < length; i++)
            {
                u16 value = program.read_word((source + (uint)(i * 2)) & 0x00fffffe);
                m_current_vram_write_source = source + (uint)(i * 2);
                VdpWriteDataToSelectedTarget(value);
            }
            m_current_vram_write_source = 0xffffffff;
        }


        void VdpRunFill(u16 data)
        {
            m_dma_fill_this_frame++;
            int length = VdpDmaLength();
            if (ShouldTraceMcsVdpQueue())
            {
                Console.Error.WriteLine(
                    $"[HSH-MCS-VDP-DMA] frame={m_frame_counter} mode=fill value=0x{data:X4} dest=0x{m_vdp_address:X4} len=0x{length:X} code=0x{m_vdp_code:X2} inc=0x{VdpAutoIncrement():X2}");
            }
            for (int i = 0; i < length; i++)
            {
                m_current_vram_write_source = 0xfffffff0;
                VdpWriteDataToSelectedTarget(data);
            }
            m_current_vram_write_source = 0xffffffff;
        }


        void VdpRunVramCopy()
        {
            m_dma_copy_this_frame++;
            int length = VdpDmaLength();
            int source = (int)(((m_vdp_reg[22] << 8) | m_vdp_reg[21]) & 0xffff);
            if (ShouldTraceMcsVdpQueue())
            {
                Console.Error.WriteLine(
                    $"[HSH-MCS-VDP-DMA] frame={m_frame_counter} mode=copy src=0x{source:X4} dest=0x{m_vdp_address:X4} len=0x{length:X} code=0x{m_vdp_code:X2} inc=0x{VdpAutoIncrement():X2}");
            }
            for (int i = 0; i < length; i++)
            {
                u16 value = VramReadWord((u16)source);
                VdpWriteDataToSelectedTarget(value);
                source = (source + 2) & 0xffff;
            }
        }


        void FlushVdpCommandBlocks()
        {
            address_space program = m_maincpu.op0.memory().space(AS_PROGRAM);
            for (uint block = VdpCommandBlockStart; block <= VdpCommandBlockEnd; block += 2)
            {
                if (block == LatchedVdpQueueBlock)
                    continue;

                u16 reg19 = program.read_word(block);
                u16 reg20 = program.read_word(block + 2);
                u16 reg21 = program.read_word(block + 4);
                u16 reg22 = program.read_word(block + 6);
                u16 reg23 = program.read_word(block + 8);
                u16 control1 = program.read_word(block + 10);
                u16 control2 = program.read_word(block + 12);
                if (!LooksLikeVdpCommandBlock(program, reg19, reg20, reg21, reg22, reg23, control1, control2))
                {
                    m_flushed_vdp_command_blocks.Remove(block);
                    continue;
                }

                ulong signature = BuildVdpCommandBlockSignature(program, block, reg19, reg20, reg21, reg22, reg23, control1, control2);
                if (m_flushed_vdp_command_blocks.TryGetValue(block, out ulong previousSignature) &&
                    previousSignature == signature)
                {
                    continue;
                }

                m_flushed_vdp_command_blocks[block] = signature;
                ExecuteVdpCommandBlock(block, reg19, reg20, reg21, reg22, reg23, control1, control2);
            }
        }


        void LatchVdpQueueParameter(uint address, u16 data, uint pc)
        {
            if (IsVdpDispatcherScratchPc(pc))
                return;

            switch (address)
            {
                case 0x00ffe802:
                    m_latched_slot0_source = (m_latched_slot0_source & 0x0000ffff) | ((uint)data << 16);
                    TraceLatchedVdpQueueParameter(address, data);
                    break;
                case 0x00ffe804:
                    m_latched_slot0_source = (m_latched_slot0_source & 0xffff0000) | data;
                    TraceLatchedVdpQueueParameter(address, data);
                    break;
                case 0x00ffe806:
                    m_latched_slot0_destination = data;
                    TraceLatchedVdpQueueParameter(address, data);
                    break;
                case 0x00ffe808:
                    m_latched_slot0_length = data;
                    TraceLatchedVdpQueueParameter(address, data);
                    break;
                case 0x00ffe80a:
                    if (data != 0)
                        m_latched_slot0_active = true;
                    TraceLatchedVdpQueueParameter(address, data);
                    break;
            }
        }


        static bool IsVdpDispatcherScratchPc(uint pc)
            => pc >= 0x001300 && pc <= 0x001450;


        void TraceLatchedVdpQueueParameter(uint address, u16 data)
        {
            if (!ShouldTraceMcsVdpQueue())
                return;

            Console.Error.WriteLine(
                $"[HSH-MCS-VDPQ-LATCH] frame={m_frame_counter} pc=0x{m_maincpu.op0.Pc:X6} " +
                $"addr=0x{address:X6} data=0x{data:X4} source=0x{m_latched_slot0_source:X6} " +
                $"dest=0x{m_latched_slot0_destination:X4} len=0x{m_latched_slot0_length:X4} active={(m_latched_slot0_active ? 1 : 0)}");
        }


        void FlushLatchedVdpQueueOnAckIfRequested()
        {
            if (!m_latched_slot0_active)
                return;

            int length = m_latched_slot0_length;
            if (length == 0 || length > 0x4000)
                return;

            uint byteLength = (uint)length * 2;
            bool romSource = m_latched_slot0_source < 0x00100000 && m_latched_slot0_source + byteLength <= 0x00100000;
            bool ramSource = m_latched_slot0_source >= WorkRamStart && m_latched_slot0_source + byteLength - 1 <= WorkRamEnd;
            if (!romSource && !ramSource)
                return;

            uint sourceWord = m_latched_slot0_source >> 1;
            u16 reg19 = (u16)(0x9300 | (length & 0x00ff));
            u16 reg20 = (u16)(0x9400 | ((length >> 8) & 0x00ff));
            u16 reg21 = (u16)(0x9500 | (sourceWord & 0x00ff));
            u16 reg22 = (u16)(0x9600 | ((sourceWord >> 8) & 0x00ff));
            u16 reg23 = (u16)(0x9700 | ((sourceWord >> 16) & 0x007f));
            u16 control1 = (u16)(0x4000 | (m_latched_slot0_destination & 0x3fff));
            u16 control2 = (u16)(0x0080 | ((m_latched_slot0_destination >> 14) & 0x0007));

            ExecuteVdpCommandBlock(LatchedVdpQueueBlock, reg19, reg20, reg21, reg22, reg23, control1, control2);
            if (ShouldTraceMcsVdp())
                Console.Error.WriteLine(
                    $"[HSH-MCS-VDPQ-LATCH-FLUSH] frame={m_frame_counter} pc=0x{m_maincpu.op0.Pc:X6} " +
                    $"source=0x{m_latched_slot0_source:X6} dest=0x{m_latched_slot0_destination:X4} len=0x{m_latched_slot0_length:X4}");
            m_latched_slot0_active = false;
        }


        void ReplayLowPatternRamProbeIfRequested()
        {
            if (!LowPatternRamProbe)
                return;

            const uint source = 0x00ff0000;
            int words = Math.Clamp(LowPatternRamProbeWords, 1, 0x8000);
            address_space program = m_maincpu.op0.memory().space(AS_PROGRAM);
            if (CountNonZeroMemoryWords(program, source, words) == 0)
                return;

            ulong signature = HashMemoryWords(program, source, words);
            if (signature == m_low_pattern_probe_signature)
                return;

            for (int i = 0; i < words; i++)
            {
                m_current_vram_write_source = source + (uint)(i * 2);
                VramWriteWord(i * 2, program.read_word((source + (uint)(i * 2)) & 0x00fffffe));
            }
            m_current_vram_write_source = 0xffffffff;
            m_low_pattern_probe_signature = signature;

            if (ShouldTraceMcsVdp())
                Console.Error.WriteLine($"[HSH-MCS-LOWPAT] frame={m_frame_counter} source=0x{source:X6} words=0x{words:X} hash=0x{signature:X16}");
        }


        void ExecuteVdpCommandBlock(
            uint block,
            u16 reg19,
            u16 reg20,
            u16 reg21,
            u16 reg22,
            u16 reg23,
            u16 control1,
            u16 control2)
        {
            VdpWriteControl(reg19);
            VdpWriteControl(reg20);
            VdpWriteControl(reg21);
            VdpWriteControl(reg22);
            VdpWriteControl(reg23);
            VdpWriteControl(control1);
            VdpWriteControl(control2);

            if (ShouldTraceMcsVdpQueue())
            {
                int length = VdpCommandLength(reg19, reg20);
                uint sourceByte = DecodeVdpDmaSourceByte(reg21, reg22, reg23);
                Console.Error.WriteLine(
                    $"[HSH-MCS-VDPBLK] frame={m_frame_counter} block=0x{block:X6} len=0x{length:X4} " +
                    $"source=0x{sourceByte:X6} dest=0x{DecodeVdpDestination(control1, control2):X4} " +
                    $"code=0x{DecodeVdpCodeLow(control1, control2):X2} regs={reg19:X4},{reg20:X4},{reg21:X4},{reg22:X4},{reg23:X4} cmd={control1:X4},{control2:X4}");
            }
        }


        bool LooksLikeVdpCommandBlock(
            address_space program,
            u16 reg19,
            u16 reg20,
            u16 reg21,
            u16 reg22,
            u16 reg23,
            u16 control1,
            u16 control2)
        {
            if ((reg19 & 0xff00) != 0x9300 || (reg20 & 0xff00) != 0x9400 ||
                (reg21 & 0xff00) != 0x9500 || (reg22 & 0xff00) != 0x9600 ||
                (reg23 & 0xff00) != 0x9700)
                return false;

            if ((control1 & 0xc000) == 0x8000)
                return false;

            int codeLow = DecodeVdpCodeLow(control1, control2);
            if (codeLow != 0x01 && codeLow != 0x03 && codeLow != 0x05)
                return false;

            if ((control2 & 0x0080) == 0)
                return false;

            int length = VdpCommandLength(reg19, reg20);
            if (length == 0 || length > 0x4000)
                return false;

            uint sourceByte = DecodeVdpDmaSourceByte(reg21, reg22, reg23);
            uint byteLength = (uint)length * 2;
            bool romSource = sourceByte < 0x00100000 && sourceByte + byteLength <= 0x00100000;
            bool ramSource = sourceByte >= 0x00ff0000 && sourceByte + byteLength - 1 <= 0x00ffffff;
            return romSource || ramSource;
        }


        ulong BuildVdpCommandBlockSignature(
            address_space program,
            uint block,
            u16 reg19,
            u16 reg20,
            u16 reg21,
            u16 reg22,
            u16 reg23,
            u16 control1,
            u16 control2)
        {
            ulong signature =
                ((ulong)block << 40) ^
                ((ulong)reg19 << 48) ^
                ((ulong)reg20 << 32) ^
                ((ulong)reg21 << 16) ^
                reg22 ^
                ((ulong)reg23 << 8) ^
                ((ulong)control1 << 24) ^
                ((ulong)control2 << 4);

            uint sourceByte = DecodeVdpDmaSourceByte(reg21, reg22, reg23);
            int length = VdpCommandLength(reg19, reg20);
            return signature ^ HashMemoryWords(program, sourceByte, length);
        }


        ulong HashMemoryWords(address_space program, uint source, int words)
        {
            ulong hash = 1469598103934665603UL;
            for (int i = 0; i < words; i++)
            {
                uint address = source + (uint)(i * 2);
                u16 value = program.read_word(address & 0x00fffffe);
                hash ^= value;
                hash *= 1099511628211UL;
            }

            return hash;
        }


        static int VdpCommandLength(u16 reg19, u16 reg20)
            => (reg19 & 0x00ff) | ((reg20 & 0x00ff) << 8);


        static uint DecodeVdpDmaSourceByte(u16 reg21, u16 reg22, u16 reg23)
        {
            uint sourceWord = (uint)((reg21 & 0x00ff) | ((reg22 & 0x00ff) << 8) | ((reg23 & 0x007f) << 16));
            return sourceWord << 1;
        }


        static int DecodeVdpCodeLow(u16 control1, u16 control2)
            => ((control1 >> 14) & 0x03) | ((control2 >> 2) & 0x0c);


        static int DecodeVdpDestination(u16 control1, u16 control2)
            => (control1 & 0x3fff) | ((control2 & 0x0007) << 14);


        u16 VramReadWord(int address)
        {
            int addr = address & 0xffff;
            return (u16)((m_vram[addr] << 8) | m_vram[(addr + 1) & 0xffff]);
        }


        void VramWriteWord(int address, u16 data)
        {
            int addr = address & 0xffff;
            if (SwapVramBytes)
            {
                m_vram[addr] = (u8)data;
                m_vram[(addr + 1) & 0xffff] = (u8)(data >> 8);
            }
            else
            {
                m_vram[addr] = (u8)(data >> 8);
                m_vram[(addr + 1) & 0xffff] = (u8)data;
            }

            int wordIndex = addr >> 1;
            m_vram_last_source[wordIndex] = m_current_vram_write_source;
            m_vram_last_pc[wordIndex] = m_maincpu.op0.Pc;
            m_vram_last_frame[wordIndex] = m_frame_counter;
        }


        void RenderMegadrivePlanes(bitmap_rgb32 bitmap, rectangle cliprect)
        {
            uint backdrop = MdColor(m_vdp_reg[7] & 0x3f);
            int minY = Math.Max(cliprect.min_y, 0);
            int maxY = Math.Min(cliprect.max_y, 223);
            int minX = Math.Max(cliprect.min_x, 0);
            int maxX = Math.Min(cliprect.max_x, 319);

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    uint color = backdrop;
                    uint b = PlanePixel(false, x, y);
                    uint a = WindowEnabledAt(x, y) ? WindowPixel(x, y) : PlanePixel(true, x, y);
                    bool bOpaque = (b & MdPixelOpaque) != 0;
                    bool aOpaque = (a & MdPixelOpaque) != 0;
                    if (bOpaque && (b & MdPixelPriority) == 0)
                        color = b & MdPixelColorMask;
                    if (aOpaque && (a & MdPixelPriority) == 0)
                        color = a & MdPixelColorMask;
                    if (bOpaque && (b & MdPixelPriority) != 0)
                        color = b & MdPixelColorMask;
                    if (aOpaque && (a & MdPixelPriority) != 0)
                        color = a & MdPixelColorMask;
                    bitmap.pix(y, x)[0] = color;
                }
            }

            RenderMegadriveSprites(bitmap, cliprect);
        }


        void RenderMegadriveSprites(bitmap_rgb32 bitmap, rectangle cliprect)
        {
            int tableBase = (m_vdp_reg[5] & 0x7f) << 9;
            int minY = Math.Max(cliprect.min_y, 0);
            int maxY = Math.Min(cliprect.max_y, 223);
            int minX = Math.Max(cliprect.min_x, 0);
            int maxX = Math.Min(cliprect.max_x, 319);
            int spriteIndex = 0;
            bool [] seen = new bool[80];

            for (int count = 0; count < seen.Length; count++)
            {
                if ((uint)spriteIndex >= seen.Length || seen[spriteIndex])
                    break;
                seen[spriteIndex] = true;

                int entry = (tableBase + spriteIndex * 8) & 0xffff;
                int y = (VramReadWord(entry) & 0x01ff) - 128;
                u16 sizeLink = VramReadWord(entry + 2);
                int size = (sizeLink >> 8) & 0x0f;
                int next = sizeLink & 0x7f;
                u16 attr = VramReadWord(entry + 4);
                int x = (VramReadWord(entry + 6) & 0x01ff) - 128;

                int hCells = ((size >> 2) & 0x03) + 1;
                int vCells = (size & 0x03) + 1;
                int tileBase = attr & 0x07ff;
                int palette = (attr >> 13) & 0x03;
                bool hflip = (attr & 0x0800) != 0;
                bool vflip = (attr & 0x1000) != 0;

                for (int cellY = 0; cellY < vCells; cellY++)
                {
                    int drawCellY = vflip ? vCells - 1 - cellY : cellY;
                    for (int cellX = 0; cellX < hCells; cellX++)
                    {
                        int drawCellX = hflip ? hCells - 1 - cellX : cellX;
                        int tile = tileBase + cellY * hCells + cellX;
                        DrawSpriteCell(
                            bitmap,
                            minX,
                            maxX,
                            minY,
                            maxY,
                            x + drawCellX * 8,
                            y + drawCellY * 8,
                            tile,
                            palette,
                            hflip,
                            vflip);
                    }
                }

                if (next == 0)
                    break;
                spriteIndex = next;
            }
        }


        void DrawSpriteCell(
            bitmap_rgb32 bitmap,
            int minX,
            int maxX,
            int minY,
            int maxY,
            int x,
            int y,
            int tile,
            int palette,
            bool hflip,
            bool vflip)
        {
            for (int py = 0; py < 8; py++)
            {
                int sy = y + py;
                if (sy < minY || sy > maxY)
                    continue;
                int tileY = vflip ? 7 - py : py;

                for (int px = 0; px < 8; px++)
                {
                    int sx = x + px;
                    if (sx < minX || sx > maxX)
                        continue;
                    int tileX = hflip ? 7 - px : px;
                    int pen = PatternPixel(tile, tileX, tileY);
                    if (pen == 0)
                        continue;

                    int colorIndex = (palette << 4) | pen;
                    bitmap.pix(sy, sx)[0] = MdColor(colorIndex);
                }
            }
        }


        uint PlanePixel(bool planeA, int x, int y)
        {
            int tableBase = planeA ? ((m_vdp_reg[2] & 0x38) << 10) : ((m_vdp_reg[4] & 0x07) << 13);
            int widthTiles = ScrollPlaneTiles(m_vdp_reg[16] & 0x03);
            int heightTiles = ScrollPlaneTiles((m_vdp_reg[16] >> 4) & 0x03);
            int hscroll = ReadPlaneHScroll(planeA, y);
            int vscroll = m_vsram[planeA ? 0 : 1] & 0x03ff;
            int sx = (AdditiveHscroll ? x + hscroll : x - hscroll) & ((widthTiles * 8) - 1);
            int sy = (y + vscroll) & ((heightTiles * 8) - 1);
            int tileX = sx >> 3;
            int tileY = sy >> 3;
            int entryAddr = (tableBase + ((tileY * widthTiles + tileX) << 1)) & 0xffff;
            u16 entry = VramReadWord(entryAddr);
            int tile = entry & 0x07ff;
            int palette = (entry >> 13) & 0x03;
            bool priority = (entry & 0x8000) != 0;
            bool hflip = (entry & 0x0800) != 0;
            bool vflip = (entry & 0x1000) != 0;
            int px = sx & 7;
            int py = sy & 7;
            if (hflip) px ^= 7;
            if (vflip) py ^= 7;
            int pen = PatternPixel(tile, px, py);
            if (pen == 0)
                return 0;

            int colorIndex = (palette << 4) | pen;
            return MdPixelOpaque | (priority ? MdPixelPriority : 0) | MdColor(colorIndex);
        }


        bool WindowEnabledAt(int x, int y)
        {
            int windowCellY = m_vdp_reg[18] & 0x1f;
            bool bottom = (m_vdp_reg[18] & 0x80) != 0;
            bool inVerticalWindow = bottom ? (y >> 3) >= windowCellY : (y >> 3) < windowCellY;
            if (inVerticalWindow)
                return true;

            int splitPixel = (m_vdp_reg[17] & 0x1f) << 4;
            bool right = (m_vdp_reg[17] & 0x80) != 0;
            return right ? x >= splitPixel : x < splitPixel;
        }


        uint WindowPixel(int x, int y)
        {
            int tableBase = ((m_vdp_reg[3] & 0x3e) << 10) & (IsH40Mode() ? 0xf000 : 0xf800);
            int widthTiles = IsH40Mode() ? 64 : 32;
            int tileX = x >> 3;
            int tileY = y >> 3;
            int entryAddr = (tableBase + ((tileY * widthTiles + tileX) << 1)) & 0xffff;
            u16 entry = VramReadWord(entryAddr);
            int tile = entry & 0x07ff;
            int palette = (entry >> 13) & 0x03;
            bool priority = (entry & 0x8000) != 0;
            bool hflip = (entry & 0x0800) != 0;
            bool vflip = (entry & 0x1000) != 0;
            int px = x & 7;
            int py = y & 7;
            if (hflip) px ^= 7;
            if (vflip) py ^= 7;
            int pen = PatternPixel(tile, px, py);
            if (pen == 0)
                return 0;

            int colorIndex = (palette << 4) | pen;
            return MdPixelOpaque | (priority ? MdPixelPriority : 0) | MdColor(colorIndex);
        }


        bool IsH40Mode()
            => ForceH40 || (m_vdp_reg[12] & 0x81) != 0;


        int ReadPlaneHScroll(bool planeA, int y)
        {
            int baseReg = m_vdp_reg[13] & 0x3f;
            if (baseReg == 0 && (m_vdp_reg[11] & 0x03) != 0 && CountNonZeroVramWords(0xd000, 0x100) != 0)
                baseReg = 0x34;

            int baseAddr = baseReg << 10;
            int mode = m_vdp_reg[11] & 0x03;
            int rowOffset = mode switch
            {
                2 => (y & ~7) << 2,
                3 => y << 2,
                _ => 0
            };
            int offset = planeA ? 0 : 2;
            return VramReadWord((baseAddr + rowOffset + offset) & 0xffff) & 0x03ff;
        }


        static int ScrollPlaneTiles(int code)
        {
            return code switch
            {
                1 => 64,
                3 => 128,
                _ => 32
            };
        }


        int PatternPixel(int tile, int x, int y)
        {
            int addr = ((tile & 0x07ff) << 5) + (y << 2) + (x >> 1);
            u8 packed = m_vram[addr & 0xffff];
            return (x & 1) == 0 ? (packed >> 4) & 0x0f : packed & 0x0f;
        }


        uint MdColor(int index)
        {
            u16 raw = m_cram[index & 0x3f];
            int r = MdColorComponent((raw >> 1) & 0x07);
            int g = MdColorComponent((raw >> 5) & 0x07);
            int b = MdColorComponent((raw >> 9) & 0x07);
            return (uint)((r << 16) | (g << 8) | b);
        }


        static int MdColorComponent(int value)
        {
            return value switch
            {
                0 => 0,
                1 => 52,
                2 => 87,
                3 => 116,
                4 => 144,
                5 => 172,
                6 => 206,
                _ => 255
            };
        }


        void TraceVdpFrame()
        {
            if (!ShouldTraceMcsVdp())
                return;
            if (m_frame_counter > 10 && (m_frame_counter % 30) != 0)
                return;

            int vramNonZero = CountNonZeroBytes(m_vram);
            int cramNonZero = CountNonZeroWords(m_cram);
            int planeANonZero = CountNonZeroVramWords((m_vdp_reg[2] & 0x38) << 10, 0x1000);
            int planeBNonZero = CountNonZeroVramWords((m_vdp_reg[4] & 0x07) << 13, 0x1000);
            int spriteNonZero = CountNonZeroVramWords((m_vdp_reg[5] & 0x7f) << 9, 0x140);
            int planeAOpaque = CountOpaquePlanePixels(true);
            int planeBOpaque = CountOpaquePlanePixels(false);
            u16 firstA = FirstNonZeroVramWord((m_vdp_reg[2] & 0x38) << 10, 0x1000);
            u16 firstB = FirstNonZeroVramWord((m_vdp_reg[4] & 0x07) << 13, 0x1000);
            Console.Error.WriteLine(
                $"[HSH-MCS-VDP-FRAME] frame={m_frame_counter} ctrl={m_ctrl_writes_this_frame} data={m_data_writes_this_frame} " +
                $"vramW={m_vram_writes_this_frame} cramW={m_cram_writes_this_frame} vsramW={m_vsram_writes_this_frame} " +
                $"dma=({m_dma_memory_this_frame},{m_dma_fill_this_frame},{m_dma_copy_this_frame}) " +
                $"nz=(vram:{vramNonZero},cram:{cramNonZero},pa:{planeANonZero},pb:{planeBNonZero},sat:{spriteNonZero}) " +
                $"opaque=(pa:{planeAOpaque},pb:{planeBOpaque}) first=(pa:{firstA:X4},pb:{firstB:X4}) " +
                $"regs=01:{m_vdp_reg[1]:X2} 02:{m_vdp_reg[2]:X2} 03:{m_vdp_reg[3]:X2} 04:{m_vdp_reg[4]:X2} 05:{m_vdp_reg[5]:X2} " +
                $"07:{m_vdp_reg[7]:X2} 0b:{m_vdp_reg[11]:X2} 0c:{m_vdp_reg[12]:X2} 0d:{m_vdp_reg[13]:X2} 10:{m_vdp_reg[16]:X2} 11:{m_vdp_reg[17]:X2} 12:{m_vdp_reg[18]:X2} " +
                $"cram0={m_cram[0]:X3},{m_cram[1]:X3},{m_cram[2]:X3},{m_cram[3]:X3} " +
                $"pc=0x{m_maincpu.op0.Pc:X6} sr=0x{m_maincpu.op0.StatusRegister:X4} " +
                $"imask={m_maincpu.op0.InterruptPriorityMask} stop={(m_maincpu.op0.IsStopped ? 1 : 0)}");
        }


        void DumpSnapshotIfRequested()
        {
            if (m_snapshot_dumped || SnapshotFrame < 0 || m_frame_counter != SnapshotFrame)
                return;

            try
            {
                Directory.CreateDirectory(SnapshotDir);
                string prefix = Path.Combine(SnapshotDir, $"hshavoc_mcs_frame_{m_frame_counter:D6}");
                File.WriteAllBytes(prefix + "_vram.bin", m_vram);
                File.WriteAllBytes(prefix + "_cram.bin", WordsToBytes(m_cram));
                File.WriteAllBytes(prefix + "_vsram.bin", WordsToBytes(m_vsram));
                File.WriteAllBytes(prefix + "_workram.bin", m_work_ram);
                File.WriteAllText(prefix + "_meta.txt", SnapshotMetaText());
                Console.Error.WriteLine($"[HSH-MCS-SNAPSHOT] frame={m_frame_counter} prefix={prefix}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[HSH-MCS-SNAPSHOT] frame={m_frame_counter} failed={ex.Message}");
            }

            m_snapshot_dumped = true;
        }


        string SnapshotMetaText()
        {
            return
                $"frame={m_frame_counter}\n" +
                $"vdp_display={(((m_vdp_reg[1] & 0x40) != 0) ? 1 : 0)}\n" +
                $"vdp_plane_a=0x{((m_vdp_reg[2] & 0x38) << 10):X4}\n" +
                $"vdp_plane_b=0x{((m_vdp_reg[4] & 0x07) << 13):X4}\n" +
                $"vdp_sprite=0x{((m_vdp_reg[5] & 0x7f) << 9):X4}\n" +
                $"vdp_hscroll=0x{((m_vdp_reg[13] & 0x3f) << 10):X4}\n" +
                $"vdp_scroll_h=0x{(m_vdp_reg[16] & 0x03):X2}\n" +
                $"vdp_scroll_v=0x{((m_vdp_reg[16] >> 4) & 0x03):X2}\n" +
                $"vdp_reg_01=0x{m_vdp_reg[1]:X2}\n" +
                $"vdp_reg_02=0x{m_vdp_reg[2]:X2}\n" +
                $"vdp_reg_03=0x{m_vdp_reg[3]:X2}\n" +
                $"vdp_reg_04=0x{m_vdp_reg[4]:X2}\n" +
                $"vdp_reg_05=0x{m_vdp_reg[5]:X2}\n" +
                $"vdp_reg_07=0x{m_vdp_reg[7]:X2}\n" +
                $"vdp_reg_0b=0x{m_vdp_reg[11]:X2}\n" +
                $"vdp_reg_0c=0x{m_vdp_reg[12]:X2}\n" +
                $"vdp_reg_0d=0x{m_vdp_reg[13]:X2}\n" +
                $"vdp_reg_10=0x{m_vdp_reg[16]:X2}\n" +
                $"vdp_reg_11=0x{m_vdp_reg[17]:X2}\n" +
                $"vdp_reg_12=0x{m_vdp_reg[18]:X2}\n";
        }


        static byte [] WordsToBytes(u16 [] words)
        {
            byte [] bytes = new byte[words.Length * 2];
            for (int i = 0; i < words.Length; i++)
            {
                bytes[i * 2] = (byte)(words[i] >> 8);
                bytes[i * 2 + 1] = (byte)words[i];
            }
            return bytes;
        }


        void ResetVdpFrameCounters()
        {
            m_ctrl_writes_this_frame = 0;
            m_data_writes_this_frame = 0;
            m_vram_writes_this_frame = 0;
            m_cram_writes_this_frame = 0;
            m_vsram_writes_this_frame = 0;
            m_dma_memory_this_frame = 0;
            m_dma_fill_this_frame = 0;
            m_dma_copy_this_frame = 0;
        }


        static int CountNonZeroBytes(u8 [] data)
        {
            int count = 0;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != 0)
                    count++;
            }

            return count;
        }


        static int CountNonZeroWords(u16 [] data)
        {
            int count = 0;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != 0)
                    count++;
            }

            return count;
        }


        int CountNonZeroVramWords(int start, int words)
        {
            int count = 0;
            for (int i = 0; i < words; i++)
            {
                if (VramReadWord(start + i * 2) != 0)
                    count++;
            }

            return count;
        }


        u16 FirstNonZeroVramWord(int start, int words)
        {
            for (int i = 0; i < words; i++)
            {
                u16 value = VramReadWord(start + i * 2);
                if (value != 0)
                    return value;
            }

            return 0;
        }


        int CountOpaquePlanePixels(bool planeA)
        {
            int count = 0;
            for (int y = 0; y < 224; y++)
            {
                for (int x = 0; x < 320; x++)
                {
                    if ((PlanePixel(planeA, x, y) & 0xff000000U) != 0)
                        count++;
                }
            }

            return count;
        }


        static int CountNonZeroMemoryWords(address_space program, uint source, int words)
        {
            int count = 0;
            for (int i = 0; i < words; i++)
            {
                if (program.read_word((source + (uint)(i * 2)) & 0x00fffffe) != 0)
                    count++;
            }

            return count;
        }


        static void NormalizeLoadedWordOrder(MemoryU8 rom)
        {
            int length = Math.Min(InterleavedSize, rom.Count) & ~1;
            for (int offset = 0; offset < length; offset += 2)
            {
                u8 tmp = rom[offset];
                rom[offset] = rom[offset + 1];
                rom[offset + 1] = tmp;
            }
        }


        public void init_hshavoc()
        {
            memory_region region = memregion("maincpu");
            if (region == null || region.base_() == null || region.bytes() < InterleavedSize)
                return;

            NormalizeLoadedWordOrder(region.base_());
            DecodeBaseInPlace(region.base_());
            ApplyPatch(region.base_(), BestStartupPatch);
            ApplyPatch(region.base_(), InputIllegalBridgePatch);
            NormalizeLoadedWordOrder(region.base_());
            m_vdp_reg[15] = 2;
            m_maincpu.op0.reset_from_bus();
            if (TraceMcsVdp)
            {
                address_space program = m_maincpu.op0.memory().space(AS_PROGRAM);
                Console.Error.WriteLine(
                    $"[HSH-MCS-INIT] raw_vectors={ReadWord(region.base_(), 0):X4} {ReadWord(region.base_(), 1):X4} " +
                    $"{ReadWord(region.base_(), 2):X4} {ReadWord(region.base_(), 3):X4} " +
                    $"bus_vectors={program.read_word(0):X4} {program.read_word(2):X4} {program.read_word(4):X4} {program.read_word(6):X4} " +
                    $"pc=0x{m_maincpu.op0.Pc:X6}");
            }
        }


        static void DecodeBaseInPlace(MemoryU8 rom)
        {
            int wordCount = Math.Min(InterleavedSize, rom.Count) / 2;
            for (int index = 0; index < BaseDecodeEnd / 2 && index < wordCount; index++)
            {
                u16 word = DecodeDataWord(ReadWord(rom, index), Typedat[index & 0x0f]);
                WriteWord(rom, index, word);
            }

            for (int index = BaseDecodeEnd / 2; index < wordCount; index++)
                WriteWord(rom, index, BitSwap16(ReadWord(rom, index), TailBitswap));

            WriteWord(rom, 0, (u16)(ReadWord(rom, 0) ^ 0x0107));
            WriteWord(rom, 1, (u16)(ReadWord(rom, 1) ^ 0x0107));
            WriteWord(rom, 2, (u16)(ReadWord(rom, 2) ^ 0x0107));
            WriteWord(rom, 3, (u16)(ReadWord(rom, 3) ^ 0x0707));
        }


        static u16 DecodeDataWord(u16 rawWord, int typedat)
        {
            u16 word = BitSwap16(rawWord, DataBitswap);
            word ^= typedat != 0 ? (u16)0x0501 : (u16)0x0406;
            if ((word & 0x0400) != 0)
                word ^= 0x0200;
            if (typedat == 0)
            {
                if ((word & 0x0100) != 0)
                    word ^= 0x0004;
                word = BitSwap16(word, new [] { 15, 14, 13, 12, 11, 9, 10, 8, 7, 6, 5, 4, 3, 2, 1, 0 });
            }

            return word;
        }


        static u16 BitSwap16(u16 value, int [] order)
        {
            u16 result = 0;
            for (int i = 0; i < 16; i++)
                result |= (u16)(((value >> order[i]) & 1) << (15 - i));

            return result;
        }


        static u16 ReadWord(MemoryU8 memory, int wordIndex)
        {
            int offset = wordIndex * 2;
            return (u16)((memory[offset] << 8) | memory[offset + 1]);
        }


        static void WriteWord(MemoryU8 memory, int wordIndex, u16 value)
        {
            int offset = wordIndex * 2;
            if (offset < 0 || offset + 1 >= memory.Count)
                return;

            memory[offset] = (u8)(value >> 8);
            memory[offset + 1] = (u8)value;
        }


        static void ApplyPatch(MemoryU8 rom, (int Address, u16 Value) [] patch)
        {
            foreach ((int address, u16 value) in patch)
                WriteWord(rom, address / 2, value);
        }
    }


    public class hshavoc : construct_ioport_helper
    {
        static readonly hshavoc m_hshavoc = new hshavoc();

        static readonly tiny_rom_entry [] rom_hshavoc =
        {
            ROM_REGION(0x100000, "maincpu", 0),
            ROM_LOAD16_BYTE("d-25.11a", 0x000000, 0x080000, CRC("6a155060") + SHA1("ecb47bd428786e50e300a062b5038f943419a389")),
            ROM_LOAD16_BYTE("d-26.9a",  0x000001, 0x080000, CRC("1afa84fe") + SHA1("041296e0360b7747aedc2d948c39e06ba03a7d08")),

            ROM_END,
        };


        static void hshavoc_state_hshavoc(machine_config config, device_t device) { ((hshavoc_state)device).hshavoc(config); }
        static void hshavoc_state_init_hshavoc(device_t owner) { ((hshavoc_state)owner).init_hshavoc(); }


        static device_t device_creator_hshavoc_state(emu.detail.device_type_impl_base type, machine_config mconfig, string tag, device_t owner, u32 clock) { return new hshavoc_state(mconfig, (device_type)type, tag); }


        void construct_ioport_hshavoc(device_t owner, ioport_list portlist, ref string errorbuf)
        {
            INPUT_PORTS_START(owner, portlist, ref errorbuf);

            PORT_START("P1");
            PORT_BIT(0x01, IP_ACTIVE_LOW, IPT_JOYSTICK_UP); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x02, IP_ACTIVE_LOW, IPT_JOYSTICK_DOWN); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x04, IP_ACTIVE_LOW, IPT_JOYSTICK_LEFT); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x08, IP_ACTIVE_LOW, IPT_JOYSTICK_RIGHT); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x10, IP_ACTIVE_LOW, IPT_BUTTON1); PORT_PLAYER(1);
            PORT_BIT(0x20, IP_ACTIVE_LOW, IPT_BUTTON2); PORT_PLAYER(1);
            PORT_BIT(0x40, IP_ACTIVE_LOW, IPT_BUTTON3); PORT_PLAYER(1);
            PORT_BIT(0x80, IP_ACTIVE_LOW, IPT_START1);

            PORT_START("P2");
            PORT_BIT(0xff, IP_ACTIVE_LOW, IPT_UNUSED);

            PORT_START("SYSTEM");
            PORT_BIT(0x01, IP_ACTIVE_LOW, IPT_COIN1);
            PORT_BIT(0xfe, IP_ACTIVE_LOW, IPT_UNUSED);
        }


        //                                                         creator,                       rom          YEAR,   NAME,     PARENT, MACHINE,              INPUT, INIT,                       MONITOR, COMPANY,              FULLNAME,             FLAGS
        public static readonly game_driver driver_hshavoc = GAME(device_creator_hshavoc_state, rom_hshavoc, "1993", "hshavoc", "0",    hshavoc_state_hshavoc, m_hshavoc.construct_ioport_hshavoc,  hshavoc_state_init_hshavoc, ROT0,   "Data East Corporation", "Heavy Smash Havoc", MACHINE_IS_SKELETON | MACHINE_UNEMULATED_PROTECTION);
    }
}
