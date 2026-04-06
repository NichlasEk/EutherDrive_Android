using Serilog;

namespace EutherDrive.Core.GbEmu
{
    internal class Joypad
    {
        // These will hold the current state of each button. True = pressed.
        private bool _up, _down, _left, _right;
        private bool _a, _b, _start, _select;

        // Stores the value written by the game to 0xFF00
        private byte _p1Register = 0xCF;

        public Joypad(Cpu cpu)
        {
            _ = cpu;
        }

        // This method will be called by the MMU when the game writes to 0xFF00
        public void WriteP1(byte value)
        {
            Log.Debug($"Writing joypad value:{value:X2}");
            // We only care about bits 4 and 5, the rest are read-only
            _p1Register = (byte)((_p1Register & 0xCF) | (value & 0x30));
        }

        // This method will be called by the MMU when the game reads from 0xFF00
        public byte ReadP1()
        {
            byte result = (byte)(_p1Register | 0x0F); // Start with all buttons appearing "unpressed"

            // Check if Direction buttons are selected (bit 4 is 0)
            // This must be its own 'if', not part of an 'else if' chain.
            if ((result & 0x10) == 0)
            {
                if (_right) result &= 0b11111110;
                if (_left) result &= 0b11111101;
                if (_up) result &= 0b11111011;
                if (_down) result &= 0b11110111;
            }

            // Check if Action buttons are selected (bit 5 is 0)
            // Changed from 'else if' to 'if'
            if ((result & 0x20) == 0)
            {
                if (_a) result &= 0b11111110;
                if (_b) result &= 0b11111101;
                if (_select) result &= 0b11111011;
                if (_start) result &= 0b11110111;
            }
            return result;
        }

        public void SetState(bool up, bool down, bool left, bool right, bool a, bool b, bool start, bool select)
        {
            _up = up;
            _down = down;
            _left = left;
            _right = right;
            _a = a;
            _b = b;
            _start = start;
            _select = select;
        }
    }
}
