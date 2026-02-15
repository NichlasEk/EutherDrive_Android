using System;
using System.Collections.Generic;
using System.IO;

namespace EutherDrive.UI.Zuul;

internal sealed class JoxRuntime
{
    private const uint FlagLoop = 1u;
    private const int MaxOpsPerTick = 256;

    private readonly float[] _f = new float[8];
    private readonly int[] _i = new int[4];
    private readonly float[] _lerpStep = new float[8];
    private readonly int[] _lerpRemaining = new int[8];
    private readonly float[] _lerpTarget = new float[8];

    private readonly List<Line2> _lines = new();
    private readonly List<Line2> _sourceLines = new();
    private readonly Random _rng = new();

    private byte[] _code = Array.Empty<byte>();
    private int _pc;
    private int _waitTicks;
    private float _yaw;
    private float _pitch;
    private float _scale = 1f;
    private float _offsetX;
    private float _offsetY;
    private float _rotYVel;
    private float _rotXVel;
    private float _shakeAmount;
    private int _shakeTicks;
    private int _shakeTotal;
    private uint _flags;
    private uint _tps = 60;

    public event Action<ushort>? EventEmitted;

    public uint TicksPerSecond => _tps;
    public IReadOnlyList<Line2> Lines => _lines;
    public float Yaw => _yaw;
    public float Pitch => _pitch;

    public void Load(JoxFile file)
    {
        _flags = file.Flags;
        _tps = file.TicksPerSecond == 0 ? 60u : file.TicksPerSecond;
        _pc = 0;
        _waitTicks = 0;
        _yaw = 0f;
        _pitch = 0f;
        _scale = 1f;
        _offsetX = 0f;
        _offsetY = 0f;
        _rotYVel = 0f;
        _rotXVel = 0f;
        _shakeAmount = 0f;
        _shakeTicks = 0;
        _shakeTotal = 0;
        Array.Clear(_f);
        Array.Clear(_i);
        Array.Clear(_lerpStep);
        Array.Clear(_lerpRemaining);
        Array.Clear(_lerpTarget);

        LoadVEC2(file, (int)file.EntryChunkIndex);
        LoadANIM(file);
        RebuildLines();
    }

    public void Tick()
    {
        ApplyLerps();
        _yaw += _rotYVel;
        _pitch += _rotXVel;

        if (_waitTicks > 0)
        {
            _waitTicks--;
            RebuildLines();
            return;
        }

        int ops = 0;
        while (_pc < _code.Length && ops++ < MaxOpsPerTick)
        {
            byte op = _code[_pc++];
            switch (op)
            {
                case 0x00:
                    break;
                case 0x01:
                    if ((_flags & FlagLoop) != 0)
                    {
                        _pc = 0;
                        break;
                    }
                    _pc = _code.Length;
                    RebuildLines();
                    return;
                case 0x10:
                    _waitTicks = ReadU16();
                    RebuildLines();
                    return;
                case 0x20:
                    {
                        int reg = ReadU8();
                        float val = ReadF32();
                        if (reg < _f.Length)
                            _f[reg] = val;
                        break;
                    }
                case 0x21:
                    {
                        int reg = ReadU8();
                        float val = ReadF32();
                        if (reg < _f.Length)
                            _f[reg] += val;
                        break;
                    }
                case 0x30:
                    {
                        int reg = ReadU8();
                        float target = ReadF32();
                        int ticks = ReadU16();
                        if (reg < _f.Length && ticks > 0)
                        {
                            _lerpTarget[reg] = target;
                            _lerpRemaining[reg] = ticks;
                            _lerpStep[reg] = (target - _f[reg]) / ticks;
                        }
                        break;
                    }
                case 0x40:
                    {
                        ushort ev = ReadU16();
                        EventEmitted?.Invoke(ev);
                        break;
                    }
                case 0x50:
                    {
                        float amount = ReadF32();
                        int ticks = ReadU16();
                        _shakeAmount = amount;
                        _shakeTicks = ticks;
                        _shakeTotal = Math.Max(1, ticks);
                        break;
                    }
                case 0x60:
                    {
                        int reg = ReadU8();
                        if (reg < _f.Length)
                            _rotYVel = _f[reg];
                        break;
                    }
                case 0x61:
                    {
                        int reg = ReadU8();
                        if (reg < _f.Length)
                            _rotXVel = _f[reg];
                        break;
                    }
                case 0x70:
                    _scale = ReadF32();
                    break;
                case 0x71:
                    _offsetX = ReadF32();
                    _offsetY = ReadF32();
                    break;
                case 0x80:
                    {
                        uint idx = ReadU32();
                        LoadVEC2(file: null, (int)idx);
                        break;
                    }
                default:
                    _pc = _code.Length;
                    RebuildLines();
                    return;
            }
        }

        RebuildLines();
    }

    public void TickMany(int ticks)
    {
        for (int i = 0; i < ticks; i++)
            Tick();
    }

    public void SetRotation(float yaw, float pitch)
    {
        _yaw = yaw;
        _pitch = pitch;
        RebuildLines();
    }

    public void AddRotation(float yawDelta, float pitchDelta)
    {
        _yaw += yawDelta;
        _pitch += pitchDelta;
        RebuildLines();
    }

    private void ApplyLerps()
    {
        for (int i = 0; i < _lerpRemaining.Length; i++)
        {
            if (_lerpRemaining[i] <= 0)
                continue;
            _f[i] += _lerpStep[i];
            _lerpRemaining[i]--;
            if (_lerpRemaining[i] == 0)
                _f[i] = _lerpTarget[i];
        }
    }

    private void RebuildLines()
    {
        _lines.Clear();
        float angle = _yaw;
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);
        float pitchScale = 1f + (_pitch * 0.1f);

        float shakeX = 0f;
        float shakeY = 0f;
        if (_shakeTicks > 0)
        {
            float t = _shakeTicks / (float)_shakeTotal;
            float amp = _shakeAmount * t;
            shakeX = ((float)_rng.NextDouble() * 2f - 1f) * amp;
            shakeY = ((float)_rng.NextDouble() * 2f - 1f) * amp;
            _shakeTicks--;
        }

        foreach (var line in _sourceLines)
        {
            var a = Transform(line.A, cos, sin, pitchScale, shakeX, shakeY);
            var b = Transform(line.B, cos, sin, pitchScale, shakeX, shakeY);
            _lines.Add(new Line2(a, b));
        }
    }

    private Vector2 Transform(Vector2 v, float cos, float sin, float pitchScale, float shakeX, float shakeY)
    {
        float x = v.X * _scale;
        float y = v.Y * _scale;
        float rx = x * cos - y * sin;
        float ry = x * sin + y * cos;
        ry *= pitchScale;
        rx += _offsetX + shakeX;
        ry += _offsetY + shakeY;
        return new Vector2(rx, ry);
    }

    private void LoadVEC2(JoxFile? file, int index)
    {
        if (file == null)
            return;
        if (index < 0 || index >= file.Chunks.Count)
            return;
        var chunk = file.Chunks[index];
        if (!string.Equals(chunk.Tag, "VEC2", StringComparison.Ordinal))
            return;

        _sourceLines.Clear();
        using var ms = new MemoryStream(chunk.Data);
        using var br = new BinaryReader(ms);
        uint lineCount = br.ReadUInt32();
        for (uint i = 0; i < lineCount; i++)
        {
            float x1 = br.ReadSingle();
            float y1 = br.ReadSingle();
            float x2 = br.ReadSingle();
            float y2 = br.ReadSingle();
            _sourceLines.Add(new Line2(new Vector2(x1, y1), new Vector2(x2, y2)));
        }
    }

    private void LoadANIM(JoxFile file)
    {
        foreach (var chunk in file.Chunks)
        {
            if (!string.Equals(chunk.Tag, "ANIM", StringComparison.Ordinal))
                continue;
            using var ms = new MemoryStream(chunk.Data);
            using var br = new BinaryReader(ms);
            uint len = br.ReadUInt32();
            _code = br.ReadBytes(checked((int)len));
            _pc = 0;
            return;
        }
        _code = Array.Empty<byte>();
        _pc = 0;
    }

    private int ReadU8() => _pc < _code.Length ? _code[_pc++] : 0;

    private ushort ReadU16()
    {
        if (_pc + 1 >= _code.Length)
        {
            _pc = _code.Length;
            return 0;
        }
        ushort value = (ushort)(_code[_pc] | (_code[_pc + 1] << 8));
        _pc += 2;
        return value;
    }

    private uint ReadU32()
    {
        if (_pc + 3 >= _code.Length)
        {
            _pc = _code.Length;
            return 0;
        }
        uint value = (uint)(_code[_pc] | (_code[_pc + 1] << 8) | (_code[_pc + 2] << 16) | (_code[_pc + 3] << 24));
        _pc += 4;
        return value;
    }

    private float ReadF32()
    {
        uint raw = ReadU32();
        return BitConverter.Int32BitsToSingle((int)raw);
    }
}

internal readonly record struct Vector2(float X, float Y);
internal readonly record struct Line2(Vector2 A, Vector2 B);
