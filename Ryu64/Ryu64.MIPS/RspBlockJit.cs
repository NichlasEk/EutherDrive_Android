using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace Ryu64.MIPS
{
    internal sealed partial class RspInterpreter
    {
        // Default-on after SM64 differential/replay checks. Keep an explicit
        // interpreter override for diagnosis and platform fallback below.
        private static readonly bool BlockJitEnabled =
            Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_RSP_BLOCK_JIT") != "0";
        private const int MaxBlockInstructions = 16;
        // Per interpreter: no cross-emulator mutable compilation cache.
        [NonSerialized] private readonly Func<RspInterpreter, uint, uint, int>[] _blocks = BlockJitEnabled ? new Func<RspInterpreter, uint, uint, int>[1024] : null;
        [NonSerialized] private readonly uint[] _blockFirstWords = BlockJitEnabled ? new uint[1024] : null;
        [NonSerialized] private bool _blockJitUnavailable;
        [NonSerialized] private readonly Dictionary<string, Func<RspInterpreter, uint, uint, int>> _compiledBlocks = BlockJitEnabled ? new Dictionary<string, Func<RspInterpreter, uint, uint, int>>() : null;
        [NonSerialized] private long _blockInstructions;
        [NonSerialized] private int _blockCompilations;

        private int TryExecuteBlock(uint budget, uint stagnantLimit)
        {
            // Only DMEM/register operations and a final branch/delay pair compile.
            // Without pending lifecycle work, neither code nor MMIO can change.
            if (_blockJitUnavailable || _memory.RspLifecyclePending) return 0;
            uint pc = _pc & 0xffc;
            int slot = (int)(pc >> 2);
            uint word = _memory.ReadSpImemWord(pc);
            var block = _blocks[slot];
            if (block == null || _blockFirstWords[slot] != word)
            {
                block = GetOrCompileBlock(pc, slot, word);
                if (block == null) return 0;
            }
            int count = block(this, budget, stagnantLimit);
            _blockInstructions += count;
            // The complete block is checked before executing anything. A changed
            // interior word invalidates this entry; the interpreter takes over.
            if (count == 0 && block != NoBlock && budget > 0 && _stagnantInstructionCount < stagnantLimit)
                _blocks[slot] = null;
            return count;
        }

        // Compilation and its platform fallback are cold. Keeping the exception
        // region here leaves the normal cached-block dispatch free of EH state.
        private Func<RspInterpreter, uint, uint, int> GetOrCompileBlock(uint pc, int slot, uint word)
        {
            Func<RspInterpreter, uint, uint, int> block;
            try { block = CompileBlock(pc); }
            catch (PlatformNotSupportedException) { _blockJitUnavailable = true; return null; }
            _blocks[slot] = block;
            _blockFirstWords[slot] = word;
            return block;
        }

        private static readonly Func<RspInterpreter, uint, uint, int> NoBlock = (r, b, s) => 0;

        private Func<RspInterpreter, uint, uint, int> CompileBlock(uint start)
        {
            // Overlay switches often revisit an already compiled block. Look up
            // its complete identity before allocating any expression nodes.
            var key = new System.Text.StringBuilder(start.ToString("x3"));
            int length = 0;
            for (; length < MaxBlockInstructions && start + length * 4 < 4096; length++)
            {
                uint word = _memory.ReadSpImemWord(start + (uint)length * 4);
                if (IsBlockBranch(word))
                {
                    if (length < MaxBlockInstructions - 1 && start + length * 4 + 4 < 4096)
                    {
                        uint delay = _memory.ReadSpImemWord(start + (uint)length * 4 + 4);
                        if (IsBlockInstruction(delay))
                        {
                            key.Append(':').Append(word.ToString("x8")).Append(':').Append(delay.ToString("x8"));
                            length += 2;
                        }
                    }
                    break;
                }
                if (!IsBlockInstruction(word)) break;
                key.Append(':').Append(word.ToString("x8"));
            }
            if (length < 2) return NoBlock;
            string identity = key.ToString();
            if (_compiledBlocks.TryGetValue(identity, out var cached)) return cached;
            if (_compiledBlocks.Count >= 2048) return NoBlock;
            var self = Expression.Parameter(typeof(RspInterpreter), "rsp");
            var budget = Expression.Parameter(typeof(uint), "budget");
            var limit = Expression.Parameter(typeof(uint), "limit");
            var done = Expression.Label(typeof(int));
            var nextPc = Expression.Variable(typeof(uint), "nextPc");
            var code = new List<Expression>();
            code.Add(Expression.Assign(nextPc, Expression.Constant((start + (uint)length * 4) & 0xffc)));
            var guards = new List<Expression>();
            var words = new uint[length];
            Expression Call(string name, params Expression[] args) => Expression.Call(self,
                typeof(RspInterpreter).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic), args);
            int count = 0;
            int pendingProgress = 0;
            for (; count < length; count++)
            {
                uint pc = start + (uint)count * 4;
                uint word = _memory.ReadSpImemWord(pc);
                words[count] = word;
                Expression operation = CompileScalarExpression(self, word);
                if (IsBlockBranch(word)) operation = CompileBlockBranch(self, nextPc, pc, word);
                int vectorOp = (int)(word & 63);
                if ((word >> 25) == 0x25 && (vectorOp <= 0x11 || vectorOp == 0x13
                    || vectorOp == 0x14 || vectorOp == 0x15 || vectorOp == 0x1d
                    || (vectorOp >= 0x20 && vectorOp <= 0x2d) || (vectorOp >= 0x30 && vectorOp <= 0x37)))
                {
                    var reason = Expression.Variable(typeof(string));
                    operation = Expression.Block(new[] { reason }, Call("ExecuteVectorCompute",
                        Expression.Constant(pc), Expression.Constant(word), reason));
                    if (vectorOp >= 0x0d && vectorOp <= 0x0f)
                        operation = Call(nameof(ExecuteBlockVectorAccumulate), Expression.Constant(vectorOp),
                            Expression.Constant((int)((word >> 6) & 31)),
                            Expression.Constant((int)((word >> 11) & 31)),
                            Expression.Constant((int)((word >> 16) & 31)),
                            Expression.Constant((int)((word >> 21) & 15)));
#if NET8_0_OR_GREATER
                    if (VectorSimdEnabled && IsSimdVectorOp(vectorOp))
                        operation = Call(nameof(ExecuteVectorSimd), Expression.Constant(vectorOp),
                            Expression.Constant((int)((word >> 6) & 31)),
                            Expression.Constant((int)((word >> 11) & 31)),
                            Expression.Constant((int)((word >> 16) & 31)),
                            Expression.Constant((int)((word >> 21) & 15)));
#endif
                }
                if ((word >> 26 == 0x32 || word >> 26 == 0x3a) && ((word >> 11) & 31) <= 11)
                    operation = CompileVectorMemoryExpression(self, word);
                if (operation == null) throw new InvalidOperationException("Block decoder and compiler disagree.");
                // Compare adjacent instruction bytes together. BitConverter's
                // unaligned native reads avoid decoding every word just to test
                // equality; the constants use the host's byte order as well.
                if ((count & 1) == 0)
                {
                    bool pair = count + 1 < length;
                    var bytes = Expression.Field(Expression.Field(self, "_memory"), "SP_MEM_RW");
                    var read = Expression.Call(typeof(BitConverter).GetMethod(pair ? "ToUInt64" : "ToUInt32",
                        new[] { typeof(byte[]), typeof(int) }), bytes, Expression.Constant((int)(0x1000 + pc)));
                    Expression expected;
                    if (pair)
                    {
                        uint next = _memory.ReadSpImemWord(pc + 4);
                        ulong packed = BitConverter.IsLittleEndian
                            ? (ulong)SwapBlockWord(word) | ((ulong)SwapBlockWord(next) << 32)
                            : ((ulong)word << 32) | next;
                        expected = Expression.Constant(packed);
                    }
                    else expected = Expression.Constant(BitConverter.IsLittleEndian ? SwapBlockWord(word) : word);
                    guards.Add(Expression.IfThen(Expression.NotEqual(read, expected), Expression.Return(done, Expression.Constant(0))));
                }
                code.Add(operation);
                if (count == 0)
                    code.Add(Expression.Assign(Expression.ArrayAccess(Expression.Field(self, "_gpr"), Expression.Constant(0)), Expression.Constant(0u)));
                pendingProgress++;
                int op = (int)(word >> 26);
                int target = (int)((word >> (op == 0 ? 11 : 16)) & 31);
                bool writesTrackedGpr = op == 3 || (!IsBlockBranch(word)
                    && !IsScalarStore(op) && op != 0x12 && op != 0x32 && op != 0x3a
                    && (ProgressGprMask & (1u << target)) != 0);
                if (writesTrackedGpr)
                {
                    code.Add(Call(nameof(UpdateBlockProgress), Expression.Constant((uint)pendingProgress)));
                    pendingProgress = 0;
                }
            }
            if (pendingProgress != 0)
                code.Add(Call(nameof(UpdateBlockProgress), Expression.Constant((uint)pendingProgress)));
            code.Add(Call(nameof(EndBlock), Expression.Constant(start), Expression.Constant(words), nextPc));
            guards.Insert(0, Expression.IfThen(Expression.OrElse(
                Expression.LessThan(budget, Expression.Constant((uint)count)),
                Expression.OrElse(Expression.LessThan(limit, Expression.Constant((uint)count)),
                    Expression.GreaterThanOrEqual(Expression.Field(self, "_stagnantInstructionCount"), Expression.Subtract(limit, Expression.Constant((uint)count))))),
                Expression.Return(done, Expression.Constant(0))));
            code.InsertRange(0, guards);
            code.Add(Expression.Label(done, Expression.Constant(count)));
            var compiled = Expression.Lambda<Func<RspInterpreter, uint, uint, int>>(Expression.Block(new[] { nextPc }, code), self, budget, limit).Compile();
            _compiledBlocks.Add(identity, compiled);
            _blockCompilations++;
            return compiled;
        }

        private static bool IsBlockInstruction(uint word)
        {
            int op = (int)(word >> 26), function = (int)(word & 63);
            if ((word >> 25) == 0x25)
                return function <= 0x11 || function == 0x13 || function == 0x14 || function == 0x15
                    || function == 0x1d || (function >= 0x20 && function <= 0x2d)
                    || (function >= 0x30 && function <= 0x37);
            if (op == 0x32 || op == 0x3a) return ((word >> 11) & 31) <= 11;
            return IsScalarBlockInstruction(word);
        }

        private static bool IsScalarStore(int op) => op == 0x28 || op == 0x29 || op == 0x2b;

        private static Expression CompileVectorMemoryExpression(ParameterExpression self, uint word)
        {
            // Decode immutable instruction fields once, but read the base GPR
            // at execution time. Keep all alignment/wrapping/aliasing semantics
            // in the same transfer helpers used by the reference interpreter.
            int subop = (int)((word >> 11) & 31);
            bool load = word >> 26 == 0x32;
            int shift = subop <= 3 ? subop : subop == 6 || subop == 7 ? 3 : 4;
            int offset = SignExtend7((int)(word & 127)) << shift;
            var address = Expression.Add(Expression.ArrayIndex(Expression.Field(self, "_gpr"),
                Expression.Constant((int)((word >> 21) & 31))), Expression.Constant(unchecked((uint)offset)));
            var vt = Expression.Constant((int)((word >> 16) & 31));
            var element = Expression.Constant((int)((word >> 7) & 15));
            Expression Call(string name, params Expression[] args) => Expression.Call(self,
                typeof(RspInterpreter).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic), args);
            if (subop <= 3)
                return Call(nameof(TransferVectorBytes), Expression.Constant(load), vt, element, address,
                    Expression.Constant(1 << subop), Expression.Constant(subop != 0));
            if (subop == 6 || subop == 7)
                return Call(nameof(TransferVectorPacked), Expression.Constant(load), vt, element, address,
                    Expression.Constant(subop == 7));
            if (subop == 9)
                return Call(load ? nameof(LoadVectorFour) : nameof(StoreVectorFour), vt, element, address);
            string helper = subop == 4 ? nameof(TransferVectorQuad)
                : subop == 5 ? nameof(TransferVectorReverse)
                : subop == 8 ? nameof(TransferVectorHalfPacked)
                : subop == 10 ? nameof(TransferVectorWrapped) : nameof(TransferVectorTable);
            return Call(helper, Expression.Constant(load), vt, element, address);
        }

        private static bool IsScalarBlockInstruction(uint word)
        {
            int op = (int)(word >> 26), function = (int)(word & 63);
            return op == 0 ? function == 0 || function == 2 || function == 3 || function == 4
                || function == 6 || function == 7 || (function >= 0x20 && function <= 0x27)
                || function == 0x2a || function == 0x2b
                : (op >= 8 && op <= 15) || op == 0x20 || op == 0x21 || op == 0x23
                    || op == 0x24 || op == 0x25 || IsScalarStore(op);
        }

        private static uint SwapBlockWord(uint word) => (word >> 24) | ((word >> 8) & 0xff00)
            | ((word << 8) & 0xff0000) | (word << 24);

        private static bool IsBlockBranch(uint word)
        {
            uint op = word >> 26;
            return (op >= 2 && op <= 7) || (op == 0 && (word & 63) == 8);
        }

        private static Expression CompileBlockBranch(ParameterExpression self, ParameterExpression nextPc, uint pc, uint word)
        {
            uint op = word >> 26;
            Expression Register(int index) => Expression.ArrayIndex(Expression.Field(self, "_gpr"), Expression.Constant(index));
            Expression rs = Register((int)((word >> 21) & 31)), rt = Register((int)((word >> 16) & 31));
            Expression take = op == 4 ? Expression.Equal(rs, rt)
                : op == 5 ? Expression.NotEqual(rs, rt)
                : op == 6 ? Expression.LessThanOrEqual(Expression.Convert(rs, typeof(int)), Expression.Constant(0))
                : op == 7 ? Expression.GreaterThan(Expression.Convert(rs, typeof(int)), Expression.Constant(0))
                : (Expression)Expression.Constant(true);
            Expression target = op == 0 ? rs : Expression.Constant(op <= 3
                ? (word & 0x03ffffff) << 2 : BranchTarget(pc, unchecked((short)word)));
            Expression branch = Expression.IfThen(take, Expression.Block(
                Expression.Assign(Expression.Field(self, "_branchTarget"), target),
                Expression.Assign(nextPc, Expression.And(Expression.Field(self, "_branchTarget"), Expression.Constant(0xffcu)))));
            if (op != 3) return branch;
            return Expression.Block(Expression.Call(self, typeof(RspInterpreter).GetMethod("WriteGpr", BindingFlags.Instance | BindingFlags.NonPublic),
                Expression.Constant(31u), Expression.Constant((pc + 8) & 0xffc)), branch);
        }

        private void EndBlock(uint start, uint[] words, uint nextPc)
        {
            // No block can stop partway: its budget and code were checked first.
            // Preserve the exact ring history, but publish it once per block.
            int index = _recentIndex;
            for (int i = 0; i < words.Length; i++)
            {
                _recentPcs[index] = start + (uint)i * 4;
                _recentInstrs[index] = words[i];
                index = (index + 1) & (RecentInstructionCount - 1);
            }
            _recentIndex = index;
            uint pc = start + (uint)(words.Length - 1) * 4;
            // Active trace PC is observed by CP0 operations, which never compile.
            // Preserve its saved final value even though tracing is inactive here.
            _memory.SetActiveRspTracePc(pc);
            _memory.ClearActiveRspTracePc();
            // A block contains at least two distinct sequential PCs; the final
            // instruction therefore always resets the repeated-PC counter.
            _lastPc = pc;
            _lastInstr = words[words.Length - 1];
            _samePcRunLength = 0;
            _pc = nextPc;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void UpdateBlockProgress(uint instructions)
        {
            ulong signature = _progressRegistersDirty
                ? ComputeProgressSignature(true) : _lastProgressSignature;
            _progressRegistersDirty = false;
            if (signature != _lastProgressSignature)
            { _lastProgressSignature = signature; _stagnantInstructionCount = 0; }
            else _stagnantInstructionCount += instructions;
        }

        private static Expression CompileScalarExpression(ParameterExpression self, uint word)
        {
            int op = (int)(word >> 26), function = (int)(word & 63);
            if (!IsScalarBlockInstruction(word)) return null;
            bool special = op == 0;
            int rs = (int)((word >> 21) & 31), rt = (int)((word >> 16) & 31), rd = (int)((word >> 11) & 31);
            int shift = (int)((word >> 6) & 31);
            Expression Register(int index) => Expression.ArrayIndex(Expression.Field(self, "_gpr"), Expression.Constant(index));
            Expression Uint(uint bits) => Expression.Constant(bits);
            Expression Call(string name, params Expression[] args) => Expression.Call(self,
                typeof(RspInterpreter).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic), args);
            Expression value;
            if (special)
            {
                Expression left = Register(rs), right = Register(rt);
                switch (function)
                {
                    case 0: value = Expression.LeftShift(right, Expression.Constant(shift)); break;
                    case 2: value = Expression.RightShift(right, Expression.Constant(shift)); break;
                    case 3: value = Expression.Convert(Expression.RightShift(Expression.Convert(right, typeof(int)), Expression.Constant(shift)), typeof(uint)); break;
                    case 4: value = Expression.LeftShift(right, Expression.Convert(Expression.And(left, Uint(31)), typeof(int))); break;
                    case 6: value = Expression.RightShift(right, Expression.Convert(Expression.And(left, Uint(31)), typeof(int))); break;
                    case 7: value = Expression.Convert(Expression.RightShift(Expression.Convert(right, typeof(int)), Expression.Convert(Expression.And(left, Uint(31)), typeof(int))), typeof(uint)); break;
                    case 0x20:
                    case 0x21: value = Expression.Add(left, right); break;
                    case 0x22:
                    case 0x23: value = Expression.Subtract(left, right); break;
                    case 0x24: value = Expression.And(left, right); break;
                    case 0x25: value = Expression.Or(left, right); break;
                    case 0x26: value = Expression.ExclusiveOr(left, right); break;
                    case 0x27: value = Expression.Not(Expression.Or(left, right)); break;
                    case 0x2a: value = Expression.Condition(Expression.LessThan(Expression.Convert(left, typeof(int)), Expression.Convert(right, typeof(int))), Uint(1), Uint(0)); break;
                    default: value = Expression.Condition(Expression.LessThan(left, right), Uint(1), Uint(0)); break;
                }
            }
            else if (op == 15) value = Uint(word << 16);
            else if (op >= 0x20)
            {
                Expression address = Expression.Add(Register(rs), Uint(unchecked((uint)(short)word)));
                if (op == 0x28) return Call("WriteByte", address, Expression.Convert(Register(rt), typeof(byte)));
                if (op == 0x29) return Call("WriteHalf", address, Expression.Convert(Register(rt), typeof(ushort)));
                if (op == 0x2b) return Call("WriteWord", address, Register(rt));
                value = Call(op == 0x20 || op == 0x24 ? "ReadByte" : op == 0x21 || op == 0x25 ? "ReadHalf" : "ReadWord", address);
                if (op == 0x20 || op == 0x21)
                    value = Expression.Convert(Expression.Convert(value, op == 0x20 ? typeof(sbyte) : typeof(short)), typeof(int));
                if (value.Type != typeof(uint)) value = Expression.Convert(value, typeof(uint));
            }
            else
            {
                Expression right = Uint(op <= 11 ? unchecked((uint)(short)word) : word & 65535);
                value = op <= 9 ? (Expression)Expression.Add(Register(rs), right)
                    : op == 10 ? (Expression)Expression.Condition(Expression.LessThan(Expression.Convert(Register(rs), typeof(int)), Expression.Constant((int)(short)word)), Uint(1), Uint(0))
                    : op == 11 ? (Expression)Expression.Condition(Expression.LessThan(Register(rs), right), Uint(1), Uint(0))
                    : op == 12 ? Expression.And(Register(rs), right)
                    : op == 13 ? Expression.Or(Register(rs), right)
                    : Expression.ExclusiveOr(Register(rs), right);
            }
            return Call("WriteGpr", Uint((uint)(special ? rd : rt)), value);
        }

    }
}
