using XamariNES.Common.Extensions;

namespace XamariNES.Cartridge.Mappers.impl
{
    internal sealed class KonamiIrqCounter
    {
        private enum IrqMode
        {
            Scanline,
            Cycle
        }

        private static readonly int[] PrescalerSequence = { 114, 114, 113 };

        private byte _irqCounter;
        private int _prescalerCounter;
        private int _prescalerSeqIndex;
        private bool _enabled;
        private bool _pending;
        private IrqMode _mode = IrqMode.Scanline;
        private byte _reloadValue;
        private bool _enableAfterAck;

        public void SetReloadValue(byte value)
        {
            _reloadValue = value;
        }

        public void SetReloadValueLow4Bits(byte value)
        {
            _reloadValue = (byte)((_reloadValue & 0xF0) | (value & 0x0F));
        }

        public void SetReloadValueHigh4Bits(byte value)
        {
            _reloadValue = (byte)((_reloadValue & 0x0F) | ((value & 0x0F) << 4));
        }

        public void SetControl(byte value)
        {
            _pending = false;
            ResetPrescaler();

            _enableAfterAck = value.IsBitSet(0);
            _enabled = value.IsBitSet(1);
            _mode = value.IsBitSet(2) ? IrqMode.Cycle : IrqMode.Scanline;

            if (_enabled)
                _irqCounter = _reloadValue;
        }

        public void Acknowledge()
        {
            _pending = false;
            _enabled = _enableAfterAck;
        }

        public void TickCpu()
        {
            if (!_enabled)
                return;

            if (_mode == IrqMode.Scanline)
            {
                _prescalerCounter++;
                if (_prescalerCounter == PrescalerSequence[_prescalerSeqIndex])
                {
                    ClockIrq();
                    _prescalerCounter = 0;
                    _prescalerSeqIndex = (_prescalerSeqIndex + 1) % PrescalerSequence.Length;
                }
            }
            else
            {
                ClockIrq();
            }
        }

        public bool InterruptFlag => _pending;

        private void ClockIrq()
        {
            if (_irqCounter == byte.MaxValue)
            {
                _irqCounter = _reloadValue;
                _pending = true;
            }
            else
            {
                _irqCounter++;
            }
        }

        private void ResetPrescaler()
        {
            _prescalerCounter = 0;
            _prescalerSeqIndex = 0;
        }
    }
}
