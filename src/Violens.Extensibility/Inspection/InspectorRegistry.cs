using Violens.Extensibility.Inspection.Inspectors;

namespace Violens.Extensibility.Inspection;

/// <summary>The available inspectors; picks the ones that apply to a context, in display order.</summary>
public sealed class InspectorRegistry
{
    private readonly List<IInspector> _inspectors;

    public InspectorRegistry(IEnumerable<IInspector> inspectors)
    {
        _inspectors = [.. inspectors];
    }

    public static InspectorRegistry CreateDefault() => CreateDefault(additional: null);

    /// <summary>The built-in inspectors plus <paramref name="additional"/> ones, such as extensions' inspectors.</summary>
    public static InspectorRegistry CreateDefault(IEnumerable<IInspector>? additional)
    {
        IInspector[] builtins =
        [
            new HeadersInspector(),
            new RawTextInspector(),
            new JsonInspector(),
            new TreeInspector(),
            new WebFormsInspector(),
            new ImageInspector(),
            new HexInspector(),
            new CookiesInspector(),
            new NotesInspector(),
        ];

        return new InspectorRegistry(additional is null ? builtins : builtins.Concat(additional));
    }

    /// <summary>
    /// The inspectors that apply to <paramref name="context"/>, sorted by
    /// <see cref="IInspector.SortOrder"/> then <see cref="IInspector.DisplayName"/>.
    /// </summary>
    public IReadOnlyList<IInspector> GetApplicable(InspectorContext context) =>
        _inspectors
            .Where(inspector => inspector.CanInspect(context))
            .OrderBy(inspector => inspector.SortOrder)
            .ThenBy(inspector => inspector.DisplayName, StringComparer.Ordinal)
            .ToArray();
}
