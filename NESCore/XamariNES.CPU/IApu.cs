namespace XamariNES.CPU
{
    public interface IApu
    {
        void WriteRegister(int offset, byte value);
        byte ReadStatus();
        bool IrqPending { get; }
        void TickCpu(int cycles);
        short[] ConsumeAudioBuffer();
        int SampleRate { get; }
    }
}
