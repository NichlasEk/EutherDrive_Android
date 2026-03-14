using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ProjectPSX {
    public class JOYPAD {

        private byte JOY_TX_DATA; //1F801040h JOY_TX_DATA(W)
        private readonly Queue<byte> rxFifo = new Queue<byte>(8);

        //1F801044 JOY_STAT(R)
        private bool TXreadyFlag1 = true;
        private bool TXreadyFlag2 = true;
        private bool RXparityError;
        private bool ackInputLevel;
        private bool interruptRequest;
        private int baudrateTimer;
        private int transferCyclesRemaining;
        private int transferStartCyclesRemaining;
        private bool transferActive;
        private bool txLatchedEnable;
        private byte transferByte;
        private bool transferBytePending;
        private byte pendingTransferByte;
        private bool pendingTransferLatchedEnable;

        //1F801048 JOY_MODE(R/W)
        private uint baudrateReloadFactor;
        private uint characterLength;
        private bool parityEnable;
        private bool parityTypeOdd;
        private bool clkOutputPolarity;

        //1F80104Ah JOY_CTRL (R/W) (usually 1003h,3003h,0000h)
        private bool TXenable;
        private bool JoyOutput;
        private bool RXenable;
        private bool joyControl_unknow_bit3;
        private bool controlAck;
        private bool joyControl_unknow_bit5;
        private bool controlReset;
        private uint RXinterruptMode;
        private bool TXinterruptEnable;
        private bool RXinterruptEnable;
        private bool ACKinterruptEnable;
        private uint desiredSlotNumber;

        private ushort JOY_BAUD;    //1F80104Eh JOY_BAUD(R/W) (usually 0088h, ie.circa 250kHz, when Factor = MUL1)

        private enum JoypadDevice {
            None,
            Controller,
            MemoryCard
        }
        JoypadDevice joypadDevice = JoypadDevice.None;

        Controller controller;
        MemoryCard memoryCard;
        private static readonly string? PadTraceFile = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_PAD_TRACE_FILE");
        private static readonly int PadTraceLimit = ParseTraceLimit(Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_PAD_TRACE_LIMIT"), 4096);
        private int _padTraceCount;

        public JOYPAD(Controller controller, MemoryCard memoryCard) {
            this.controller = controller;
            this.memoryCard = memoryCard;
            if (!string.IsNullOrWhiteSpace(PadTraceFile)) {
                Console.WriteLine($"[PSX-PAD] Trace enabled: {PadTraceFile}");
                TracePad("INIT", "joypad created");
            }
        }

        private int ackCounter;

        public bool tick(int cycles) {
            if (transferActive) {
                if (!TXreadyFlag1) {
                    transferStartCyclesRemaining -= cycles;
                    if (transferStartCyclesRemaining <= 0) {
                        TXreadyFlag1 = true;
                    }
                }

                transferCyclesRemaining -= cycles;
                if (transferCyclesRemaining <= 0) {
                    FinishTransfer();
                }
            }

            if (ackCounter > 0) {
                ackCounter -= cycles;
                if (ackCounter <= 0) {
                    ackCounter = 0;
                    ackInputLevel = false;
                    RaiseInterruptIfEnabled();
                }
            }

            if (interruptRequest) return true;

            return false;
        }

        private void reloadTimer() {
            //Console.WriteLine("[JOYPAD] RELOAD TIMER");
            baudrateTimer = (int)(JOY_BAUD * baudrateReloadFactor) & ~0x1;
        }

        private int GetBaudFactor() {
            return baudrateReloadFactor switch {
                2 => 16,
                3 => 64,
                _ => 1,
            };
        }

        private int GetBitCycles() {
            int reload = ((JOY_BAUD * GetBaudFactor()) & ~0x1);
            return Math.Max(reload, 8);
        }

        private void StartTransferIfPossible() {
            if (transferActive || !transferBytePending || !(TXenable || txLatchedEnable)) {
                return;
            }

            transferByte = pendingTransferByte;
            transferBytePending = false;
            transferActive = true;
            int bitCycles = GetBitCycles();
            transferStartCyclesRemaining = bitCycles;
            transferCyclesRemaining = bitCycles * 8;
            TXreadyFlag1 = false;
            TXreadyFlag2 = false;
        }

        private void PushRxByte(byte value) {
            if (rxFifo.Count >= 8) {
                rxFifo.Dequeue();
            }
            rxFifo.Enqueue(value);
        }

        private void RaiseInterruptIfEnabled() {
            if (ACKinterruptEnable || (RXinterruptEnable && rxFifo.Count >= 1)) {
                interruptRequest = true;
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

        private void TracePad(string tag, string message) {
            if (string.IsNullOrWhiteSpace(PadTraceFile) || _padTraceCount >= PadTraceLimit) {
                return;
            }

            try {
                File.AppendAllText(PadTraceFile, $"[{tag}] {message}{Environment.NewLine}");
                _padTraceCount++;
            } catch {
            }
        }

        private void FinishTransfer() {
            transferActive = false;
            transferCyclesRemaining = 0;
            transferStartCyclesRemaining = 0;
            TXreadyFlag1 = true;
            TXreadyFlag2 = true;

            byte response = 0xFF;
            bool ack = false;

            if (JoyOutput) {
                if (desiredSlotNumber == 1) {
                    response = 0xFF;
                    ack = false;
                } else {
                    if (joypadDevice == JoypadDevice.None) {
                        if (transferByte == 0x01) {
                            joypadDevice = JoypadDevice.Controller;
                        } else if (transferByte == 0x81) {
                            joypadDevice = JoypadDevice.MemoryCard;
                        }
                    }

                    if (joypadDevice == JoypadDevice.Controller) {
                        response = controller.process(transferByte);
                        ack = controller.ack;
                    } else if (joypadDevice == JoypadDevice.MemoryCard) {
                        response = memoryCard.process(transferByte);
                        ack = memoryCard.ack;
                    }

                    if (!ack) {
                        joypadDevice = JoypadDevice.None;
                    }
                }
            } else {
                joypadDevice = JoypadDevice.None;
                memoryCard.resetToIdle();
                controller.resetToIdle();
            }

            PushRxByte(response);
            ackInputLevel = ack;
            if (ackInputLevel) {
                ackCounter = 500;
            }
            TracePad("PAD", $"tx={transferByte:X2} rx={response:X2} ack={(ack ? 1 : 0)} dev={joypadDevice} txr1={(TXreadyFlag1 ? 1 : 0)} txr2={(TXreadyFlag2 ? 1 : 0)} joyOut={(JoyOutput ? 1 : 0)} txEn={(TXenable ? 1 : 0)} slot={desiredSlotNumber}");
            txLatchedEnable = pendingTransferLatchedEnable;
            pendingTransferLatchedEnable = false;
            StartTransferIfPossible();
        }

        public void write(uint addr, uint value) {
            switch (addr & 0xFF) {
                case 0x40:
                    //Console.WriteLine("[JOYPAD] TX DATA ENQUEUE " + value.ToString("x2"));
                    JOY_TX_DATA = (byte)value;
                    TracePad("TX", $"value={JOY_TX_DATA:X2} transferActive={(transferActive ? 1 : 0)} txr1={(TXreadyFlag1 ? 1 : 0)} txr2={(TXreadyFlag2 ? 1 : 0)} pending={(transferBytePending ? 1 : 0)}");
                    TXreadyFlag2 = false;
                    if (transferActive) {
                        if (TXreadyFlag1 && !transferBytePending) {
                            transferBytePending = true;
                            pendingTransferByte = JOY_TX_DATA;
                            pendingTransferLatchedEnable = TXenable;
                            TXreadyFlag1 = false;
                            break;
                        }

                        if (transferBytePending) {
                            pendingTransferByte = JOY_TX_DATA;
                            pendingTransferLatchedEnable = TXenable;
                            break;
                        }

                        transferByte = JOY_TX_DATA;
                        txLatchedEnable = TXenable;
                        break;
                    }
                    transferBytePending = true;
                    pendingTransferByte = JOY_TX_DATA;
                    pendingTransferLatchedEnable = TXenable;
                    txLatchedEnable = pendingTransferLatchedEnable;
                    StartTransferIfPossible();
                    break;
                case 0x48:
                    //Console.WriteLine($"[JOYPAD] SET MODE {value:x4}");
                    setJOY_MODE(value);
                    break;
                case 0x4A:
                    //Console.WriteLine($"[JOYPAD] SET CONTROL {value:x4}");
                    setJOY_CTRL(value);
                    TracePad("CTRL", $"value={value:X4} joyOut={(JoyOutput ? 1 : 0)} txEn={(TXenable ? 1 : 0)} rxEn={(RXenable ? 1 : 0)} ackIrq={(ACKinterruptEnable ? 1 : 0)} slot={desiredSlotNumber}");
                    break;
                case 0x4E:
                    //Console.WriteLine($"[JOYPAD] SET BAUD {value:x4}");
                    JOY_BAUD = (ushort)value;
                    reloadTimer();
                    break;
                default: 
                    Console.WriteLine($"Unhandled JOYPAD Write {addr:x8} {value:x8}");
                    //Console.ReadLine();
                    break;
            }
        }

        private void setJOY_CTRL(uint value) {
            TXenable = (value & 0x1) != 0;
            JoyOutput = ((value >> 1) & 0x1) != 0;
            RXenable = ((value >> 2) & 0x1) != 0;
            joyControl_unknow_bit3 = ((value >> 3) & 0x1) != 0;
            controlAck = ((value >> 4) & 0x1) != 0;
            joyControl_unknow_bit5 = ((value >> 5) & 0x1) != 0;
            controlReset = ((value >> 6) & 0x1) != 0;
            RXinterruptMode = (value >> 8) & 0x3;
            TXinterruptEnable = ((value >> 10) & 0x1) != 0;
            RXinterruptEnable = ((value >> 11) & 0x1) != 0;
            ACKinterruptEnable = ((value >> 12) & 0x1) != 0;
            desiredSlotNumber = (value >> 13) & 0x1;

            if (controlAck) {
                //Console.WriteLine("[JOYPAD] CONTROL ACK");
                RXparityError = false;
                interruptRequest = false;
                controlAck = false;
            }

            if (controlReset) {
                //Console.WriteLine("[JOYPAD] CONTROL RESET");
                joypadDevice = JoypadDevice.None;
                controller.resetToIdle();
                memoryCard.resetToIdle();
                rxFifo.Clear();

                setJOY_MODE(0);
                setJOY_CTRL(0);
                JOY_BAUD = 0;
                ackCounter = 0;
                transferCyclesRemaining = 0;
                transferStartCyclesRemaining = 0;
                transferActive = false;
                txLatchedEnable = false;
                transferByte = 0xFF;
                transferBytePending = false;
                pendingTransferByte = 0xFF;
                pendingTransferLatchedEnable = false;
                ackInputLevel = false;
                interruptRequest = false;

                JOY_TX_DATA = 0xFF;

                TXreadyFlag1 = true;
                TXreadyFlag2 = true;

                controlReset = false;
            }

            if (!JoyOutput) {
                joypadDevice = JoypadDevice.None;
                memoryCard.resetToIdle();
                controller.resetToIdle();
            }

            StartTransferIfPossible();
        }

        private void setJOY_MODE(uint value) {
            baudrateReloadFactor = value & 0x3;
            characterLength = (value >> 2) & 0x3;
            parityEnable = ((value >> 4) & 0x1) != 0;
            parityTypeOdd = ((value >> 5) & 0x1) != 0;
            clkOutputPolarity = ((value >> 8) & 0x1) != 0;
        }

        public uint load(uint addr) {
            switch (addr & 0xFF) {
                case 0x40:
                    //Console.WriteLine($"[JOYPAD] GET RX DATA {JOY_RX_DATA:x2}");
                    if (rxFifo.Count > 0) {
                        return rxFifo.Dequeue();
                    }
                    return 0xFF;
                case 0x44:
                    //Console.WriteLine($"[JOYPAD] GET STAT {getJOY_STAT():x8}");
                    return getJOY_STAT();
                case 0x48:
                    //Console.WriteLine($"[JOYPAD] GET MODE {getJOY_MODE():x8}");
                    return getJOY_MODE();
                case 0x4A:
                    //Console.WriteLine($"[JOYPAD] GET CONTROL {getJOY_CTRL():x8}");
                    return getJOY_CTRL();
                case 0x4E:
                    //Console.WriteLine($"[JOYPAD] GET BAUD {JOY_BAUD:x8}");
                    return JOY_BAUD;
                default:
                    //Console.WriteLine($"[JOYPAD] Unhandled Read at {addr}"); Console.ReadLine();
                    return 0xFFFF_FFFF;
            }
        }

        private uint getJOY_CTRL() {
            uint joy_ctrl = 0;
            joy_ctrl |= TXenable ? 1u : 0u;
            joy_ctrl |= (JoyOutput ? 1u : 0u) << 1;
            joy_ctrl |= (RXenable ? 1u : 0u) << 2;
            joy_ctrl |= (joyControl_unknow_bit3 ? 1u : 0u) << 3;
            //joy_ctrl |= (ack ? 1u : 0u) << 4; // only writeable
            joy_ctrl |= (joyControl_unknow_bit5 ? 1u : 0u) << 5;
            //joy_ctrl |= (reset ? 1u : 0u) << 6; // only writeable
            //bit 7 allways 0
            joy_ctrl |= RXinterruptMode << 8;
            joy_ctrl |= (TXinterruptEnable ? 1u : 0u) << 10;
            joy_ctrl |= (RXinterruptEnable ? 1u : 0u) << 11;
            joy_ctrl |= (ACKinterruptEnable ? 1u : 0u) << 12;
            joy_ctrl |= desiredSlotNumber << 13;
            return joy_ctrl;
        }

        private uint getJOY_MODE() {
            uint joy_mode = 0;
            joy_mode |= baudrateReloadFactor;
            joy_mode |= characterLength << 2;
            joy_mode |= (parityEnable ? 1u : 0u) << 4;
            joy_mode |= (parityTypeOdd ? 1u : 0u) << 5;
            joy_mode |= (clkOutputPolarity ? 1u : 0u) << 8;
            return joy_mode;
        }

        private uint getJOY_STAT() {
            uint joy_stat = 0;
            joy_stat |= TXreadyFlag1 ? 1u : 0u;
            joy_stat |= (rxFifo.Count > 0 ? 1u : 0u) << 1;
            joy_stat |= (TXreadyFlag2 ? 1u : 0u) << 2;
            joy_stat |= (RXparityError ? 1u : 0u) << 3;
            joy_stat |= (ackInputLevel ? 1u : 0u) << 7;
            joy_stat |= (interruptRequest ? 1u : 0u) << 9;
            joy_stat |= (uint)baudrateTimer << 11;

            return joy_stat;
        }
    }
}
