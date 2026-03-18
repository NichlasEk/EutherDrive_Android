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
            byte absoluteFrame) {
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

        public static SubchannelQ FromCloneCdSub(ReadOnlySpan<byte> subFrame) =>
            new SubchannelQ(
                subFrame[12],
                subFrame[13],
                subFrame[14],
                subFrame[15],
                subFrame[16],
                subFrame[17],
                subFrame[18],
                subFrame[19],
                subFrame[20],
                subFrame[21]);
    }
}
