using System;
using System.Globalization;
using System.IO;

namespace ProjectPSX {
    public sealed class DigitalController : Controller {
        private bool analogCapable;

        private const byte DigitalId = 0x41;
        private const byte AnalogId = 0x73;
        private const byte ConfigId = 0xF3;
        private const byte ReadyByte = 0x5A;
        private const byte AnalogCenter = 0x80;

        private enum Mode {
            Idle,
            Connected,
            Transfering,
        }
        Mode mode = Mode.Idle;
        private bool analogMode;
        private bool configMode;
        private bool analogButtonLocked;
        private byte currentCommand;
        private int transferIndex;
        private readonly byte[] rumbleConfig = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        private readonly byte[] pendingRumbleConfig = new byte[6];
        private byte variableResponseIndex;
        private static readonly string? PadTraceFile = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_PAD_TRACE_FILE");
        private static readonly int PadTraceLimit = ParseTraceLimit(Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_PAD_TRACE_LIMIT"), 4096);
        private int _padTraceCount;

        public DigitalController(bool analogCapable = true) {
            this.analogCapable = analogCapable;
            if (!string.IsNullOrWhiteSpace(PadTraceFile)) {
                Console.WriteLine($"[PSX-PAD] Controller trace enabled: {PadTraceFile}");
                TracePad("controller created");
            }
        }

        private static int ParseTraceLimit(string? raw, int fallback) {
            if (string.IsNullOrWhiteSpace(raw)) {
                return fallback;
            }

            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
                ? parsed
                : fallback;
        }

        private void TracePad(string message) {
            if (string.IsNullOrWhiteSpace(PadTraceFile) || _padTraceCount >= PadTraceLimit) {
                return;
            }

            try {
                File.AppendAllText(PadTraceFile, $"[CTL] {message}{Environment.NewLine}");
                _padTraceCount++;
            } catch {
            }
        }

        public void SetAnalogControllerEnabled(bool enabled) {
            analogCapable = enabled;
            if (!analogCapable) {
                analogMode = false;
                configMode = false;
                analogButtonLocked = false;
            }
            TracePad($"analogCapable={(analogCapable ? 1 : 0)} analogMode={(analogMode ? 1 : 0)} configMode={(configMode ? 1 : 0)}");
            resetToIdle();
        }

        public override byte process(byte b) {
            switch (mode) {
                case Mode.Idle:
                    switch (b) {
                        case 0x01:
                            //Console.WriteLine("[Controller] Idle Process 0x01");
                            mode = Mode.Connected;
                            ack = true;
                            return 0xFF;
                        default:
                            //Console.WriteLine($"[Controller] Idle Process Warning: {b:x2}");
                            transferDataFifo.Clear();
                            ack = false;
                            return 0xFF;
                    }

                case Mode.Connected:
                    switch (b) {
                        case 0x42:
                            mode = Mode.Transfering;
                            currentCommand = b;
                            transferIndex = 0;
                            GenerateReadResponse(forceAnalog: configMode);
                            ack = true;
                            return transferDataFifo.Dequeue();
                        case 0x43:
                            mode = Mode.Transfering;
                            currentCommand = b;
                            transferIndex = 0;
                            if (configMode)
                                GenerateConfigResponse(0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
                            else
                                GenerateReadResponse(forceAnalog: false);
                            ack = true;
                            return transferDataFifo.Dequeue();
                        case 0x44:
                            if (!configMode)
                            {
                                mode = Mode.Idle;
                                transferDataFifo.Clear();
                                ack = false;
                                return 0xFF;
                            }
                            mode = Mode.Transfering;
                            currentCommand = b;
                            transferIndex = 0;
                            GenerateConfigResponse(0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
                            ack = true;
                            return transferDataFifo.Dequeue();
                        case 0x45:
                            if (!configMode)
                            {
                                mode = Mode.Idle;
                                transferDataFifo.Clear();
                                ack = false;
                                return 0xFF;
                            }
                            mode = Mode.Transfering;
                            currentCommand = b;
                            transferIndex = 0;
                            GenerateConfigResponse(0x01, 0x02, analogMode ? (byte)0x01 : (byte)0x00, 0x02, 0x01, 0x00);
                            ack = true;
                            return transferDataFifo.Dequeue();
                        case 0x46:
                        case 0x47:
                        case 0x48:
                        case 0x4C:
                        case 0x4D:
                        case 0x40:
                        case 0x41:
                        case 0x49:
                        case 0x4A:
                        case 0x4B:
                        case 0x4E:
                        case 0x4F:
                            if (!configMode)
                            {
                                mode = Mode.Idle;
                                transferDataFifo.Clear();
                                ack = false;
                                return 0xFF;
                            }
                            mode = Mode.Transfering;
                            currentCommand = b;
                            transferIndex = 0;
                            GenerateConfigCommandResponse(b);
                            ack = true;
                            return transferDataFifo.Dequeue();
                        default:
                            //Console.WriteLine("[Controller] Connected Transfer Process unknow command {b:x2} RESET TO IDLE");
                            mode = Mode.Idle;
                            transferDataFifo.Clear();
                            ack = false;
                            return 0xFF;
                    }

                case Mode.Transfering:
                    HandleTransferByte(b);
                    byte data = transferDataFifo.Dequeue();
                    transferIndex++;
                    ack = transferDataFifo.Count > 0;
                    if (!ack) {
                        mode = Mode.Idle;
                        currentCommand = 0;
                        transferIndex = 0;
                    }
                    return data;
                default:
                    return 0xFF;
            }
        }

        private void GenerateReadResponse(bool forceAnalog) {
            transferDataFifo.Clear();
            bool replyAnalog = forceAnalog || analogMode;
            transferDataFifo.Enqueue(replyAnalog ? AnalogId : DigitalId);
            transferDataFifo.Enqueue(ReadyByte);
            transferDataFifo.Enqueue((byte)(buttons & 0xFF));
            transferDataFifo.Enqueue((byte)((buttons >> 8) & 0xFF));
            if (replyAnalog)
            {
                transferDataFifo.Enqueue(AnalogCenter);
                transferDataFifo.Enqueue(AnalogCenter);
                transferDataFifo.Enqueue(AnalogCenter);
                transferDataFifo.Enqueue(AnalogCenter);
            }
        }

        private void GenerateConfigResponse(
            byte b2,
            byte b3,
            byte b4,
            byte b5,
            byte b6,
            byte b7) {
            transferDataFifo.Clear();
            transferDataFifo.Enqueue(ConfigId);
            transferDataFifo.Enqueue(ReadyByte);
            transferDataFifo.Enqueue(b2);
            transferDataFifo.Enqueue(b3);
            transferDataFifo.Enqueue(b4);
            transferDataFifo.Enqueue(b5);
            transferDataFifo.Enqueue(b6);
            transferDataFifo.Enqueue(b7);
        }

        private void GenerateConfigCommandResponse(byte command) {
            switch (command) {
                case 0x46:
                    GenerateVariableResponseA(0x00);
                    break;
                case 0x47:
                    GenerateConfigResponse(0x00, 0x00, 0x02, 0x00, 0x01, 0x00);
                    break;
                case 0x48:
                    GenerateVariableResponseH(0x00);
                    break;
                case 0x4C:
                    GenerateVariableResponseB(0x00);
                    break;
                case 0x4D:
                    GenerateConfigResponse(
                        rumbleConfig[0],
                        rumbleConfig[1],
                        rumbleConfig[2],
                        rumbleConfig[3],
                        rumbleConfig[4],
                        rumbleConfig[5]);
                    Array.Copy(rumbleConfig, pendingRumbleConfig, rumbleConfig.Length);
                    break;
                default:
                    GenerateConfigResponse(0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
                    break;
            }
        }

        private void GenerateVariableResponseA(byte index) {
            if (index == 0x00)
                GenerateConfigResponse(0x00, 0x00, 0x01, 0x02, 0x00, 0x0A);
            else if (index == 0x01)
                GenerateConfigResponse(0x00, 0x00, 0x01, 0x01, 0x01, 0x14);
            else
                GenerateConfigResponse(0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
        }

        private void GenerateVariableResponseB(byte index) {
            byte dd = index switch
            {
                0x00 => 0x04,
                0x01 => 0x07,
                _ => (byte)0x00
            };
            GenerateConfigResponse(0x00, 0x00, 0x00, dd, 0x00, 0x00);
        }

        private void GenerateVariableResponseH(byte index) {
            byte ee = index <= 0x01 ? (byte)0x01 : (byte)0x00;
            GenerateConfigResponse(0x00, 0x00, 0x00, 0x00, ee, 0x00);
        }

        private void HandleTransferByte(byte b) {
            switch (currentCommand) {
                case 0x43:
                    if (transferIndex == 1)
                    {
                        configMode = b == 0x01;
                        TracePad($"cmd43 configMode={(configMode ? 1 : 0)}");
                    }
                    break;
                case 0x44:
                    if (transferIndex == 1)
                    {
                        analogMode = b == 0x01;
                        TracePad($"cmd44 analogMode={(analogMode ? 1 : 0)}");
                    }
                    else if (transferIndex == 2)
                    {
                        analogButtonLocked = (b & 0x03) == 0x03;
                        TracePad($"cmd44 analogButtonLocked={(analogButtonLocked ? 1 : 0)}");
                    }
                    break;
                case 0x46:
                    if (transferIndex == 1)
                    {
                        variableResponseIndex = b;
                        GenerateVariableResponseA(variableResponseIndex);
                    }
                    break;
                case 0x48:
                    if (transferIndex == 1)
                    {
                        variableResponseIndex = b;
                        GenerateVariableResponseH(variableResponseIndex);
                    }
                    break;
                case 0x4C:
                    if (transferIndex == 1)
                    {
                        variableResponseIndex = b;
                        GenerateVariableResponseB(variableResponseIndex);
                    }
                    break;
                case 0x4D:
                    if (transferIndex >= 1 && transferIndex <= 6)
                        pendingRumbleConfig[transferIndex - 1] = b;
                    if (transferIndex == 6)
                        Array.Copy(pendingRumbleConfig, rumbleConfig, rumbleConfig.Length);
                    break;
            }
        }

        public override void resetToIdle() {
            mode = Mode.Idle;
            transferDataFifo.Clear();
            ack = false;
            currentCommand = 0;
            transferIndex = 0;
        }
    }
}
