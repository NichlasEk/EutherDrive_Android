using System;
using System.IO;

class TestBiosSearch
{
    static void Main()
    {
        Console.WriteLine("Testing BIOS search logic from BUS.cs...");
        
        // Simulate the search logic from BUS.cs
        string[] biosFilenames = { "SCPH1001.BIN", "scph1001.bin", "SCPH1001.bin", "scph1001.BIN" };
        string[] biosPaths = {
            "./{0}",                    // Current directory
            "{0}",                      // Current directory (no ./)
            "../bios/{0}",              // ../bios directory
            "../../bios/{0}",           // ../../bios directory
            "../../../bios/{0}",        // ../../../bios directory
            "/bios/{0}",                // Absolute /bios directory
            Path.Combine(Environment.CurrentDirectory, "bios", "{0}"), // Current dir/bios
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bios", "{0}") // App base/bios
        };
        
        Console.WriteLine($"Current Directory: {Environment.CurrentDirectory}");
        Console.WriteLine($"App Base Directory: {AppDomain.CurrentDomain.BaseDirectory}");
        Console.WriteLine();
        
        string foundBiosPath = null;
        foreach (string filename in biosFilenames)
        {
            foreach (string pathTemplate in biosPaths)
            {
                string path = string.Format(pathTemplate, filename);
                bool exists = File.Exists(path);
                Console.WriteLine($"{exists} - {path}");
                if (exists && foundBiosPath == null)
                {
                    foundBiosPath = path;
                }
            }
        }
        
        Console.WriteLine();
        if (foundBiosPath != null)
        {
            Console.WriteLine($"SUCCESS: Would load BIOS from: {foundBiosPath}");
        }
        else
        {
            Console.WriteLine("ERROR: No BIOS file found in any location.");
            Console.WriteLine("Note: BIOS files should be in ~/EutherDrive/bios/");
        }
    }
}