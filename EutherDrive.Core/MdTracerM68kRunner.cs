using System;
using System.Linq;
using System.Reflection;

namespace EutherDrive.Core;

/// <summary>
/// Kör MDTracer m68k via reflection så vi slipper gissa metodnamn.
/// </summary>
public sealed class MdTracerM68kRunner
{
    private readonly Type _t;
    private MethodInfo? _init;
    private MethodInfo? _reset;
    private MethodInfo? _runInt;
    private MethodInfo? _runNoArgs;
    private MethodInfo? _stepNoArgs;

    private bool _inited;

    public string DebugApi { get; }

    public MdTracerM68kRunner()
    {
        var asm = typeof(MdTracerM68kRunner).Assembly;

        _t = asm.GetTypes()
        .FirstOrDefault(x => x.FullName != null && x.FullName.EndsWith(".md_m68k", StringComparison.Ordinal))
        ?? throw new InvalidOperationException("Could not find type '* .md_m68k' in EutherDrive.Core assembly. (Did you re-enable md_m68k*.cs in csproj?)");

        _init = FindStaticNoArgs(_t, "init", "Init", "initialize", "Initialize");
        _reset = FindStaticNoArgs(_t, "reset", "Reset");

        _runInt = FindStaticOneInt(_t, "run", "Run", "execute", "Execute", "exec", "Exec");
        _runNoArgs = FindStaticNoArgs(_t, "run", "Run", "execute", "Execute", "exec", "Exec");

        _stepNoArgs = FindStaticNoArgs(_t, "step", "Step");

        DebugApi = BuildDebugApiString();
    }

    public void EnsureInitAndReset()
    {
        if (!_inited)
        {
            _init?.Invoke(null, null);
            _inited = true;
        }

        _reset?.Invoke(null, null);
    }

    /// <summary>
    /// Kör CPU “lite grann” per frame.
    /// Om det finns run(int cycles) använder vi den.
    /// Annars step() loop.
    /// </summary>
    public void RunSome(int budget)
    {
        // 1) run(int)
        if (_runInt != null)
        {
            _runInt.Invoke(null, new object[] { budget });
            return;
        }

        // 2) run() utan args
        if (_runNoArgs != null)
        {
            // kör budget gånger, men begränsa så vi inte låser UI
            int n = Math.Clamp(budget, 1, 5000);
            for (int i = 0; i < n; i++)
                _runNoArgs.Invoke(null, null);
            return;
        }

        // 3) step()
        if (_stepNoArgs != null)
        {
            int n = Math.Clamp(budget, 1, 20000);
            for (int i = 0; i < n; i++)
                _stepNoArgs.Invoke(null, null);
            return;
        }

        // annars: vi kan inte köra
        throw new InvalidOperationException(
            $"md_m68k found, but no runnable method found. Methods available:\n{DebugApi}");
    }

    private static MethodInfo? FindStaticNoArgs(Type t, params string[] names)
    => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
    .FirstOrDefault(m => names.Contains(m.Name) && m.GetParameters().Length == 0);

    private static MethodInfo? FindStaticOneInt(Type t, params string[] names)
    => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
    .FirstOrDefault(m =>
    names.Contains(m.Name) &&
    m.GetParameters().Length == 1 &&
    m.GetParameters()[0].ParameterType == typeof(int));

    private string BuildDebugApiString()
    {
        var ms = _t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        .OrderBy(m => m.Name)
        .Select(m =>
        {
            var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
            return $"{m.Name}({ps})";
        });

        return string.Join("\n", ms);
    }
}
