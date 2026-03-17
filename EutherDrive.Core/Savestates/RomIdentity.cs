using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EutherDrive.Core.Savestates;

public sealed class RomIdentity
{
    public RomIdentity(string name, byte[] hash, string? preferredStateDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("ROM name is required.", nameof(name));
        if (hash == null || hash.Length == 0)
            throw new ArgumentException("ROM hash is required.", nameof(hash));

        Name = name;
        Hash = hash;
        PreferredStateDirectory = preferredStateDirectory;
    }

    public string Name { get; }
    public byte[] Hash { get; }
    public string? PreferredStateDirectory { get; }

    public string HashHex
    {
        get
        {
            var sb = new StringBuilder(Hash.Length * 2);
            for (int i = 0; i < Hash.Length; i++)
                sb.Append(Hash[i].ToString("x2"));
            return sb.ToString();
        }
    }

    public string HashPrefix(int length = 8)
    {
        string hex = HashHex;
        if (length <= 0 || length >= hex.Length)
            return hex;
        return hex.Substring(0, length);
    }

    public static byte[] ComputeSha256(byte[] data)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
    }

    public static byte[] ComputeSha256(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var sha = SHA256.Create();
        return sha.ComputeHash(stream);
    }
}
