using System;
using System.IO;
using System.Text;

namespace ePceCD
{
    internal sealed class PceCdBiosTrace
    {
        private readonly BUS _bus;
        private readonly object _sync = new object();
        private readonly int _lineLimit;
        private readonly bool _stdout;
        private readonly string? _filePath;
        private int _linesWritten;

        public bool Enabled { get; }

        public PceCdBiosTrace(BUS bus, bool forceEnable)
        {
            _bus = bus;
            Enabled = forceEnable || IsEnvEnabled("EUTHERDRIVE_PCE_BIOS_TRACE");
            _stdout = IsEnvEnabled("EUTHERDRIVE_PCE_BIOS_TRACE_STDOUT");
            _lineLimit = ParseLimit("EUTHERDRIVE_PCE_BIOS_TRACE_LIMIT", 4000);
            _filePath = ResolveTracePath();
        }

        public void LogControlTransfer(PceCdBiosTransferType type, ushort caller, ushort target, HuC6280 cpu)
        {
            Write(
                $"[PCE-BIOS] call type={type} caller=0x{caller:X4} target=0x{target:X4} {FormatCpu(cpu)}");
        }

        public void LogReturn(PceCdBiosTransferType type, ushort source, ushort target, HuC6280 cpu)
        {
            Write(
                $"[PCE-BIOS] return type={type} source=0x{source:X4} target=0x{target:X4} {FormatCpu(cpu)}");
        }

        public void LogDispatch(ushort target, string handlerName, string phase, HuC6280 cpu)
        {
            Write(
                $"[PCE-BIOS] hle phase={phase} target=0x{target:X4} handler={handlerName} {FormatCpu(cpu)}");
        }

        public void LogBootRead(string profileName, int lba, int count, ushort destAddress, int bytes)
        {
            Write(
                $"[PCE-BIOS] boot profile={profileName} read6 lba={lba} count={count} dest=0x{destAddress:X4} bytes={bytes} frame={_bus.PPU.PeekFrameCounter()} cpuclk={_bus.CPU.m_Clock}");
        }

        public void LogCdDataStage(string profileName, int lba, int count, int bytes)
        {
            Write(
                $"[PCE-BIOS] cddata profile={profileName} stage lba={lba} count={count} bytes={bytes} frame={_bus.PPU.PeekFrameCounter()} cpuclk={_bus.CPU.m_Clock}");
        }

        public void LogMemoryWrite(string reason, ushort address, byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            string preview = data.Length == 0
                ? "-"
                : BitConverter.ToString(data, 0, Math.Min(8, data.Length));
            Write(
                $"[PCE-BIOS] mem reason={reason} addr=0x{address:X4} len={data.Length} preview={preview} frame={_bus.PPU.PeekFrameCounter()} cpuclk={_bus.CPU.m_Clock}");
        }

        public void LogNote(string message)
        {
            Write($"[PCE-BIOS] note {message}");
        }

        private void Write(string line)
        {
            if (!Enabled || _linesWritten >= _lineLimit)
                return;

            _linesWritten++;
            bool wroteFile = false;
            if (!string.IsNullOrWhiteSpace(_filePath))
            {
                string? directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                lock (_sync)
                {
                    File.AppendAllText(_filePath!, line + Environment.NewLine, Encoding.UTF8);
                }
                wroteFile = true;
            }

            if (_stdout || !wroteFile)
                Console.WriteLine(line);
        }

        private string FormatCpu(HuC6280 cpu)
        {
            var sb = new StringBuilder(160);
            sb.Append("pc=0x").Append(cpu.PeekProgramCounter().ToString("X4"));
            sb.Append(" a=0x").Append(cpu.PeekA().ToString("X2"));
            sb.Append(" x=0x").Append(cpu.PeekX().ToString("X2"));
            sb.Append(" y=0x").Append(cpu.PeekY().ToString("X2"));
            sb.Append(" s=0x").Append(cpu.PeekS().ToString("X2"));
            sb.Append(" p=0x").Append(cpu.PeekP().ToString("X2"));
            sb.Append(" frame=").Append(_bus.PPU.PeekFrameCounter());
            sb.Append(" cpuclk=").Append(cpu.m_Clock);
            sb.Append(" mpr=");
            for (int i = 0; i < 8; i++)
            {
                if (i != 0)
                    sb.Append(',');
                sb.Append(cpu.PeekMpr(i).ToString("X2"));
            }
            return sb.ToString();
        }

        private static bool IsEnvEnabled(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.Trim();
            return value == "1" ||
                   value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseLimit(string name, int fallback)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value) && int.TryParse(value, out int parsed) && parsed > 0)
                return parsed;
            return fallback;
        }

        private static string? ResolveTracePath()
        {
            string? value = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_BIOS_TRACE_FILE");
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (Path.IsPathRooted(value))
                return value;
            return Path.Combine(Environment.CurrentDirectory, value);
        }
    }
}
