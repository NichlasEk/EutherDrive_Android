using System.IO;

namespace EutherDrive.Core.MdTracerCore;

internal partial class md_m68k
{
    public sealed class MdM68kContext
    {
        public ushort Opcode;
        public byte Op;
        public byte Op1;
        public byte Op2;
        public byte Op3;
        public byte Op4;

        public int Clock;
        public int ClockTotal;
        public int ClockNow;

        public uint RegPc;
        public readonly uint[] RegData = new uint[8];
        public readonly uint[] RegAddr = new uint[8];
        public uint RegAddrUsp;

        public uint InitialPc;
        public uint StackTop;

        public bool InterruptVReq;
        public bool InterruptHReq;
        public bool InterruptExtReq;
        public bool InterruptVAct;
        public bool InterruptHAct;
        public bool InterruptExtAct;

        public bool Stop;

        public bool StatusT;
        public bool StatusS;
        public bool StatusB1;
        public bool StatusB2;
        public bool StatusB3;
        public bool StatusB4;
        public bool StatusB5;
        public bool StatusB6;
        public bool StatusX;
        public bool StatusN;
        public bool StatusZ;
        public bool StatusV;
        public bool StatusC;

        public byte StatusInterruptMask;

        public ushort RegSr;
        public byte StatusCcr;
        public int ForceB154ReadRemaining;

        public byte[]? Memory;
    }

    internal static MdM68kContext CaptureContext()
    {
        var ctx = new MdM68kContext();
        CaptureContext(ctx);
        return ctx;
    }

    internal static void CaptureContext(MdM68kContext ctx)
    {
        ctx.Opcode = g_opcode;
        ctx.Op = g_op;
        ctx.Op1 = g_op1;
        ctx.Op2 = g_op2;
        ctx.Op3 = g_op3;
        ctx.Op4 = g_op4;

        ctx.Clock = g_clock;
        ctx.ClockTotal = g_clock_total;
        ctx.ClockNow = g_clock_now;

        ctx.RegPc = g_reg_PC;
        for (int i = 0; i < g_reg_data.Length; i++)
            ctx.RegData[i] = g_reg_data[i].l;
        for (int i = 0; i < g_reg_addr.Length; i++)
            ctx.RegAddr[i] = g_reg_addr[i].l;
        ctx.RegAddrUsp = g_reg_addr_usp.l;

        ctx.InitialPc = g_initial_PC;
        ctx.StackTop = g_stack_top;

        ctx.InterruptVReq = g_interrupt_V_req;
        ctx.InterruptHReq = g_interrupt_H_req;
        ctx.InterruptExtReq = g_interrupt_EXT_req;
        ctx.InterruptVAct = g_interrupt_V_act;
        ctx.InterruptHAct = g_interrupt_H_act;
        ctx.InterruptExtAct = g_interrupt_EXT_act;

        ctx.Stop = g_68k_stop;

        ctx.StatusT = g_status_T;
        ctx.StatusS = g_status_S;
        ctx.StatusB1 = g_status_B1;
        ctx.StatusB2 = g_status_B2;
        ctx.StatusB3 = g_status_B3;
        ctx.StatusB4 = g_status_B4;
        ctx.StatusB5 = g_status_B5;
        ctx.StatusB6 = g_status_B6;
        ctx.StatusX = g_status_X;
        ctx.StatusN = g_status_N;
        ctx.StatusZ = g_status_Z;
        ctx.StatusV = g_status_V;
        ctx.StatusC = g_status_C;

        ctx.StatusInterruptMask = g_status_interrupt_mask;

        ctx.RegSr = g_reg_SR;
        ctx.StatusCcr = g_status_CCR;
        ctx.ForceB154ReadRemaining = _forceB154ReadRemaining;

        ctx.Memory = g_memory;
    }

    internal static void ApplyContext(MdM68kContext ctx)
    {
        g_opcode = ctx.Opcode;
        g_op = ctx.Op;
        g_op1 = ctx.Op1;
        g_op2 = ctx.Op2;
        g_op3 = ctx.Op3;
        g_op4 = ctx.Op4;

        g_clock = ctx.Clock;
        g_clock_total = ctx.ClockTotal;
        g_clock_now = ctx.ClockNow;

        g_reg_PC = ctx.RegPc;
        for (int i = 0; i < g_reg_data.Length; i++)
            g_reg_data[i].l = ctx.RegData[i];
        for (int i = 0; i < g_reg_addr.Length; i++)
            g_reg_addr[i].l = ctx.RegAddr[i];
        g_reg_addr_usp.l = ctx.RegAddrUsp;

        g_initial_PC = ctx.InitialPc;
        g_stack_top = ctx.StackTop;

        g_interrupt_V_req = ctx.InterruptVReq;
        g_interrupt_H_req = ctx.InterruptHReq;
        g_interrupt_EXT_req = ctx.InterruptExtReq;
        g_interrupt_V_act = ctx.InterruptVAct;
        g_interrupt_H_act = ctx.InterruptHAct;
        g_interrupt_EXT_act = ctx.InterruptExtAct;

        g_68k_stop = ctx.Stop;

        g_status_T = ctx.StatusT;
        g_status_S = ctx.StatusS;
        g_status_B1 = ctx.StatusB1;
        g_status_B2 = ctx.StatusB2;
        g_status_B3 = ctx.StatusB3;
        g_status_B4 = ctx.StatusB4;
        g_status_B5 = ctx.StatusB5;
        g_status_B6 = ctx.StatusB6;
        g_status_X = ctx.StatusX;
        g_status_N = ctx.StatusN;
        g_status_Z = ctx.StatusZ;
        g_status_V = ctx.StatusV;
        g_status_C = ctx.StatusC;

        g_status_interrupt_mask = ctx.StatusInterruptMask;

        _forceB154ReadRemaining = ctx.ForceB154ReadRemaining;

        g_memory = ctx.Memory;
    }

    internal static void SaveState(BinaryWriter writer)
    {
        writer.Write(g_opcode);
        writer.Write(g_op);
        writer.Write(g_op1);
        writer.Write(g_op2);
        writer.Write(g_op3);
        writer.Write(g_op4);

        writer.Write(g_clock);
        writer.Write(g_clock_total);
        writer.Write(g_clock_now);

        writer.Write(g_reg_PC);
        for (int i = 0; i < g_reg_data.Length; i++)
            writer.Write(g_reg_data[i].l);
        for (int i = 0; i < g_reg_addr.Length; i++)
            writer.Write(g_reg_addr[i].l);
        writer.Write(g_reg_addr_usp.l);

        writer.Write(g_initial_PC);
        writer.Write(g_stack_top);

        writer.Write(g_interrupt_V_req);
        writer.Write(g_interrupt_H_req);
        writer.Write(g_interrupt_EXT_req);
        writer.Write(g_interrupt_V_act);
        writer.Write(g_interrupt_H_act);
        writer.Write(g_interrupt_EXT_act);

        writer.Write(g_68k_stop);

        writer.Write(g_status_T);
        writer.Write(g_status_S);
        writer.Write(g_status_B1);
        writer.Write(g_status_B2);
        writer.Write(g_status_B3);
        writer.Write(g_status_B4);
        writer.Write(g_status_B5);
        writer.Write(g_status_B6);
        writer.Write(g_status_X);
        writer.Write(g_status_N);
        writer.Write(g_status_Z);
        writer.Write(g_status_V);
        writer.Write(g_status_C);
        writer.Write(g_status_interrupt_mask);

        writer.Write(g_reg_SR);
        writer.Write(g_status_CCR);
        writer.Write(_forceB154ReadRemaining);

        if (g_memory == null)
        {
            writer.Write(-1);
        }
        else
        {
            writer.Write(g_memory.Length);
            writer.Write(g_memory);
        }
    }

    internal static void LoadState(BinaryReader reader)
    {
        g_opcode = reader.ReadUInt16();
        g_op = reader.ReadByte();
        g_op1 = reader.ReadByte();
        g_op2 = reader.ReadByte();
        g_op3 = reader.ReadByte();
        g_op4 = reader.ReadByte();

        g_clock = reader.ReadInt32();
        g_clock_total = reader.ReadInt32();
        g_clock_now = reader.ReadInt32();

        g_reg_PC = reader.ReadUInt32();
        for (int i = 0; i < g_reg_data.Length; i++)
            g_reg_data[i].l = reader.ReadUInt32();
        for (int i = 0; i < g_reg_addr.Length; i++)
            g_reg_addr[i].l = reader.ReadUInt32();
        g_reg_addr_usp.l = reader.ReadUInt32();

        g_initial_PC = reader.ReadUInt32();
        g_stack_top = reader.ReadUInt32();

        g_interrupt_V_req = reader.ReadBoolean();
        g_interrupt_H_req = reader.ReadBoolean();
        g_interrupt_EXT_req = reader.ReadBoolean();
        g_interrupt_V_act = reader.ReadBoolean();
        g_interrupt_H_act = reader.ReadBoolean();
        g_interrupt_EXT_act = reader.ReadBoolean();

        g_68k_stop = reader.ReadBoolean();

        g_status_T = reader.ReadBoolean();
        g_status_S = reader.ReadBoolean();
        g_status_B1 = reader.ReadBoolean();
        g_status_B2 = reader.ReadBoolean();
        g_status_B3 = reader.ReadBoolean();
        g_status_B4 = reader.ReadBoolean();
        g_status_B5 = reader.ReadBoolean();
        g_status_B6 = reader.ReadBoolean();
        g_status_X = reader.ReadBoolean();
        g_status_N = reader.ReadBoolean();
        g_status_Z = reader.ReadBoolean();
        g_status_V = reader.ReadBoolean();
        g_status_C = reader.ReadBoolean();
        g_status_interrupt_mask = reader.ReadByte();

        g_reg_SR = reader.ReadUInt16();
        g_status_CCR = reader.ReadByte();
        _forceB154ReadRemaining = reader.ReadInt32();

        int memLength = reader.ReadInt32();
        if (memLength < 0)
        {
            g_memory = null;
            return;
        }

        byte[] memory = reader.ReadBytes(memLength);
        if (g_memory == null || g_memory.Length != memLength)
            g_memory = new byte[memLength];
        Buffer.BlockCopy(memory, 0, g_memory, 0, memLength);
    }
}
