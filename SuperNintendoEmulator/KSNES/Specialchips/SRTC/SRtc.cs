using System;

namespace KSNES.Specialchips.SRTC;

[Serializable]
public sealed class SRtc
{
    private enum ReadState
    {
        Ack,
        Digit,
        End
    }

    private enum WriteState
    {
        Start,
        Command,
        Digit,
        End
    }

    private long _lastUpdateTicksUtc;
    private int _subSecondTicks;
    private byte _seconds;
    private byte _minutes;
    private byte _hours;
    private byte _day;
    private byte _month;
    private byte _year;
    private byte _century;
    private byte _dayOfWeek;
    private ReadState _readState;
    private WriteState _writeState;
    private byte _readIndex;
    private byte _writeIndex;

    public SRtc()
    {
        _lastUpdateTicksUtc = DateTime.UtcNow.Ticks;
        _day = 1;
        _month = 1;
        _century = 9;
        _readState = ReadState.Ack;
        _writeState = WriteState.Start;
    }

    public byte Read()
    {
        _writeState = WriteState.Start;
        _writeIndex = 0;
        UpdateTime();

        switch (_readState)
        {
            case ReadState.Ack:
                _readState = ReadState.Digit;
                _readIndex = 0;
                return 0x0F;
            case ReadState.Digit:
                byte value = GetDigit(_readIndex);
                if (_readIndex == 12)
                {
                    _readState = ReadState.End;
                }
                else
                {
                    _readIndex++;
                }
                return value;
            default:
                _readState = ReadState.Ack;
                return 0x0F;
        }
    }

    public void Write(byte value)
    {
        _readState = ReadState.Ack;
        _readIndex = 0;
        UpdateTime();

        value &= 0x0F;
        switch (_writeState)
        {
            case WriteState.Start:
                if (value == 0x0E)
                    _writeState = WriteState.Command;
                break;
            case WriteState.Command:
                switch (value)
                {
                    case 0x04:
                        _writeState = WriteState.End;
                        break;
                    case 0x00:
                        _writeState = WriteState.Digit;
                        _writeIndex = 0;
                        break;
                }
                break;
            case WriteState.Digit:
                WriteDigit(_writeIndex, value);
                if (_writeIndex == 11)
                {
                    _writeState = WriteState.End;
                }
                else
                {
                    _writeIndex++;
                }
                break;
            case WriteState.End:
                if (value == 0x0D)
                {
                    _writeState = WriteState.Start;
                    _writeIndex = 0;
                }
                break;
        }
    }

    public void ResetState()
    {
        _readState = ReadState.Ack;
        _writeState = WriteState.Start;
        _readIndex = 0;
        _writeIndex = 0;
    }

    private byte GetDigit(byte index)
    {
        return index switch
        {
            0 => (byte)(_seconds % 10),
            1 => (byte)(_seconds / 10),
            2 => (byte)(_minutes % 10),
            3 => (byte)(_minutes / 10),
            4 => (byte)(_hours % 10),
            5 => (byte)(_hours / 10),
            6 => (byte)(_day % 10),
            7 => (byte)(_day / 10),
            8 => _month,
            9 => (byte)(_year % 10),
            10 => (byte)(_year / 10),
            11 => _century,
            12 => _dayOfWeek,
            _ => 0x0F
        };
    }

    private void WriteDigit(byte index, byte value)
    {
        switch (index)
        {
            case 0:
                _seconds = (byte)((_seconds / 10) * 10 + value);
                break;
            case 1:
                _seconds = (byte)(10 * value + (_seconds % 10));
                break;
            case 2:
                _minutes = (byte)((_minutes / 10) * 10 + value);
                break;
            case 3:
                _minutes = (byte)(10 * value + (_minutes % 10));
                break;
            case 4:
                _hours = (byte)((_hours / 10) * 10 + value);
                break;
            case 5:
                _hours = (byte)(10 * value + (_hours % 10));
                break;
            case 6:
                _day = (byte)((_day / 10) * 10 + value);
                UpdateDayOfWeek();
                break;
            case 7:
                _day = (byte)(10 * value + (_day % 10));
                UpdateDayOfWeek();
                break;
            case 8:
                _month = value;
                UpdateDayOfWeek();
                break;
            case 9:
                _year = (byte)((_year / 10) * 10 + value);
                UpdateDayOfWeek();
                break;
            case 10:
                _year = (byte)(10 * value + (_year % 10));
                UpdateDayOfWeek();
                break;
            case 11:
                _century = value;
                UpdateDayOfWeek();
                break;
        }
    }

    private void UpdateTime()
    {
        long nowTicks = DateTime.UtcNow.Ticks;
        long elapsedTicks = Math.Max(0, nowTicks - _lastUpdateTicksUtc);
        _lastUpdateTicksUtc = nowTicks;

        long totalTicks = _subSecondTicks + elapsedTicks;
        _subSecondTicks = (int)(totalTicks % TimeSpan.TicksPerSecond);
        long elapsedSeconds = totalTicks / TimeSpan.TicksPerSecond;
        while (elapsedSeconds-- > 0)
        {
            IncrementSecond();
        }
    }

    private void IncrementSecond()
    {
        _seconds++;
        if (_seconds >= 60)
        {
            _seconds = 0;
            _minutes++;
            if (_minutes >= 60)
            {
                _minutes = 0;
                _hours++;
                if (_hours >= 24)
                {
                    _hours = 0;
                    IncrementDay();
                }
            }
        }
    }

    private void IncrementDay()
    {
        _day++;
        _dayOfWeek = (byte)((_dayOfWeek + 1) % 7);
        if (_day > DateTime.DaysInMonth(FourDigitYear(), Math.Clamp(_month, (byte)1, (byte)12)))
        {
            _day = 1;
            _month++;
            if (_month > 12)
            {
                _month = 1;
                _year++;
                if (_year > 99)
                {
                    _year = 0;
                    _century++;
                }
            }
        }
    }

    private void UpdateDayOfWeek()
    {
        int year = FourDigitYear();
        int month = Math.Clamp(_month, (byte)1, (byte)12);
        int day = Math.Clamp(_day, (byte)1, (byte)DateTime.DaysInMonth(year, month));
        _dayOfWeek = DateTimeExtensions.ToSrtcWeekday(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc).DayOfWeek);
    }

    private int FourDigitYear() => 1000 + (_century * 100) + _year;

    private static class DateTimeExtensions
    {
        public static byte ToSrtcWeekday(DayOfWeek dayOfWeek)
        {
            return dayOfWeek switch
            {
                DayOfWeek.Sunday => 0,
                DayOfWeek.Monday => 1,
                DayOfWeek.Tuesday => 2,
                DayOfWeek.Wednesday => 3,
                DayOfWeek.Thursday => 4,
                DayOfWeek.Friday => 5,
                DayOfWeek.Saturday => 6,
                _ => 0
            };
        }
    }
}
