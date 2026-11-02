using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Violens.Tests.Shared;

/// <summary>
/// The 1.0 API freeze: writes an assembly's public surface (every public
/// type, and every public or protected member, with parameter names and
/// defaults) as sorted text, one line each, to compare with the checked-in
/// <c>PublicApi/&lt;assembly&gt;.txt</c>. A change to the surface fails the
/// test until that file is updated in the same commit, so breaking an API
/// is always a deliberate, reviewed change. Linked into each test project
/// that guards a shipped assembly.
/// </summary>
internal static class PublicApi
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static string Describe(Assembly assembly)
    {
        var lines = new List<string>();
        foreach (var type in assembly.GetExportedTypes())
        {
            var typeName = Name(type);
            lines.Add($"{Kind(type)} {typeName}{Bases(type)}");
            if (type.IsEnum)
            {
                foreach (var name in Enum.GetNames(type))
                {
                    lines.Add($"{typeName}.{name} = {Convert.ToInt64(Enum.Parse(type, name), System.Globalization.CultureInfo.InvariantCulture)}");
                }

                continue;
            }

            foreach (var member in type.GetMembers(Declared))
            {
                if (Describe(member) is { } line)
                {
                    lines.Add($"{typeName}::{line}");
                }
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// Compares with the checked-in file. To accept a deliberate change, run
    /// the tests with VIOLENS_UPDATE_PUBLIC_API=1, which rewrites the file,
    /// and commit it.
    /// </summary>
    public static void Verify(Assembly assembly, [CallerFilePath] string callerPath = "")
    {
        var folder = Path.Combine(Path.GetDirectoryName(callerPath)!, "PublicApi");
        var path = Path.Combine(folder, assembly.GetName().Name + ".txt");
        var actual = Describe(assembly);
        if (Environment.GetEnvironmentVariable("VIOLENS_UPDATE_PUBLIC_API") == "1")
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            return;
        }

        var expected = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : string.Empty;
        if (expected == actual)
        {
            return;
        }

        var expectedLines = expected.Split('\n').ToHashSet(StringComparer.Ordinal);
        var actualLines = actual.Split('\n').ToHashSet(StringComparer.Ordinal);
        var removed = expectedLines.Except(actualLines).Where(l => l.Length > 0).Select(l => "- " + l);
        var added = actualLines.Except(expectedLines).Where(l => l.Length > 0).Select(l => "+ " + l);
        throw new Xunit.Sdk.XunitException(
            $"The public API of {assembly.GetName().Name} changed. If that's deliberate (and, after 1.0, not breaking), " +
            "run the tests with VIOLENS_UPDATE_PUBLIC_API=1 and commit the updated file.\n" +
            string.Join("\n", removed.Concat(added)));
    }

    private static string? Describe(MemberInfo member)
    {
        switch (member)
        {
            case ConstructorInfo { IsStatic: false } constructor when Visible(constructor):
                return $".ctor({Parameters(constructor.GetParameters())})";

            case MethodInfo method when Visible(method) && !method.IsSpecialName:
                return $"{Modifiers(method)}{Name(method.ReturnType)} {method.Name}{GenericArguments(method)}({Parameters(method.GetParameters())})";

            case MethodInfo method when Visible(method) && method.Name.StartsWith("op_", StringComparison.Ordinal):
                return $"static {Name(method.ReturnType)} {method.Name}({Parameters(method.GetParameters())})";

            case PropertyInfo property:
            {
                var getter = property.GetMethod is { } g && Visible(g) ? "get; " : string.Empty;
                var setter = property.SetMethod is { } s && Visible(s)
                    ? (s.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit") ? "init; " : "set; ")
                    : string.Empty;
                if (getter.Length + setter.Length == 0)
                {
                    return null;
                }

                var accessor = property.GetMethod ?? property.SetMethod!;
                var indexer = property.GetIndexParameters() is { Length: > 0 } index ? $"[{Parameters(index)}]" : string.Empty;
                return $"{Modifiers(accessor)}{Name(property.PropertyType)} {property.Name}{indexer} {{ {getter}{setter}}}";
            }

            case FieldInfo field when field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly:
            {
                var constant = field.IsLiteral ? $" = {Literal(field.GetRawConstantValue())}" : string.Empty;
                var kind = field.IsLiteral ? "const " : field.IsStatic ? (field.IsInitOnly ? "static readonly " : "static ") : (field.IsInitOnly ? "readonly " : string.Empty);
                return $"{kind}{Name(field.FieldType)} {field.Name}{constant}";
            }

            case EventInfo @event when @event.AddMethod is { } add && Visible(add):
                return $"{Modifiers(add)}event {Name(@event.EventHandlerType!)} {@event.Name}";

            default:
                return null;
        }
    }

    private static bool Visible(MethodBase method) => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static string Modifiers(MethodInfo method)
    {
        var text = new StringBuilder();
        if (method.IsFamily || method.IsFamilyOrAssembly)
        {
            text.Append("protected ");
        }

        if (method.IsStatic)
        {
            text.Append("static ");
        }
        else if (method.IsAbstract && !method.DeclaringType!.IsInterface)
        {
            text.Append("abstract ");
        }
        else if (method.IsVirtual && !method.IsFinal && !method.DeclaringType!.IsInterface)
        {
            text.Append(method.GetBaseDefinition() == method ? "virtual " : "override ");
        }

        return text.ToString();
    }

    private static string Kind(Type type) => type switch
    {
        { IsInterface: true } => "interface",
        { IsEnum: true } => "enum",
        { IsValueType: true } => "struct",
        _ when type.IsSubclassOf(typeof(MulticastDelegate)) => "delegate",
        { IsAbstract: true, IsSealed: true } => "static class",
        { IsAbstract: true } => "abstract class",
        { IsSealed: true } => "sealed class",
        _ => "class",
    };

    private static string Bases(Type type)
    {
        var bases = new List<string>();
        if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType) &&
            baseType != typeof(Enum) && baseType != typeof(MulticastDelegate))
        {
            bases.Add(Name(baseType));
        }

        bases.AddRange(type.GetInterfaces()
            .Where(i => i.IsPublic || i.IsNestedPublic)
            .Select(Name)
            .Order(StringComparer.Ordinal));
        return bases.Count == 0 ? string.Empty : " : " + string.Join(", ", bases);
    }

    private static string GenericArguments(MethodInfo method) =>
        method.IsGenericMethodDefinition ? "<" + string.Join(", ", method.GetGenericArguments().Select(a => a.Name)) + ">" : string.Empty;

    private static string Parameters(ParameterInfo[] parameters) => string.Join(", ", parameters.Select(p =>
    {
        var prefix = p.ParameterType.IsByRef ? (p.IsOut ? "out " : p.IsIn ? "in " : "ref ") : string.Empty;
        var type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
        var isParams = p.GetCustomAttributes(typeof(ParamArrayAttribute), false).Length > 0 ? "params " : string.Empty;
        var defaultValue = p.HasDefaultValue ? " = " + Literal(p.RawDefaultValue) : string.Empty;
        return $"{isParams}{prefix}{Name(type)} {p.Name}{defaultValue}";
    }));

    private static string Literal(object? value) => value switch
    {
        null => "null",
        string text => "\"" + text + "\"",
        bool flag => flag ? "true" : "false",
        IFormattable number => number.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null",
    };

    private static string Name(Type type)
    {
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.IsArray)
        {
            return Name(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Name(underlying) + "?";
        }

        var name = type.IsNested ? Name(type.DeclaringType!) + "." + type.Name : (type.Namespace is { } ns ? ns + "." : string.Empty) + type.Name;
        if (!type.IsGenericType)
        {
            return name;
        }

        var tick = name.IndexOf('`');
        var bare = tick < 0 ? name : name[..tick];
        var arguments = type.GetGenericArguments();
        if (type.IsNested && type.DeclaringType!.IsGenericType)
        {
            // Arguments belonging to the declaring type were already written with it.
            arguments = arguments.Skip(type.DeclaringType.GetGenericArguments().Length).ToArray();
            if (arguments.Length == 0)
            {
                return bare;
            }
        }

        return bare + "<" + string.Join(", ", arguments.Select(Name)) + ">";
    }
}
