using System.Diagnostics;
using EutherDrive.OpenRA;

var installation = args.Length > 0
    ? OpenRaInstallation.FromRepositoryRoot(args[0])
    : OpenRaPaths.FromCurrentDirectory();

Console.WriteLine($"OpenRA root: {installation.RepositoryRoot}");
Console.WriteLine($"Content root: {installation.ContentRoot}");
Console.WriteLine($"Checkout: {(installation.HasCheckout ? "yes" : "no")}");
Console.WriteLine($"Engine: {(installation.HasBuiltEngine ? "yes" : "no")}");
Console.WriteLine($"Utility: {(installation.HasUtility ? "yes" : "no")}");

if (!installation.HasUtility)
    return 2;

var command = installation.CreateUtilityCommand("ra");
using var process = Process.Start(command.ToProcessStartInfo());
if (process is null)
{
    Console.Error.WriteLine("Failed to start OpenRA utility.");
    return 3;
}

process.WaitForExit();
return process.ExitCode;
