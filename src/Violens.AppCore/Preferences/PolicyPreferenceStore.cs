using System.Globalization;
using Violens.ProxyCore.Preferences;

namespace Violens.AppCore.Preferences;

/// <summary>
/// The user's <see cref="PreferenceStore"/> with a machine
/// <see cref="PolicySettings"/> over it. A locked preference reads as the
/// policy's value whatever the user's file says, and writes to it are
/// dropped (and logged), so nothing in the app, a FiddlerScript or an
/// extension can change it. A policy default reads only while the user's
/// file has no value of its own. Everything else passes straight through.
///
/// A policy value of <c>1</c> or <c>0</c> also reads as true or false,
/// since that's how Group Policy writes a switch to the registry.
/// </summary>
public sealed class PolicyPreferenceStore : IPreferenceStore, IDisposable
{
    private readonly PreferenceStore _inner;
    private readonly Action<string> _log;

    public PolicyPreferenceStore(PreferenceStore inner, PolicySettings policy, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(policy);
        _inner = inner;
        Policy = policy;
        _log = log ?? (_ => { });
    }

    public PolicySettings Policy { get; }

    /// <summary>The user's own store, under the policy.</summary>
    public PreferenceStore Inner => _inner;

    public bool IsLocked(string prefName) => Policy.Locked.ContainsKey(prefName);

    public string? this[string prefName] =>
        Policy.Locked.TryGetValue(prefName, out var locked) ? locked
        : _inner[prefName] ?? (Policy.Defaults.TryGetValue(prefName, out var fallback) ? fallback : null);

    public string GetStringPref(string prefName, string defaultValue) => this[prefName] ?? defaultValue;

    public bool GetBoolPref(string prefName, bool defaultValue)
    {
        var raw = this[prefName];
        if (bool.TryParse(raw, out var value))
        {
            return value;
        }

        if (IsFromPolicy(prefName))
        {
            return raw?.Trim() switch
            {
                "1" => true,
                "0" => false,
                _ => defaultValue,
            };
        }

        return defaultValue;
    }

    public int GetInt32Pref(string prefName, int defaultValue) =>
        int.TryParse(this[prefName], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : defaultValue;

    public IReadOnlyList<string> GetListPref(string prefName, IReadOnlyList<string> defaultValue) =>
        this[prefName] is { } raw ? PreferenceStore.ParseList(raw) : defaultValue;

    public void SetStringPref(string prefName, string value)
    {
        if (Allow(prefName))
        {
            _inner.SetStringPref(prefName, value);
        }
    }

    public void SetBoolPref(string prefName, bool value)
    {
        if (Allow(prefName))
        {
            _inner.SetBoolPref(prefName, value);
        }
    }

    public void SetInt32Pref(string prefName, int value)
    {
        if (Allow(prefName))
        {
            _inner.SetInt32Pref(prefName, value);
        }
    }

    public void SetListPref(string prefName, IEnumerable<string> values)
    {
        if (Allow(prefName))
        {
            _inner.SetListPref(prefName, values);
        }
    }

    public void SetPrefs(IEnumerable<KeyValuePair<string, string>> prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        _inner.SetPrefs(prefs.Where(pref => Allow(pref.Key)).ToList());
    }

    public void RemovePref(string prefName)
    {
        if (Allow(prefName))
        {
            _inner.RemovePref(prefName);
        }
    }

    public PrefWatcher AddWatcher(string prefixFilter, EventHandler<PrefChangeEventArgs> handler) =>
        _inner.AddWatcher(prefixFilter, handler);

    public void RemoveWatcher(PrefWatcher watcher) => _inner.RemoveWatcher(watcher);

    public void Flush() => _inner.Flush();

    public void Dispose() => _inner.Dispose();

    private bool IsFromPolicy(string prefName) =>
        Policy.Locked.ContainsKey(prefName) || (_inner[prefName] is null && Policy.Defaults.ContainsKey(prefName));

    private bool Allow(string prefName)
    {
        if (!IsLocked(prefName))
        {
            return true;
        }

        _log($"\"{prefName}\" is locked by machine policy, so the change was ignored.");
        return false;
    }
}
