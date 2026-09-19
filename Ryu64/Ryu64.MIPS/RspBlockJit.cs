using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace Ryu64.MIPS
{
    internal sealed partial class RspInterpreter
    {
        // Experimental, deliberately off by default: correctness passes, but the
        // captured SM64 workload is currently slower than the interpreter.
        private static readonly bool BlockJitEnabled =
            Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_RSP_BLOCK_JIT") == "1";
        // Per interpreter: no cross-emulator mutable compilation cache.
        [NonSerialized] private readonly Func<RspInterpreter, uint, uint, int>[] _blocks = BlockJitEnabled ? new Func<RspInterpreter, uint, uint, int>[1024] : null;
        [NonSerialized] private readonly uint[] _blockFirstWords = BlockJitEnabled ? new uint[1024] : null;
        [NonSerialized] private bool _blockJitUnavailable;
        [NonSerialized] private readonly Dictionary<string, Func<RspInterpreter, uint, uint, int>> _compiledBlocks = BlockJitEnabled ? new Dictionary<string, Func<RspInterpreter, uint, uint, int>>() : null;
        [NonSerialized] private long _blockInstructions;
        [NonSerialized] private int _blockCompilations;

        private int TryExecuteBlock(uint budget, uint stagnantLimit)
        {
            // Only straight-line DMEM/register operations are compiled. With no
            // pending lifecycle event, neither code nor MMIO can change mid-block.
            if (_blockJitUnavailable || _memory.RspLifecyclePending) return 0;
            uint pc = _pc & 0xffc;
            int slot = (int)(pc >> 2);
            uint word = _memory.ReadSpImemWord(pc);
            if (_blocks[slot] == null || _blockFirstWords[slot] != word)
            {
                try { _blocks[slot] = CompileBlock(pc); }
                catch (PlatformNotSupportedException) { _blockJitUnavailable = true; return 0; }
                _blockFirstWords[slot] = word;
            }
            int count = _blocks[slot](this, budget, stagnantLimit);
            _blockInstructions += count;
            // The complete block is checked before executing anything. A changed
            // interior word invalidates this entry; the interpreter takes over.
            if (count == 0 && _blocks[slot] != NoBlock && budget > 0 && _stagnantInstructionCount < stagnantLimit)
                _blocks[slot] = null;
            return count;
        }

        private static readonly Func<RspInterpreter, uint, uint, int> NoBlock = (r, b, s) => 0;

        private Func<RspInterpreter, uint, uint, int> CompileBlock(uint start)
        {
            var self = Expression.Parameter(typeof(RspInterpreter), "rsp");
            var budget = Expression.Parameter(typeof(uint), "budget");
            var limit = Expression.Parameter(typeof(uint), "limit");
            var done = Expression.Label(typeof(int));
            var code = new List<Expression>();
            var guards = new List<Expression>();
            var key = new System.Text.StringBuilder(start.ToString("x3"));
            Expression Call(string name, params Expression[] args) => Expression.Call(self,
                typeof(RspInterpreter).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic), args);
            int count = 0;
            for (; count < 8 && start + count * 4 < 4096; count++)
            {
                uint pc = start + (uint)count * 4;
                uint word = _memory.ReadSpImemWord(pc);
                Expression operation = CompileScalarExpression(self, word);
                int vectorOp = (int)(word & 63);
                if ((word >> 25) == 0x25 && (vectorOp <= 0x11 || vectorOp == 0x13
                    || vectorOp == 0x14 || vectorOp == 0x15 || vectorOp == 0x1d
                    || (vectorOp >= 0x20 && vectorOp <= 0x2d) || (vectorOp >= 0x30 && vectorOp <= 0x37)))
                {
                    var reason = Expression.Variable(typeof(string));
                    operation = Expression.Block(new[] { reason }, Call("ExecuteVectorCompute",
                        Expression.Constant(pc), Expression.Constant(word), reason));
                    if (vectorOp == 0x0d || vectorOp == 0x0e)
                        operation = CompileVectorMultiplyAdd(self, word);
                }
                if ((word >> 26 == 0x32 || word >> 26 == 0x3a) && ((word >> 11) & 31) <= 11)
                {
                    var reason = Expression.Variable(typeof(string));
                    operation = Expression.Block(new[] { reason }, Call("ExecuteVectorMemory",
                        Expression.Constant(pc), Expression.Constant(word >> 26 == 0x32),
                        Expression.Constant((word >> 21) & 31), Expression.Constant((word >> 16) & 31),
                        Expression.Constant(word), reason));
                }
                if (operation == null) break;
                key.Append(':').Append(word.ToString("x8"));
                guards.Add(Expression.IfThen(Expression.NotEqual(
                    Expression.Call(Expression.Field(self, "_memory"), typeof(Memory).GetMethod("ReadSpImemWord", BindingFlags.Instance | BindingFlags.NonPublic), Expression.Constant(pc)),
                    Expression.Constant(word)), Expression.Return(done, Expression.Constant(0))));
                code.Add(Call(nameof(BeginBlockInstruction), Expression.Constant(pc), Expression.Constant(word)));
                code.Add(operation);
                code.Add(Call(nameof(EndBlockInstruction), Expression.Constant(pc)));
            }
            if (count < 2) return NoBlock;
            string identity = key.ToString();
            if (_compiledBlocks.TryGetValue(identity, out var cached)) return cached;
            if (_compiledBlocks.Count >= 2048) return NoBlock;
            guards.Insert(0, Expression.IfThen(Expression.OrElse(
                Expression.LessThan(budget, Expression.Constant((uint)count)),
                Expression.OrElse(Expression.LessThan(limit, Expression.Constant((uint)count)),
                    Expression.GreaterThanOrEqual(Expression.Field(self, "_stagnantInstructionCount"), Expression.Subtract(limit, Expression.Constant((uint)count))))),
                Expression.Return(done, Expression.Constant(0))));
            code.InsertRange(0, guards);
            code.Add(Expression.Label(done, Expression.Constant(count)));
            var compiled = Expression.Lambda<Func<RspInterpreter, uint, uint, int>>(Expression.Block(code), self, budget, limit).Compile();
            _compiledBlocks.Add(identity, compiled);
            _blockCompilations++;
            return compiled;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void BeginBlockInstruction(uint pc, uint word)
        {
            _recentPcs[_recentIndex] = pc;
            _recentInstrs[_recentIndex] = word;
            _recentIndex = (_recentIndex + 1) & (RecentInstructionCount - 1);
            if (pc == _lastPc && word == _lastInstr) _samePcRunLength++;
            else { _lastPc = pc; _lastInstr = word; _samePcRunLength = 0; }
            _memory.SetActiveRspTracePc(pc);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void EndBlockInstruction(uint pc)
        {
            _memory.ClearActiveRspTracePc();
            _gpr[0] = 0;
            ulong signature = _progressRegistersDirty
                ? ComputeProgressSignature(true) : _lastProgressSignature;
            _progressRegistersDirty = false;
            if (signature != _lastProgressSignature)
            { _lastProgressSignature = signature; _stagnantInstructionCount = 0; }
            else _stagnantInstructionCount++;
            _pc = (pc + 4) & 0xffc;
        }

        private static Expression CompileScalarExpression(ParameterExpression self, uint word)
        {
            int op = (int)(word >> 26), function = (int)(word & 63);
            bool special = op == 0 && (function == 0 || function == 2 || function == 3
                || function == 0x21 || function == 0x23 || (function >= 0x24 && function <= 0x27)
                || function == 0x2a || function == 0x2b);
            if (!special && op != 9 && op != 12 && op != 13 && op != 14 && op != 15 && op != 0x23 && op != 0x2b)
                return null;
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
                    case 0x21: value = Expression.Add(left, right); break;
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
            else if (op == 0x23 || op == 0x2b)
            {
                Expression address = Expression.Add(Register(rs), Uint(unchecked((uint)(short)word)));
                if (op == 0x2b) return Call("WriteWord", address, Register(rt));
                value = Call("ReadWord", address);
            }
            else
            {
                Expression right = Uint(op == 9 ? unchecked((uint)(short)word) : word & 65535);
                value = op == 9 ? Expression.Add(Register(rs), right)
                    : op == 12 ? Expression.And(Register(rs), right)
                    : op == 13 ? Expression.Or(Register(rs), right)
                    : Expression.ExclusiveOr(Register(rs), right);
            }
            return Call("WriteGpr", Uint((uint)(special ? rd : rt)), value);
        }

        private static Expression CompileVectorMultiplyAdd(ParameterExpression self, uint word)
        {
            int op = (int)(word & 63), vd = (int)((word >> 6) & 31), vs = (int)((word >> 11) & 31);
            int vt = (int)((word >> 16) & 31), element = (int)((word >> 21) & 15);
            Expression Call(string name, params Expression[] args)
            {
                var method = typeof(RspInterpreter).GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic);
                return Expression.Call(method.IsStatic ? null : self, method, args);
            }
            Expression Array(string name) => Expression.Field(self, name);
            Expression Lane(string name, int lane) => Expression.ArrayAccess(Array(name), Expression.Constant(lane));
            var acc = Expression.Variable(typeof(long), "acc");
            var body = new List<Expression>();
            if (ProfileVectorOps)
                body.Add(Expression.PostIncrementAssign(Expression.ArrayAccess(Expression.Field(null,
                    typeof(RspInterpreter).GetField("_vectorOpCounts", BindingFlags.Static | BindingFlags.NonPublic)), Expression.Constant(op))));
            body.Add(Call("LoadVectorUnshuffled", Expression.Constant(vs), Array("_vectorLhs")));
            body.Add(Call("LoadVectorShuffled", Expression.Constant(vt), Expression.Constant(element), Array("_vectorRhs")));
            for (int lane = 0; lane < 8; lane++)
            {
                Expression lhs = Lane("_vectorLhs", lane), rhs = Lane("_vectorRhs", lane);
                if (op == 0x0d) lhs = Expression.Convert(lhs, typeof(short));
                else rhs = Expression.Convert(rhs, typeof(short));
                body.Add(Expression.Assign(acc, Expression.Add(Call("ReadAccumulator", Expression.Constant(lane)),
                    Expression.Multiply(Expression.Convert(lhs, typeof(long)), Expression.Convert(rhs, typeof(long))))));
                body.Add(Call("WriteAccumulator", Expression.Constant(lane), acc));
                Expression result = op == 0x0d
                    ? Expression.Convert(Call("ClampSigned16", Expression.Convert(Expression.RightShift(acc, Expression.Constant(16)), typeof(int))), typeof(ushort))
                    : Call("UnsignedClampAccumulator", acc);
                body.Add(Expression.Assign(Lane("_vectorResult", lane), result));
            }
            body.Add(Call("StoreVector", Expression.Constant(vd), Array("_vectorResult")));
            return Expression.Block(new[] { acc }, body);
        }
    }
}
