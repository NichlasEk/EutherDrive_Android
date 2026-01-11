using System.IO;

namespace EutherDrive.Core.MdTracerCore;

internal partial class md_m68k
{
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
