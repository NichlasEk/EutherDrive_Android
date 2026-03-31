using System;

namespace EutherDrive.Core;

public sealed class PsxInterlaceReconstructor
{
    private const int BytesPerPixel = 4;
    private const int MotionSensitivity = 20;

    private byte[] _latestEvenField = Array.Empty<byte>();
    private byte[] _latestOddField = Array.Empty<byte>();
    private byte[] _previousEvenField = Array.Empty<byte>();
    private byte[] _previousOddField = Array.Empty<byte>();
    private int _width;
    private int _height;
    private int _rowBytes;
    private int _fieldCapacityRows;
    private int _evenRows;
    private int _oddRows;
    private int _previousEvenRows;
    private int _previousOddRows;
    private bool _hasEvenField;
    private bool _hasOddField;
    private bool _hasPreviousEvenField;
    private bool _hasPreviousOddField;

    public void Reset()
    {
        _width = 0;
        _height = 0;
        _rowBytes = 0;
        _fieldCapacityRows = 0;
        _evenRows = 0;
        _oddRows = 0;
        _previousEvenRows = 0;
        _previousOddRows = 0;
        _hasEvenField = false;
        _hasOddField = false;
        _hasPreviousEvenField = false;
        _hasPreviousOddField = false;
    }

    public bool TryApplyInPlace(
        Span<byte> frame,
        int width,
        int height,
        int stride,
        in PsxAdapter.PresentationFrameInfo frameInfo)
    {
        if (!frameInfo.IsInterlaceWeave || width <= 0 || height < 2 || stride < (width * BytesPerPixel))
        {
            Reset();
            return false;
        }

        int requiredBytes = stride * height;
        if (requiredBytes <= 0 || requiredBytes > frame.Length)
        {
            Reset();
            return false;
        }

        int fieldParity = frameInfo.InterlaceFieldParity & 1;
        EnsureGeometry(width, height);
        StoreCurrentField(frame[..requiredBytes], height, stride, fieldParity);

        if (frameInfo.HasCompleteInterlacePair && _hasEvenField && _hasOddField)
            ReconstructMotionAdaptiveFrame(frame[..requiredBytes], height, stride, fieldParity);
        else
            ReconstructBobFrame(frame[..requiredBytes], height, stride, fieldParity);

        return true;
    }

    private void EnsureGeometry(int width, int height)
    {
        int rowBytes = checked(width * BytesPerPixel);
        int fieldCapacityRows = (height + 1) >> 1;
        int requiredBytes = checked(rowBytes * fieldCapacityRows);
        bool geometryChanged = width != _width || height != _height || rowBytes != _rowBytes;

        if (geometryChanged)
        {
            Reset();
            _width = width;
            _height = height;
            _rowBytes = rowBytes;
            _fieldCapacityRows = fieldCapacityRows;
        }

        if (_latestEvenField.Length < requiredBytes)
            _latestEvenField = new byte[requiredBytes];
        if (_latestOddField.Length < requiredBytes)
            _latestOddField = new byte[requiredBytes];
        if (_previousEvenField.Length < requiredBytes)
            _previousEvenField = new byte[requiredBytes];
        if (_previousOddField.Length < requiredBytes)
            _previousOddField = new byte[requiredBytes];
    }

    private void StoreCurrentField(ReadOnlySpan<byte> frame, int height, int stride, int fieldParity)
    {
        if (fieldParity == 0)
        {
            PromoteFieldHistory(_latestEvenField, _evenRows, _hasEvenField, _previousEvenField, out _previousEvenRows, out _hasPreviousEvenField);
            _evenRows = ExtractField(frame, height, stride, 0, _latestEvenField);
            _hasEvenField = _evenRows > 0;
            return;
        }

        PromoteFieldHistory(_latestOddField, _oddRows, _hasOddField, _previousOddField, out _previousOddRows, out _hasPreviousOddField);
        _oddRows = ExtractField(frame, height, stride, 1, _latestOddField);
        _hasOddField = _oddRows > 0;
    }

    private void PromoteFieldHistory(
        byte[] latestField,
        int latestRows,
        bool hasLatestField,
        byte[] previousField,
        out int previousRows,
        out bool hasPreviousField)
    {
        if (hasLatestField && latestRows > 0)
        {
            latestField.AsSpan(0, latestRows * _rowBytes).CopyTo(previousField);
            previousRows = latestRows;
            hasPreviousField = true;
            return;
        }

        previousRows = 0;
        hasPreviousField = false;
    }

    private int ExtractField(ReadOnlySpan<byte> frame, int height, int stride, int fieldParity, byte[] destination)
    {
        int rows = 0;
        int dstOffset = 0;
        for (int y = fieldParity; y < height; y += 2)
        {
            frame.Slice(y * stride, _rowBytes).CopyTo(destination.AsSpan(dstOffset, _rowBytes));
            dstOffset += _rowBytes;
            rows++;
        }

        if (rows == 0 && height > 0)
        {
            frame.Slice(0, _rowBytes).CopyTo(destination.AsSpan(0, _rowBytes));
            rows = 1;
        }

        return rows;
    }

    private void ReconstructBobFrame(Span<byte> frame, int height, int stride, int fieldParity)
    {
        byte[] currentField = fieldParity == 0 ? _latestEvenField : _latestOddField;
        int currentRows = fieldParity == 0 ? _evenRows : _oddRows;
        if (currentRows <= 0)
            return;

        for (int y = 0; y < height; y++)
            WriteBobRow(frame.Slice(y * stride, _rowBytes), currentField, currentRows, y, fieldParity);
    }

    private void ReconstructMotionAdaptiveFrame(Span<byte> frame, int height, int stride, int fieldParity)
    {
        byte[] currentField = fieldParity == 0 ? _latestEvenField : _latestOddField;
        int currentRows = fieldParity == 0 ? _evenRows : _oddRows;
        byte[]? previousSameField = fieldParity == 0 && _hasPreviousEvenField
            ? _previousEvenField
            : fieldParity == 1 && _hasPreviousOddField
                ? _previousOddField
                : null;
        int previousSameRows = fieldParity == 0 ? _previousEvenRows : _previousOddRows;
        byte[]? previousOppositeField = fieldParity == 0 && _hasPreviousOddField
            ? _previousOddField
            : fieldParity == 1 && _hasPreviousEvenField
                ? _previousEvenField
                : null;
        int previousOppositeRows = fieldParity == 0 ? _previousOddRows : _previousEvenRows;

        for (int y = 0; y < height; y++)
        {
            Span<byte> dstRow = frame.Slice(y * stride, _rowBytes);
            WriteMotionAdaptiveRow(
                dstRow,
                currentField,
                currentRows,
                previousSameField,
                previousSameRows,
                previousOppositeField,
                previousOppositeRows,
                y,
                fieldParity);
        }
    }

    private void WriteMotionAdaptiveRow(
        Span<byte> dstRow,
        byte[] currentField,
        int currentRows,
        byte[]? previousSameField,
        int previousSameRows,
        byte[]? previousOppositeField,
        int previousOppositeRows,
        int outputRow,
        int fieldParity)
    {
        int rowParity = outputRow & 1;
        byte[] weaveField = rowParity == 0 ? _latestEvenField : _latestOddField;
        int weaveRows = rowParity == 0 ? _evenRows : _oddRows;
        WriteWeaveRow(dstRow, weaveField, weaveRows, outputRow);

        if (currentRows <= 0
            || weaveRows <= 0
            || previousSameField == null
            || previousSameRows <= 0
            || previousOppositeField == null
            || previousOppositeRows <= 0)
        {
            return;
        }

        GetFieldSampleIndices(outputRow, fieldParity, currentRows, out int upperIndex, out int lowerIndex, out int weight256);
        int weaveRowIndex = Math.Min(outputRow >> 1, weaveRows - 1);
        int previousWeaveRowIndex = Math.Min(weaveRowIndex, previousOppositeRows - 1);
        int previousUpperIndex = Math.Min(upperIndex, previousSameRows - 1);
        int previousLowerIndex = Math.Min(lowerIndex, previousSameRows - 1);

        int upperOffset = upperIndex * _rowBytes;
        int lowerOffset = lowerIndex * _rowBytes;
        int weaveOffset = weaveRowIndex * _rowBytes;
        int previousWeaveOffset = previousWeaveRowIndex * _rowBytes;
        int previousUpperOffset = previousUpperIndex * _rowBytes;
        int previousLowerOffset = previousLowerIndex * _rowBytes;

        for (int x = 0; x < _rowBytes; x += BytesPerPixel)
        {
            bool motion =
                HasMotion(
                    currentField[upperOffset + x + 0],
                    previousSameField[previousUpperOffset + x + 0],
                    weaveField[weaveOffset + x + 0],
                    previousOppositeField[previousWeaveOffset + x + 0],
                    currentField[lowerOffset + x + 0],
                    previousSameField[previousLowerOffset + x + 0])
                || HasMotion(
                    currentField[upperOffset + x + 1],
                    previousSameField[previousUpperOffset + x + 1],
                    weaveField[weaveOffset + x + 1],
                    previousOppositeField[previousWeaveOffset + x + 1],
                    currentField[lowerOffset + x + 1],
                    previousSameField[previousLowerOffset + x + 1])
                || HasMotion(
                    currentField[upperOffset + x + 2],
                    previousSameField[previousUpperOffset + x + 2],
                    weaveField[weaveOffset + x + 2],
                    previousOppositeField[previousWeaveOffset + x + 2],
                    currentField[lowerOffset + x + 2],
                    previousSameField[previousLowerOffset + x + 2]);

            if (!motion)
                continue;

            dstRow[x + 0] = LerpByte(currentField[upperOffset + x + 0], currentField[lowerOffset + x + 0], weight256);
            dstRow[x + 1] = LerpByte(currentField[upperOffset + x + 1], currentField[lowerOffset + x + 1], weight256);
            dstRow[x + 2] = LerpByte(currentField[upperOffset + x + 2], currentField[lowerOffset + x + 2], weight256);
            dstRow[x + 3] = LerpByte(currentField[upperOffset + x + 3], currentField[lowerOffset + x + 3], weight256);
        }
    }

    private void WriteWeaveRow(Span<byte> dstRow, byte[] field, int fieldRows, int outputRow)
    {
        if (fieldRows <= 0)
        {
            dstRow.Clear();
            return;
        }

        int fieldRowIndex = Math.Min(outputRow >> 1, fieldRows - 1);
        field.AsSpan(fieldRowIndex * _rowBytes, _rowBytes).CopyTo(dstRow);
    }

    private void WriteBobRow(Span<byte> dstRow, byte[] field, int fieldRows, int outputRow, int fieldParity)
    {
        GetFieldSampleIndices(outputRow, fieldParity, fieldRows, out int upperIndex, out int lowerIndex, out int weight256);
        int upperOffset = upperIndex * _rowBytes;
        int lowerOffset = lowerIndex * _rowBytes;

        for (int x = 0; x < _rowBytes; x += BytesPerPixel)
        {
            dstRow[x + 0] = LerpByte(field[upperOffset + x + 0], field[lowerOffset + x + 0], weight256);
            dstRow[x + 1] = LerpByte(field[upperOffset + x + 1], field[lowerOffset + x + 1], weight256);
            dstRow[x + 2] = LerpByte(field[upperOffset + x + 2], field[lowerOffset + x + 2], weight256);
            dstRow[x + 3] = LerpByte(field[upperOffset + x + 3], field[lowerOffset + x + 3], weight256);
        }
    }

    private static bool HasMotion(byte a, byte b, byte c, byte d, byte e, byte f)
        => Math.Abs(a - b) > MotionSensitivity
            || Math.Abs(c - d) > MotionSensitivity
            || Math.Abs(e - f) > MotionSensitivity;

    private static byte LerpByte(byte a, byte b, int weight256)
    {
        if (weight256 <= 0 || a == b)
            return a;
        if (weight256 >= 256)
            return b;

        int inverseWeight = 256 - weight256;
        return (byte)(((a * inverseWeight) + (b * weight256) + 128) >> 8);
    }

    private static void GetFieldSampleIndices(
        int outputRow,
        int fieldParity,
        int fieldRows,
        out int upperIndex,
        out int lowerIndex,
        out int weight256)
    {
        if (fieldRows <= 1)
        {
            upperIndex = 0;
            lowerIndex = 0;
            weight256 = 0;
            return;
        }

        double fieldPosition = Math.Clamp((outputRow - fieldParity) * 0.5, 0.0, fieldRows - 1.0);
        upperIndex = (int)fieldPosition;
        lowerIndex = Math.Min(upperIndex + 1, fieldRows - 1);
        weight256 = (int)Math.Round((fieldPosition - upperIndex) * 256.0);
    }
}
