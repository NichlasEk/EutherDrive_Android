using System;

namespace Ryu64.MIPS
{
    internal sealed class RspInterpreter
    {
        private const uint LoopReportThreshold = 2048;
        private const uint NoProgressInstructionLimit = 2_000_000;
        private const uint AbsoluteMaxInstructionsPerTask = 100_000_000;
        private static readonly bool TraceRspCp0 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_SP_DMA"), "1", StringComparison.Ordinal)
            || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_SP_MMIO"), "1", StringComparison.Ordinal)
            || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_IO"), "1", StringComparison.Ordinal);
        private static readonly bool TraceRspFlow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_RSP_FLOW"), "1", StringComparison.Ordinal);
        private static readonly ushort[] ReciprocalRom =
        {
            0xFFFF, 0xFF00, 0xFE01, 0xFD04, 0xFC07, 0xFB0C, 0xFA11, 0xF918, 0xF81F, 0xF727, 0xF631, 0xF53B, 0xF446, 0xF352, 0xF25F, 0xF16D,
            0xF07C, 0xEF8B, 0xEE9C, 0xEDAE, 0xECC0, 0xEBD3, 0xEAE8, 0xE9FD, 0xE913, 0xE829, 0xE741, 0xE65A, 0xE573, 0xE48D, 0xE3A9, 0xE2C5,
            0xE1E1, 0xE0FF, 0xE01E, 0xDF3D, 0xDE5D, 0xDD7E, 0xDCA0, 0xDBC2, 0xDAE6, 0xDA0A, 0xD92F, 0xD854, 0xD77B, 0xD6A2, 0xD5CA, 0xD4F3,
            0xD41D, 0xD347, 0xD272, 0xD19E, 0xD0CB, 0xCFF8, 0xCF26, 0xCE55, 0xCD85, 0xCCB5, 0xCBE6, 0xCB18, 0xCA4B, 0xC97E, 0xC8B2, 0xC7E7,
            0xC71C, 0xC652, 0xC589, 0xC4C0, 0xC3F8, 0xC331, 0xC26B, 0xC1A5, 0xC0E0, 0xC01C, 0xBF58, 0xBE95, 0xBDD2, 0xBD10, 0xBC4F, 0xBB8F,
            0xBACF, 0xBA10, 0xB951, 0xB894, 0xB7D6, 0xB71A, 0xB65E, 0xB5A2, 0xB4E8, 0xB42E, 0xB374, 0xB2BB, 0xB203, 0xB14B, 0xB094, 0xAFDE,
            0xAF28, 0xAE73, 0xADBE, 0xAD0A, 0xAC57, 0xABA4, 0xAAF1, 0xAA40, 0xA98E, 0xA8DE, 0xA82E, 0xA77E, 0xA6D0, 0xA621, 0xA574, 0xA4C6,
            0xA41A, 0xA36E, 0xA2C2, 0xA217, 0xA16D, 0xA0C3, 0xA01A, 0x9F71, 0x9EC8, 0x9E21, 0x9D79, 0x9CD3, 0x9C2D, 0x9B87, 0x9AE2, 0x9A3D,
            0x9999, 0x98F6, 0x9852, 0x97B0, 0x970E, 0x966C, 0x95CB, 0x952B, 0x948B, 0x93EB, 0x934C, 0x92AD, 0x920F, 0x9172, 0x90D4, 0x9038,
            0x8F9C, 0x8F00, 0x8E65, 0x8DCA, 0x8D30, 0x8C96, 0x8BFC, 0x8B64, 0x8ACB, 0x8A33, 0x899C, 0x8904, 0x886E, 0x87D8, 0x8742, 0x86AD,
            0x8618, 0x8583, 0x84F0, 0x845C, 0x83C9, 0x8336, 0x82A4, 0x8212, 0x8181, 0x80F0, 0x8060, 0x7FD0, 0x7F40, 0x7EB1, 0x7E22, 0x7D93,
            0x7D05, 0x7C78, 0x7BEB, 0x7B5E, 0x7AD2, 0x7A46, 0x79BA, 0x792F, 0x78A4, 0x781A, 0x7790, 0x7706, 0x767D, 0x75F5, 0x756C, 0x74E4,
            0x745D, 0x73D5, 0x734F, 0x72C8, 0x7242, 0x71BC, 0x7137, 0x70B2, 0x702E, 0x6FA9, 0x6F26, 0x6EA2, 0x6E1F, 0x6D9C, 0x6D1A, 0x6C98,
            0x6C16, 0x6B95, 0x6B14, 0x6A94, 0x6A13, 0x6993, 0x6914, 0x6895, 0x6816, 0x6798, 0x6719, 0x669C, 0x661E, 0x65A1, 0x6524, 0x64A8,
            0x642C, 0x63B0, 0x6335, 0x62BA, 0x623F, 0x61C5, 0x614B, 0x60D1, 0x6058, 0x5FDF, 0x5F66, 0x5EED, 0x5E75, 0x5DFD, 0x5D86, 0x5D0F,
            0x5C98, 0x5C22, 0x5BAB, 0x5B35, 0x5AC0, 0x5A4B, 0x59D6, 0x5961, 0x58ED, 0x5879, 0x5805, 0x5791, 0x571E, 0x56AC, 0x5639, 0x55C7,
            0x5555, 0x54E3, 0x5472, 0x5401, 0x5390, 0x5320, 0x52AF, 0x5240, 0x51D0, 0x5161, 0x50F2, 0x5083, 0x5015, 0x4FA6, 0x4F38, 0x4ECB,
            0x4E5E, 0x4DF1, 0x4D84, 0x4D17, 0x4CAB, 0x4C3F, 0x4BD3, 0x4B68, 0x4AFD, 0x4A92, 0x4A27, 0x49BD, 0x4953, 0x48E9, 0x4880, 0x4817,
            0x47AE, 0x4745, 0x46DC, 0x4674, 0x460C, 0x45A5, 0x453D, 0x44D6, 0x446F, 0x4408, 0x43A2, 0x433C, 0x42D6, 0x4270, 0x420B, 0x41A6,
            0x4141, 0x40DC, 0x4078, 0x4014, 0x3FB0, 0x3F4C, 0x3EE8, 0x3E85, 0x3E22, 0x3DC0, 0x3D5D, 0x3CFB, 0x3C99, 0x3C37, 0x3BD6, 0x3B74,
            0x3B13, 0x3AB2, 0x3A52, 0x39F1, 0x3991, 0x3931, 0x38D2, 0x3872, 0x3813, 0x37B4, 0x3755, 0x36F7, 0x3698, 0x363A, 0x35DC, 0x357F,
            0x3521, 0x34C4, 0x3467, 0x340A, 0x33AE, 0x3351, 0x32F5, 0x3299, 0x323E, 0x31E2, 0x3187, 0x312C, 0x30D1, 0x3076, 0x301C, 0x2FC2,
            0x2F68, 0x2F0E, 0x2EB4, 0x2E5B, 0x2E02, 0x2DA9, 0x2D50, 0x2CF8, 0x2C9F, 0x2C47, 0x2BEF, 0x2B97, 0x2B40, 0x2AE8, 0x2A91, 0x2A3A,
            0x29E4, 0x298D, 0x2937, 0x28E0, 0x288B, 0x2835, 0x27DF, 0x278A, 0x2735, 0x26E0, 0x268B, 0x2636, 0x25E2, 0x258D, 0x2539, 0x24E5,
            0x2492, 0x243E, 0x23EB, 0x2398, 0x2345, 0x22F2, 0x22A0, 0x224D, 0x21FB, 0x21A9, 0x2157, 0x2105, 0x20B4, 0x2063, 0x2012, 0x1FC1,
            0x1F70, 0x1F1F, 0x1ECF, 0x1E7F, 0x1E2E, 0x1DDF, 0x1D8F, 0x1D3F, 0x1CF0, 0x1CA1, 0x1C52, 0x1C03, 0x1BB4, 0x1B66, 0x1B17, 0x1AC9,
            0x1A7B, 0x1A2D, 0x19E0, 0x1992, 0x1945, 0x18F8, 0x18AB, 0x185E, 0x1811, 0x17C4, 0x1778, 0x172C, 0x16E0, 0x1694, 0x1648, 0x15FD,
            0x15B1, 0x1566, 0x151B, 0x14D0, 0x1485, 0x143B, 0x13F0, 0x13A6, 0x135C, 0x1312, 0x12C8, 0x127F, 0x1235, 0x11EC, 0x11A3, 0x1159,
            0x1111, 0x10C8, 0x107F, 0x1037, 0x0FEF, 0x0FA6, 0x0F5E, 0x0F17, 0x0ECF, 0x0E87, 0x0E40, 0x0DF9, 0x0DB2, 0x0D6B, 0x0D24, 0x0CDD,
            0x0C97, 0x0C50, 0x0C0A, 0x0BC4, 0x0B7E, 0x0B38, 0x0AF2, 0x0AAD, 0x0A68, 0x0A22, 0x09DD, 0x0998, 0x0953, 0x090F, 0x08CA, 0x0886,
            0x0842, 0x07FD, 0x07B9, 0x0776, 0x0732, 0x06EE, 0x06AB, 0x0668, 0x0624, 0x05E1, 0x059E, 0x055C, 0x0519, 0x04D6, 0x0494, 0x0452,
            0x0410, 0x03CE, 0x038C, 0x034A, 0x0309, 0x02C7, 0x0286, 0x0245, 0x0204, 0x01C3, 0x0182, 0x0141, 0x0101, 0x00C0, 0x0080, 0x0040,
            0x6A09, 0xFFFF, 0x6955, 0xFF00, 0x68A1, 0xFE02, 0x67EF, 0xFD06, 0x673E, 0xFC0B, 0x668D, 0xFB12, 0x65DE, 0xFA1A, 0x6530, 0xF923,
            0x6482, 0xF82E, 0x63D6, 0xF73B, 0x632B, 0xF648, 0x6280, 0xF557, 0x61D7, 0xF467, 0x612E, 0xF379, 0x6087, 0xF28C, 0x5FE0, 0xF1A0,
            0x5F3A, 0xF0B6, 0x5E95, 0xEFCD, 0x5DF1, 0xEEE5, 0x5D4E, 0xEDFF, 0x5CAC, 0xED19, 0x5C0B, 0xEC35, 0x5B6B, 0xEB52, 0x5ACB, 0xEA71,
            0x5A2C, 0xE990, 0x598F, 0xE8B1, 0x58F2, 0xE7D3, 0x5855, 0xE6F6, 0x57BA, 0xE61B, 0x5720, 0xE540, 0x5686, 0xE467, 0x55ED, 0xE38E,
            0x5555, 0xE2B7, 0x54BE, 0xE1E1, 0x5427, 0xE10D, 0x5391, 0xE039, 0x52FC, 0xDF66, 0x5268, 0xDE94, 0x51D5, 0xDDC4, 0x5142, 0xDCF4,
            0x50B0, 0xDC26, 0x501F, 0xDB59, 0x4F8E, 0xDA8C, 0x4EFE, 0xD9C1, 0x4E6F, 0xD8F7, 0x4DE1, 0xD82D, 0x4D53, 0xD765, 0x4CC6, 0xD69E,
            0x4C3A, 0xD5D7, 0x4BAF, 0xD512, 0x4B24, 0xD44E, 0x4A9A, 0xD38A, 0x4A10, 0xD2C8, 0x4987, 0xD206, 0x48FF, 0xD146, 0x4878, 0xD086,
            0x47F1, 0xCFC7, 0x476B, 0xCF0A, 0x46E5, 0xCE4D, 0x4660, 0xCD91, 0x45DC, 0xCCD6, 0x4558, 0xCC1B, 0x44D5, 0xCB62, 0x4453, 0xCAA9,
            0x43D1, 0xC9F2, 0x434F, 0xC93B, 0x42CF, 0xC885, 0x424F, 0xC7D0, 0x41CF, 0xC71C, 0x4151, 0xC669, 0x40D2, 0xC5B6, 0x4055, 0xC504,
            0x3FD8, 0xC453, 0x3F5B, 0xC3A3, 0x3EDF, 0xC2F4, 0x3E64, 0xC245, 0x3DE9, 0xC198, 0x3D6E, 0xC0EB, 0x3CF5, 0xC03F, 0x3C7C, 0xBF93,
            0x3C03, 0xBEE9, 0x3B8B, 0xBE3F, 0x3B13, 0xBD96, 0x3A9C, 0xBCED, 0x3A26, 0xBC46, 0x39B0, 0xBB9F, 0x393A, 0xBAF8, 0x38C5, 0xBA53,
            0x3851, 0xB9AE, 0x37DD, 0xB90A, 0x3769, 0xB867, 0x36F6, 0xB7C5, 0x3684, 0xB723, 0x3612, 0xB681, 0x35A0, 0xB5E1, 0x352F, 0xB541,
            0x34BF, 0xB4A2, 0x344F, 0xB404, 0x33DF, 0xB366, 0x3370, 0xB2C9, 0x3302, 0xB22C, 0x3293, 0xB191, 0x3226, 0xB0F5, 0x31B9, 0xB05B,
            0x314C, 0xAFC1, 0x30DF, 0xAF28, 0x3074, 0xAE8F, 0x3008, 0xADF7, 0x2F9D, 0xAD60, 0x2F33, 0xACC9, 0x2EC8, 0xAC33, 0x2E5F, 0xAB9E,
            0x2DF6, 0xAB09, 0x2D8D, 0xAA75, 0x2D24, 0xA9E1, 0x2CBC, 0xA94E, 0x2C55, 0xA8BC, 0x2BEE, 0xA82A, 0x2B87, 0xA799, 0x2B21, 0xA708,
            0x2ABB, 0xA678, 0x2A55, 0xA5E8, 0x29F0, 0xA559, 0x298B, 0xA4CB, 0x2927, 0xA43D, 0x28C3, 0xA3B0, 0x2860, 0xA323, 0x27FD, 0xA297,
            0x279A, 0xA20B, 0x2738, 0xA180, 0x26D6, 0xA0F6, 0x2674, 0xA06C, 0x2613, 0x9FE2, 0x25B2, 0x9F59, 0x2552, 0x9ED1, 0x24F2, 0x9E49,
            0x2492, 0x9DC2, 0x2432, 0x9D3B, 0x23D3, 0x9CB4, 0x2375, 0x9C2F, 0x2317, 0x9BA9, 0x22B9, 0x9B25, 0x225B, 0x9AA0, 0x21FE, 0x9A1C,
            0x21A1, 0x9999, 0x2145, 0x9916, 0x20E8, 0x9894, 0x208D, 0x9812, 0x2031, 0x9791, 0x1FD6, 0x9710, 0x1F7B, 0x968F, 0x1F21, 0x960F,
            0x1EC7, 0x9590, 0x1E6D, 0x9511, 0x1E13, 0x9492, 0x1DBA, 0x9414, 0x1D61, 0x9397, 0x1D09, 0x931A, 0x1CB1, 0x929D, 0x1C59, 0x9221,
            0x1C01, 0x91A5, 0x1BAA, 0x9129, 0x1B53, 0x90AF, 0x1AFC, 0x9034, 0x1AA6, 0x8FBA, 0x1A50, 0x8F40, 0x19FA, 0x8EC7, 0x19A5, 0x8E4F,
            0x1950, 0x8DD6, 0x18FB, 0x8D5E, 0x18A7, 0x8CE7, 0x1853, 0x8C70, 0x17FF, 0x8BF9, 0x17AB, 0x8B83, 0x1758, 0x8B0D, 0x1705, 0x8A98,
            0x16B2, 0x8A23, 0x1660, 0x89AE, 0x160D, 0x893A, 0x15BC, 0x88C6, 0x156A, 0x8853, 0x1519, 0x87E0, 0x14C8, 0x876D, 0x1477, 0x86FB,
            0x1426, 0x8689, 0x13D6, 0x8618, 0x1386, 0x85A7, 0x1337, 0x8536, 0x12E7, 0x84C6, 0x1298, 0x8456, 0x1249, 0x83E7, 0x11FB, 0x8377,
            0x11AC, 0x8309, 0x115E, 0x829A, 0x1111, 0x822C, 0x10C3, 0x81BF, 0x1076, 0x8151, 0x1029, 0x80E4, 0x0FDC, 0x8078, 0x0F8F, 0x800C,
            0x0F43, 0x7FA0, 0x0EF7, 0x7F34, 0x0EAB, 0x7EC9, 0x0E60, 0x7E5E, 0x0E15, 0x7DF4, 0x0DCA, 0x7D8A, 0x0D7F, 0x7D20, 0x0D34, 0x7CB6,
            0x0CEA, 0x7C4D, 0x0CA0, 0x7BE5, 0x0C56, 0x7B7C, 0x0C0C, 0x7B14, 0x0BC3, 0x7AAC, 0x0B7A, 0x7A45, 0x0B31, 0x79DE, 0x0AE8, 0x7977,
            0x0AA0, 0x7911, 0x0A58, 0x78AB, 0x0A10, 0x7845, 0x09C8, 0x77DF, 0x0981, 0x777A, 0x0939, 0x7715, 0x08F2, 0x76B1, 0x08AB, 0x764D,
            0x0865, 0x75E9, 0x081E, 0x7585, 0x07D8, 0x7522, 0x0792, 0x74BF, 0x074D, 0x745D, 0x0707, 0x73FA, 0x06C2, 0x7398, 0x067D, 0x7337,
            0x0638, 0x72D5, 0x05F3, 0x7274, 0x05AF, 0x7213, 0x056A, 0x71B3, 0x0526, 0x7152, 0x04E2, 0x70F2, 0x049F, 0x7093, 0x045B, 0x7033,
            0x0418, 0x6FD4, 0x03D5, 0x6F76, 0x0392, 0x6F17, 0x0350, 0x6EB9, 0x030D, 0x6E5B, 0x02CB, 0x6DFD, 0x0289, 0x6DA0, 0x0247, 0x6D43,
            0x0206, 0x6CE6, 0x01C4, 0x6C8A, 0x0183, 0x6C2D, 0x0142, 0x6BD1, 0x0101, 0x6B76, 0x00C0, 0x6B1A, 0x0080, 0x6ABF, 0x0040, 0x6A64,
        };
        private readonly Memory _memory;
        private readonly uint[] _gpr = new uint[32];
        private readonly byte[,] _vr = new byte[32, 16];
        private readonly ushort[] _vcc = new ushort[2];
        private readonly ushort[] _vco = new ushort[2];
        private readonly ushort[] _accHi = new ushort[8];
        private readonly ushort[] _accMd = new ushort[8];
        private readonly ushort[] _accLo = new ushort[8];
        private byte _vce;
        private ushort _divIn;
        private ushort _divOut;
        private byte _dpFlag;
        private uint _pc;
        private bool _branchPending;
        private uint _branchTarget;
        private bool _skipNextInstruction;
        private uint _hi;
        private uint _lo;
        private uint _lastPc;
        private uint _lastInstr;
        private uint _samePcRunLength;
        private ulong _lastProgressSignature;
        private uint _stagnantInstructionCount;
        private readonly uint[] _recentPcs = new uint[16];
        private readonly uint[] _recentInstrs = new uint[16];
        private int _recentIndex;

        public RspInterpreter(Memory memory)
        {
            _memory = memory;
        }

        public bool ExecuteTask(out uint executedInstructions, out string stopReason)
        {
            _pc = _memory.ReadRspPc();
            _branchPending = false;
            _branchTarget = 0;
            _skipNextInstruction = false;
            _lastPc = 0xffffffffu;
            _lastInstr = 0;
            _samePcRunLength = 0;
            _lastProgressSignature = _memory.GetRspProgressSignature();
            _stagnantInstructionCount = 0;
            _recentIndex = 0;
            Array.Clear(_recentPcs, 0, _recentPcs.Length);
            Array.Clear(_recentInstrs, 0, _recentInstrs.Length);
            stopReason = "no-progress";

            for (executedInstructions = 0; executedInstructions < AbsoluteMaxInstructionsPerTask; executedInstructions++)
            {
                if (_stagnantInstructionCount >= NoProgressInstructionLimit)
                {
                    stopReason = $"no-progress stagnant={_stagnantInstructionCount}";
                    break;
                }

                uint pc = _pc & 0x0FFCu;
                uint instr = _memory.ReadSpImemWord(pc);
                _recentPcs[_recentIndex] = pc;
                _recentInstrs[_recentIndex] = instr;
                _recentIndex = (_recentIndex + 1) % _recentPcs.Length;
                TraceRspWindow(pc, instr);
                if (pc == _lastPc && instr == _lastInstr)
                {
                    _samePcRunLength++;
                    if (_samePcRunLength >= LoopReportThreshold)
                    {
                        stopReason = $"loop pc=0x{pc:x3} op=0x{instr:x8} repeats={_samePcRunLength}";
                        _memory.WriteRspPc(_pc);
                        _gpr[0] = 0;
                        return false;
                    }
                }
                else
                {
                    _lastPc = pc;
                    _lastInstr = instr;
                    _samePcRunLength = 0;
                }
                uint sequentialPc = (pc + 4) & 0x0FFCu;
                bool branchDue = _branchPending;
                uint dueTarget = _branchTarget;
                _branchPending = false;
                _memory.SetActiveRspTracePc(pc);
                bool dmaBusyPollDelaySlot = IsRspDmaBusyPollDelaySlot(pc, instr, branchDue, dueTarget);

                if (!Step(pc, instr, out stopReason))
                {
                    _memory.ClearActiveRspTracePc();
                    if (string.IsNullOrEmpty(stopReason))
                        stopReason = $"unknown-stop pc=0x{pc:x3} op=0x{instr:x8}";
                    _memory.WriteRspPc(_pc);
                    _gpr[0] = 0;
                    return stopReason == "break";
                }

                _memory.ClearActiveRspTracePc();
                _gpr[0] = 0;

                _memory.TickRspInterpreter(1);
                if (dmaBusyPollDelaySlot && _memory.IsRspDmaDelayArmed())
                {
                    uint remaining = _memory.GetRspDmaDelayRemaining();
                    if (remaining > 1)
                    {
                        // Match the event-driven model more closely: this three-instruction
                        // loop is a pure wait for SP DMA completion, so consuming the whole
                        // remaining delay here avoids artificial interpreter timeouts.
                        _memory.TickRspInterpreter(remaining - 1);
                    }
                }

                ulong progressSignature = _memory.GetRspProgressSignature();
                if (progressSignature != _lastProgressSignature)
                {
                    _lastProgressSignature = progressSignature;
                    _stagnantInstructionCount = 0;
                }
                else
                {
                    _stagnantInstructionCount++;
                }

                bool skipNext = _skipNextInstruction;
                _skipNextInstruction = false;
                if (branchDue)
                {
                    _pc = dueTarget & 0x0FFCu;
                }
                else if (skipNext)
                {
                    _pc = (sequentialPc + 4) & 0x0FFCu;
                }
                else
                {
                    _pc = sequentialPc;
                }
            }

            _memory.WriteRspPc(_pc);
            _memory.ClearActiveRspTracePc();
            stopReason =
                $"{stopReason} rspPc=0x{_pc:x3} current=0x{_memory.ReadSpImemWord(_pc & 0x0FFCu):x8} " +
                $"t3=0x{_gpr[11]:x8} t4=0x{_gpr[12]:x8} s3=0x{_gpr[19]:x8} s4=0x{_gpr[20]:x8} t8=0x{_gpr[24]:x8} ra=0x{_gpr[31]:x8} " +
                $"spStatus=0x{_memory.ReadRspCp0(4):x8} dmaFull=0x{_memory.ReadRspCp0(5):x8} dmaBusy=0x{_memory.ReadRspCp0(6):x8} " +
                $"spMem=0x{_memory.ReadRspCp0(0):x8} spDram=0x{_memory.ReadRspCp0(1):x8} rdLen=0x{_memory.ReadRspCp0(2):x8} " +
                $"dmaDelayArmed={_memory.IsRspDmaDelayArmed()} dmaDelayRemaining=0x{_memory.GetRspDmaDelayRemaining():x8} queuedDma={_memory.HasQueuedRspDma()} " +
                $"tail={FormatRecentTrace()}";
            return false;
        }

        private bool Step(uint pc, uint instr, out string stopReason)
        {
            uint op = instr >> 26;
            uint rs = (instr >> 21) & 0x1F;
            uint rt = (instr >> 16) & 0x1F;
            uint rd = (instr >> 11) & 0x1F;
            uint sa = (instr >> 6) & 0x1F;
            uint funct = instr & 0x3F;
            short imm = unchecked((short)instr);
            uint uimm = instr & 0xFFFFu;
            uint target = instr & 0x03FFFFFFu;

            stopReason = string.Empty;

            switch (op)
            {
                case 0x00:
                    return ExecuteSpecial(pc, funct, rs, rt, rd, sa, out stopReason);
                case 0x01:
                    return ExecuteRegImm(pc, rt, rs, imm, out stopReason);
                case 0x02:
                    SetBranch(((pc + 4) & 0xF0000000u) | (target << 2));
                    return true;
                case 0x03:
                    WriteGpr(31, pc + 8);
                    SetBranch(((pc + 4) & 0xF0000000u) | (target << 2));
                    return true;
                case 0x04:
                    if (_gpr[rs] == _gpr[rt]) SetBranch(BranchTarget(pc, imm));
                    return true;
                case 0x05:
                    if (_gpr[rs] != _gpr[rt]) SetBranch(BranchTarget(pc, imm));
                    return true;
                case 0x06:
                    if ((int)_gpr[rs] <= 0) SetBranch(BranchTarget(pc, imm));
                    return true;
                case 0x07:
                    if ((int)_gpr[rs] > 0) SetBranch(BranchTarget(pc, imm));
                    return true;
                case 0x14:
                    return ExecuteLikelyBranch(_gpr[rs] == _gpr[rt], BranchTarget(pc, imm));
                case 0x15:
                    return ExecuteLikelyBranch(_gpr[rs] != _gpr[rt], BranchTarget(pc, imm));
                case 0x16:
                    return ExecuteLikelyBranch((int)_gpr[rs] <= 0, BranchTarget(pc, imm));
                case 0x17:
                    return ExecuteLikelyBranch((int)_gpr[rs] > 0, BranchTarget(pc, imm));
                case 0x08:
                case 0x09:
                    WriteGpr(rt, _gpr[rs] + (uint)(int)imm);
                    return true;
                case 0x0A:
                    WriteGpr(rt, (int)_gpr[rs] < imm ? 1u : 0u);
                    return true;
                case 0x0B:
                    WriteGpr(rt, _gpr[rs] < unchecked((uint)(int)imm) ? 1u : 0u);
                    return true;
                case 0x0C:
                    WriteGpr(rt, _gpr[rs] & uimm);
                    return true;
                case 0x0D:
                    WriteGpr(rt, _gpr[rs] | uimm);
                    return true;
                case 0x0E:
                    WriteGpr(rt, _gpr[rs] ^ uimm);
                    return true;
                case 0x0F:
                    WriteGpr(rt, uimm << 16);
                    return true;
                case 0x10:
                    return ExecuteCop0(pc, rs, rt, rd, out stopReason);
                case 0x12:
                    return ExecuteCop2(pc, rs, rt, rd, instr, out stopReason);
                case 0x20:
                    WriteGpr(rt, unchecked((uint)(int)(sbyte)ReadByte(_gpr[rs] + (uint)(int)imm)));
                    return true;
                case 0x21:
                    WriteGpr(rt, unchecked((uint)(int)(short)ReadHalf(_gpr[rs] + (uint)(int)imm)));
                    return true;
                case 0x23:
                    WriteGpr(rt, ReadWord(_gpr[rs] + (uint)(int)imm));
                    return true;
                case 0x24:
                    WriteGpr(rt, ReadByte(_gpr[rs] + (uint)(int)imm));
                    return true;
                case 0x25:
                    WriteGpr(rt, ReadHalf(_gpr[rs] + (uint)(int)imm));
                    return true;
                case 0x28:
                    WriteByte(_gpr[rs] + (uint)(int)imm, (byte)_gpr[rt]);
                    return true;
                case 0x29:
                    WriteHalf(_gpr[rs] + (uint)(int)imm, (ushort)_gpr[rt]);
                    return true;
                case 0x2B:
                    WriteWord(_gpr[rs] + (uint)(int)imm, _gpr[rt]);
                    return true;
                case 0x32:
                    return ExecuteVectorMemory(pc, true, rs, rt, instr, out stopReason);
                case 0x3A:
                    return ExecuteVectorMemory(pc, false, rs, rt, instr, out stopReason);
                default:
                    stopReason = $"unsupported-op pc=0x{pc:x3} op=0x{instr:x8}";
                    return false;
            }
        }

        private void TraceRspWindow(uint pc, uint instr)
        {
            if (!TraceRspFlow || !IsTraceRspWindow(pc))
                return;

            Common.Logger.PrintWarningLine(
                $"[N64RSPFLOW] pc=0x{pc:x3} op=0x{instr:x8} " +
                $"r1=0x{_gpr[1]:x8} r2=0x{_gpr[2]:x8} r3=0x{_gpr[3]:x8} " +
                $"t3=0x{_gpr[11]:x8} t4=0x{_gpr[12]:x8} s3=0x{_gpr[19]:x8} s4=0x{_gpr[20]:x8} " +
                $"r24=0x{_gpr[24]:x8} r25=0x{_gpr[25]:x8} r26=0x{_gpr[26]:x8} r27=0x{_gpr[27]:x8} " +
                $"r28=0x{_gpr[28]:x8} r29=0x{_gpr[29]:x8} ra=0x{_gpr[31]:x8}");

            if (pc == 0x0FB4u || pc == 0x02D0u)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64RSPDMEMSNAP] pc=0x{pc:x3} " +
                    $"dmem410=0x{_memory.ReadSpDmemWord(0x0410):x8} dmem414=0x{_memory.ReadSpDmemWord(0x0414):x8} " +
                    $"dmem418=0x{_memory.ReadSpDmemWord(0x0418):x8} dmem41c=0x{_memory.ReadSpDmemWord(0x041c):x8} " +
                    $"dmem420=0x{_memory.ReadSpDmemWord(0x0420):x8} dmem424=0x{_memory.ReadSpDmemWord(0x0424):x8} dmem428=0x{_memory.ReadSpDmemWord(0x0428):x8} " +
                    $"dmem2d0=0x{_memory.ReadSpDmemWord(0x02d0):x8} dmem2d4=0x{_memory.ReadSpDmemWord(0x02d4):x8} dmem2d8=0x{_memory.ReadSpDmemWord(0x02d8):x8}");
            }
        }

        private static bool IsTraceRspWindow(uint pc)
        {
            return (pc >= 0x0B0 && pc <= 0x0E0)
                || (pc >= 0x140 && pc <= 0x198)
                || (pc >= 0x2D0 && pc <= 0x2E0)
                || (pc >= 0x7E0 && pc <= 0x820)
                || (pc >= 0x820 && pc <= 0x850)
                || (pc >= 0xA40 && pc <= 0xAB0)
                || (pc >= 0xC40 && pc <= 0xC80)
                || (pc >= 0xFB0 && pc <= 0xFF8);
        }

        private bool IsRspDmaBusyPollDelaySlot(uint pc, uint instr, bool branchDue, uint dueTarget)
        {
            if (!branchDue)
                return false;

            if (dueTarget != ((pc - 8u) & 0x0FFCu))
                return false;

            if (!IsMfc0(instr, out uint delayRt, out uint delayRd) || delayRd != 4)
                return false;

            uint pollPc = dueTarget & 0x0FFCu;
            uint pollInstr = _memory.ReadSpImemWord(pollPc);
            uint branchInstr = _memory.ReadSpImemWord((pollPc + 4) & 0x0FFCu);

            if (!IsMfc0(pollInstr, out uint pollRt, out uint pollRd) || pollRd != 6 || pollRt != delayRt)
                return false;

            return IsBneBackTwo(branchInstr, pollRt);
        }

        private static bool IsMfc0(uint instr, out uint rt, out uint rd)
        {
            rt = (instr >> 16) & 0x1Fu;
            rd = (instr >> 11) & 0x1Fu;
            return (instr >> 26) == 0x10 && ((instr >> 21) & 0x1Fu) == 0x00;
        }

        private static bool IsBneBackTwo(uint instr, uint rs)
        {
            return (instr >> 26) == 0x05
                && ((instr >> 21) & 0x1Fu) == rs
                && ((instr >> 16) & 0x1Fu) == 0x00
                && unchecked((short)instr) == -2;
        }

        private bool ExecuteSpecial(uint pc, uint funct, uint rs, uint rt, uint rd, uint sa, out string stopReason)
        {
            stopReason = string.Empty;
            switch (funct)
            {
                case 0x00: WriteGpr(rd, _gpr[rt] << (int)sa); return true;
                case 0x02: WriteGpr(rd, _gpr[rt] >> (int)sa); return true;
                case 0x03: WriteGpr(rd, unchecked((uint)((int)_gpr[rt] >> (int)sa))); return true;
                case 0x04: WriteGpr(rd, _gpr[rt] << (int)(_gpr[rs] & 0x1F)); return true;
                case 0x06: WriteGpr(rd, _gpr[rt] >> (int)(_gpr[rs] & 0x1F)); return true;
                case 0x07: WriteGpr(rd, unchecked((uint)((int)_gpr[rt] >> (int)(_gpr[rs] & 0x1F)))); return true;
                case 0x08: SetBranch(_gpr[rs]); return true;
                case 0x09: WriteGpr(rd == 0 ? 31u : rd, pc + 8); SetBranch(_gpr[rs]); return true;
                case 0x0D: stopReason = "break"; return false;
                case 0x10: WriteGpr(rd, _hi); return true;
                case 0x12: WriteGpr(rd, _lo); return true;
                case 0x18:
                    {
                        long prod = (long)(int)_gpr[rs] * (long)(int)_gpr[rt];
                        _lo = (uint)prod;
                        _hi = (uint)(prod >> 32);
                        return true;
                    }
                case 0x19:
                    {
                        ulong prod = (ulong)_gpr[rs] * (ulong)_gpr[rt];
                        _lo = (uint)prod;
                        _hi = (uint)(prod >> 32);
                        return true;
                    }
                case 0x20:
                case 0x21: WriteGpr(rd, _gpr[rs] + _gpr[rt]); return true;
                case 0x22:
                case 0x23: WriteGpr(rd, _gpr[rs] - _gpr[rt]); return true;
                case 0x24: WriteGpr(rd, _gpr[rs] & _gpr[rt]); return true;
                case 0x25: WriteGpr(rd, _gpr[rs] | _gpr[rt]); return true;
                case 0x26: WriteGpr(rd, _gpr[rs] ^ _gpr[rt]); return true;
                case 0x27: WriteGpr(rd, ~(_gpr[rs] | _gpr[rt])); return true;
                case 0x2A: WriteGpr(rd, (int)_gpr[rs] < (int)_gpr[rt] ? 1u : 0u); return true;
                case 0x2B: WriteGpr(rd, _gpr[rs] < _gpr[rt] ? 1u : 0u); return true;
                default:
                    stopReason = $"unsupported-special pc=0x{pc:x3} funct=0x{funct:x2}";
                    return false;
            }
        }

        private bool ExecuteRegImm(uint pc, uint rt, uint rs, short imm, out string stopReason)
        {
            stopReason = string.Empty;
            switch (rt)
            {
                case 0x00: if ((int)_gpr[rs] < 0) SetBranch(BranchTarget(pc, imm)); return true;
                case 0x01: if ((int)_gpr[rs] >= 0) SetBranch(BranchTarget(pc, imm)); return true;
                case 0x02: return ExecuteLikelyBranch((int)_gpr[rs] < 0, BranchTarget(pc, imm));
                case 0x03: return ExecuteLikelyBranch((int)_gpr[rs] >= 0, BranchTarget(pc, imm));
                case 0x10:
                    WriteGpr(31, pc + 8);
                    if ((int)_gpr[rs] < 0) SetBranch(BranchTarget(pc, imm));
                    return true;
                case 0x11:
                    WriteGpr(31, pc + 8);
                    if ((int)_gpr[rs] >= 0) SetBranch(BranchTarget(pc, imm));
                    return true;
                case 0x12:
                    WriteGpr(31, pc + 8);
                    return ExecuteLikelyBranch((int)_gpr[rs] < 0, BranchTarget(pc, imm));
                case 0x13:
                    WriteGpr(31, pc + 8);
                    return ExecuteLikelyBranch((int)_gpr[rs] >= 0, BranchTarget(pc, imm));
                default:
                    stopReason = $"unsupported-regimm pc=0x{pc:x3} rt=0x{rt:x2}";
                    return false;
            }
        }

        private bool ExecuteCop0(uint pc, uint rs, uint rt, uint rd, out string stopReason)
        {
            stopReason = string.Empty;
            switch (rs)
            {
                case 0x00:
                    WriteGpr(rt, _memory.ReadRspCp0((int)rd));
                    return true;
                case 0x04:
                    if (TraceRspCp0)
                    {
                        Common.Logger.PrintWarningLine(
                            $"[N64RSPMTC0] rspPc=0x{pc:x3} rt=r{rt} value=0x{_gpr[rt]:x8} rd={rd}");
                    }
                    _memory.WriteRspCp0((int)rd, _gpr[rt]);
                    return true;
                default:
                    stopReason = $"unsupported-cop0 pc=0x{pc:x3} rs=0x{rs:x2}";
                    return false;
            }
        }

        private bool ExecuteCop2(uint pc, uint rs, uint rt, uint rd, uint instr, out string stopReason)
        {
            stopReason = string.Empty;
            if ((instr >> 25) == 0x25)
                return ExecuteVectorCompute(pc, instr, out stopReason);

            uint element = (instr >> 7) & 0xF;
            switch (rs)
            {
                case 0x00: // MFC2
                    WriteGpr(rt, unchecked((uint)(int)(short)ReadVectorElement((int)rd, (int)(element & 0xEu))));
                    return true;
                case 0x02: // CFC2
                    WriteGpr(rt, ReadVectorControl((int)rd));
                    return true;
                case 0x04: // MTC2
                    WriteVectorElement((int)rd, (int)(element & 0xEu), (ushort)_gpr[rt]);
                    return true;
                case 0x06: // CTC2
                    WriteVectorControl((int)rd, (ushort)_gpr[rt]);
                    return true;
                default:
                    stopReason = $"unsupported-cop2 pc=0x{pc:x3} rs=0x{rs:x2} op=0x{instr:x8}";
                    return false;
            }
        }

        private bool ExecuteVectorCompute(uint pc, uint instr, out string stopReason)
        {
            stopReason = string.Empty;
            int op = (int)(instr & 0x3F);
            int vd = (int)((instr >> 6) & 0x1F);
            int vs = (int)((instr >> 11) & 0x1F);
            int vt = (int)((instr >> 16) & 0x1F);
            int element = (int)((instr >> 21) & 0xF);

            ushort[] lhs = new ushort[8];
            ushort[] rhs = new ushort[8];
            ushort[] result = new ushort[8];
            LoadVectorUnshuffled(vs, lhs);
            LoadVectorShuffled(vt, element, rhs);

            switch (op)
            {
                case 0x00: // VMULF
                    for (int lane = 0; lane < 8; lane++)
                    {
                        int lhsSigned = (short)lhs[lane];
                        int rhsSigned = (short)rhs[lane];
                        int prod = (lhsSigned * rhsSigned << 1) + 0x8000;
                        ushort accLo = unchecked((ushort)prod);
                        short accMd = unchecked((short)(prod >> 16));
                        bool negative = accMd < 0;
                        bool equal = lhs[lane] == rhs[lane];

                        _accLo[lane] = accLo;
                        _accMd[lane] = unchecked((ushort)accMd);
                        _accHi[lane] = (ushort)(negative && !equal ? 0xffff : 0x0000);

                        short laneResult = accMd;
                        if (negative && equal)
                            laneResult--;

                        result[lane] = unchecked((ushort)laneResult);
                    }
                    StoreVector(vd, result);
                    return true;

                case 0x01: // VMULU
                    for (int lane = 0; lane < 8; lane++)
                    {
                        int lhsSigned = (short)lhs[lane];
                        int rhsSigned = (short)rhs[lane];
                        int prod = (lhsSigned * rhsSigned << 1) + 0x8000;
                        ushort accLo = unchecked((ushort)prod);
                        short accMd = unchecked((short)(prod >> 16));
                        bool negative = accMd < 0;

                        _accLo[lane] = accLo;
                        _accMd[lane] = unchecked((ushort)accMd);
                        _accHi[lane] = (ushort)(negative ? 0xffff : 0x0000);

                        int laneValue = _accHi[lane] != 0
                            ? 0
                            : (ushort)(_accMd[lane] | _accHi[lane]);
                        result[lane] = unchecked((ushort)laneValue);
                    }
                    StoreVector(vd, result);
                    return true;

                case 0x08: // VMACF
                    for (int lane = 0; lane < 8; lane++)
                    {
                        long acc = ReadAccumulator(lane);
                        int lhsSigned = (short)lhs[lane];
                        int rhsSigned = (short)rhs[lane];
                        long prod = ((long)lhsSigned * rhsSigned) << 1;
                        acc += prod;
                        WriteAccumulator(lane, acc);
                        result[lane] = unchecked((ushort)SaturateAccumulatorToSignedMd(lane));
                    }
                    StoreVector(vd, result);
                    return true;

                case 0x05: // VMUDM
                    for (int lane = 0; lane < 8; lane++)
                    {
                        long prod = (long)(short)lhs[lane] * (ushort)rhs[lane];
                        _accLo[lane] = unchecked((ushort)prod);
                        _accMd[lane] = unchecked((ushort)(prod >> 16));
                        _accHi[lane] = (ushort)(((short)_accMd[lane] < 0) ? 0xffff : 0x0000);
                        result[lane] = _accMd[lane];
                    }
                    StoreVector(vd, result);
                    return true;

                case 0x04: // VMUDL
                    for (int lane = 0; lane < 8; lane++)
                    {
                        uint hi = (uint)(((ulong)(ushort)lhs[lane] * (ushort)rhs[lane]) >> 16);
                        _accLo[lane] = unchecked((ushort)hi);
                        _accMd[lane] = 0;
                        _accHi[lane] = 0;
                        result[lane] = unchecked((ushort)hi);
                    }
                    StoreVector(vd, result);
                    return true;

                case 0x06: // VMUDN
                    for (int lane = 0; lane < 8; lane++)
                    {
                        long prod = (long)(ushort)lhs[lane] * (short)rhs[lane];
                        _accLo[lane] = unchecked((ushort)prod);
                        _accMd[lane] = unchecked((ushort)(prod >> 16));
                        _accHi[lane] = (ushort)(((short)_accMd[lane] < 0) ? 0xffff : 0x0000);
                        result[lane] = _accLo[lane];
                    }
                    StoreVector(vd, result);
                    return true;

                case 0x07: // VMUDH
                    for (int lane = 0; lane < 8; lane++)
                    {
                        long prod = (long)(short)lhs[lane] * (short)rhs[lane];
                        _accLo[lane] = 0;
                        _accMd[lane] = unchecked((ushort)prod);
                        _accHi[lane] = unchecked((ushort)(prod >> 16));
                        result[lane] = unchecked((ushort)SaturateAccumulatorToSignedMd(lane));
                    }
                    StoreVector(vd, result);
                    return true;

                case 0x0d: // VMADM
                    for (int lane = 0; lane < 8; lane++)
                    {
                        long acc = ReadAccumulator(lane);
                        long prod = (long)(short)lhs[lane] * (ushort)rhs[lane];
                        acc += prod;
                        WriteAccumulator(lane, acc);
                        result[lane] = unchecked((ushort)SaturateAccumulatorToSignedMd(lane));
                    }
                    StoreVector(vd, result);
                    return true;

                case 0x0c: // VMADL
                    for (int lane = 0; lane < 8; lane++)
                    {
                        long acc = ReadAccumulator(lane);
                        long term = (long)(((ulong)(ushort)lhs[lane] * (ushort)rhs[lane]) >> 16);
                        WriteAccumulator(lane, acc + term);
                        result[lane] = UnsignedClampAccumulator(_accLo[lane], _accMd[lane], _accHi[lane]);
                    }
                    StoreVector(vd, result);
                    return true;

                case 0x0e: // VMADN
                    for (int lane = 0; lane < 8; lane++)
                    {
                        long acc = ReadAccumulator(lane);
                        long prod = (long)(ushort)lhs[lane] * (short)rhs[lane];
                        WriteAccumulator(lane, acc + prod);
                        result[lane] = UnsignedClampAccumulator(_accLo[lane], _accMd[lane], _accHi[lane]);
                    }
                    StoreVector(vd, result);
                    return true;

                case 0x0f: // VMADH
                    for (int lane = 0; lane < 8; lane++)
                    {
                        long acc = ReadAccumulator(lane);
                        long prod = ((long)(short)lhs[lane] * (short)rhs[lane]) << 16;
                        WriteAccumulator(lane, acc + prod);
                        result[lane] = unchecked((ushort)SaturateAccumulatorToSignedMd(lane));
                    }
                    StoreVector(vd, result);
                    return true;

                case 0x10: // VADD
                    for (int lane = 0; lane < 8; lane++)
                    {
                        int carry = GetMaskBit(_vco[1], lane) ? 1 : 0;
                        int sum = (short)lhs[lane] + (short)rhs[lane] + carry;
                        _accLo[lane] = unchecked((ushort)sum);
                        result[lane] = unchecked((ushort)ClampSigned16(sum));
                    }
                    _vco[0] = 0;
                    _vco[1] = 0;
                    StoreVector(vd, result);
                    return true;

                case 0x11: // VSUB
                    for (int lane = 0; lane < 8; lane++)
                    {
                        int carry = GetMaskBit(_vco[1], lane) ? 1 : 0;
                        int diff = (short)lhs[lane] - (short)rhs[lane] - carry;
                        _accLo[lane] = unchecked((ushort)diff);
                        result[lane] = unchecked((ushort)ClampSigned16(diff));
                    }
                    _vco[0] = 0;
                    _vco[1] = 0;
                    StoreVector(vd, result);
                    return true;

                case 0x14: // VADDC
                    {
                        ushort carryMask = 0;
                        for (int lane = 0; lane < 8; lane++)
                        {
                            uint sum = (uint)lhs[lane] + rhs[lane];
                            if (sum > 0xFFFFu)
                                carryMask |= (ushort)(1u << lane);
                            ushort laneResult = unchecked((ushort)sum);
                            _accLo[lane] = laneResult;
                            result[lane] = laneResult;
                        }
                        _vco[0] = 0;
                        _vco[1] = carryMask;
                        StoreVector(vd, result);
                        return true;
                    }

                case 0x15: // VSUBC
                    {
                        ushort neMask = 0;
                        ushort ltMask = 0;
                        for (int lane = 0; lane < 8; lane++)
                        {
                            ushort laneResult = unchecked((ushort)(lhs[lane] - rhs[lane]));
                            _accLo[lane] = laneResult;
                            result[lane] = laneResult;
                            if (lhs[lane] != rhs[lane])
                                neMask |= (ushort)(1u << lane);
                            if (lhs[lane] < rhs[lane])
                                ltMask |= (ushort)(1u << lane);
                        }
                        _vco[0] = neMask;
                        _vco[1] = ltMask;
                        StoreVector(vd, result);
                        return true;
                    }

                case 0x13: // VABS
                    for (int lane = 0; lane < 8; lane++)
                    {
                        short sel = (short)lhs[lane];
                        short value = sel == 0 ? (short)0 : (short)rhs[lane];
                        ushort acc = unchecked((ushort)(sel < 0 ? -value : value));
                        _accLo[lane] = acc;
                        result[lane] = sel < 0 && value == short.MinValue
                            ? (ushort)short.MaxValue
                            : acc;
                    }
                    StoreVector(vd, result);
                    return true;

                case 0x1D: // VSAR
                    switch (element & 0xF)
                    {
                        case 8:
                            for (int lane = 0; lane < 8; lane++)
                                result[lane] = _accHi[lane];
                            break;
                        case 9:
                            for (int lane = 0; lane < 8; lane++)
                                result[lane] = _accMd[lane];
                            break;
                        case 10:
                            for (int lane = 0; lane < 8; lane++)
                                result[lane] = _accLo[lane];
                            break;
                        default:
                            Array.Clear(result, 0, result.Length);
                            break;
                    }
                    StoreVector(vd, result);
                    return true;

                case 0x20: // VLT
                    {
                        ushort vccLo = 0;
                        for (int lane = 0; lane < 8; lane++)
                        {
                            bool equal = lhs[lane] == rhs[lane];
                            bool select = (short)lhs[lane] < (short)rhs[lane]
                                || (equal && GetMaskBit(_vco[0], lane) && GetMaskBit(_vco[1], lane));
                            if (select)
                                vccLo |= (ushort)(1u << lane);
                            result[lane] = select ? lhs[lane] : rhs[lane];
                            _accLo[lane] = result[lane];
                        }
                        _vcc[0] = 0;
                        _vcc[1] = vccLo;
                        _vco[0] = 0;
                        _vco[1] = 0;
                        StoreVector(vd, result);
                        return true;
                    }

                case 0x21: // VEQ
                    {
                        ushort vccLo = 0;
                        for (int lane = 0; lane < 8; lane++)
                        {
                            bool select = lhs[lane] == rhs[lane] && !GetMaskBit(_vco[0], lane);
                            if (select)
                                vccLo |= (ushort)(1u << lane);
                            result[lane] = select ? lhs[lane] : rhs[lane];
                            _accLo[lane] = result[lane];
                        }
                        _vcc[0] = 0;
                        _vcc[1] = vccLo;
                        _vco[0] = 0;
                        _vco[1] = 0;
                        StoreVector(vd, result);
                        return true;
                    }

                case 0x22: // VNE
                    {
                        ushort vccLo = 0;
                        for (int lane = 0; lane < 8; lane++)
                        {
                            bool equal = lhs[lane] == rhs[lane];
                            bool select = (GetMaskBit(_vco[0], lane) && equal) || !equal;
                            if (select)
                                vccLo |= (ushort)(1u << lane);
                            result[lane] = select ? lhs[lane] : rhs[lane];
                            _accLo[lane] = result[lane];
                        }
                        _vcc[0] = 0;
                        _vcc[1] = vccLo;
                        _vco[0] = 0;
                        _vco[1] = 0;
                        StoreVector(vd, result);
                        return true;
                    }

                case 0x23: // VGE
                    {
                        ushort vccLo = 0;
                        for (int lane = 0; lane < 8; lane++)
                        {
                            bool equal = lhs[lane] == rhs[lane];
                            bool select = (short)lhs[lane] > (short)rhs[lane]
                                || (equal && !(GetMaskBit(_vco[0], lane) && GetMaskBit(_vco[1], lane)));
                            if (select)
                                vccLo |= (ushort)(1u << lane);
                            result[lane] = select ? lhs[lane] : rhs[lane];
                            _accLo[lane] = result[lane];
                        }
                        _vcc[0] = 0;
                        _vcc[1] = vccLo;
                        _vco[0] = 0;
                        _vco[1] = 0;
                        StoreVector(vd, result);
                        return true;
                    }

                case 0x24: // VCL
                    {
                        ushort newGe = _vcc[0];
                        ushort newLe = _vcc[1];
                        ushort eq = _vco[0];
                        ushort sign = _vco[1];
                        ushort vceMask = _vce;
                        for (int lane = 0; lane < 8; lane++)
                        {
                            short vsLane = unchecked((short)lhs[lane]);
                            short vtLane = unchecked((short)rhs[lane]);
                            bool signBit = GetMaskBit(sign, lane);
                            short signNegVt = signBit ? unchecked((short)(-vtLane)) : vtLane;
                            short diff = unchecked((short)(vsLane - signNegVt));
                            bool diffZero = diff == 0;
                            bool ncarry = unchecked((ushort)diff) == SaturatingAddUnsigned((ushort)lhs[lane], (ushort)rhs[lane]);
                            bool vceSet = GetMaskBit(vceMask, lane);
                            bool eqBit = GetMaskBit(eq, lane);

                            if (signBit && !eqBit)
                            {
                                bool leEq = (!vceSet && diffZero && ncarry) || (vceSet && (diffZero || ncarry));
                                newLe = SetMaskBit(newLe, lane, leEq);
                            }

                            if (!signBit && !eqBit)
                            {
                                bool geEq = SaturatingSubUnsigned((ushort)rhs[lane], (ushort)lhs[lane]) == 0;
                                newGe = SetMaskBit(newGe, lane, geEq);
                            }

                            bool mux = signBit ? GetMaskBit(newLe, lane) : GetMaskBit(newGe, lane);
                            result[lane] = mux ? unchecked((ushort)signNegVt) : lhs[lane];
                            _accLo[lane] = result[lane];
                        }

                        _vcc[0] = newGe;
                        _vcc[1] = newLe;
                        _vco[0] = 0;
                        _vco[1] = 0;
                        _vce = 0;
                        StoreVector(vd, result);
                        return true;
                    }

                case 0x25: // VCH
                    {
                        ushort ge = 0;
                        ushort le = 0;
                        ushort eq = 0;
                        ushort sign = 0;
                        ushort vceMask = 0;
                        for (int lane = 0; lane < 8; lane++)
                        {
                            short vsLane = unchecked((short)lhs[lane]);
                            short vtLane = unchecked((short)rhs[lane]);
                            bool signBit = ((vsLane ^ vtLane) < 0);
                            sign = SetMaskBit(sign, lane, signBit);

                            short signNegVt = signBit ? unchecked((short)(-vtLane)) : vtLane;
                            short diff = unchecked((short)(vsLane - signNegVt));
                            bool diffZero = diff == 0;
                            bool vtNeg = vtLane < 0;
                            bool diffLez = diff <= 0;
                            bool diffGez = diff >= 0;
                            bool geBit = signBit ? vtNeg : diffGez;
                            bool leBit = signBit ? diffLez : vtNeg;
                            bool vceBit = signBit && diff == -1;
                            bool eqBit = !(diffZero || vceBit);

                            ge = SetMaskBit(ge, lane, geBit);
                            le = SetMaskBit(le, lane, leBit);
                            eq = SetMaskBit(eq, lane, eqBit);
                            vceMask = SetMaskBit(vceMask, lane, vceBit);

                            bool mux = signBit ? leBit : geBit;
                            result[lane] = mux ? unchecked((ushort)signNegVt) : lhs[lane];
                            _accLo[lane] = result[lane];
                        }

                        _vcc[0] = ge;
                        _vcc[1] = le;
                        _vco[0] = eq;
                        _vco[1] = sign;
                        _vce = (byte)vceMask;
                        StoreVector(vd, result);
                        return true;
                    }

                case 0x26: // VCR
                    {
                        ushort ge = 0;
                        ushort le = 0;
                        for (int lane = 0; lane < 8; lane++)
                        {
                            short vsLane = unchecked((short)lhs[lane]);
                            short vtLane = unchecked((short)rhs[lane]);
                            bool signBit = ((vsLane ^ vtLane) < 0);

                            int diffLe = (vsLane & (signBit ? -1 : 0)) + vtLane;
                            bool leBit = diffLe < 0;
                            int diffGe = Math.Min(vsLane | (signBit ? -1 : 0), vtLane);
                            bool geBit = diffGe == vtLane;

                            ge = SetMaskBit(ge, lane, geBit);
                            le = SetMaskBit(le, lane, leBit);

                            short signNotVt = signBit ? unchecked((short)~vtLane) : vtLane;
                            bool mux = signBit ? leBit : geBit;
                            result[lane] = mux ? unchecked((ushort)signNotVt) : lhs[lane];
                            _accLo[lane] = result[lane];
                        }

                        _vcc[0] = ge;
                        _vcc[1] = le;
                        _vco[0] = 0;
                        _vco[1] = 0;
                        _vce = 0;
                        StoreVector(vd, result);
                        return true;
                    }

                case 0x27: // VMRG
                    for (int lane = 0; lane < 8; lane++)
                    {
                        result[lane] = GetMaskBit(_vcc[1], lane) ? lhs[lane] : rhs[lane];
                        _accLo[lane] = result[lane];
                    }
                    _vco[0] = 0;
                    _vco[1] = 0;
                    StoreVector(vd, result);
                    return true;

                case 0x28: // VAND
                    for (int lane = 0; lane < 8; lane++)
                    {
                        result[lane] = (ushort)(lhs[lane] & rhs[lane]);
                        _accLo[lane] = result[lane];
                    }
                    StoreVector(vd, result);
                    return true;

                case 0x29: // VNAND
                    for (int lane = 0; lane < 8; lane++)
                    {
                        result[lane] = (ushort)~(lhs[lane] & rhs[lane]);
                        _accLo[lane] = result[lane];
                    }
                    StoreVector(vd, result);
                    return true;

                case 0x2A: // VOR
                    for (int lane = 0; lane < 8; lane++)
                    {
                        result[lane] = (ushort)(lhs[lane] | rhs[lane]);
                        _accLo[lane] = result[lane];
                    }
                    StoreVector(vd, result);
                    return true;

                case 0x2B: // VNOR
                    for (int lane = 0; lane < 8; lane++)
                    {
                        result[lane] = (ushort)~(lhs[lane] | rhs[lane]);
                        _accLo[lane] = result[lane];
                    }
                    StoreVector(vd, result);
                    return true;

                case 0x2C: // VXOR
                    for (int lane = 0; lane < 8; lane++)
                    {
                        result[lane] = (ushort)(lhs[lane] ^ rhs[lane]);
                        _accLo[lane] = result[lane];
                    }
                    StoreVector(vd, result);
                    return true;

                case 0x2D: // VNXOR
                    for (int lane = 0; lane < 8; lane++)
                    {
                        result[lane] = (ushort)~(lhs[lane] ^ rhs[lane]);
                        _accLo[lane] = result[lane];
                    }
                    StoreVector(vd, result);
                    return true;

                case 0x33: // VMOV
                    for (int lane = 0; lane < 8; lane++)
                        _accLo[lane] = rhs[lane];
                    WriteVectorLane16(vd, vs & 7, _accLo[vs & 7]);
                    return true;

                case 0x30: // VRCP
                    ExecuteReciprocal(vd, vs, vt, element, rsq: false, low: false);
                    return true;

                case 0x31: // VRCPL
                    ExecuteReciprocal(vd, vs, vt, element, rsq: false, low: true);
                    return true;

                case 0x32: // VRCPH
                    ExecuteReciprocalHigh(vd, vs, vt, element);
                    return true;

                case 0x34: // VRSQ
                    ExecuteReciprocal(vd, vs, vt, element, rsq: true, low: false);
                    return true;

                case 0x35: // VRSQL
                    ExecuteReciprocal(vd, vs, vt, element, rsq: true, low: true);
                    return true;

                case 0x36: // VRSQH
                    ExecuteReciprocalHigh(vd, vs, vt, element);
                    return true;

                case 0x37: // VNOP
                    return true;

                case 0x09: // VMACU
                    for (int lane = 0; lane < 8; lane++)
                    {
                        long acc = ReadAccumulator(lane);
                        long prod = ((long)(short)lhs[lane] * (short)rhs[lane]) << 1;
                        WriteAccumulator(lane, acc + prod);
                        result[lane] = UnsignedClampAccumulator(_accMd[lane], _accMd[lane], _accHi[lane]);
                    }
                    StoreVector(vd, result);
                    return true;

                default:
                    stopReason = $"unsupported-vector-op pc=0x{pc:x3} op=0x{instr:x8}";
                    return false;
            }
        }

        private bool ExecuteVectorMemory(uint pc, bool isLoad, uint rs, uint vt, uint instr, out string stopReason)
        {
            int subop = (int)((instr >> 11) & 0x1F);
            int element = (int)((instr >> 7) & 0xF);
            int offset = SignExtend7((int)(instr & 0x7F));
            uint baseAddress = _gpr[rs];
            stopReason = string.Empty;

            switch (subop)
            {
                case 0: // BV
                    TransferVectorBytes(isLoad, (int)vt, element, baseAddress + (uint)offset, 1);
                    return true;
                case 1: // SV
                    TransferVectorBytes(isLoad, (int)vt, element, baseAddress + (uint)(offset << 1), 2);
                    return true;
                case 2: // LV
                    TransferVectorBytes(isLoad, (int)vt, element, baseAddress + (uint)(offset << 2), 4);
                    return true;
                case 3: // DV
                    TransferVectorBytes(isLoad, (int)vt, element, baseAddress + (uint)(offset << 3), 8);
                    return true;
                case 4: // QV
                    TransferVectorBytes(isLoad, (int)vt, element, baseAddress + (uint)(offset << 4), 16);
                    return true;
                case 5: // RV
                    TransferVectorReverse(isLoad, (int)vt, element, baseAddress + (uint)(offset << 4));
                    return true;
                case 6: // PV
                    TransferVectorPacked(isLoad, (int)vt, element, baseAddress + (uint)(offset << 3), shiftBy7: false);
                    return true;
                case 7: // UV
                    TransferVectorPacked(isLoad, (int)vt, element, baseAddress + (uint)(offset << 3), shiftBy7: true);
                    return true;
                case 8: // HV
                    TransferVectorHalfPacked(isLoad, (int)vt, element, baseAddress + (uint)(offset << 4));
                    return true;
                case 9: // FV
                    if (isLoad)
                        LoadVectorFour((int)vt, element, baseAddress + (uint)(offset << 4));
                    else
                        StoreVectorFour((int)vt, element, baseAddress + (uint)(offset << 4));
                    return true;
                case 10: // WV
                    if (!isLoad)
                    {
                        StoreVectorWrapped((int)vt, element, baseAddress + (uint)(offset << 4));
                        return true;
                    }
                    stopReason = $"unsupported-vector-mem pc=0x{pc:x3} op=0x{instr:x8}";
                    return false;
                case 11: // TV
                    TransferVectorTable(isLoad, (int)vt, element, baseAddress + (uint)(offset << 4));
                    return true;
                default:
                    stopReason = $"unsupported-vector-mem pc=0x{pc:x3} op=0x{instr:x8}";
                    return false;
            }
        }

        private void TransferVectorBytes(bool isLoad, int vt, int element, uint address, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int lane = (element + i) & 0xF;
                uint addr = (address + (uint)i) & 0xFFFu;
                if (isLoad)
                    _vr[vt, lane] = ReadByte(addr);
                else
                    WriteByte(addr, _vr[vt, lane]);
            }
        }

        private void TransferVectorReverse(bool isLoad, int vt, int element, uint address)
        {
            uint addr = address & 0xFFFu;
            if (isLoad)
            {
                int start = 16 - (int)((addr & 0xFu) - (uint)element);
                addr &= ~0xFu;
                for (int i = start; i < 16; i++)
                    _vr[vt, i & 0xF] = ReadByte(addr++ & 0xFFFu);
            }
            else
            {
                uint end = (uint)(element + (int)(addr & 0xFu));
                uint baseIndex = 16u - (addr & 0xFu);
                addr &= ~0xFu;
                for (uint i = (uint)element; i < end; i++)
                    WriteByte(addr++, _vr[vt, (int)((i + baseIndex) & 0xFu)]);
            }
        }

        private void TransferVectorPacked(bool isLoad, int vt, int element, uint address, bool shiftBy7)
        {
            uint addr = address & 0xFFFu;
            if (isLoad)
            {
                int index = (int)(addr & 7u) - element;
                addr &= ~7u;
                for (int i = 0; i < 8; i++)
                {
                    byte value = ReadByte((addr + (uint)((i + index) & 0xF)) & 0xFFFu);
                    WriteVectorLane16(vt, i, (ushort)(value << (shiftBy7 ? 7 : 8)));
                }
            }
            else
            {
                for (int i = element; i < element + 8; i++)
                {
                    int lane = i & 7;
                    short value = unchecked((short)ReadVectorLane16(vt, lane));
                    int shift = ((i & 0xF) < 8) == shiftBy7 ? 7 : 8;
                    WriteByte(addr++ & 0xFFFu, unchecked((byte)(value >> shift)));
                }
            }
        }

        private void TransferVectorHalfPacked(bool isLoad, int vt, int element, uint address)
        {
            uint addr = address & 0xFFFu;
            if (isLoad)
            {
                int index = (int)(addr & 7u) - element;
                addr &= ~7u;
                for (int i = 0; i < 8; i++)
                {
                    byte value = ReadByte((addr + (uint)((index + (i * 2)) & 0xF)) & 0xFFFu);
                    WriteVectorLane16(vt, i, (ushort)(value << 7));
                }
            }
            else
            {
                uint baseIndex = addr & 7u;
                addr &= ~7u;
                for (int i = 0; i < 8; i++)
                {
                    int b = element + (i << 1);
                    byte packed = (byte)((_vr[vt, b & 0xF] << 1) | (_vr[vt, (b + 1) & 0xF] >> 7));
                    WriteByte(addr + ((baseIndex + (uint)(i * 2)) & 0xFu), packed);
                }
            }
        }

        private void LoadVectorFour(int vt, int element, uint address)
        {
            uint addr = address & 0xFFFu;
            int index = (int)(addr & 7u) - element;
            int end = element > 8 ? 16 : element + 8;
            addr &= ~7u;

            byte[] temp = new byte[16];
            for (int i = 0; i < 4; i++)
            {
                temp[i] = ReadByte((addr + (uint)((index + (i * 4)) & 0xF)) & 0xFFFu);
                temp[i + 4] = ReadByte((addr + (uint)((index + (i * 4) + 8) & 0xF)) & 0xFFFu);
            }

            for (int i = element; i < end; i++)
                _vr[vt, i & 0xF] = temp[i & 0xF];
        }

        private void StoreVectorFour(int vt, int element, uint address)
        {
            uint addr = address & 0xFFFu;
            uint baseIndex = addr & 7u;
            addr &= ~7u;

            void WriteSfvCase(int a, int b, int c, int d)
            {
                WriteByte(addr + baseIndex, unchecked((byte)((short)ReadVectorLane16(vt, a) >> 7)));
                WriteByte(addr + 4 + baseIndex, unchecked((byte)((short)ReadVectorLane16(vt, b) >> 7)));
                WriteByte(addr + ((8 + baseIndex) & 0xFu), unchecked((byte)((short)ReadVectorLane16(vt, c) >> 7)));
                WriteByte(addr + ((12 + baseIndex) & 0xFu), unchecked((byte)((short)ReadVectorLane16(vt, d) >> 7)));
            }

            switch (element & 0xF)
            {
                case 0:
                case 15:
                    WriteSfvCase(0, 1, 2, 3);
                    break;
                case 1:
                    WriteSfvCase(6, 7, 4, 5);
                    break;
                case 4:
                    WriteSfvCase(1, 2, 3, 0);
                    break;
                case 5:
                    WriteSfvCase(7, 4, 5, 6);
                    break;
                case 8:
                    WriteSfvCase(4, 5, 6, 7);
                    break;
                case 11:
                    WriteSfvCase(3, 0, 1, 2);
                    break;
                case 12:
                    WriteSfvCase(5, 6, 7, 4);
                    break;
                default:
                    WriteByte(addr + baseIndex, 0);
                    WriteByte(addr + 4 + baseIndex, 0);
                    WriteByte(addr + ((8 + baseIndex) & 0xFu), 0);
                    WriteByte(addr + ((12 + baseIndex) & 0xFu), 0);
                    break;
            }
        }

        private void StoreVectorWrapped(int vt, int element, uint address)
        {
            uint addr = address & 0xFFFu;
            uint baseIndex = addr & 7u;
            addr &= ~7u;

            for (int i = element; i < element + 16; i++)
                WriteByte(addr + (baseIndex++ & 0xFu), _vr[vt, i & 0xF]);
        }

        private void TransferVectorTable(bool isLoad, int vt, int element, uint address)
        {
            if (isLoad)
            {
                uint addr = address & 0xFFFu;
                uint start = addr & ~7u;
                int baseVt = vt & ~7;
                addr = start + (uint)((element + (int)(addr & 8u)) & 0xF);
                int regIndex = element >> 1;

                for (int i = 0; i < 16; regIndex++)
                {
                    regIndex &= 7;
                    _vr[baseVt + regIndex, i++] = ReadByte(addr++ & 0xFFFu);
                    if (addr == start + 16)
                        addr = start;
                    _vr[baseVt + regIndex, i++] = ReadByte(addr++ & 0xFFFu);
                    if (addr == start + 16)
                        addr = start;
                }
            }
            else
            {
                int e = element & ~1;
                int baseVt = vt & ~7;
                uint addr = address & 0xFFFu;
                int outElement = 16 - e;
                uint baseIndex = (addr & 7u) - (uint)e;
                addr &= ~7u;

                for (int i = baseVt; i < baseVt + 8; i++)
                {
                    WriteByte(addr + (baseIndex++ & 0xFu), _vr[i, outElement++ & 0xF]);
                    WriteByte(addr + (baseIndex++ & 0xFu), _vr[i, outElement++ & 0xF]);
                }
            }
        }

        private ushort ReadVectorElement(int vt, int element)
        {
            int lane = element & 0xF;
            byte hi = _vr[vt, lane];
            byte lo = _vr[vt, (lane + 1) & 0xF];
            return (ushort)((hi << 8) | lo);
        }

        private void WriteVectorElement(int vt, int element, ushort value)
        {
            int lane = element & 0xF;
            _vr[vt, lane] = (byte)(value >> 8);
            _vr[vt, (lane + 1) & 0xF] = (byte)value;
        }

        private ushort ReadVectorLane16(int vt, int lane)
        {
            int byteIndex = (lane & 7) * 2;
            return (ushort)((_vr[vt, byteIndex] << 8) | _vr[vt, byteIndex + 1]);
        }

        private void WriteVectorLane16(int vt, int lane, ushort value)
        {
            int byteIndex = (lane & 7) * 2;
            _vr[vt, byteIndex] = (byte)(value >> 8);
            _vr[vt, byteIndex + 1] = (byte)value;
        }

        private void LoadVectorUnshuffled(int vt, ushort[] dest)
        {
            for (int lane = 0; lane < 8; lane++)
                dest[lane] = ReadVectorLane16(vt, lane);
        }

        private void LoadVectorShuffled(int vt, int element, ushort[] dest)
        {
            for (int lane = 0; lane < 8; lane++)
            {
                int sourceLane;
                switch (element & 0xF)
                {
                    case 0:
                    case 1:
                        sourceLane = lane;
                        break;
                    case 2:
                        sourceLane = (lane & ~1) & 7;
                        break;
                    case 3:
                        sourceLane = ((lane & ~1) | 1) & 7;
                        break;
                    case 4:
                    case 5:
                    case 6:
                    case 7:
                        sourceLane = ((lane & 2) != 0 ? element : element - 4) & 7;
                        break;
                    default:
                        sourceLane = (element - 8) & 7;
                        break;
                }

                dest[lane] = ReadVectorLane16(vt, sourceLane);
            }
        }

        private void StoreVector(int vt, ushort[] src)
        {
            for (int lane = 0; lane < 8; lane++)
                WriteVectorLane16(vt, lane, src[lane]);
        }

        private uint ReadVectorControl(int rd)
        {
            switch (rd & 3)
            {
                case 0:
                    return (uint)((_vco[0] << 8) | _vco[1]);
                case 1:
                    return (uint)((_vcc[0] << 8) | _vcc[1]);
                default:
                    return _vce;
            }
        }

        private void WriteVectorControl(int rd, ushort value)
        {
            switch (rd & 3)
            {
                case 0:
                    _vco[0] = (ushort)((value >> 8) & 0xFF);
                    _vco[1] = (ushort)(value & 0xFF);
                    break;
                case 1:
                    _vcc[0] = (ushort)((value >> 8) & 0xFF);
                    _vcc[1] = (ushort)(value & 0xFF);
                    break;
                default:
                    _vce = (byte)value;
                    break;
            }
        }

        private uint ReadWord(uint address)
        {
            uint aligned = address & 0x1FFFu;
            uint value = _memory.ReadSpMemoryWord(aligned);
            TraceRspStackWordAccess(isWrite: false, aligned, value);
            TraceRspScalarRead("word", aligned, value);
            return value;
        }

        private ushort ReadHalf(uint address)
        {
            uint word = ReadWord(address & ~3u);
            int shift = (int)((1 - ((address & 2u) >> 1)) * 16);
            ushort value = (ushort)((word >> shift) & 0xFFFFu);
            TraceRspScalarRead("half", address & 0x0FFFu, value);
            return value;
        }

        private byte ReadByte(uint address)
        {
            uint word = ReadWord(address & ~3u);
            int shift = (int)((3 - (address & 3u)) * 8);
            byte value = (byte)((word >> shift) & 0xFFu);
            TraceRspScalarRead("byte", address & 0x0FFFu, value);
            return value;
        }

        private void WriteWord(uint address, uint value)
        {
            uint aligned = address & 0x1FFFu;
            TraceRspStackWordAccess(isWrite: true, aligned, value);
            _memory.WriteSpMemoryWord(aligned, value);
        }

        private void TraceRspStackWordAccess(bool isWrite, uint address, uint value)
        {
            if (!TraceRspFlow)
                return;

            if (address < 0x02B0u || address > 0x02D8u)
                return;

            Common.Logger.PrintWarningLine(
                $"[N64RSPSTACK] {(isWrite ? "write" : "read")} pc=0x{(_pc & 0x0ffcu):x3} op=0x{_memory.ReadSpImemWord(_pc & 0x0ffcu):x8} " +
                $"addr=0x{address:x3} value=0x{value:x8} r25=0x{_gpr[25]:x8} r26=0x{_gpr[26]:x8} r27=0x{_gpr[27]:x8} r28=0x{_gpr[28]:x8} r29=0x{_gpr[29]:x8} ra=0x{_gpr[31]:x8}");
        }

        private void TraceRspScalarRead(string kind, uint address, uint value)
        {
            if (!TraceRspFlow)
                return;

            uint pc = _pc & 0x0FFCu;
            bool interestingPc =
                (pc >= 0x2D0 && pc <= 0x2E0)
                || (pc >= 0xFB0 && pc <= 0xFC4);
            bool interestingAddr =
                (address >= 0x0400 && address <= 0x0430)
                || (address >= 0x02B0 && address <= 0x02D8);
            if (!interestingPc && !interestingAddr)
                return;

            Common.Logger.PrintWarningLine(
                $"[N64RSPLOAD] pc=0x{pc:x3} kind={kind} addr=0x{(address & 0x0FFFu):x3} value=0x{value:x8} " +
                $"t3=0x{_gpr[11]:x8} t4=0x{_gpr[12]:x8} s3=0x{_gpr[19]:x8} s4=0x{_gpr[20]:x8} t8=0x{_gpr[24]:x8} ra=0x{_gpr[31]:x8}");
        }

        private void WriteHalf(uint address, ushort value)
        {
            uint aligned = address & ~3u;
            uint word = ReadWord(aligned);
            int shift = (int)((1 - ((address & 2u) >> 1)) * 16);
            uint mask = 0xFFFFu << shift;
            WriteWord(aligned, (word & ~mask) | ((uint)value << shift));
        }

        private void WriteByte(uint address, byte value)
        {
            uint aligned = address & ~3u;
            uint word = ReadWord(aligned);
            int shift = (int)((3 - (address & 3u)) * 8);
            uint mask = 0xFFu << shift;
            WriteWord(aligned, (word & ~mask) | ((uint)value << shift));
        }

        private void WriteGpr(uint reg, uint value)
        {
            if (reg != 0)
            {
                if (TraceRspFlow && (reg == 2 || reg == 11 || reg == 12 || reg == 19 || reg == 20 || reg == 24 || reg == 25 || reg == 26 || reg == 27 || reg == 31))
                {
                    Common.Logger.PrintWarningLine(
                        $"[N64RSPGPR] pc=0x{(_pc & 0x0ffcu):x3} r{reg} old=0x{_gpr[reg]:x8} new=0x{value:x8} " +
                        $"t3=0x{_gpr[11]:x8} t4=0x{_gpr[12]:x8} s3=0x{_gpr[19]:x8} s4=0x{_gpr[20]:x8} " +
                        $"r24=0x{_gpr[24]:x8} ra=0x{_gpr[31]:x8} r25=0x{_gpr[25]:x8} r26=0x{_gpr[26]:x8} r27=0x{_gpr[27]:x8} r28=0x{_gpr[28]:x8} r29=0x{_gpr[29]:x8}");
                }
                _gpr[reg] = value;
            }
        }

        private void SetBranch(uint target)
        {
            _branchPending = true;
            _branchTarget = target;
        }

        private bool ExecuteLikelyBranch(bool take, uint target)
        {
            if (take)
            {
                SetBranch(target);
            }
            else
            {
                _skipNextInstruction = true;
            }

            return true;
        }

        private static int SignExtend7(int value)
        {
            value &= 0x7F;
            return (value & 0x40) != 0 ? value | unchecked((int)0xFFFFFF80) : value;
        }

        private static uint BranchTarget(uint pc, short imm)
        {
            return unchecked((uint)((int)(pc + 4) + (imm << 2)));
        }

        private static int ClampSigned16(int value)
        {
            if (value > short.MaxValue)
                return short.MaxValue;
            if (value < short.MinValue)
                return short.MinValue;
            return value;
        }

        private long ReadAccumulator(int lane)
        {
            return ((long)(short)_accHi[lane] << 32)
                 | ((long)_accMd[lane] << 16)
                 | _accLo[lane];
        }

        private void WriteAccumulator(int lane, long value)
        {
            value = NormalizeAccumulator48(value);
            _accLo[lane] = unchecked((ushort)value);
            _accMd[lane] = unchecked((ushort)(value >> 16));
            _accHi[lane] = unchecked((ushort)(value >> 32));
        }

        private short SaturateAccumulatorToSignedMd(int lane)
        {
            int top = ((short)_accHi[lane] << 16) | _accMd[lane];
            if (top > short.MaxValue)
                return short.MaxValue;
            if (top < short.MinValue)
                return short.MinValue;
            return unchecked((short)top);
        }

        private static long NormalizeAccumulator48(long value)
        {
            const long Mask48 = 0x0000ffffffffffffL;
            value &= Mask48;
            if ((value & 0x0000800000000000L) != 0)
                value |= unchecked((long)0xffff000000000000UL);
            return value;
        }

        private static ushort UnsignedClampAccumulator(ushort value, ushort accMd, ushort accHi)
        {
            short hiSigned = unchecked((short)accHi);
            short mdSigned = unchecked((short)accMd);
            bool hiIsSignExtension = accHi == 0x0000 || accHi == 0xffff;
            bool signMatches = (hiSigned < 0) == (mdSigned < 0);

            if (hiIsSignExtension && signMatches)
                return value;

            return hiSigned < 0 ? (ushort)0x0000 : (ushort)0xffff;
        }

        private static bool GetMaskBit(ushort mask, int lane)
        {
            return ((mask >> lane) & 1) != 0;
        }

        private static ushort SetMaskBit(ushort mask, int lane, bool value)
        {
            ushort bit = (ushort)(1u << lane);
            return value ? (ushort)(mask | bit) : (ushort)(mask & ~bit);
        }

        private static ushort SaturatingAddUnsigned(ushort a, ushort b)
        {
            uint sum = (uint)a + b;
            return (ushort)(sum > 0xffffu ? 0xffffu : sum);
        }

        private static ushort SaturatingSubUnsigned(ushort a, ushort b)
        {
            return a > b ? (ushort)(a - b) : (ushort)0;
        }

        private void ExecuteReciprocalHigh(int vd, int vs, int vt, int element)
        {
            int de = vs & 7;
            int srcLane = element & 7;
            ushort vtValue = ReadVectorLane16(vt, srcLane);
            _divIn = vtValue;
            _dpFlag = 1;
            WriteVectorLane16(vd, de, _divOut);
        }

        private void ExecuteReciprocal(int vd, int vs, int vt, int element, bool rsq, bool low)
        {
            int de = vs & 7;
            int srcLane = element & 7;
            ushort vtValue = ReadVectorLane16(vt, srcLane);

            for (int lane = 0; lane < 8; lane++)
                _accLo[lane] = ReadVectorLane16(vt, lane);

            int input = low && (_dpFlag & 1) != 0
                ? (int)(((uint)_divIn << 16) | vtValue)
                : unchecked((short)vtValue);
            _dpFlag = 0;

            int result = ComputeRspReciprocal(rsq, input);
            _divOut = (ushort)(result >> 16);
            WriteVectorLane16(vd, de, (ushort)result);
        }

        private static int ComputeRspReciprocal(bool rsq, int input)
        {
            int inputMask = input >> 31;
            int data = input ^ inputMask;
            if (input > -32768)
                data -= inputMask;

            if (data == 0)
                return unchecked((int)0x7fffffffU);
            if (input == -32768)
                return unchecked((int)0xffff0000U);

            uint absData = (uint)data;
            int shift = CountLeadingZeros(absData);
            int index = (int)((((ulong)absData << shift) & 0x7FC00000UL) >> 22);

            int result;
            if (rsq)
            {
                index = ((index | 0x200) & 0x3FE) | (shift & 1);
                result = ReciprocalRom[index];
                result = (int)(((0x10000u | (uint)result) << 14) >> ((31 - shift) >> 1));
            }
            else
            {
                result = ReciprocalRom[index];
                result = (int)(((0x10000u | (uint)result) << 14) >> (31 - shift));
            }

            return result ^ inputMask;
        }

        private string FormatRecentTrace()
        {
            string[] parts = new string[_recentPcs.Length];
            for (int i = 0; i < _recentPcs.Length; i++)
            {
                int idx = (_recentIndex + i) % _recentPcs.Length;
                parts[i] = $"0x{_recentPcs[idx]:x3}:0x{_recentInstrs[idx]:x8}";
            }

            return string.Join(",", parts);
        }

        private static int CountLeadingZeros(uint value)
        {
            int count = 0;
            uint mask = 0x80000000u;
            while ((value & mask) == 0)
            {
                count++;
                mask >>= 1;
            }

            return count;
        }
    }
}
