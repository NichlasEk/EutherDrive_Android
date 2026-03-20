using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace EutherDrive.Core.Savestates;

internal static class StateBinarySerializer
{
    private const int MaxArrayElements = 16 * 1024 * 1024;

    private static readonly ConcurrentDictionary<Type, FieldInfo[]> FieldCache = new();

    public static void WriteObject(BinaryWriter writer, object? obj)
    {
        if (obj == null)
        {
            writer.Write(false);
            return;
        }

        writer.Write(true);
        WriteInto(writer, obj);
    }

    public static void WriteInto(BinaryWriter writer, object obj)
    {
        Type type = obj.GetType();
        foreach (var field in GetFields(type))
        {
            object? value = field.GetValue(obj);
            WriteValue(writer, field.FieldType, value);
        }
    }

    public static void ReadInto(BinaryReader reader, object obj)
    {
        Type type = obj.GetType();
        foreach (var field in GetFields(type))
        {
            ReadValueInto(reader, obj, field);
        }
    }

    private static FieldInfo[] GetFields(Type type)
    {
        return FieldCache.GetOrAdd(type, t =>
        {
            return t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => !Attribute.IsDefined(f, typeof(NonSerializedAttribute)))
                .Where(f => IsSupportedType(f.FieldType))
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .ToArray();
        });
    }

    private static bool IsSupportedType(Type type)
    {
        Type? nullableUnderlying = Nullable.GetUnderlyingType(type);
        if (nullableUnderlying != null)
            return IsSupportedType(nullableUnderlying);

        if (type.IsPointer || type == typeof(IntPtr) || type == typeof(UIntPtr))
            return false;
        if (type.IsByRef || type.IsByRefLike)
            return false;
        if (typeof(Delegate).IsAssignableFrom(type))
            return false;
        if (type == typeof(Type))
            return false;

        if (type.IsPrimitive || type.IsEnum || type == typeof(string))
            return true;

        if (type.IsArray)
        {
            Type? elementType = type.GetElementType();
            return elementType != null && IsSupportedType(elementType);
        }

        return true;
    }

    private static void WriteValue(BinaryWriter writer, Type type, object? value)
    {
        Type? nullableUnderlying = Nullable.GetUnderlyingType(type);
        if (nullableUnderlying != null)
        {
            bool hasValue = value != null;
            writer.Write(hasValue);
            if (hasValue)
                WriteValue(writer, nullableUnderlying, value);
            return;
        }

        if (type.IsEnum)
        {
            WriteValue(writer, Enum.GetUnderlyingType(type), value);
            return;
        }

        if (type == typeof(string))
        {
            if (value == null)
            {
                writer.Write(false);
            }
            else
            {
                writer.Write(true);
                writer.Write((string)value);
            }
            return;
        }

        if (type.IsPrimitive)
        {
            WritePrimitive(writer, type, value);
            return;
        }

        if (type.IsArray)
        {
            WriteArray(writer, (Array?)value, type.GetElementType()!);
            return;
        }

        if (type.IsValueType)
        {
            WriteStruct(writer, value!, type);
            return;
        }

        WriteObject(writer, value);
    }

    private static void ReadValueInto(BinaryReader reader, object target, FieldInfo field)
    {
        Type type = field.FieldType;
        bool isReadonly = field.IsInitOnly;

        Type? nullableUnderlying = Nullable.GetUnderlyingType(type);
        if (nullableUnderlying != null)
        {
            bool hasValue = reader.ReadBoolean();
            if (!hasValue)
            {
                if (!isReadonly)
                    field.SetValue(target, null);
                return;
            }

            object? value = ReadValue(reader, nullableUnderlying);
            if (!isReadonly)
                field.SetValue(target, value);
            return;
        }

        if (type.IsEnum)
        {
            object value = ReadPrimitive(reader, Enum.GetUnderlyingType(type));
            field.SetValue(target, Enum.ToObject(type, value));
            return;
        }

        if (type == typeof(string))
        {
            bool hasValue = reader.ReadBoolean();
            string? result = hasValue ? reader.ReadString() : null;
            if (!isReadonly)
                field.SetValue(target, result);
            return;
        }

        if (type.IsPrimitive)
        {
            object value = ReadPrimitive(reader, type);
            if (!isReadonly)
                field.SetValue(target, value);
            return;
        }

        if (type.IsArray)
        {
            ReadArrayInto(reader, target, field, type.GetElementType()!);
            return;
        }

        if (type.IsValueType)
        {
            object value = ReadStruct(reader, type);
            if (!isReadonly)
                field.SetValue(target, value);
            return;
        }

        bool hasObject = reader.ReadBoolean();
        if (!hasObject)
        {
            if (!isReadonly)
                field.SetValue(target, null);
            return;
        }

        object? current = field.GetValue(target);
        if (current != null)
        {
            ReadInto(reader, current);
            return;
        }

        object created = CreateInstanceForRead(type);
        ReadInto(reader, created);
        if (!isReadonly)
            field.SetValue(target, created);
    }

    private static void WritePrimitive(BinaryWriter writer, Type type, object? value)
    {
        if (type == typeof(bool)) writer.Write(value != null && (bool)value);
        else if (type == typeof(byte)) writer.Write(value != null ? (byte)value : (byte)0);
        else if (type == typeof(sbyte)) writer.Write(value != null ? (sbyte)value : (sbyte)0);
        else if (type == typeof(short)) writer.Write(value != null ? (short)value : (short)0);
        else if (type == typeof(ushort)) writer.Write(value != null ? (ushort)value : (ushort)0);
        else if (type == typeof(int)) writer.Write(value != null ? (int)value : 0);
        else if (type == typeof(uint)) writer.Write(value != null ? (uint)value : 0u);
        else if (type == typeof(long)) writer.Write(value != null ? (long)value : 0L);
        else if (type == typeof(ulong)) writer.Write(value != null ? (ulong)value : 0UL);
        else if (type == typeof(float)) writer.Write(value != null ? (float)value : 0f);
        else if (type == typeof(double)) writer.Write(value != null ? (double)value : 0d);
        else if (type == typeof(char)) writer.Write(value != null ? (char)value : '\0');
        else throw new InvalidOperationException($"Unsupported primitive type {type}.");
    }

    private static object ReadPrimitive(BinaryReader reader, Type type)
    {
        if (type == typeof(bool)) return reader.ReadBoolean();
        if (type == typeof(byte)) return reader.ReadByte();
        if (type == typeof(sbyte)) return reader.ReadSByte();
        if (type == typeof(short)) return reader.ReadInt16();
        if (type == typeof(ushort)) return reader.ReadUInt16();
        if (type == typeof(int)) return reader.ReadInt32();
        if (type == typeof(uint)) return reader.ReadUInt32();
        if (type == typeof(long)) return reader.ReadInt64();
        if (type == typeof(ulong)) return reader.ReadUInt64();
        if (type == typeof(float)) return reader.ReadSingle();
        if (type == typeof(double)) return reader.ReadDouble();
        if (type == typeof(char)) return reader.ReadChar();
        throw new InvalidOperationException($"Unsupported primitive type {type}.");
    }

    private static void WriteArray(BinaryWriter writer, Array? array, Type elementType)
    {
        if (array == null)
        {
            writer.Write(-1);
            return;
        }

        int rank = array.Rank;
        writer.Write(rank);
        for (int i = 0; i < rank; i++)
            writer.Write(array.GetLength(i));

        if (rank == 1 && elementType == typeof(byte))
        {
            writer.Write((byte[])array);
            return;
        }

        foreach (object? value in array)
            WriteValue(writer, elementType, value);
    }

    private static void ReadArrayInto(BinaryReader reader, object target, FieldInfo field, Type elementType)
    {
        try
        {
            int rank = reader.ReadInt32();
            bool isReadonly = field.IsInitOnly;
            
            // Backward compatibility:
            // Older savestates wrote 1D arrays as [length] instead of [rank][length].
            // If we see an unreasonable rank for a 1D array, treat the first int as legacy length.
            int? legacyLength = null;
            if (rank > 10 && field.FieldType.IsArray && field.FieldType.GetArrayRank() == 1)
            {
                legacyLength = rank;
                rank = 1;
                Console.WriteLine($"[DEBUG-StateBinarySerializer] Legacy 1D array header detected for field={field.Name}, length={legacyLength.Value}");
            }
            else if (rank > 10)
            {
                Console.WriteLine($"[DEBUG-StateBinarySerializer] ReadArrayInto: field={field.Name} type={field.FieldType} rank={rank} (HUGE!)");
                long pos = reader.BaseStream.Position;
                byte[] next16 = new byte[Math.Min(16, reader.BaseStream.Length - pos)];
                if (next16.Length > 0)
                {
                    reader.BaseStream.Read(next16, 0, next16.Length);
                    reader.BaseStream.Position = pos;
                    Console.WriteLine($"[DEBUG] Next {next16.Length} bytes: {BitConverter.ToString(next16)}");
                }
                throw new InvalidDataException($"Array rank {rank} is unreasonable for field {field.Name}. Savestate file may be corrupt.");
            }
            
            if (rank < 0)
            {
                if (!isReadonly)
                    field.SetValue(target, null);
                return;
            }
            if (rank == 0)
            {
                // Handle rank=0 as empty array (shouldn't happen but UI savestates work)
                // Create an empty array with rank=1, length=0
                Array emptyArray = Array.CreateInstance(elementType, 0);
                if (!isReadonly)
                    field.SetValue(target, emptyArray);
                return;
            }

            int[] lengths = new int[rank];
            long totalLong = 1;
            if (legacyLength.HasValue)
            {
                if (legacyLength.Value < 0 || legacyLength.Value > MaxArrayElements)
                    throw new InvalidDataException(
                        $"Array length {legacyLength.Value} is unreasonable for field {field.Name}.");
                lengths[0] = legacyLength.Value;
                totalLong = legacyLength.Value;
            }
            else
            {
                for (int i = 0; i < rank; i++)
                {
                    lengths[i] = reader.ReadInt32();
                    if (lengths[i] < 0)
                        throw new InvalidDataException($"Negative array length for field {field.Name}.");
                    totalLong *= lengths[i];
                    if (totalLong > MaxArrayElements)
                        throw new InvalidDataException(
                            $"Array element count {totalLong} is unreasonable for field {field.Name}.");
                }
            }
            int total = (int)totalLong;

        int targetRank = field.FieldType.IsArray ? field.FieldType.GetArrayRank() : rank;
        if (targetRank != rank)
        {
            // Backward compatibility: if a savestate stored this array with a different rank,
            // flatten into 1D for current field shape when possible.
            if (targetRank == 1)
            {
                bool isReadonlyFlat = field.IsInitOnly;
                Array? existingFlat = (Array?)field.GetValue(target);
                Array flatBuffer;
                if (existingFlat == null || existingFlat.Rank != 1 || existingFlat.Length != total)
                {
                    flatBuffer = Array.CreateInstance(elementType, total);
                    if (!isReadonlyFlat)
                        field.SetValue(target, flatBuffer);
                }
                else
                {
                    flatBuffer = existingFlat;
                }

                for (int i = 0; i < total; i++)
                {
                    object? value = ReadValue(reader, elementType);
                    flatBuffer.SetValue(value, i);
                }
                return;
            }

            // Unsupported rank migration for this field shape; consume payload to keep stream aligned.
            for (int i = 0; i < total; i++)
                _ = ReadValue(reader, elementType);
            return;
        }

        Array? existing = (Array?)field.GetValue(target);
        Array buffer;

        if (existing == null || existing.Rank != rank)
        {
            buffer = Array.CreateInstance(elementType, lengths);
            if (!isReadonly)
                field.SetValue(target, buffer);
        }
        else
        {
            buffer = existing;
            bool shapeMismatch = false;
            for (int i = 0; i < rank; i++)
            {
                if (buffer.GetLength(i) != lengths[i])
                {
                    shapeMismatch = true;
                    break;
                }
            }

            if (shapeMismatch)
            {
                buffer = Array.CreateInstance(elementType, lengths);
                if (!isReadonly)
                    field.SetValue(target, buffer);
            }
        }

        if (rank == 1 && elementType == typeof(byte))
        {
            byte[] data = reader.ReadBytes(lengths[0]);
            int copy = Math.Min(lengths[0], buffer.Length);
            Array.Copy(data, 0, buffer, 0, copy);
            return;
        }

        int[] strides = BuildStrides(lengths);
        int[] indices = new int[rank];
        for (int i = 0; i < total; i++)
        {
            object? value = ReadValue(reader, elementType);
            ToIndices(i, strides, indices);
            buffer.SetValue(value, indices);
        }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG-StateBinarySerializer] ReadArrayInto failed: field={field?.Name} type={elementType?.Name} error={ex.Message}");
            throw;
        }
    }

    private static object? ReadValue(BinaryReader reader, Type type)
    {
        Type? nullableUnderlying = Nullable.GetUnderlyingType(type);
        if (nullableUnderlying != null)
        {
            bool hasValue = reader.ReadBoolean();
            if (!hasValue)
                return null;
            return ReadValue(reader, nullableUnderlying);
        }

        if (type.IsEnum)
        {
            object value = ReadPrimitive(reader, Enum.GetUnderlyingType(type));
            return Enum.ToObject(type, value);
        }

        if (type == typeof(string))
        {
            bool hasValue = reader.ReadBoolean();
            return hasValue ? reader.ReadString() : null;
        }

        if (type.IsPrimitive)
            return ReadPrimitive(reader, type);

        if (type.IsArray)
        {
            Type elementType = type.GetElementType()!;
            int rank = reader.ReadInt32();
            if (rank < 0)
                return null!;
            int[] lengths = new int[rank];
            int total = 1;
            for (int i = 0; i < rank; i++)
            {
                lengths[i] = reader.ReadInt32();
                total *= lengths[i];
            }

            Array array = Array.CreateInstance(elementType, lengths);
            if (rank == 1 && elementType == typeof(byte))
            {
                byte[] data = reader.ReadBytes(lengths[0]);
                Array.Copy(data, array, data.Length);
                return array;
            }

            int[] strides = BuildStrides(lengths);
            int[] indices = new int[rank];
            for (int i = 0; i < total; i++)
            {
                object? value = ReadValue(reader, elementType);
                ToIndices(i, strides, indices);
                array.SetValue(value, indices);
            }
            return array;
        }

        if (type.IsValueType)
            return ReadStruct(reader, type);

        bool hasObject = reader.ReadBoolean();
        if (!hasObject)
            return null!;
        object created = CreateInstanceForRead(type);
        ReadInto(reader, created);
        return created;
    }

    private static void WriteStruct(BinaryWriter writer, object value, Type type)
    {
        foreach (var field in GetFields(type))
        {
            WriteValue(writer, field.FieldType, field.GetValue(value));
        }
    }

    private static object ReadStruct(BinaryReader reader, Type type)
    {
        object created = CreateInstanceForRead(type);
        foreach (var field in GetFields(type))
        {
            ReadValueInto(reader, created, field);
        }
        return created;
    }

    private static object CreateInstanceForRead(Type type)
    {
        try
        {
            return Activator.CreateInstance(type, nonPublic: true)!;
        }
        catch (MissingMethodException)
        {
            return RuntimeHelpers.GetUninitializedObject(type);
        }
        catch (MemberAccessException)
        {
            return RuntimeHelpers.GetUninitializedObject(type);
        }
    }

    private static int[] BuildStrides(int[] lengths)
    {
        int rank = lengths.Length;
        int[] strides = new int[rank];
        int stride = 1;
        for (int i = rank - 1; i >= 0; i--)
        {
            strides[i] = stride;
            stride *= lengths[i];
        }
        return strides;
    }

    private static void ToIndices(int linearIndex, int[] strides, int[] indices)
    {
        int remaining = linearIndex;
        for (int i = 0; i < strides.Length; i++)
        {
            int stride = strides[i];
            indices[i] = remaining / stride;
            remaining -= indices[i] * stride;
        }
    }
}
