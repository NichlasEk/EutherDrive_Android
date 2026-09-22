#if NET8_0_OR_GREATER
using System;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Ryu64.MIPS
{
    internal sealed partial class RspInterpreter
    {
        // The portable interpreter remains the fallback for older targets and
        // hosts without SSSE3. No architectural state depends on this choice.
        private static readonly bool VectorSimdEnabled = Ssse3.IsSupported
            && Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_RSP_SIMD") != "0";
        private static class VectorSimdMasks
        {
            // Initialize after the outer type's shuffle setting, independently
            // of the compiler's ordering of partial-class field initializers.
            internal static readonly Vector128<byte>[] ByteShuffles = CreateVectorByteShuffles();
        }

        private static bool IsSimdVectorOp(int op) => op <= 0x01 || (op >= 0x04 && op <= 0x09)
            || (op >= 0x0c && op <= 0x11) || op == 0x13 || op == 0x14 || op == 0x15
            || (op >= 0x20 && op <= 0x2d);

        private static Vector128<byte>[] CreateVectorByteShuffles()
        {
            var masks = new Vector128<byte>[16];
            byte[] bytes = new byte[16];
            for (int element = 0; element < masks.Length; element++)
            {
                for (int lane = 0; lane < 8; lane++)
                {
                    int source = element < 2 ? lane
                        : element < 4 ? (lane & ~1) + (element & 1)
                        : element < 8 ? element - 4 + ((lane & (StrictHalfVectorShuffle ? 4 : 2)) == 0 ? 0 : 4)
                        : element - 8;
                    // Fuse element selection with the register's big-endian
                    // halfword conversion. Destination lanes stay in RSP order.
                    bytes[lane * 2] = (byte)(source * 2 + 1);
                    bytes[lane * 2 + 1] = (byte)(source * 2);
                }
                masks[element] = Vector128.LoadUnsafe(ref bytes[0]);
            }
            return masks;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<ushort> ReadVectorPlane(ushort[] plane) => Vector128.LoadUnsafe(ref plane[0]);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteVectorPlane(ushort[] plane, Vector128<ushort> value) => value.StoreUnsafe(ref plane[0]);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<ushort> VectorSign(Vector128<ushort> value) => Sse2.ShiftRightArithmetic(value.AsInt16(), 15).AsUInt16();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<ushort> VectorLessUnsigned(Vector128<ushort> left, Vector128<ushort> right)
        {
            var sign = Vector128.Create((ushort)0x8000);
            return Sse2.CompareGreaterThan(Sse2.Xor(right, sign).AsInt16(), Sse2.Xor(left, sign).AsInt16()).AsUInt16();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<ushort> VectorSelect(Vector128<ushort> mask, Vector128<ushort> yes, Vector128<ushort> no)
            => Sse2.Or(Sse2.And(mask, yes), Sse2.AndNot(mask, no));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<ushort> VectorSignedClamp(Vector128<ushort> hi, Vector128<ushort> md)
            => Sse2.PackSignedSaturate(Sse2.UnpackLow(md, hi).AsInt32(), Sse2.UnpackHigh(md, hi).AsInt32()).AsUInt16();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<ushort> VectorUnsignedClamp(Vector128<ushort> hi, Vector128<ushort> md, Vector128<ushort> lo)
        {
            var inRange = Sse2.CompareEqual(hi, VectorSign(md));
            return VectorSelect(inRange, lo, Sse2.Xor(VectorSign(hi), Vector128.Create(ushort.MaxValue)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<ushort> VectorFlagLanes(ushort bits)
        {
            var weights = Vector128.Create((ushort)1, 2, 4, 8, 16, 32, 64, 128);
            return Sse2.CompareEqual(Sse2.And(Vector128.Create(bits), weights), weights);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AddVectorAccumulator(Vector128<ushort> termLo, Vector128<ushort> termMd, Vector128<ushort> termHi,
            out Vector128<ushort> lo, out Vector128<ushort> md, out Vector128<ushort> hi)
        {
            var oldLo = ReadVectorPlane(_accLo);
            var oldMd = ReadVectorPlane(_accMd);
            lo = Sse2.Add(oldLo, termLo);
            var carryLo = VectorLessUnsigned(lo, oldLo);
            var sumMd = Sse2.Add(oldMd, termMd);
            md = Sse2.Subtract(sumMd, carryLo); // true masks are -1: subtracting adds the carry.
            var carryMd = Sse2.Or(VectorLessUnsigned(sumMd, oldMd), VectorLessUnsigned(md, sumMd));
            hi = Sse2.Subtract(Sse2.Add(ReadVectorPlane(_accHi), termHi), carryMd);
        }

        // Value-type specializations let the host JIT fold the operation tests.
        // The interpreter uses the same body with a runtime operation number.
        private interface IVectorSimdOperation { static abstract int Opcode { get; } }
        private readonly struct RuntimeVectorOperation : IVectorSimdOperation { public static int Opcode => -1; }
        private readonly struct VectorOperation00 : IVectorSimdOperation { public static int Opcode => 0x00; }
        private readonly struct VectorOperation01 : IVectorSimdOperation { public static int Opcode => 0x01; }
        private readonly struct VectorOperation04 : IVectorSimdOperation { public static int Opcode => 0x04; }
        private readonly struct VectorOperation05 : IVectorSimdOperation { public static int Opcode => 0x05; }
        private readonly struct VectorOperation06 : IVectorSimdOperation { public static int Opcode => 0x06; }
        private readonly struct VectorOperation07 : IVectorSimdOperation { public static int Opcode => 0x07; }
        private readonly struct VectorOperation08 : IVectorSimdOperation { public static int Opcode => 0x08; }
        private readonly struct VectorOperation09 : IVectorSimdOperation { public static int Opcode => 0x09; }
        private readonly struct VectorOperation0C : IVectorSimdOperation { public static int Opcode => 0x0c; }
        private readonly struct VectorOperation0D : IVectorSimdOperation { public static int Opcode => 0x0d; }
        private readonly struct VectorOperation0E : IVectorSimdOperation { public static int Opcode => 0x0e; }
        private readonly struct VectorOperation0F : IVectorSimdOperation { public static int Opcode => 0x0f; }
        private readonly struct VectorOperation10 : IVectorSimdOperation { public static int Opcode => 0x10; }
        private readonly struct VectorOperation11 : IVectorSimdOperation { public static int Opcode => 0x11; }
        private readonly struct VectorOperation13 : IVectorSimdOperation { public static int Opcode => 0x13; }
        private readonly struct VectorOperation14 : IVectorSimdOperation { public static int Opcode => 0x14; }
        private readonly struct VectorOperation15 : IVectorSimdOperation { public static int Opcode => 0x15; }
        private readonly struct VectorOperation20 : IVectorSimdOperation { public static int Opcode => 0x20; }
        private readonly struct VectorOperation21 : IVectorSimdOperation { public static int Opcode => 0x21; }
        private readonly struct VectorOperation22 : IVectorSimdOperation { public static int Opcode => 0x22; }
        private readonly struct VectorOperation23 : IVectorSimdOperation { public static int Opcode => 0x23; }
        private readonly struct VectorOperation24 : IVectorSimdOperation { public static int Opcode => 0x24; }
        private readonly struct VectorOperation25 : IVectorSimdOperation { public static int Opcode => 0x25; }
        private readonly struct VectorOperation26 : IVectorSimdOperation { public static int Opcode => 0x26; }
        private readonly struct VectorOperation27 : IVectorSimdOperation { public static int Opcode => 0x27; }
        private readonly struct VectorOperation28 : IVectorSimdOperation { public static int Opcode => 0x28; }
        private readonly struct VectorOperation29 : IVectorSimdOperation { public static int Opcode => 0x29; }
        private readonly struct VectorOperation2A : IVectorSimdOperation { public static int Opcode => 0x2a; }
        private readonly struct VectorOperation2B : IVectorSimdOperation { public static int Opcode => 0x2b; }
        private readonly struct VectorOperation2C : IVectorSimdOperation { public static int Opcode => 0x2c; }
        private readonly struct VectorOperation2D : IVectorSimdOperation { public static int Opcode => 0x2d; }

        private static class VectorSimdOperations
        {
            internal static readonly MethodInfo[] Methods = Create();
            private static MethodInfo[] Create()
            {
                var methods = new MethodInfo[64];
                var definition = typeof(RspInterpreter).GetMethod(nameof(ExecuteVectorSimdOperation), BindingFlags.Instance | BindingFlags.NonPublic);
                methods[0x00] = definition.MakeGenericMethod(typeof(VectorOperation00));
                methods[0x01] = definition.MakeGenericMethod(typeof(VectorOperation01));
                methods[0x04] = definition.MakeGenericMethod(typeof(VectorOperation04));
                methods[0x05] = definition.MakeGenericMethod(typeof(VectorOperation05));
                methods[0x06] = definition.MakeGenericMethod(typeof(VectorOperation06));
                methods[0x07] = definition.MakeGenericMethod(typeof(VectorOperation07));
                methods[0x08] = definition.MakeGenericMethod(typeof(VectorOperation08));
                methods[0x09] = definition.MakeGenericMethod(typeof(VectorOperation09));
                methods[0x0c] = definition.MakeGenericMethod(typeof(VectorOperation0C));
                methods[0x0d] = definition.MakeGenericMethod(typeof(VectorOperation0D));
                methods[0x0e] = definition.MakeGenericMethod(typeof(VectorOperation0E));
                methods[0x0f] = definition.MakeGenericMethod(typeof(VectorOperation0F));
                methods[0x10] = definition.MakeGenericMethod(typeof(VectorOperation10));
                methods[0x11] = definition.MakeGenericMethod(typeof(VectorOperation11));
                methods[0x13] = definition.MakeGenericMethod(typeof(VectorOperation13));
                methods[0x14] = definition.MakeGenericMethod(typeof(VectorOperation14));
                methods[0x15] = definition.MakeGenericMethod(typeof(VectorOperation15));
                methods[0x20] = definition.MakeGenericMethod(typeof(VectorOperation20));
                methods[0x21] = definition.MakeGenericMethod(typeof(VectorOperation21));
                methods[0x22] = definition.MakeGenericMethod(typeof(VectorOperation22));
                methods[0x23] = definition.MakeGenericMethod(typeof(VectorOperation23));
                methods[0x24] = definition.MakeGenericMethod(typeof(VectorOperation24));
                methods[0x25] = definition.MakeGenericMethod(typeof(VectorOperation25));
                methods[0x26] = definition.MakeGenericMethod(typeof(VectorOperation26));
                methods[0x27] = definition.MakeGenericMethod(typeof(VectorOperation27));
                methods[0x28] = definition.MakeGenericMethod(typeof(VectorOperation28));
                methods[0x29] = definition.MakeGenericMethod(typeof(VectorOperation29));
                methods[0x2a] = definition.MakeGenericMethod(typeof(VectorOperation2A));
                methods[0x2b] = definition.MakeGenericMethod(typeof(VectorOperation2B));
                methods[0x2c] = definition.MakeGenericMethod(typeof(VectorOperation2C));
                methods[0x2d] = definition.MakeGenericMethod(typeof(VectorOperation2D));
                return methods;
            }
        }

        private static MethodInfo GetSpecializedVectorSimd(int op) => VectorSimdOperations.Methods[op];

        private void ExecuteVectorSimd(int op, int vd, int vs, int vt, int element)
            => ExecuteVectorSimdOperation<RuntimeVectorOperation>(op, vd, vs, vt, element);

        private void ExecuteVectorSimdOperation<TOperation>(int op, int vd, int vs, int vt, int element)
            where TOperation : struct, IVectorSimdOperation
        {
            if (TOperation.Opcode >= 0) op = TOperation.Opcode;
            if (ProfileVectorOps) _vectorOpCounts[op]++;
            // All register indices and selectors are decoded instruction fields.
            // Load both operands before any destination write, including aliases.
            var masks = VectorSimdMasks.ByteShuffles;
            var swap = masks[0];
            var lhs = Ssse3.Shuffle(Vector128.LoadUnsafe(ref _vr[vs * 16]), swap).AsUInt16();
            var rhs = Ssse3.Shuffle(Vector128.LoadUnsafe(ref _vr[vt * 16]), masks[element]).AsUInt16();
            var zero = Vector128<ushort>.Zero;
            Vector128<ushort> result, lo, md, hi;
            if (op <= 0x01)
            {
                var productLo = Sse2.MultiplyLow(lhs, rhs);
                var productHi = Sse2.MultiplyHigh(lhs.AsInt16(), rhs.AsInt16()).AsUInt16();
                var doubledLo = Sse2.ShiftLeftLogical(productLo, 1);
                lo = Sse2.Add(doubledLo, Vector128.Create((ushort)0x8000));
                md = Sse2.Add(Sse2.ShiftLeftLogical(productHi, 1),
                    Sse2.Add(Sse2.ShiftRightLogical(productLo, 15), Sse2.ShiftRightLogical(doubledLo, 15)));
                var negative = VectorSign(md);
                if (op == 0x00) // VMULF, including the -32768 * -32768 exception.
                {
                    var equal = Sse2.CompareEqual(lhs, rhs);
                    hi = Sse2.AndNot(equal, negative);
                    result = Sse2.Add(md, Sse2.And(equal, negative));
                }
                else // VMULU preserves the existing accumulator sign convention.
                {
                    hi = negative;
                    result = Sse2.AndNot(negative, md);
                }
            }
            else if (op <= 0x0f)
            {
                var productLo = Sse2.MultiplyLow(lhs, rhs);
                var productHi = Sse2.MultiplyHigh(lhs, rhs);
                int kind = op & 7;
                if (kind == 0x00 || kind == 0x01 || kind == 0x07)
                    productHi = Sse2.MultiplyHigh(lhs.AsInt16(), rhs.AsInt16()).AsUInt16();
                else if (kind == 0x05)
                    productHi = Sse2.Subtract(productHi, Sse2.And(VectorSign(lhs), rhs));
                else if (kind == 0x06)
                    productHi = Sse2.Subtract(productHi, Sse2.And(VectorSign(rhs), lhs));

                if (kind == 0x00 || kind == 0x01) // VMACF/U: double the signed 32-bit product into 48 bits.
                {
                    lo = Sse2.ShiftLeftLogical(productLo, 1);
                    md = Sse2.Or(Sse2.ShiftLeftLogical(productHi, 1), Sse2.ShiftRightLogical(productLo, 15));
                    hi = VectorSign(productHi);
                }
                else if (kind == 0x04) // VMUDL / VMADL
                {
                    lo = productHi; md = zero; hi = zero;
                }
                else if (kind == 0x07) // VMUDH / VMADH
                {
                    lo = zero; md = productLo; hi = productHi;
                }
                else // VMUDM/N / VMADM/N
                {
                    lo = productLo; md = productHi; hi = VectorSign(productHi);
                }
                if (op >= 0x08)
                    AddVectorAccumulator(lo, md, hi, out lo, out md, out hi);

                if (op == 0x04 || op == 0x06) result = lo;
                else if (op == 0x05) result = md;
                else if (op == 0x0c || op == 0x0e) result = VectorUnsignedClamp(hi, md, lo);
                else if (op == 0x09) // VMACU: negative -> zero, positive overflow -> 0xffff.
                {
                    var negative = VectorSign(hi);
                    var overflow = Sse2.Or(Sse2.CompareGreaterThan(hi.AsInt16(), zero.AsInt16()).AsUInt16(), VectorSign(md));
                    result = Sse2.AndNot(negative, Sse2.Or(overflow, md));
                }
                else result = VectorSignedClamp(hi, md);
            }
            else
            {
                if (op == 0x10 || op == 0x11) // VADD / VSUB, with carry before signed saturation.
                {
                    var carry = VectorFlagLanes(_vco[1]);
                    var aLo = Sse2.UnpackLow(lhs, VectorSign(lhs)).AsInt32();
                    var aHi = Sse2.UnpackHigh(lhs, VectorSign(lhs)).AsInt32();
                    var bLo = Sse2.UnpackLow(rhs, VectorSign(rhs)).AsInt32();
                    var bHi = Sse2.UnpackHigh(rhs, VectorSign(rhs)).AsInt32();
                    var cLo = Sse2.UnpackLow(carry, carry).AsInt32();
                    var cHi = Sse2.UnpackHigh(carry, carry).AsInt32();
                    if (op == 0x10)
                    {
                        lo = Sse2.Subtract(Sse2.Add(lhs, rhs), carry);
                        result = Sse2.PackSignedSaturate(Sse2.Subtract(Sse2.Add(aLo, bLo), cLo), Sse2.Subtract(Sse2.Add(aHi, bHi), cHi)).AsUInt16();
                    }
                    else
                    {
                        lo = Sse2.Add(Sse2.Subtract(lhs, rhs), carry);
                        result = Sse2.PackSignedSaturate(Sse2.Add(Sse2.Subtract(aLo, bLo), cLo), Sse2.Add(Sse2.Subtract(aHi, bHi), cHi)).AsUInt16();
                    }
                    _vco[0] = _vco[1] = 0;
                }
                else if (op == 0x14 || op == 0x15) // VADDC / VSUBC
                {
                    lo = op == 0x14 ? Sse2.Add(lhs, rhs) : Sse2.Subtract(lhs, rhs);
                    var carry = VectorLessUnsigned(op == 0x14 ? lo : lhs, op == 0x14 ? lhs : rhs);
                    _vco[1] = (ushort)Vector128.ExtractMostSignificantBits(carry);
                    _vco[0] = op == 0x14 ? (ushort)0 : (ushort)(~Vector128.ExtractMostSignificantBits(Sse2.CompareEqual(lhs, rhs)) & 0xff);
                    result = lo;
                }
                else if (op == 0x27) // VMRG
                {
                    result = lo = VectorSelect(VectorFlagLanes(_vcc[1]), lhs, rhs);
                    _vco[0] = _vco[1] = 0;
                }
                else if (op == 0x13) // VABS: accumulator wraps, destination saturates.
                {
                    lo = Ssse3.Sign(rhs.AsInt16(), lhs.AsInt16()).AsUInt16();
                    var overflow = Sse2.And(VectorSign(lhs), Sse2.CompareEqual(rhs, Vector128.Create((ushort)0x8000)));
                    result = VectorSelect(overflow, Vector128.Create((ushort)0x7fff), lo);
                }
                else if (op >= 0x20 && op <= 0x23)
                {
                    var equal = Sse2.CompareEqual(lhs, rhs);
                    var co0 = VectorFlagLanes(_vco[0]);
                    Vector128<ushort> select;
                    if (op == 0x20) // VLT
                        select = Sse2.Or(Sse2.CompareGreaterThan(rhs.AsInt16(), lhs.AsInt16()).AsUInt16(),
                            Sse2.And(equal, Sse2.And(co0, VectorFlagLanes(_vco[1]))));
                    else if (op == 0x21) // VEQ
                        select = Sse2.AndNot(co0, equal);
                    else if (op == 0x22) // VNE
                        select = Sse2.Or(Sse2.Xor(equal, Vector128.Create(ushort.MaxValue)), co0);
                    else // VGE
                        select = Sse2.Or(Sse2.CompareGreaterThan(lhs.AsInt16(), rhs.AsInt16()).AsUInt16(),
                            Sse2.AndNot(Sse2.And(co0, VectorFlagLanes(_vco[1])), equal));
                    result = lo = VectorSelect(select, lhs, rhs);
                    _vcc[0] = 0;
                    _vcc[1] = (ushort)Vector128.ExtractMostSignificantBits(select);
                    _vco[0] = _vco[1] = 0;
                }
                else if (op >= 0x24 && op <= 0x26)
                {
                    var all = Vector128.Create(ushort.MaxValue);
                    var sign = op == 0x24 ? VectorFlagLanes(_vco[1]) : VectorSign(Sse2.Xor(lhs, rhs));
                    var adjusted = Sse2.Xor(rhs, sign);
                    if (op != 0x26) adjusted = Sse2.Subtract(adjusted, sign);
                    Vector128<ushort> ge, le;
                    if (op == 0x24) // VCL updates only lanes not marked unequal by VCH.
                    {
                        var eq = VectorFlagLanes(_vco[0]);
                        var sum = Sse2.Add(lhs, rhs);
                        var sumZero = Sse2.CompareEqual(sum, zero);
                        var carry = VectorLessUnsigned(sum, lhs);
                        // Widened sum <= 0x10000, or == 0 when VCE is clear.
                        var leTest = VectorSelect(VectorFlagLanes(_vce),
                            Sse2.Or(Sse2.Xor(carry, all), sumZero), Sse2.AndNot(carry, sumZero));
                        le = VectorSelect(Sse2.AndNot(eq, sign), leTest, VectorFlagLanes(_vcc[1]));
                        ge = VectorSelect(Sse2.AndNot(Sse2.Or(sign, eq), all),
                            Sse2.Xor(VectorLessUnsigned(lhs, rhs), all), VectorFlagLanes(_vcc[0]));
                        // Preserve unused upper bits just as SetMaskBit does.
                        _vcc[0] = (ushort)((_vcc[0] & 0xff00) | Vector128.ExtractMostSignificantBits(ge));
                        _vcc[1] = (ushort)((_vcc[1] & 0xff00) | Vector128.ExtractMostSignificantBits(le));
                        _vco[0] = _vco[1] = 0;
                        _vce = 0;
                    }
                    else if (op == 0x25) // VCH uses wrapping signed 16-bit differences.
                    {
                        var diff = Sse2.Subtract(lhs, adjusted);
                        ge = VectorSelect(sign, VectorSign(rhs), Sse2.Xor(VectorSign(diff), all));
                        le = VectorSelect(sign,
                            Sse2.Xor(Sse2.CompareGreaterThan(diff.AsInt16(), zero.AsInt16()).AsUInt16(), all), VectorSign(rhs));
                        var vce = Sse2.And(sign, Sse2.CompareEqual(diff, all));
                        var unequal = Sse2.Xor(Sse2.Or(Sse2.CompareEqual(diff, zero), vce), all);
                        _vcc[0] = (ushort)Vector128.ExtractMostSignificantBits(ge);
                        _vcc[1] = (ushort)Vector128.ExtractMostSignificantBits(le);
                        _vco[0] = (ushort)Vector128.ExtractMostSignificantBits(unequal);
                        _vco[1] = (ushort)Vector128.ExtractMostSignificantBits(sign);
                        _vce = (byte)Vector128.ExtractMostSignificantBits(vce);
                    }
                    else // VCR: opposite-sign inputs cannot overflow the signed sum.
                    {
                        ge = VectorSelect(sign, VectorSign(rhs),
                            Sse2.Xor(Sse2.CompareGreaterThan(rhs.AsInt16(), lhs.AsInt16()).AsUInt16(), all));
                        le = VectorSelect(sign,
                            Sse2.Xor(Sse2.CompareGreaterThan(Sse2.Add(lhs, rhs).AsInt16(), zero.AsInt16()).AsUInt16(), all), VectorSign(rhs));
                        _vcc[0] = (ushort)Vector128.ExtractMostSignificantBits(ge);
                        _vcc[1] = (ushort)Vector128.ExtractMostSignificantBits(le);
                        _vco[0] = _vco[1] = 0;
                        _vce = 0;
                    }
                    result = lo = VectorSelect(VectorSelect(sign, le, ge), adjusted, lhs);
                }
                else
                {
                    result = op <= 0x29 ? Sse2.And(lhs, rhs) : op <= 0x2b ? Sse2.Or(lhs, rhs) : Sse2.Xor(lhs, rhs);
                    if ((op & 1) != 0) result = Sse2.Xor(result, Vector128.Create(ushort.MaxValue));
                    lo = result;
                }
                // Non-multiply operations only replace the low accumulator plane.
                WriteVectorPlane(_accLo, lo);
                Ssse3.Shuffle(result.AsByte(), swap).StoreUnsafe(ref _vr[vd * 16]);
                return;
            }
            WriteVectorPlane(_accLo, lo);
            WriteVectorPlane(_accMd, md);
            WriteVectorPlane(_accHi, hi);
            Ssse3.Shuffle(result.AsByte(), swap).StoreUnsafe(ref _vr[vd * 16]);
        }
    }
}
#endif
