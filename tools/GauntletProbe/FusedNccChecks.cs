using System.Reflection;

internal static class FusedNccChecks
{
    public static void Run(Assembly core)
    {
        const string env = "EUTHERDRIVE_GAUNTDL_EXPERIMENT_FUSED_NCC";
        string? saved = Environment.GetEnvironmentVariable(env);
        Environment.SetEnvironmentVariable(env, "1");
        try
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            Type type = core.GetType("EutherDrive.Core.Arcade.Vegas.VoodooBringupBackend", true)!;
            object backend = Activator.CreateInstance(type, true)!;
            object Field(string name) => type.GetField(name, flags)!.GetValue(backend)!;
            object? Call(string name, params object[] args) => type.GetMethod(name, flags)!.Invoke(backend,args);
            void CheckLut(object state)
            {
                Type stype=state.GetType();
                Array colors=(Array)stype.GetProperty("NccRgbaLut")!.GetValue(state)!;
                Array packed=(Array)stype.GetProperty("PackedNccLut")!.GetValue(state)!;
                for(int i=0;i<256;i++)
                {
                    object c=colors.GetValue(i)!, p=packed.GetValue(i)!;
                    Type ct=c.GetType(), pt=p.GetType();
                    ulong rg=(byte)ct.GetProperty("R")!.GetValue(c)! | (ulong)(byte)ct.GetProperty("G")!.GetValue(c)!<<32;
                    if((ulong)pt.GetProperty("Rg")!.GetValue(p)! != rg ||
                        (ulong)pt.GetProperty("B")!.GetValue(p)! != (byte)ct.GetProperty("B")!.GetValue(c)!)
                        throw new InvalidOperationException("Stale packed NCC lookup");
                }
            }
            int ncc = (int)type.GetField("RegNccTable", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
            void Write(int tmu, int register, uint value) => Call("WriteTmuRegisterOrPalette",tmu,register,value);
            var random = new Random(91826);
            uint[] ram = (uint[])Field("_textureMemory");
            for (int i=0;i<ram.Length;i++) ram[i]=(uint)random.NextInt64(1L<<32);
            for(int t=0;t<2;t++) for(int r=0;r<24;r++) Write(t,ncc+r,(uint)random.NextInt64(1L<<31));
            int cases=0;
            object State(int tmu, int format, uint bits)
            {
                Write(tmu,0xc0,0x08240000U | (uint)format<<8 | bits);
                Write(tmu,0xc1,0x800);
                Write(tmu,0xc3,tmu==0 ? 0U : 0x10000U);
                return Call("BuildMameTextureTriangleState",tmu)!;
            }
            void Compare(int tmu, object state, long s, long t, long w, int lod)
            {
                var packedProperty = state.GetType().GetProperty("PackedNccLut")!;
                object? packed=packedProperty.GetValue(state);
                object expected;
                packedProperty.SetValue(state,null);
                expected=Call("SampleTextureMameFixedForTmu",tmu,s,t,w,lod,state,0.0)!;
                string[] diagnosticFields=["_lastTextureSampleByteAddress","_lastTextureSampleRaw","_lastTextureSampleResult","_lastTextureSampleValid"];
                bool tracked=(bool)state.GetType().GetProperty("TrackSampleDiagnostics")!.GetValue(state)!;
                object[] before=diagnosticFields.Select(Field).ToArray();
                if(tracked) foreach(string name in diagnosticFields)
                {
                    FieldInfo f=type.GetField(name,flags)!;
                    f.SetValue(backend,Activator.CreateInstance(f.FieldType));
                }
                packedProperty.SetValue(state,packed);
                object actual=Call("SampleTextureMameFixedForTmu",tmu,s,t,w,lod,state,0.0)!;
                if(!expected.Equals(actual)) throw new InvalidOperationException($"Fused NCC mismatch tmu={tmu} lod={lod}: {expected} != {actual}");
                if(!before.SequenceEqual(diagnosticFields.Select(Field))) throw new InvalidOperationException("Sample diagnostic state differs");
                cases++;
            }
            foreach(int tmu in new[]{0,1}) foreach(int format in new[]{1,9})
            foreach(uint bits in new uint[]{6,7,0x26,0x27,0xc7,0xe7})
            foreach(int variants in Enumerable.Range(0,8))
            {
                type.GetField("_experimentReverse8BitTextureSampleLanes",flags)!.SetValue(backend,(variants&1)!=0);
                type.GetField("_experimentReverse16BitTextureSampleLanes",flags)!.SetValue(backend,(variants&2)!=0);
                type.GetField("_experimentTmu1SampleTmu0Memory",flags)!.SetValue(backend,(variants&4)!=0);
                object state=State(tmu,format,bits);
                if(state.GetType().GetProperty("PackedNccLut")!.GetValue(state) is null) throw new InvalidOperationException("Missing packed table");
                state.GetType().GetProperty("Swap16BitBytes")!.SetValue(state,(variants&1)!=0);
                for(int i=0;i<100;i++)
                {
                    long w=(i%7) switch {0=>0,1=>-1,2=>long.MinValue,3=>long.MaxValue,_=>random.NextInt64(1,1L<<40)};
                    Compare(tmu,state,random.NextInt64(-1L<<44,1L<<44),random.NextInt64(-1L<<44,1L<<44),w,i%10-1);
                }
                // Real register write must invalidate and rebuild both tables.
                Write(tmu,ncc+(int)((bits>>5)&1)*12,0xfedcba98);
                object rebuilt=Call("BuildMameTextureTriangleState",tmu)!;
                CheckLut(rebuilt);
                Compare(tmu,rebuilt,1L<<28,2L<<28,1L<<32,0);
                // Tracked diagnostics must use the reference path.
                rebuilt.GetType().GetProperty("TrackSampleDiagnostics")!.SetValue(rebuilt,true);
                Compare(tmu,rebuilt,1L<<28,2L<<28,1L<<32,0);
            }
            // Raw restore bypasses guest writes: use the exact invalidation
            // hook called by LoadVoodoo, then rebuild from replaced registers.
            ((uint[][])Field("_tmuRegisters"))[0][ncc]=0x12345678;
            type.GetMethod("InvalidateNccSamplingCaches")!.Invoke(backend,null);
            if(((Array)Field("_packedNccLuts")).Cast<object?>().Any(x=>x is not null))
                throw new InvalidOperationException("Restore retained packed cache");
            object restored=State(0,1,7);
            CheckLut(restored);
            Compare(0,restored,0,0,1L<<32,0);
            // Coverage canary: changing only packed RGB must affect the fused
            // sampler. Otherwise differential tests might exercise fallback only.
            uint[] previousRegisters=((uint[][])Field("_tmuRegisters"))[0].Skip(ncc).Take(12).ToArray();
            for(int r=0;r<12;r++) Write(0,ncc+r,r<4 ? 0x80808080U : 0U);
            object canary=State(0,1,6);
            object normal=Call("SampleTextureMameFixedForTmu",0,0L,0L,0L,0,canary,0.0)!;
            Array canaryLut=(Array)canary.GetType().GetProperty("PackedNccLut")!.GetValue(canary)!;
            Array copy=(Array)canaryLut.Clone();
            Array.Clear(canaryLut);
            object changed=Call("SampleTextureMameFixedForTmu",0,0L,0L,0L,0,canary,0.0)!;
            copy.CopyTo(canaryLut,0);
            if(normal.Equals(changed)) throw new InvalidOperationException("Fused sampler was not exercised");
            for(int r=0;r<12;r++) Write(0,ncc+r,previousRegisters[r]);
            cases++;
            // Exhaustive fractions, including 256, versus existing filter math.
            object st=State(0,9,7);
            Array colors=(Array)st.GetType().GetProperty("NccRgbaLut")!.GetValue(st)!;
            object lut=st.GetType().GetProperty("PackedNccLut")!.GetValue(st)!;
            Type rgba=colors.GetType().GetElementType()!;
            object Color(byte index, byte alpha)
            {
                object c=colors.GetValue(index)!;
                return Activator.CreateInstance(rgba, [rgba.GetProperty("R")!.GetValue(c)!,rgba.GetProperty("G")!.GetValue(c)!,rgba.GetProperty("B")!.GetValue(c)!,alpha])!;
            }
            MethodInfo reference=type.GetMethod("BilinearTextureRgba",BindingFlags.Static|BindingFlags.NonPublic)!;
            MethodInfo fused=type.GetMethod("FilterPackedNcc",BindingFlags.Static|BindingFlags.NonPublic)!;
            foreach(bool alpha in new[]{false,true})
            {
                object[] colors4=[Color(0,alpha?(byte)0:(byte)255),Color(63,255),Color(129,alpha?(byte)1:(byte)255),Color(255,alpha?(byte)127:(byte)255)];
                for(int x=0;x<=256;x++) for(int y=0;y<=256;y++)
                {
                    object a=reference.Invoke(null,[colors4[0],colors4[1],colors4[2],colors4[3],x,y])!;
                    object b=fused.Invoke(null,[lut,(ushort)0,(ushort)0xff3f,(ushort)0x0181,(ushort)0x7fff,alpha,x,y])!;
                    if(!a.Equals(b)) throw new InvalidOperationException($"Packed filter mismatch {x},{y}");
                    cases++;
                }
            }
            Console.WriteLine($"fusedNccChecks PASS cases={cases}");
        }
        finally { Environment.SetEnvironmentVariable(env,saved); }
    }
}
