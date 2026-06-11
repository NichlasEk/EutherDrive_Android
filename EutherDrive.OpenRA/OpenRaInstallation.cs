using System.Diagnostics;

namespace EutherDrive.OpenRA;

public sealed class OpenRaInstallation
{
    public OpenRaInstallation(string repositoryRoot, string contentRoot, string engineDll, string utilityDll)
    {
        RepositoryRoot = repositoryRoot;
        ContentRoot = contentRoot;
        EngineDll = engineDll;
        UtilityDll = utilityDll;
    }

    public string RepositoryRoot { get; }

    public string ContentRoot { get; }

    public string EngineDll { get; }

    public string UtilityDll { get; }

    public bool HasCheckout => Directory.Exists(RepositoryRoot);

    public bool HasBuiltEngine => File.Exists(EngineDll);

    public bool HasUtility => File.Exists(UtilityDll);

    public static OpenRaInstallation FromRepositoryRoot(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
            throw new ArgumentException("Repository root is required.", nameof(repositoryRoot));

        var fullRoot = Path.GetFullPath(repositoryRoot);
        return new OpenRaInstallation(
            fullRoot,
            Path.Combine(Path.GetDirectoryName(fullRoot) ?? fullRoot, "openra-content"),
            Path.Combine(fullRoot, "bin", "OpenRA.dll"),
            Path.Combine(fullRoot, "bin", "OpenRA.Utility.dll"));
    }

    public OpenRaProcessCommand CreateLaunchCommand(string mod = "ra", params string[] arguments)
    {
        if (string.IsNullOrWhiteSpace(mod))
            throw new ArgumentException("OpenRA mod is required.", nameof(mod));

        var args = new List<string>
        {
            EngineDll,
            "Engine.EngineDir=.",
            $"Game.Mod={mod}"
        };

        args.AddRange(arguments);
        return new OpenRaProcessCommand("dotnet", args, RepositoryRoot);
    }

    public OpenRaProcessCommand CreateUtilityCommand(string mod = "ra", params string[] arguments)
    {
        if (string.IsNullOrWhiteSpace(mod))
            throw new ArgumentException("OpenRA mod is required.", nameof(mod));

        var args = new List<string> { UtilityDll, mod };
        args.AddRange(arguments);
        return new OpenRaProcessCommand("dotnet", args, RepositoryRoot)
            .WithEnvironment("ENGINE_DIR", "..");
    }
}

public sealed class OpenRaProcessCommand
{
    public OpenRaProcessCommand(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        FileName = fileName;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
        Environment = environment;
    }

    public string FileName { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string WorkingDirectory { get; }

    public IReadOnlyDictionary<string, string>? Environment { get; }

    public ProcessStartInfo ToProcessStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FileName,
            WorkingDirectory = WorkingDirectory,
            UseShellExecute = false
        };

        foreach (var argument in Arguments)
            startInfo.ArgumentList.Add(argument);

        if (Environment is not null)
            foreach (var (key, value) in Environment)
                startInfo.Environment[key] = value;

        return startInfo;
    }

    public OpenRaProcessCommand WithEnvironment(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Environment key is required.", nameof(key));

        var environment = Environment is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(Environment, StringComparer.Ordinal);

        environment[key] = value;
        return new OpenRaProcessCommand(FileName, Arguments, WorkingDirectory, environment);
    }
}
