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
    private MethodInfo? _initInstance;
    private MethodInfo? _resetInstance;
    private MethodInfo? _runIntInstance;
    private MethodInfo? _runNoArgsInstance;
    private MethodInfo? _stepNoArgsInstance;

    private object? _instance;

    private bool _inited;

    public string DebugApi { get; }
    public string SelectedRunApi { get; }

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

        _initInstance = FindInstanceNoArgs(_t, "init", "Init", "initialize", "Initialize");
        _resetInstance = FindInstanceNoArgs(_t, "reset", "Reset");
        _runIntInstance = FindInstanceOneInt(_t, "run", "Run", "execute", "Execute", "exec", "Exec");
        _runNoArgsInstance = FindInstanceNoArgs(_t, "run", "Run", "execute", "Execute", "exec", "Exec");
        _stepNoArgsInstance = FindInstanceNoArgs(_t, "step", "Step");

        if (_initInstance != null || _resetInstance != null ||
            _runIntInstance != null || _runNoArgsInstance != null || _stepNoArgsInstance != null)
            _instance = Activator.CreateInstance(_t);

        SelectedRunApi = PickSelectedRunApi();

        DebugApi = BuildDebugApiString();
    }

    public void EnsureInitAndReset()
    {
        if (!_inited)
        {
            if (_init != null)
            {
                _init.Invoke(null, null);
            }
            else if (_initInstance != null && _instance != null)
            {
                _initInstance.Invoke(_instance, null);
            }
            _inited = true;
        }

        if (_reset != null)
        {
            _reset.Invoke(null, null);
        }
        else if (_resetInstance != null && _instance != null)
        {
            _resetInstance.Invoke(_instance, null);
        }
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
        if (_runIntInstance != null && _instance != null)
        {
            _runIntInstance.Invoke(_instance, new object[] { budget });
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
        if (_runNoArgsInstance != null && _instance != null)
        {
            int n = Math.Clamp(budget, 1, 5000);
            for (int i = 0; i < n; i++)
                _runNoArgsInstance.Invoke(_instance, null);
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
        if (_stepNoArgsInstance != null && _instance != null)
        {
            int n = Math.Clamp(budget, 1, 20000);
            for (int i = 0; i < n; i++)
                _stepNoArgsInstance.Invoke(_instance, null);
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

    private static MethodInfo? FindInstanceNoArgs(Type t, params string[] names)
    => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
    .FirstOrDefault(m => names.Contains(m.Name) && m.GetParameters().Length == 0);

    private static MethodInfo? FindInstanceOneInt(Type t, params string[] names)
    => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
    .FirstOrDefault(m =>
    names.Contains(m.Name) &&
    m.GetParameters().Length == 1 &&
    m.GetParameters()[0].ParameterType == typeof(int));

    private string BuildDebugApiString()
    {
        var ms = _t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
        .OrderBy(m => m.Name)
        .Select(m =>
        {
            var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
            var scope = m.IsStatic ? "static" : "instance";
            return $"{m.Name}({ps}) [{scope}]";
        });

        return string.Join("\n", ms);
    }

    private string PickSelectedRunApi()
    {
        if (_runInt != null) return "static run(int)";
        if (_runIntInstance != null) return "instance run(int)";
        if (_runNoArgs != null) return "static run()";
        if (_runNoArgsInstance != null) return "instance run()";
        if (_stepNoArgs != null) return "static step()";
        if (_stepNoArgsInstance != null) return "instance step()";
        return "none";
    }
}
