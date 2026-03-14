using System;
using System.IO;

namespace KSNES.Specialchips.SPC7110;

[Serializable]
internal sealed class Rtc4513
{
    public const byte StatusByte = 0x80;

    private enum Command
    {
        None,
        Read,
        Write
    }

    private enum HourType
    {
        Am,
        Pm
    }

    private enum ClockHours
    {
        Twelve,
        TwentyFour
    }

    private enum InterruptDuty
    {
        FixedTime,
        UntilAck
    }

    [Serializable]
    private struct RtcTime
    {
        public long LastUpdateTicksUtc;
        public int SubSecondTicks;
        public byte Seconds;
        public byte Minutes;
        public byte Hours;
        public HourType HourType;
        public byte Day;
        public byte Month;
        public byte Year;
        public byte DayOfWeek;
        public ClockHours ClockHours;
        public bool CalendarEnabled;
    }

    private Command _command;
    private byte? _register;
    private RtcTime _time;
    private bool _selected;
    private bool _wrapped;
    private bool _paused;
    private bool _pendingSecondsIncrement;
    private bool _stopped;
    private bool _reset;
    private bool _timeLost = true;
    private bool _irq;
    private bool _irqEnabled = true;
    private byte _irqRateBits;
    private long _irqRateTicks = TimeSpan.TicksPerSecond / 64;
    private long _lastIrqTicksUtc;
    private InterruptDuty _irqDuty;
    private byte _lastDayWriteHigh;
    private byte _lastMonthWriteHigh;

    public Rtc4513()
    {
        long now = DateTime.UtcNow.Ticks;
        _time = new RtcTime
        {
            LastUpdateTicksUtc = now
        };
        _lastIrqTicksUtc = now;
    }

    public void Save(BinaryWriter writer)
    {
        writer.Write((byte)_command);
        writer.Write(_register.HasValue);
        if (_register.HasValue)
            writer.Write(_register.Value);
        writer.Write(_time.LastUpdateTicksUtc);
        writer.Write(_time.SubSecondTicks);
        writer.Write(_time.Seconds);
        writer.Write(_time.Minutes);
        writer.Write(_time.Hours);
        writer.Write((byte)_time.HourType);
        writer.Write(_time.Day);
        writer.Write(_time.Month);
        writer.Write(_time.Year);
        writer.Write(_time.DayOfWeek);
        writer.Write((byte)_time.ClockHours);
        writer.Write(_time.CalendarEnabled);
        writer.Write(_selected);
        writer.Write(_wrapped);
        writer.Write(_paused);
        writer.Write(_pendingSecondsIncrement);
        writer.Write(_stopped);
        writer.Write(_reset);
        writer.Write(_timeLost);
        writer.Write(_irq);
        writer.Write(_irqEnabled);
        writer.Write(_irqRateBits);
        writer.Write(_irqRateTicks);
        writer.Write(_lastIrqTicksUtc);
        writer.Write((byte)_irqDuty);
        writer.Write(_lastDayWriteHigh);
        writer.Write(_lastMonthWriteHigh);
    }

    public void Load(BinaryReader reader)
    {
        _command = (Command)reader.ReadByte();
        _register = reader.ReadBoolean() ? reader.ReadByte() : null;
        _time.LastUpdateTicksUtc = reader.ReadInt64();
        _time.SubSecondTicks = reader.ReadInt32();
        _time.Seconds = reader.ReadByte();
        _time.Minutes = reader.ReadByte();
        _time.Hours = reader.ReadByte();
        _time.HourType = (HourType)reader.ReadByte();
        _time.Day = reader.ReadByte();
        _time.Month = reader.ReadByte();
        _time.Year = reader.ReadByte();
        _time.DayOfWeek = reader.ReadByte();
        _time.ClockHours = (ClockHours)reader.ReadByte();
        _time.CalendarEnabled = reader.ReadBoolean();
        _selected = reader.ReadBoolean();
        _wrapped = reader.ReadBoolean();
        _paused = reader.ReadBoolean();
        _pendingSecondsIncrement = reader.ReadBoolean();
        _stopped = reader.ReadBoolean();
        _reset = reader.ReadBoolean();
        _timeLost = reader.ReadBoolean();
        _irq = reader.ReadBoolean();
        _irqEnabled = reader.ReadBoolean();
        _irqRateBits = reader.ReadByte();
        _irqRateTicks = reader.ReadInt64();
        _lastIrqTicksUtc = reader.ReadInt64();
        _irqDuty = (InterruptDuty)reader.ReadByte();
        _lastDayWriteHigh = reader.ReadByte();
        _lastMonthWriteHigh = reader.ReadByte();
    }

    public byte ReadChipSelect() => (byte)(_selected ? 1 : 0);

    public void WriteChipSelect(byte value)
    {
        bool previous = _selected;
        _selected = (value & 0x01) != 0;
        if (previous && !_selected)
        {
            _wrapped = false;
            _reset = false;
            _command = Command.None;
            _register = null;
        }
    }

    public byte ReadDataPort()
    {
        if (!_selected || _command != Command.Read)
            return 0;

        UpdateTime();
        if (_register is not byte register)
            return 0;

        byte value = ReadRegister(register);
        _register = (byte)((register + 1) & 0x0F);
        return value;
    }

    public void WriteDataPort(byte value)
    {
        if (!_selected)
            return;

        UpdateTime();

        switch (_command, _register)
        {
            case (Command.None, _):
                _command = value switch
                {
                    0x03 => Command.Write,
                    0x0C => Command.Read,
                    _ => Command.None
                };
                break;
            case (_, null):
                _register = (byte)(value & 0x0F);
                break;
            case (Command.Write, byte register):
                WriteRegister(register, value);
                _register = (byte)((register + 1) & 0x0F);
                break;
        }
    }

    private byte ReadRegister(byte register) => register switch
    {
        0x0 => (byte)(_time.Seconds % 10),
        0x1 => (byte)((_timeLost ? 0x08 : 0x00) | (_time.Seconds / 10)),
        0x2 => (byte)(_time.Minutes % 10),
        0x3 => (byte)((_wrapped ? 0x08 : 0x00) | (_time.Minutes / 10)),
        0x4 => (byte)(_time.Hours % 10),
        0x5 => (byte)((_wrapped ? 0x08 : 0x00) | (_time.HourType == HourType.Pm ? 0x04 : 0x00) | (_time.Hours / 10)),
        0x6 => (byte)(_time.Day % 10),
        0x7 => (byte)((_wrapped ? 0x08 : 0x00) | (_lastDayWriteHigh & 0x04) | (_time.Day / 10)),
        0x8 => (byte)(_time.Month % 10),
        0x9 => (byte)((_wrapped ? 0x08 : 0x00) | (_lastMonthWriteHigh & 0x06) | (_time.Month / 10)),
        0xA => (byte)(_time.Year % 10),
        0xB => (byte)(_time.Year / 10),
        0xC => (byte)((_wrapped ? 0x08 : 0x00) | _time.DayOfWeek),
        0xD => ReadControl1(),
        0xE => ReadControl2(),
        0xF => ReadControl3(),
        _ => 0
    };

    private void WriteRegister(byte register, byte value)
    {
        switch (register)
        {
            case 0x0: _time.Seconds = (byte)((_time.Seconds / 10) * 10 + (value & 0x0F)); break;
            case 0x1:
                _time.Seconds = (byte)(10 * (value & 0x07) + (_time.Seconds % 10));
                _timeLost = (value & 0x08) != 0;
                break;
            case 0x2: _time.Minutes = (byte)((_time.Minutes / 10) * 10 + (value & 0x0F)); break;
            case 0x3: _time.Minutes = (byte)(10 * (value & 0x07) + (_time.Minutes % 10)); break;
            case 0x4: _time.Hours = (byte)((_time.Hours / 10) * 10 + (value & 0x0F)); break;
            case 0x5:
            {
                int mask = _time.ClockHours == ClockHours.TwentyFour ? 0x03 : 0x01;
                _time.Hours = (byte)(10 * (value & mask) + (_time.Hours % 10));
                if (_time.ClockHours == ClockHours.Twelve)
                    _time.HourType = (value & 0x04) != 0 ? HourType.Pm : HourType.Am;
                break;
            }
            case 0x6: _time.Day = (byte)((_time.Day / 10) * 10 + (value & 0x0F)); break;
            case 0x7:
                _time.Day = (byte)(10 * (value & 0x03) + (_time.Day % 10));
                _lastDayWriteHigh = (byte)(value & 0x0F);
                break;
            case 0x8: _time.Month = (byte)((_time.Month / 10) * 10 + (value & 0x0F)); break;
            case 0x9:
                _time.Month = (byte)(10 * (value & 0x01) + (_time.Month % 10));
                _lastMonthWriteHigh = (byte)(value & 0x0F);
                break;
            case 0xA: _time.Year = (byte)((_time.Year / 10) * 10 + (value & 0x0F)); break;
            case 0xB: _time.Year = (byte)(10 * (value & 0x0F) + (_time.Year % 10)); break;
            case 0xC: _time.DayOfWeek = (byte)(value & 0x07); break;
            case 0xD: WriteControl1(value); break;
            case 0xE: WriteControl2(value); break;
            case 0xF: WriteControl3(value); break;
        }
    }

    private byte ReadControl1()
    {
        bool irq = _irq;
        _irq = false;
        return (byte)((_paused ? 1 : 0) | (_time.CalendarEnabled ? 0x02 : 0x00) | (irq && _irqEnabled ? 0x04 : 0x00));
    }

    private void WriteControl1(byte value)
    {
        _paused = (value & 0x01) != 0;
        _time.CalendarEnabled = (value & 0x02) != 0;
        if ((value & 0x08) != 0)
        {
            byte previousSeconds = _time.Seconds;
            _time.Seconds = 0;
            if (previousSeconds >= 30)
                IncrementMinutes();
        }

        if (!_paused && _pendingSecondsIncrement)
        {
            _pendingSecondsIncrement = false;
            IncrementSeconds();
        }
    }

    private byte ReadControl2()
    {
        return (byte)((!_irqEnabled ? 1 : 0) | (_irqDuty == InterruptDuty.UntilAck ? 0x02 : 0x00) | (_irqRateBits << 2));
    }

    private void WriteControl2(byte value)
    {
        _irqEnabled = (value & 0x01) == 0;
        _irqDuty = (value & 0x02) != 0 ? InterruptDuty.UntilAck : InterruptDuty.FixedTime;
        _irqRateBits = (byte)((value >> 2) & 0x03);
        _irqRateTicks = _irqRateBits switch
        {
            0 => TimeSpan.TicksPerSecond / 64,
            1 => TimeSpan.TicksPerSecond,
            2 => 60 * TimeSpan.TicksPerSecond,
            _ => 60 * 60 * TimeSpan.TicksPerSecond
        };
    }

    private byte ReadControl3()
    {
        return (byte)((_reset ? 1 : 0) | (_stopped ? 0x02 : 0x00) | (_time.ClockHours == ClockHours.TwentyFour ? 0x04 : 0x00));
    }

    private void WriteControl3(byte value)
    {
        _reset = (value & 0x01) != 0;
        if (_reset)
        {
            _time.Seconds = 0;
            _pendingSecondsIncrement = false;
        }

        _stopped = (value & 0x02) != 0;
        _time.ClockHours = (value & 0x04) != 0 ? ClockHours.TwentyFour : ClockHours.Twelve;
        if (_time.ClockHours == ClockHours.TwentyFour)
            _time.HourType = HourType.Am;
    }

    private void UpdateTime()
    {
        long now = DateTime.UtcNow.Ticks;
        if (_stopped || _reset)
        {
            _time.LastUpdateTicksUtc = now;
            return;
        }

        long elapsed = Math.Max(0, now - _time.LastUpdateTicksUtc);
        long newTicks = _time.SubSecondTicks + elapsed;
        _time.SubSecondTicks = (int)(newTicks % TimeSpan.TicksPerSecond);
        _time.LastUpdateTicksUtc = now;

        long elapsedSeconds = newTicks / TimeSpan.TicksPerSecond;
        for (long i = 0; i < elapsedSeconds; i++)
        {
            if (_paused)
            {
                _pendingSecondsIncrement = true;
                break;
            }

            IncrementSeconds();
            if (_command != Command.None)
                _wrapped = true;
        }

        if (now - _lastIrqTicksUtc >= _irqRateTicks)
        {
            if (_irqEnabled)
                _irq = true;

            while (_lastIrqTicksUtc + _irqRateTicks <= now)
                _lastIrqTicksUtc += _irqRateTicks;
        }

        if (_irqDuty == InterruptDuty.FixedTime && now - _lastIrqTicksUtc >= TimeSpan.TicksPerMillisecond * 8)
            _irq = false;
    }

    private void IncrementSeconds()
    {
        _time.Seconds++;
        if (_time.Seconds >= 60)
        {
            _time.Seconds = 0;
            IncrementMinutes();
        }
    }

    private void IncrementMinutes()
    {
        _time.Minutes++;
        if (_time.Minutes >= 60)
        {
            _time.Minutes = 0;
            IncrementHours();
        }
    }

    private void IncrementHours()
    {
        _time.Hours++;
        if (_time.ClockHours == ClockHours.TwentyFour)
        {
            if (_time.Hours >= 24)
            {
                _time.Hours = 0;
                IncrementDay();
            }
            return;
        }

        if (_time.Hours == 12)
        {
            _time.HourType = _time.HourType == HourType.Am ? HourType.Pm : HourType.Am;
            if (_time.HourType == HourType.Am)
                IncrementDay();
        }
        else if (_time.Hours > 12)
        {
            _time.Hours = 1;
        }
    }

    private void IncrementDay()
    {
        if (!_time.CalendarEnabled)
        {
            _time.DayOfWeek = (byte)((_time.DayOfWeek + 1) % 7);
            return;
        }

        _time.Day++;
        _time.DayOfWeek = (byte)((_time.DayOfWeek + 1) % 7);
        if (_time.Day > DateTime.DaysInMonth(FourDigitYear(), Math.Clamp(_time.Month, (byte)1, (byte)12)))
        {
            _time.Day = 1;
            _time.Month++;
            if (_time.Month > 12)
            {
                _time.Month = 1;
                _time.Year = (byte)((_time.Year + 1) % 100);
            }
        }
    }

    private int FourDigitYear() => 1900 + _time.Year;
}
