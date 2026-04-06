using System;
using System.Collections.Generic;

namespace EutherDrive.Core.GbEmu
{
    internal class Ppu
    {
        [Flags]
        public enum LcdcFlags : byte
        {
            // Bit 0: BG/Window Enable/Priority
            // In DMG: 0=Off, 1=On
            // In GBC: 0=BG and Window lose priority to sprites, 1=BG and Window have priority
            BgWindowEnable = 1 << 0, // 0b0000_0001

            // Bit 1: OBJ (Sprite) Enable
            // 0=Off, 1=On
            SpriteEnable = 1 << 1,   // 0b0000_0010

            // Bit 2: OBJ (Sprite) Size
            // 0=8x8, 1=8x16
            SpriteSize = 1 << 2,     // 0b0000_0100

            // Bit 3: BG Tile Map Display Select
            // 0=9800-9BFF, 1=9C00-9FFF
            BgTileMapSelect = 1 << 3, // 0b0000_1000

            // Bit 4: BG & Window Tile Data Select
            // 0=8800-97FF, 1=8000-8FFF
            BgWindowTileDataSelect = 1 << 4, // 0b0001_0000

            // Bit 5: Window Display Enable
            // 0=Off, 1=On
            WindowEnable = 1 << 5,    // 0b0010_0000

            // Bit 6: Window Tile Map Display Select
            // 0=9800-9BFF, 1=9C00-9FFF
            WindowTileMapSelect = 1 << 6, // 0b0100_0000

            // Bit 7: LCD and PPU Enable
            // 0=Off, 1=On
            LcdPpuEnable = 1 << 7      // 0b1000_0000
        }
        // --- GBC ENHANCEMENT: Updated SpriteInfo struct ---
        private struct SpriteInfo
        {
            public byte X;
            public byte Y;
            public byte TileIndex;
            public byte OamIndex;
            public bool FlipX;
            public bool FlipY;

            // DMG specific
            public bool DmgPalette; // false = OBP0, true = OBP1
            public bool DmgPriority; // false = above BG, true = below BG

            // GBC specific
            public byte GbcPaletteNumber;
            public byte VramBank;
            public bool GbcBgPriority; // From attribute bit 7
        }

        public enum PpuMode
        {
            HBlank = 0,
            VBlank = 1,
            OamScan = 2,
            PixelTransfer = 3
        }

        // Screen dimensions
        private const int ScreenWidth = 160;
        private const int ScreenHeight = 144;
        private const int VBlankStartLine = 144;
        private const int MaxScanline = 153;

        // PPU cycle timings for each mode
        private const int OamScanCycles = 80;
        private const int PixelTransferCycles = 172;
        private const int HBlankCycles = 204;
        private const int VBlankLineCycles = 456; // Cycles per line in VBlank

        public event Action<uint[]> FrameReady = delegate { };

        [NonSerialized]
        private Mmu? _mmu;
        private Mmu Mmu => _mmu ?? throw new InvalidOperationException("MMU not initialized");
        [NonSerialized]
        private Cpu _cpu;

        private uint[] _frameBuffer = new uint[160 * 144];
        private int _cyclesCounter = 0;
        private PpuMode _currentMode = PpuMode.OamScan;
        private int _windowLineCounter = 0;

        // --- GBC ENHANCEMENT: Flag to enable GBC features ---
        private bool _isGbcMode = false; // Set this to true for GBC games

        // --- DMG monochrome colors (FIX: Format is now AABBGGRR for consistency) ---
        internal static readonly uint[] _dmgColors = { 0xFF0FBC9B, 0xFF0FAC8B, 0xFF306230, 0xFF0F380F };

        // --- PPU Registers (DMG) ---
        private byte _lcdc = 0x91;
        private byte _stat = 0x85;
        private byte _scy = 0x00;
        private byte _scx = 0x00;
        private byte _ly = 0x00;
        private byte _lyc = 0x00;
        private byte _bgp = 0xFC;
        private byte _obp0 = 0xFF;
        private byte _obp1 = 0xFF;
        private byte _wy = 0x00;
        private byte _wx = 0x00;

        // This is used only for debugging, these are not hardware parameters.
        public bool RenderBackground = true;
        public bool RenderWindow = true;
        public bool RenderSprites = true;

        // --- GBC ENHANCEMENT: CGB Registers and Palette Memory ---
        private byte _bcps; // Background Color Palette Specification / Index (FF68)
        private byte _ocps; // Object Color Palette Specification / Index (FF6A)

        private byte[] _bgPaletteRam = new byte[64];
        private byte[] _spritePaletteRam = new byte[64];

        public Ppu(Mmu? mmu, Cpu cpu)
        {
            _mmu = mmu;
            _cpu = cpu;
            
            // Initialize GBC palettes with default values
            InitializeGbcPalettes();
        }

        public void SetMmu(Mmu mmu)
        {
            _mmu = mmu;
        }

        public void SetPostBootState(byte ly, PpuMode mode)
        {
            _ly = ly;
            _cyclesCounter = 0;
            _windowLineCounter = 0;
            _currentMode = mode;
            _stat = (byte)((_stat & ~0b11) | (byte)mode);
            CheckLyLyc();
        }
        
        // --- GBC ENHANCEMENT: Method to set the PPU into GBC mode ---
        public void SetGbcMode(bool isGbc)
        {
            _isGbcMode = isGbc;
        }

        // --- GBC ENHANCEMENT: Initialize GBC palettes with default values ---
        private void InitializeGbcPalettes()
        {
            // Initialize background palettes with default GBC colors
            // Each palette has 4 colors, each color is 2 bytes (15-bit RGB)
            for (int palette = 0; palette < 8; palette++)
            {
                for (int color = 0; color < 4; color++)
                {
                    int offset = (palette * 8) + (color * 2);
                    
                    // Default GBC colors (white, light gray, dark gray, black)
                    ushort defaultColor = color switch
                    {
                        0 => 0x7FFF, // White
                        1 => 0x5294, // Light gray
                        2 => 0x294A, // Dark gray
                        3 => 0x0000, // Black
                        _ => 0x0000
                    };
                    
                    _bgPaletteRam[offset] = (byte)(defaultColor & 0xFF);
                    _bgPaletteRam[offset + 1] = (byte)((defaultColor >> 8) & 0xFF);
                    
                    _spritePaletteRam[offset] = (byte)(defaultColor & 0xFF);
                    _spritePaletteRam[offset + 1] = (byte)((defaultColor >> 8) & 0xFF);
                }
            }
        }

        public uint[] GetFrameBuffer() => _frameBuffer;

        // PPU Register Read Methods
        public byte ReadLCDC() => _lcdc;
        public byte ReadSTAT() => _stat;
        public byte ReadSCY() => _scy;
        public byte ReadSCX() => _scx;
        public byte ReadLY() => _ly;
        public byte ReadLYC() => _lyc;
        public byte ReadBGP() => _bgp;
        public byte ReadOBP0() => _obp0;
        public byte ReadOBP1() => _obp1;
        public byte ReadWY() => _wy;
        public byte ReadWX() => _wx;
        
        // PPU Register Write Methods
        public void WriteLCDC(byte value)
        {
            _lcdc = value;
            if ((value & 0x80) == 0)
            {
                _ly = 0;
                _windowLineCounter = 0;
                SetPpuMode((byte)PpuMode.VBlank);
            }
        }
        public void WriteSTAT(byte value) => _stat = (byte)((_stat & 0b00000111) | (value & 0b11111000));
        public void WriteSCY(byte value) => _scy = value;
        public void WriteSCX(byte value) => _scx = value;
        public void WriteLY(byte value) { /* Read-only */ }
        public void WriteLYC(byte value) => _lyc = value;
        public void WriteBGP(byte value) => _bgp = value;
        public void WriteOBP0(byte value) => _obp0 = value;
        public void WriteOBP1(byte value) => _obp1 = value;
        public void WriteWY(byte value) => _wy = value;
        public void WriteWX(byte value) => _wx = value;

        // Read/Write for GBC registers ---
        public byte ReadBCPS() => _bcps;
        public void WriteBCPS(byte value) => _bcps = value;
        public byte ReadBCPD() => _bgPaletteRam[_bcps & 0x3F];
        public void WriteBCPD(byte value)
        {
            _bgPaletteRam[_bcps & 0x3F] = value;
            if ((_bcps & 0x80) != 0) // Auto-increment
            {
                _bcps = (byte)((((_bcps & 0x3F) + 1) & 0x3F) | 0x80);
            }
        }
        public byte ReadOCPS() => _ocps;
        public void WriteOCPS(byte value) => _ocps = value;
        public byte ReadOCPD() => _spritePaletteRam[_ocps & 0x3F];
        public void WriteOCPD(byte value)
        {
            _spritePaletteRam[_ocps & 0x3F] = value;
            if ((_ocps & 0x80) != 0) // Auto-increment
            {
                _ocps = (byte)((((_ocps & 0x3F) + 1) & 0x3F) | 0x80);
            }
        }

        // VRAM and OAM Access Restriction Methods
        public bool IsVRAMAccessRestricted()
        {
            return _currentMode == PpuMode.PixelTransfer;
        }

        public bool IsOAMAccessRestricted()
        {
            return _currentMode == PpuMode.OamScan || _currentMode == PpuMode.PixelTransfer;
        }

        public void Step(int cpuCycles)
        {
            _cyclesCounter += cpuCycles;
            if ((_lcdc & 0b10000000) == 0) return; // LCD Disabled

            byte currentMode = (byte)(_stat & 0x3);
            switch (currentMode)
            {
                case (byte)PpuMode.OamScan:
                    if (_cyclesCounter >= OamScanCycles)
                    {
                        _cyclesCounter -= OamScanCycles;
                        SetPpuMode((byte)PpuMode.PixelTransfer);
                    }
                    break;
                case (byte)PpuMode.PixelTransfer:
                    if (_cyclesCounter >= PixelTransferCycles)
                    {
                        _cyclesCounter -= PixelTransferCycles;
                        SetPpuMode((byte)PpuMode.HBlank);
                        Mmu.TickHblankDma();
                        RenderScanline();
                    }
                    break;
                case (byte)PpuMode.HBlank:
                    if (_cyclesCounter >= HBlankCycles)
                    {
                        _cyclesCounter -= HBlankCycles;
                        _ly++;
                        if (_ly == VBlankStartLine)
                        {
                            SetPpuMode((byte)PpuMode.VBlank);
                            _cpu.RequestInterrupt(Cpu.Interrupt.VBlank);
                            FrameReady?.Invoke(_frameBuffer);
                        }
                        else
                        {
                            SetPpuMode((byte)PpuMode.OamScan);
                        }
                    }
                    break;
                case (byte)PpuMode.VBlank:
                    if (_cyclesCounter >= VBlankLineCycles)
                    {
                        _cyclesCounter -= VBlankLineCycles;
                        _ly++;
                        if (_ly > MaxScanline)
                        {
                            _ly = 0;
                            _windowLineCounter = 0;
                            SetPpuMode((byte)PpuMode.OamScan);
                        }
                    }
                    break;
            }
            CheckLyLyc();
        }

        private void CheckLyLyc()
        {
            bool prev = (_stat & 0b100) != 0;
            bool now = _ly == _lyc;
            if (now)
            {
                _stat |= 0b100;
            }
            else
            {
                _stat &= unchecked((byte)~0b100);
            }
            if (!prev && now && (_stat & 0b0100_0000) != 0)
            {
                 _cpu.RequestInterrupt(Cpu.Interrupt.LcdStat);
            }
        }

        private void RenderScanline()
        {
            if (_isGbcMode)
            {
                RenderGbcScanline();
            }
            else
            {
                RenderDmgScanline();
            }
        }

        private void RenderGbcScanline()
        {
            byte[] bgLinePriorities = new byte[160];
            byte[] bgLineColorIndices = new byte[160]; // Store BG color index (0-3) for priority checks
            uint[] lineBuffer = new uint[160];

            bool bgWindowMasterEnable = (_lcdc & 0b00000001) != 0;
            bool spritesEnabled = (_lcdc & 0b00000010) != 0;
            bool windowEnabled = (_lcdc & 0b00100000) != 0;

            // 1. Render Background (if enabled)
            if (RenderBackground && bgWindowMasterEnable)
            {
                ushort bgTileMapAddress = ((_lcdc & 0b00001000) != 0) ? (ushort)0x9C00 : (ushort)0x9800;
                int mapY = (_scy + _ly) % 256;

                for (int x = 0; x < 160; x++)
                {
                    int mapX = (_scx + x) % 256;
                    ushort tileMapOffset = (ushort)((mapY / 8 * 32) + (mapX / 8));
                    
                    byte attribute = Mmu.ReadVram((ushort)(bgTileMapAddress + tileMapOffset), 1);
                    byte tileIndex = Mmu.ReadVram((ushort)(bgTileMapAddress + tileMapOffset), 0);
                    
                    byte paletteNum = (byte)(attribute & 0x07);
                    byte vramBank = (byte)((attribute >> 3) & 0x01);
                    bool flipX = (attribute & 0x20) != 0;
                    bool flipY = (attribute & 0x40) != 0;
                    bgLinePriorities[x] = (byte)((attribute >> 7) & 0x01);

                    ushort tileAddress;
                    if ((_lcdc & 0x10) != 0) {
                        // unsigned indexing via $8000
                        tileAddress = (ushort)(0x8000 + tileIndex * 16);
                    } else {
                        // signed indexing via $9000
                        tileAddress = (ushort)(0x9000 + (sbyte)tileIndex * 16);
                    }
                    
                    int tileY = mapY % 8;
                    if (flipY) tileY = 7 - tileY;

                    byte byte1 = Mmu.ReadVram((ushort)(tileAddress + tileY * 2), vramBank);
                    byte byte2 = Mmu.ReadVram((ushort)(tileAddress + tileY * 2 + 1), vramBank);

                    int tileX = mapX % 8;
                    if (flipX) tileX = 7 - tileX;
                    
                    int colorId = (((byte2 >> (7 - tileX)) & 1) << 1) | ((byte1 >> (7 - tileX)) & 1);
                    
                    lineBuffer[x] = GetGbcColor(colorId, paletteNum, false);
                    bgLineColorIndices[x] = (byte)colorId;
                }
            }
            else
            {
                // If BG is disabled, the line is white (color 0, palette 0)
                uint white = GetGbcColor(0, 0, false);
                for (int i = 0; i < 160; i++)
                {
                    lineBuffer[i] = white;
                    bgLineColorIndices[i] = 0; // BG is effectively transparent for priority
                }
            }
            
            // 2. Render Window (if enabled and visible)
            if (RenderWindow && bgWindowMasterEnable && windowEnabled && _ly >= _wy && _wx <= 166)
            {
                int windowXStart = _wx - 7;
                ushort windowTileMapAddress = ((_lcdc & 0b01000000) != 0) ? (ushort)0x9C00 : (ushort)0x9800;

                for (int x = windowXStart; x < 160; x++)
                {
                    if (x >= 0)
                    {
                        int winPixelX = x - windowXStart;
                        int winPixelY = _windowLineCounter;
                        
                        ushort tileMapOffset = (ushort)((winPixelY / 8 * 32) + (winPixelX / 8));
                        
                        byte attribute = Mmu.ReadVram((ushort)(windowTileMapAddress + tileMapOffset), 1);
                        byte tileIndex = Mmu.ReadVram((ushort)(windowTileMapAddress + tileMapOffset), 0);
                        
                        byte paletteNum = (byte)(attribute & 0x07);
                        byte vramBank = (byte)((attribute >> 3) & 0x01);
                        bool flipX = (attribute & 0x20) != 0;
                        bool flipY = (attribute & 0x40) != 0;
                        bgLinePriorities[x] = (byte)((attribute >> 7) & 0x01);
                        
                        ushort tileAddress;
                        if ((_lcdc & 0x10) != 0) {
                            // unsigned indexing via $8000
                            tileAddress = (ushort)(0x8000 + tileIndex * 16);
                        } else {
                            // signed indexing via $9000
                            tileAddress = (ushort)(0x9000 + (sbyte)tileIndex * 16);
                        }
                        
                        int tileY = winPixelY % 8;
                        if (flipY) tileY = 7 - tileY;
                        
                        byte byte1 = Mmu.ReadVram((ushort)(tileAddress + tileY * 2), vramBank);
                        byte byte2 = Mmu.ReadVram((ushort)(tileAddress + tileY * 2 + 1), vramBank);
                        
                        int tileX = winPixelX % 8;
                        if (flipX) tileX = 7 - tileX;
                        
                        int colorId = (((byte2 >> (7 - tileX)) & 1) << 1) | ((byte1 >> (7 - tileX)) & 1);
                        
                        lineBuffer[x] = GetGbcColor(colorId, paletteNum, false);
                        bgLineColorIndices[x] = (byte)colorId;
                    }
                }
            }

            // 3. Render Sprites (if enabled)
            if (RenderSprites && spritesEnabled)
            {
                var spritesOnLine = FindSpritesOnLine(_ly);
                // In GBC mode, sprite priority is determined by OAM index. We do NOT sort.
                // We render backwards so lower OAM index (higher priority) sprites are drawn last.
                for (int i = spritesOnLine.Count - 1; i >= 0; i--)
                {
                    var sprite = spritesOnLine[i];
                    int spriteX = sprite.X - 8;

                    for (int x = 0; x < 8; x++)
                    {
                        int screenX = spriteX + x;
                        if (screenX < 0 || screenX >= 160) continue;

                        int spritePixelY = _ly - (sprite.Y - 16);
                        
                        bool is8x16 = (_lcdc & 0x04) != 0;
                        int spriteHeight = is8x16 ? 16 : 8;

                        if (sprite.FlipY) spritePixelY = (spriteHeight - 1) - spritePixelY;
                        
                        byte tileToFetch = sprite.TileIndex;
                        if (is8x16)
                        {
                            tileToFetch = (spritePixelY < 8) ? (byte)(tileToFetch & 0xFE) : (byte)(tileToFetch | 0x01);
                            if (spritePixelY >= 8) spritePixelY -= 8;
                        }

                        ushort spriteTileAddress = (ushort)(0x8000 + tileToFetch * 16);
                        
                        byte byte1 = Mmu.ReadVram((ushort)(spriteTileAddress + spritePixelY * 2), sprite.VramBank);
                        byte byte2 = Mmu.ReadVram((ushort)(spriteTileAddress + spritePixelY * 2 + 1), sprite.VramBank);
                        
                        int sourcePixelX = sprite.FlipX ? 7 - x : x;
                        int bitPosition = 7 - sourcePixelX;

                        int colorId = (((byte2 >> bitPosition) & 1) << 1) | ((byte1 >> bitPosition) & 1);

                        if (colorId != 0) // Color 0 is transparent for sprites
                        {
                            bool bgPixelIsOpaque = bgLineColorIndices[screenX] != 0;
                            bool bgTileHasPriority = bgLinePriorities[screenX] == 1;

                            bool spriteBehindBg = (sprite.GbcBgPriority || (bgWindowMasterEnable && bgTileHasPriority)) && bgPixelIsOpaque;

                            if (!spriteBehindBg)
                            {
                                lineBuffer[screenX] = GetGbcColor(colorId, sprite.GbcPaletteNumber, true);
                            }
                        }
                    }
                }
            }

            // 4. Copy the completed line to the main framebuffer
            Array.Copy(lineBuffer, 0, _frameBuffer, _ly * 160, 160);

            if (windowEnabled && _ly >= _wy && _wx <= 166) _windowLineCounter++;
        }

        private void RenderDmgScanline()
        {
            byte ly = _ly;
            byte scx = _scx;
            byte scy = _scy;
            byte bgp = _bgp;
            byte obp0 = _obp0;
            byte obp1 = _obp1;

            byte lcdc = _lcdc;
            bool bgEnabled = (lcdc & 0b00000001) != 0;
            bool spritesEnabled = (lcdc & 0b00000010) != 0;
            bool windowEnabled = (lcdc & 0b00100000) != 0;
            
            bool bgTileMapSelect = (lcdc & 0b00001000) != 0;
            ushort bgTileMapAddress = bgTileMapSelect ? (ushort)0x9C00 : (ushort)0x9800;
            
            bool tileDataSelect = (lcdc & 0b00010000) != 0;
            ushort tileDataAddress = tileDataSelect ? (ushort)0x8000 : (ushort)0x8800;
            
            bool windowTileMapSelect = (lcdc & 0b01000000) != 0;
            ushort windowTileMapAddress = windowTileMapSelect ? (ushort)0x9C00 : (ushort)0x9800;
            
            byte wx = _wx;
            byte wy = _wy;

            var spritesOnLine = new List<SpriteInfo>();
            if (spritesEnabled)
            {
                spritesOnLine = FindSpritesOnLine(ly);
            }

            for (int x = 0; x < 160; x++)
            {
                uint finalColor = 0;
                bool bgIsTransparent = false;

                // 1. --- Calculate Background Pixel ---
                if (RenderBackground && bgEnabled)
                {
                    int mapX = (scx + x) % 256;
                    int mapY = (scy + ly) % 256;

                    ushort tileMapOffset = (ushort)((mapY / 8 * 32) + (mapX / 8));
                    byte tileIndex = Mmu.ReadByte((ushort)(bgTileMapAddress + tileMapOffset));

                    ushort tileAddress;
                    if (tileDataSelect)
                    {
                        tileAddress = (ushort)(tileDataAddress + tileIndex * 16);
                    }
                    else
                    {
                        tileAddress = (ushort)(0x9000 + (sbyte)tileIndex * 16);
                    }

                    int tileY = mapY % 8;
                    byte byte1 = Mmu.ReadByte((ushort)(tileAddress + tileY * 2));
                    byte byte2 = Mmu.ReadByte((ushort)(tileAddress + tileY * 2 + 1));

                    int tileX = 7 - (mapX % 8);
                    int colorBit1 = (byte1 >> tileX) & 1;
                    int colorBit2 = (byte2 >> tileX) & 1;
                    int bgColorId = (colorBit2 << 1) | colorBit1;
                    bgIsTransparent = bgColorId == 0;

                    finalColor = GetDmgColor(bgColorId, bgp);
                }
                else
                { 
                     // screen is color 0 when BG master off
                    finalColor = GetDmgColor(0, bgp);
                    bgIsTransparent = true;
                }

                // 2. --- Calculate Window Pixel ---
                if (RenderWindow && bgEnabled && windowEnabled && ly >= wy && x >= (wx - 7))
                {
                    int windowX = x - (wx - 7);
                    int windowY = _windowLineCounter;

                    if (windowX >= 0)
                    {
                        ushort windowTileMapOffset = (ushort)((windowY / 8 * 32) + (windowX / 8));
                        byte windowTileIndex = Mmu.ReadByte((ushort)(windowTileMapAddress + windowTileMapOffset));

                        ushort windowTileAddress;
                        if (tileDataSelect)
                        {
                            windowTileAddress = (ushort)(tileDataAddress + windowTileIndex * 16);
                        }
                        else
                        {
                            windowTileAddress = (ushort)(0x9000 + (sbyte)windowTileIndex * 16);
                        }
                        
                        int windowTileY = windowY % 8;
                        byte windowByte1 = Mmu.ReadByte((ushort)(windowTileAddress + windowTileY * 2));
                        byte windowByte2 = Mmu.ReadByte((ushort)(windowTileAddress + windowTileY * 2 + 1));

                        int windowTileX = 7 - (windowX % 8);
                        int windowColorId = (((windowByte2 >> windowTileX) & 1) << 1) | ((windowByte1 >> windowTileX) & 1);

                        finalColor = GetDmgColor(windowColorId, bgp);
                        bgIsTransparent = windowColorId == 0;
                    }
                }

                // 3. --- Calculate Sprite Pixels ---
                if (RenderSprites && spritesEnabled)
                {
                    foreach (var sprite in spritesOnLine)
                    {
                        int spriteX = sprite.X - 8;
                        if (x >= spriteX && x < spriteX + 8)
                        {
                            bool is8x16 = (_lcdc & 0b00000100) != 0;
                            int spriteHeight = is8x16 ? 16 : 8;

                            int spritePixelX = x - spriteX;
                            int spritePixelY = ly - (sprite.Y - 16); 
                            
                            if (sprite.FlipX) spritePixelX = 7 - spritePixelX;
                            if (sprite.FlipY) spritePixelY = (spriteHeight - 1) - spritePixelY;

                            byte tileToFetch = sprite.TileIndex;
                            if (is8x16)
                            {
                                tileToFetch = (spritePixelY < 8) ? (byte)(tileToFetch & 0xFE) : (byte)(tileToFetch | 0x01);
                                if (spritePixelY >= 8) spritePixelY -= 8;
                            }

                            ushort spriteTileAddress = (ushort)(0x8000 + tileToFetch * 16);
                            byte spriteByte1 = Mmu.ReadByte((ushort)(spriteTileAddress + spritePixelY * 2));
                            byte spriteByte2 = Mmu.ReadByte((ushort)(spriteTileAddress + spritePixelY * 2 + 1));

                            int spriteColorId = (((spriteByte2 >> (7 - spritePixelX)) & 1) << 1) | ((spriteByte1 >> (7 - spritePixelX)) & 1);

                            if (spriteColorId != 0)
                            {
                                bool bgHasPriority = sprite.DmgPriority && !bgIsTransparent;
                                if (!bgHasPriority)
                                {
                                    byte spritePalette = sprite.DmgPalette ? obp1 : obp0;
                                    finalColor = GetDmgColor(spriteColorId, spritePalette);
                                    break;
                                }
                            }
                        }
                    }
                }

                _frameBuffer[ly * 160 + x] = finalColor;
            }

            if (windowEnabled && bgEnabled && ly >= wy && wx <= 166)
            {
                _windowLineCounter++;
            }
        }

        private uint GetDmgColor(int colorId, byte palette)
        {
            int shadeBits = (palette >> (colorId * 2)) & 0b11;
            return _dmgColors[shadeBits];
        }

        private uint GetGbcColor(int colorId, int paletteIndex, bool isSprite)
        {
            byte[] paletteRam = isSprite ? _spritePaletteRam : _bgPaletteRam;
            int offset = (paletteIndex * 8) + (colorId * 2);

            if (offset + 1 >= paletteRam.Length) return 0; // Bounds check

            ushort gbcColor = (ushort)(paletteRam[offset] | (paletteRam[offset + 1] << 8));
            return Convert15BitTo32Bit(gbcColor);
        }

        // GBC uses 15-bit RGB packed as xBBBBBGGGGGRRRRR.
        // Return AARRGGBB so the little-endian byte layout becomes BGRA for the UI.
        internal uint Convert15BitTo32Bit(ushort gbcColor)
        {
            int r = (gbcColor & 0b11111); // first 5 bits
            int g = (gbcColor >> 5) & 0b11111; // second 5 bits
            int b = (gbcColor >> 10) & 0b11111; // last 5 bits

            byte r8 = (byte)((r * 255) / 31);
            byte g8 = (byte)((g * 255) / 31);
            byte b8 = (byte)((b * 255) / 31);

            return (uint)(0xFF << 24 | r8 << 16 | g8 << 8 | b8);
        }

        private void SetPpuMode(byte mode)
        {
            byte MODE_MASK = 0b11; // Bits 0-1: PPU mode
            byte previousMode = (byte)(_stat & MODE_MASK);
            _currentMode = (PpuMode)mode;
            _stat = (byte)((_stat & ~MODE_MASK) | mode); // Clear mode bits, set new mode

            if (previousMode != mode)
            {
                bool requestStat = false;
                switch ((PpuMode)mode)
                {
                    case PpuMode.HBlank:
                        requestStat = (_stat & 0b0000_1000) != 0;
                        break;
                    case PpuMode.VBlank:
                        requestStat = (_stat & 0b0001_0000) != 0;
                        break;
                    case PpuMode.OamScan:
                        requestStat = (_stat & 0b0010_0000) != 0;
                        break;
                }
                if (requestStat)
                {
                    _cpu.RequestInterrupt(Cpu.Interrupt.LcdStat);
                }
            }
        }

        private List<SpriteInfo> FindSpritesOnLine(byte ly)
        {
            var spritesOnLine = new List<SpriteInfo>();
            int spriteHeight = (_lcdc & 0x04) != 0 ? 16 : 8;

            for (int i = 0; i < 40; i++)
            {
                ushort oamAddress = (ushort)(0xFE00 + (i * 4));
                byte spriteY = Mmu.ReadByte(oamAddress);
                byte spriteX = Mmu.ReadByte((ushort)(oamAddress + 1));
                
                if (spriteY == 0 || spriteY >= 160 + 16 || spriteX == 0) continue;

                int spriteTopY = spriteY - 16;
                if (ly >= spriteTopY && ly < spriteTopY + spriteHeight)
                {
                    if (_isGbcMode)
                    {
                        // In GBC mode, we can fetch up to 10 sprites.
                        if (spritesOnLine.Count >= 10) break;
                    }
                    
                    byte tileIndex = Mmu.ReadByte((ushort)(oamAddress + 2));
                    byte attributes = Mmu.ReadByte((ushort)(oamAddress + 3));

                    var sprite = new SpriteInfo
                    {
                        X = spriteX,
                        Y = spriteY,
                        TileIndex = tileIndex,
                        OamIndex = (byte)i,
                        FlipX = (attributes & 0x20) != 0,
                        FlipY = (attributes & 0x40) != 0,
                    };

                    if (_isGbcMode)
                    {
                        sprite.GbcPaletteNumber = (byte)(attributes & 0x07);
                        sprite.VramBank = (byte)((attributes >> 3) & 0x01);
                        sprite.GbcBgPriority = (attributes & 0x80) != 0;
                    }
                    else
                    {
                        sprite.DmgPalette = (attributes & 0x10) != 0;
                        sprite.DmgPriority = (attributes & 0x80) != 0;
                    }
                    spritesOnLine.Add(sprite);
                }
            }
            
            // In DMG mode only, sort by X-coordinate for priority
            if (!_isGbcMode)
            {
                spritesOnLine.Sort((a, b) =>
                {
                    int result = a.X.CompareTo(b.X);
                    return result != 0 ? result : a.OamIndex.CompareTo(b.OamIndex);
                });
                // DMG can only display 10 sprites per line
                if (spritesOnLine.Count > 10)
                {
                    spritesOnLine.RemoveRange(10, spritesOnLine.Count - 10);
                }
            }

            return spritesOnLine;
        }

        public byte[] GetSpriteRgba(int spriteIndex)
        {
            var rgbaBuffer = new byte[8 * 8 * 4];

            ushort oamAddress = (ushort)(0xFE00 + (spriteIndex * 4));
            byte tileIndex = Mmu.ReadByte((ushort)(oamAddress + 2));
            byte attributes = Mmu.ReadByte((ushort)(oamAddress + 3));
            byte vramBank = _isGbcMode ? (byte)((attributes >> 3) & 0x01) : (byte)0;

            bool flipX = (attributes & 0b00100000) != 0;
            bool flipY = (attributes & 0b01000000) != 0;

            ushort tileAddress = (ushort)(0x8000 + (tileIndex * 16));
            
            // For debug view, choose a palette.
            uint GetPixelColor(int colorId)
            {
                if (_isGbcMode)
                {
                    byte paletteNum = (byte)(attributes & 0x07);
                    return GetGbcColor(colorId, paletteNum, true);
                }
                else
                {
                    bool palette = (attributes & 0b00010000) != 0;
                    byte dmgPalette = palette ? _obp1 : _obp0;
                    return GetDmgColor(colorId, dmgPalette);
                }
            }

            for (int y = 0; y < 8; y++)
            {
                int actualY = flipY ? 7 - y : y;
                byte byte1 = Mmu.ReadVram((ushort)(tileAddress + (actualY * 2)), vramBank);
                byte byte2 = Mmu.ReadVram((ushort)(tileAddress + (actualY * 2) + 1), vramBank);

                for (int x = 0; x < 8; x++) // x is screen pixel from left (0) to right (7)
                {
                    // VRAM bit order is 7=left, 0=right.
                    int bitPosition = flipX ? x : 7 - x;

                    int colorBit1 = (byte1 >> bitPosition) & 1;
                    int colorBit2 = (byte2 >> bitPosition) & 1;
                    int colorId = (colorBit2 << 1) | colorBit1;
                    uint color = GetPixelColor(colorId);
                    
                    // Write to buffer with x from left (0) to right (7)
                    int pixelIndex = (y * 8 + x) * 4;

                    // The color format from GetPixelColor is consistently AABBGGRR.
                    // We unpack it into an RGBA byte array for OpenGL.
                    rgbaBuffer[pixelIndex]     = (byte)(color & 0xFF);         // R
                    rgbaBuffer[pixelIndex + 1] = (byte)((color >> 8) & 0xFF);  // G
                    rgbaBuffer[pixelIndex + 2] = (byte)((color >> 16) & 0xFF); // B
                    rgbaBuffer[pixelIndex + 3] = (byte)((color >> 24) & 0xFF); // A
                }
            }
            return rgbaBuffer;
        }
    }
}
