namespace EutherDrive.OpenRA;

public static class OpenRaPaths
{
    public static OpenRaInstallation FromEutherDriveRoot(string eutherDriveRoot)
    {
        if (string.IsNullOrWhiteSpace(eutherDriveRoot))
            throw new ArgumentException("EutherDrive root is required.", nameof(eutherDriveRoot));

        return OpenRaInstallation.FromRepositoryRoot(
            Path.Combine(Path.GetFullPath(eutherDriveRoot), "external", "OpenRA"));
    }

    public static OpenRaInstallation FromCurrentDirectory()
    {
        var current = Directory.GetCurrentDirectory();
        var probe = new DirectoryInfo(current);

        while (probe is not null)
        {
            var solution = Path.Combine(probe.FullName, "EutherDrive.sln");
            if (File.Exists(solution))
                return FromEutherDriveRoot(probe.FullName);

            probe = probe.Parent;
        }

        return FromEutherDriveRoot(current);
    }
}
