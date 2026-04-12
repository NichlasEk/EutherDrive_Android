using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace EutherDrive.UI.Effects;

public sealed class EffectManager
{
    private readonly Dictionary<string, IUiEffect> _effects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _eventBindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _enabledEffects = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _triggerLock = new(1, 1);

    public bool EffectsEnabled { get; set; } = true;

    public void Register(string key, IUiEffect effect)
    {
        _effects[key] = effect;
    }

    public void BindEvent(string eventKey, string effectKey)
    {
        _eventBindings[eventKey] = effectKey;
    }

    public void SetEffectEnabled(string key, bool enabled)
    {
        _enabledEffects[key] = enabled;
    }

    public async Task TriggerAsync(string effectKey, Control? root)
    {
        if (!EffectsEnabled || root == null)
            return;

        if (!_effects.TryGetValue(effectKey, out IUiEffect? effect))
            return;

        if (_enabledEffects.TryGetValue(effectKey, out bool enabled) && !enabled)
            return;

        await _triggerLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await effect.Run(root).ConfigureAwait(false);
        }
        finally
        {
            _triggerLock.Release();
        }
    }

    public Task TriggerEventAsync(string eventKey, Control? root)
    {
        if (!_eventBindings.TryGetValue(eventKey, out string? effectKey))
            return Task.CompletedTask;

        return TriggerAsync(effectKey, root);
    }
}
