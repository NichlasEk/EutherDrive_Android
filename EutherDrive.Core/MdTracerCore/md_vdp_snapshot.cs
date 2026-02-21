using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace EutherDrive.Core.MdTracerCore
{
    public partial class md_vdp
    {
        public void DumpMdMemorySnapshot(string directory, string prefix)
        {
            Directory.CreateDirectory(directory);

            File.WriteAllBytes(Path.Combine(directory, $"{prefix}_vram.bin"), g_vram);
            WriteWordArrayText(Path.Combine(directory, $"{prefix}_cram.txt"), g_cram, "CRAM");
            WriteWordArrayText(Path.Combine(directory, $"{prefix}_vsram.txt"), g_vsram, "VSRAM");
        }

        private static void WriteWordArrayText(string path, ushort[] words, string label)
        {
            using var writer = new StreamWriter(path, false, Encoding.UTF8);
            writer.WriteLine($"{label} words={words.Length}");
            for (int i = 0; i < words.Length; i++)
            {
                writer.WriteLine(
                    $"{i.ToString("X4", CultureInfo.InvariantCulture)}: {words[i].ToString("X4", CultureInfo.InvariantCulture)}");
            }
        }
    }
}
