using System;

namespace ProjectPSX.Devices.CdRom {
    public readonly struct SubchannelQ {
        public SubchannelQ(
            byte controlAdr,
            byte track,
            byte index,
            byte minute,
            byte second,
            byte frame,
            byte absoluteZero,
            byte absoluteMinute,
            byte absoluteSecond,
            byte absoluteFrame,
            bool hasValidCrc = true) {
            ControlAdr = controlAdr;
            Track = track;
            Index = index;
            Minute = minute;
            Second = second;
            Frame = frame;
            AbsoluteZero = absoluteZero;
            AbsoluteMinute = absoluteMinute;
            AbsoluteSecond = absoluteSecond;
            AbsoluteFrame = absoluteFrame;
            HasValidCrc = hasValidCrc;
        }

        public byte ControlAdr { get; }
        public byte Track { get; }
        public byte Index { get; }
        public byte Minute { get; }
        public byte Second { get; }
        public byte Frame { get; }
        public byte AbsoluteZero { get; }
        public byte AbsoluteMinute { get; }
        public byte AbsoluteSecond { get; }
        public byte AbsoluteFrame { get; }
        public bool HasValidCrc { get; }

        public static ushort ComputeCrc(ReadOnlySpan<byte> qData) {
            ushort value = 0;
            int length = Math.Min(qData.Length, 10);
            for (int i = 0; i < length; i++) {
                value ^= (ushort)(qData[i] << 8);
                for (int bit = 0; bit < 8; bit++) {
                    value = (ushort)(((value & 0x8000) != 0)
                        ? ((value << 1) ^ 0x1021)
                        : (value << 1));
                }
            }

            ushort inverted = (ushort)~value;
            return (ushort)((inverted >> 8) | (inverted << 8));
        }

        public static SubchannelQ FromCloneCdSub(ReadOnlySpan<byte> subFrame) {
            ReadOnlySpan<byte> qData = subFrame.Slice(12, 10);
            ushort actualCrc = (ushort)(subFrame[22] | (subFrame[23] << 8));
            bool hasValidCrc = actualCrc == ComputeCrc(qData);
            return new SubchannelQ(
                subFrame[12],
                subFrame[13],
                subFrame[14],
                subFrame[15],
                subFrame[16],
                subFrame[17],
                subFrame[18],
                subFrame[19],
                subFrame[20],
                subFrame[21],
                hasValidCrc);
        }
    }
}
