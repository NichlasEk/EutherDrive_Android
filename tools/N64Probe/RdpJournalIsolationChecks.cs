using System.Reflection;
using System.Runtime.Loader;
using Ryu64.MIPS;

internal static class RdpJournalIsolationChecks
{
    // The capture observer must disappear completely in the ordinary build.
    // Compare against the accepted pre-journal DLL, including hot-path IL.
    internal static void Run(string reference)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var current = typeof(Memory);
        if (current.GetMembers(flags).Any(m => m.Name.Contains("Journal", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This check requires a normal build without N64RdpJournalCapture");
        var context = new AssemblyLoadContext("journal-production-reference", isCollectible: true);
        var expected = context.LoadFromAssemblyPath(Path.GetFullPath(reference)).GetType(current.FullName!)!;
        string[] Fields(Type type) => type.GetFields(flags).Select(f => $"{f.Name}:{f.FieldType}:{f.Attributes}").Order().ToArray();
        if (!Fields(current).SequenceEqual(Fields(expected))) throw new Exception("Production Memory fields changed");
        MethodBase[] Methods(Type type) => type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags))
            .OrderBy(m => m.ToString(), StringComparer.Ordinal).ToArray();
        var actualMethods = Methods(current); var expectedMethods = Methods(expected);
        if (actualMethods.Length != expectedMethods.Length) throw new Exception("Production Memory method count changed");
        int bytes = 0;
        for (int i = 0; i < actualMethods.Length; i++)
        {
            var a = actualMethods[i]; var b = expectedMethods[i];
            var actual = a.GetMethodBody(); var baseline = b.GetMethodBody();
            byte[] aIl = actual?.GetILAsByteArray() ?? Array.Empty<byte>();
            byte[] bIl = baseline?.GetILAsByteArray() ?? Array.Empty<byte>();
            string[] Locals(MethodBody? body) => body?.LocalVariables.Select(v => $"{v.LocalType}:{v.IsPinned}").ToArray() ?? Array.Empty<string>();
            string[] Clauses(MethodBody? body) => body?.ExceptionHandlingClauses.Select(c =>
                $"{c.Flags}:{c.TryOffset}:{c.TryLength}:{c.HandlerOffset}:{c.HandlerLength}:" +
                (c.Flags == ExceptionHandlingClauseOptions.Clause ? c.CatchType?.ToString() : c.Flags == ExceptionHandlingClauseOptions.Filter ? c.FilterOffset.ToString() : "")).ToArray() ?? Array.Empty<string>();
            if (a.ToString() != b.ToString() || a.Attributes != b.Attributes || !aIl.AsSpan().SequenceEqual(bIl)
                || !Locals(actual).SequenceEqual(Locals(baseline)) || !Clauses(actual).SequenceEqual(Clauses(baseline)))
                throw new Exception($"Production IL differs: {a}");
            bytes += aIl.Length;
        }
        Console.WriteLine($"journalProductionIsolation=passed fields={Fields(current).Length} methods={actualMethods.Length} identicalIlBytes={bytes} observerMembers=0");
        context.Unload();
    }
}
