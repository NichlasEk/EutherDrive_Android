using System.Buffers.Binary;
using System.Reflection;
using Ryu64.MIPS;

internal static class RdpStreamingChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run()
    {
        int cases = 0;
        Memory New()
        {
            var m = new Memory(new byte[4096]); R4300.memory = m;
            void Set(string name, object value) => typeof(Memory).GetField(name, Private)!.SetValue(m,value);
            Set("_rdpColorImageAddress",0x300000u); Set("_rdpColorImageWidth",64u); Set("_rdpColorImageSize",2u);
            Set("_rdpScissorX1",63); Set("_rdpScissorY1",31);
            Array.Fill((byte[])typeof(Memory).GetField("_rdpTmem", Private)!.GetValue(m)!, (byte)255);
            typeof(Memory).GetMethod("ExecuteRdpSetTile",Private)!.Invoke(m,new object[] {0xf5100200u,0u});
            return m;
        }
        byte[] Save(Memory m)
        {
            using var s = new MemoryStream(); using var w = new BinaryWriter(s); m.SaveState(w); return s.ToArray();
        }
        Memory Restore(byte[] state)
        {
            var m = New(); using var r = new BinaryReader(new MemoryStream(state)); m.LoadState(r);
            if (r.BaseStream.Position != state.Length) throw new Exception("RDP state alignment");
            return m;
        }
        void Submit(Memory m, uint[] words, int offset, int count, bool dmem, uint address)
        {
            m.WriteUInt32(0x0410000c,dmem ? 2u : 1u);
            for (int i=0;i<count;i++)
                if (dmem) BinaryPrimitives.WriteUInt32BigEndian(m.SP_MEM_RW.AsSpan((int)((address+(uint)i*4)&0xfff)),words[offset+i]);
                else BinaryPrimitives.WriteUInt32BigEndian(m.RDRAM.AsSpan((int)address+i*4),words[offset+i]);
            m.WriteUInt32(0x04100000,address); m.WriteUInt32(0x04100004,address+(uint)count*4);
            if (m.ReadUInt32(0x04100008)!=address+(uint)count*4) throw new Exception("DMA did not consume fragment");
            // The producer can recycle bytes as soon as CURRENT passes them.
            for (int i=0;i<count;i++)
                if (dmem) m.SP_MEM_RW.AsSpan((int)((address+(uint)i*4)&0xfff),4).Fill(0xcc);
                else m.RDRAM.AsSpan((int)address+i*4,4).Fill(0xcc);
        }
        foreach (int command in new[] {8,9,10,11,12,13,14,15,36,37})
        {
            int length = command>=36 ? 4 : 8+((command&4)!=0?16:0)+((command&2)!=0?16:0)+((command&1)!=0?4:0);
            var words = new uint[length];
            if (command>=36) { words[0]=(uint)command<<24 | 0x00040040u; words[1]=0; words[2]=0; words[3]=0x04000400; }
            else
            {
                words[0]=(uint)command<<24 | 0x00800040u; words[1]=0x00400000;
                words[2]=words[6]=16u<<16;
                if ((command&4)!=0) { words[8]=0x00ff0000;words[9]=0x000000ff; }
            }
            var whole=New(); Submit(whole,words,0,length,false,0x1000);
            byte[] expected=whole.RDRAM.AsSpan(0x300000,64*32*2).ToArray();
            for (int split=2;split<length;split+=2)
            foreach (bool restore in new[] {false,true})
            {
                var m=New(); Submit(m,words,0,split,false,0x1000);
                if (m.RDRAM.AsSpan(0x300000,64*32*2).ContainsAnyExcept((byte)0)) throw new Exception("Partial command rendered early");
                if (restore) m=Restore(Save(m));
                // A new START and a different DMA source must retain the prefix.
                for (int i=split;i<length;i+=2) Submit(m,words,i,2,(i&2)!=0,0xff8);
                if (!m.RDRAM.AsSpan(0x300000,expected.Length).SequenceEqual(expected)) throw new Exception($"Fragmented command {command:x2} split {split} differs");
                string field=command>=36?"_rdpTextureRectangleCommandCount":"_rdpTriangleCommandCount";
                if ((long)typeof(Memory).GetField(field,Private)!.GetValue(m)!=1) throw new Exception("Command not executed exactly once");
                if ((m.ReadUInt32(0x04300008)&0x20)!=0) throw new Exception("Early DP interrupt");
                Submit(m,new uint[]{0xe9000000,0},0,2,false,0x2000);
                if ((m.ReadUInt32(0x04300008)&0x20)==0) throw new Exception("Lost trailing FULL_SYNC");
                cases++;
            }
        }
        // Rampage's 8-KiB ring ends halfway through a rectangle. Its next
        // payload starts with 0x08, which must not become a triangle header
        // that consumes or blocks the trailing FULL_SYNC.
        var wrap=New();
        uint[] wrapped={0xe410018c,0x00000110,0x08600000,0xfc000400,0xe9000000,0};
        Submit(wrap,wrapped,0,2,false,0x18f158);
        Submit(wrap,wrapped,2,4,false,0x18d160);
        if ((wrap.ReadUInt32(0x04300008)&0x20)==0
            || (long)typeof(Memory).GetField("_rdpTextureRectangleCommandCount",Private)!.GetValue(wrap)!=1
            || (long)typeof(Memory).GetField("_rdpTriangleCommandCount",Private)!.GetValue(wrap)!=0)
            throw new Exception("Rampage ring wrap lost rectangle or FULL_SYNC");
        cases++;
        // Loading a pre-FIFO save over an in-progress command clears the queue.
        var clean=New(); byte[] legacy=Save(clean)[..^(StateChecks.FramebufferPublicationTrailerBytes + StateChecks.RdpCommandTrailerBytes)];
        BinaryPrimitives.WriteInt32LittleEndian(legacy,5);
        Submit(clean,new uint[]{0xe4000000,0},0,2,false,0x1000);
        using (var reader=new BinaryReader(new MemoryStream(legacy))) clean.LoadState(reader);
        Submit(clean,new uint[]{0xe9000000,0},0,2,false,0x2000);
        if ((clean.ReadUInt32(0x04300008)&0x20)==0) throw new Exception("Legacy load retained partial command");
        Console.WriteLine($"rdpStreamingCases={cases} pixels=passed recycledBuffers=passed sourceChanges=passed saveRestore=passed legacyLoad=passed fullSync=passed");
    }
}
