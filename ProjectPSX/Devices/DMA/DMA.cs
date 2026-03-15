using System;
using System.Numerics;

namespace ProjectPSX.Devices; 
public class DMA {

    Channel[] channels = new Channel[8];
    private uint pendingTransferMask;

    public DMA(BUS bus) {
        var interrupt = new InterruptChannel();
        channels[0] = new DmaChannel(0, interrupt, bus);
        channels[1] = new DmaChannel(1, interrupt, bus);
        channels[2] = new DmaChannel(2, interrupt, bus);
        channels[3] = new DmaChannel(3, interrupt, bus);
        channels[4] = new DmaChannel(4, interrupt, bus);
        channels[5] = new DmaChannel(5, interrupt, bus);
        channels[6] = new DmaChannel(6, interrupt, bus);
        channels[7] = interrupt;
    }

    public uint load(uint addr) {
        var channel = (addr & 0x70) >> 4;
        var register = addr & 0xF;
        //Console.WriteLine("DMA load " + channel + " " + register  + ":" + channels[channel].load(register).ToString("x8"));
        return channels[channel].load(register);
    }

    public void write(uint addr, uint value) {
        var channel = (addr & 0x70) >> 4;
        var register = addr & 0xF;
        //Console.WriteLine("DMA write " + channel + " " + register + ":" + value.ToString("x8"));

        channels[channel].write(register, value);
        if (channel < 7) {
            RefreshPendingBit((int)channel);
        }
    }

    public bool tick(int cycles) {
        uint mask = pendingTransferMask;
        while (mask != 0) {
            int channelIndex = BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;

            var channel = (DmaChannel)channels[channelIndex];
            channel.transferBlockIfPending(cycles);
            if (!channel.HasPendingTransfer) {
                pendingTransferMask &= ~(1u << channelIndex);
            }
        }
        return ((InterruptChannel)channels[7]).tick();
    }

    public bool HasPendingWork => pendingTransferMask != 0 || ((InterruptChannel)channels[7]).HasPendingInterrupt;
    public int ChannelCount => channels.Length;

    public object GetChannelStateObject(int index) => channels[index];

    public string DebugSummary(int channelIndex) {
        if ((uint)channelIndex >= 7u) {
            return $"ch={channelIndex} invalid";
        }

        return ((DmaChannel)channels[channelIndex]).DebugSummary();
    }

    private void RefreshPendingBit(int channelIndex) {
        var channel = (DmaChannel)channels[channelIndex];
        if (channel.HasPendingTransfer) {
            pendingTransferMask |= 1u << channelIndex;
        } else {
            pendingTransferMask &= ~(1u << channelIndex);
        }
    }

}
