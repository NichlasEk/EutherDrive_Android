using System;

namespace EutherDrive.Core.MdTracerCore;

internal partial class md_vdp
{
    private int _dbgX = 160;
    private int _dbgY = 112;

    internal void DebugOverlay_EndOfFrame(MdPadState pad)
    {
        // Läs padstate från IO-lagret
        var p = md_io.Pad1;

        // Flytta cursor
        int speed = p.A ? 5 : 2; // A = “snabb”
        if (p.Left)  _dbgX -= speed;
        if (p.Right) _dbgX += speed;
        if (p.Up)    _dbgY -= speed;
        if (p.Down)  _dbgY += speed;

        _dbgX = Math.Clamp(_dbgX, 0, FrameWidth - 1);
        _dbgY = Math.Clamp(_dbgY, 0, FrameHeight - 1);

        // Färg beroende på knappar (för att se A/B/C/Start tydligt)
        byte r = 255, g = 255, b = 255;
        if (p.B) { r = 80;  g = 200; b = 255; }
        if (p.C) { r = 200; g = 80;  b = 255; }
        if (p.Start) { r = 255; g = 0; b = 0; }

        DrawCrosshair(_dbgX, _dbgY, r, g, b);

        // Liten “HUD” uppe till vänster som visar bitar (superbra för debug)
        DrawBitsRow(0, 0, p);
    }

    private void DrawBitsRow(int y, int x, MdPadState p)
    {
        // 8 små block: U D L R A B C S
        DrawBit(y, x + 0,  p.Up);
        DrawBit(y, x + 8,  p.Down);
        DrawBit(y, x + 16, p.Left);
        DrawBit(y, x + 24, p.Right);
        DrawBit(y, x + 40, p.A);
        DrawBit(y, x + 48, p.B);
        DrawBit(y, x + 56, p.C);
        DrawBit(y, x + 64, p.Start);
    }

    private void DrawBit(int y, int x, bool on)
    => DrawRect(x, y, 6, 3, on ? (byte)0 : (byte)40, on ? (byte)255 : (byte)40, on ? (byte)0 : (byte)40);

    private void DrawCrosshair(int cx, int cy, byte r, byte g, byte b)
    {
        // litet plus-tecken 9x9
        DrawRect(cx - 4, cy - 1, 9, 3, r, g, b);
        DrawRect(cx - 1, cy - 4, 3, 9, r, g, b);
    }

    private void DrawRect(int x0, int y0, int w, int h, byte r, byte g, byte b)
    {
        int x1 = Math.Min(FrameWidth, x0 + w);
        int y1 = Math.Min(FrameHeight, y0 + h);
        x0 = Math.Max(0, x0);
        y0 = Math.Max(0, y0);

        for (int y = y0; y < y1; y++)
        {
            int o = y * Pitch + (x0 * 4);
            for (int x = x0; x < x1; x++)
            {
                RgbaFrame[o + 0] = r;
                RgbaFrame[o + 1] = g;
                RgbaFrame[o + 2] = b;
                RgbaFrame[o + 3] = 255;
                o += 4;
            }
        }
    }
}
