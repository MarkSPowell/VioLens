using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace Violens.ProxyCore.Preferences;

/// <summary>
/// Machine-wide settings an administrator sets for everyone on the machine
/// (Clearinet #9). <see cref="Locked"/> values win over the user's own and
/// can't be changed in the app; <see cref="Defaults"/> apply only until the
/// user changes them. See <see cref="PolicyPreferenceStore"/>.
///
/// Read once at start-up from places only an administrator can write:
/// <list type="bullet">
/// <item>Windows: the registry key <c>HKLM\SOFTWARE\Policies\VioLens</c>
/// (string or DWORD values, named by preference; where Group Policy puts
/// them) and its <c>Defaults</c> subkey, then
/// <c>%ProgramData%\VioLens\policy.json</c>.</item>
/// <item>macOS: <c>/Library/Application Support/VioLens/policy.json</c>.</item>
/// <item>Linux: <c>/etc/violens/policy.json</c>.</item>
/// </list>
/// The file looks like
/// <c>{ "locked": { "violens.proxy.allowremote": "False" }, "defaults": { … } }</c>,
/// values as strings in the preferences file's own format (booleans may also
/// be JSON <c>true</c>/<c>false</c>, numbers JSON numbers). Where both the
/// registry and the file name a preference, the registry wins. A source
/// that can't be read is logged and skipped: a broken policy never stops
/// VioLens starting.
/// </summary>
public sealed class PolicySettings
{
    public const string FileName = "policy.json";

    public const string RegistryKeyPath = @"SOFTWARE\Policies\VioLens";

    public const string RegistryDefaultsSubkey = "Defaults";

    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    public PolicySettings(
        IReadOnlyDictionary<string, string> locked,
        IReadOnlyDictionary<string, string> defaults,
        IReadOnlyList<string>? sources = null)
    {
        Locked = new Dictionary<string, string>(locked, NameComparer);
        Defaults = new Dictionary<string, string>(defaults, NameComparer);
        Sources = sources ?? [];
    }

    /// <summary>No policy: what tests and a machine without one get.</summary>
    public static PolicySettings None { get; } = new(new Dictionary<string, string>(), new Dictionary<string, string>());

    public IReadOnlyDictionary<string, string> Locked { get; }

    public IReadOnlyDictionary<string, string> Defaults { get; }

    /// <summary>Where the settings came from, for Help &gt; Copy Diagnostic Info.</summary>
    public IReadOnlyList<string> Sources { get; }

    public bool IsEmpty => Locked.Count == 0 && Defaults.Count == 0;

    /// <summary>This machine's policy file: under ProgramData, /Library/Application Support or /etc.</summary>
    public static string DefaultFilePath
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VioLens", FileName);
            }

            return OperatingSystem.IsMacOS()
                ? Path.Combine("/Library/Application Support/VioLens", FileName)
                : Path.Combine("/etc/violens", FileName);
        }
    }

    /// <summary>The registry policy (on Windows) over the policy file.</summary>
    public static PolicySettings LoadMachine(Action<string>? log = null)
    {
        log ??= _ => { };
        var locked = new Dictionary<string, string>(NameComparer);
        var defaults = new Dictionary<string, string>(NameComparer);
        var sources = new List<string>();

        var path = DefaultFilePath;
        if (File.Exists(path))
        {
            try
            {
                var file = Parse(File.ReadAllText(path));
                Merge(locked, file.Locked);
                Merge(defaults, file.Defaults);
                sources.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
            {
                log($"Couldn't read the policy file {path}: {ex.Message} It's ignored.");
            }
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (ReadRegistry(locked, defaults))
                {
                    sources.Add(@"HKLM\" + RegistryKeyPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                log($"Couldn't read the policy registry key: {ex.Message} It's ignored.");
            }
        }

        return new PolicySettings(locked, defaults, sources);
    }

    /// <summary>Reads a policy file's text.</summary>
    /// <exception cref="JsonException">Not JSON, or not shaped like a policy file.</exception>
    /// <exception cref="FormatException">A preference name isn't valid.</exception>
    public static PolicySettings Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The policy file must hold a JSON object.");
        }

        var locked = new Dictionary<string, string>(NameComparer);
        var defaults = new Dictionary<string, string>(NameComparer);
        foreach (var section in document.RootElement.EnumerateObject())
        {
            var target = section.Name.ToLowerInvariant() switch
            {
                "locked" => locked,
                "defaults" => defaults,
                _ => null,
            };

            // Anything else (a comment field, "formatVersion") is left for later versions.
            if (target is null)
            {
                continue;
            }

            if (section.Value.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException($"\"{section.Name}\" must be an object of preference names and values.");
            }

            foreach (var pref in section.Value.EnumerateObject())
            {
                if (!PreferenceStore.IsValidName(pref.Name))
                {
                    throw new FormatException($"\"{pref.Name}\" isn't a valid preference name.");
                }

                target[pref.Name] = pref.Value.ValueKind switch
                {
                    JsonValueKind.String => pref.Value.GetString()!,
                    JsonValueKind.True => bool.TrueString,
                    JsonValueKind.False => bool.FalseString,
                    JsonValueKind.Number => pref.Value.GetRawText(),
                    _ => throw new JsonException($"\"{pref.Name}\" must be a string, a number, true or false."),
                };
            }
        }

        return new PolicySettings(locked, defaults);
    }

    private static void Merge(Dictionary<string, string> target, IReadOnlyDictionary<string, string> source)
    {
        foreach (var (name, value) in source)
        {
            target[name] = value;
        }
    }

    /// <summary>True when the policy key exists.</summary>
    [SupportedOSPlatform("windows")]
    private static bool ReadRegistry(Dictionary<string, string> locked, Dictionary<string, string> defaults)
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath);
        if (key is null)
        {
            return false;
        }

        ReadValues(key, locked);
        using (var defaultsKey = key.OpenSubKey(RegistryDefaultsSubkey))
        {
            if (defaultsKey is not null)
            {
                ReadValues(defaultsKey, defaults);
            }
        }

        return true;
    }

    [SupportedOSPlatform("windows")]
    private static void ReadValues(RegistryKey key, Dictionary<string, string> target)
    {
        foreach (var name in key.GetValueNames())
        {
            if (!PreferenceStore.IsValidName(name))
            {
                continue;
            }

            // A DWORD stays a number; PolicyPreferenceStore reads 1 and 0 as
            // true and false, which is how Group Policy writes a switch.
            var value = key.GetValue(name) switch
            {
                int dword => dword.ToString(System.Globalization.CultureInfo.InvariantCulture),
                long qword => qword.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string text => text,
                string[] lines => string.Join(';', lines),
                _ => null,
            };

            if (value is not null)
            {
                target[name] = value;
            }
        }
    }
}
