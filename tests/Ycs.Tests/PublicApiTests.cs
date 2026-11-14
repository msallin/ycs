#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Renders the public surface of Ycs.dll (every public type and every public or protected member,
/// with signatures, sorted) and compares it with PublicApi.txt, so that every API change shows up
/// as a diff of that file. Set YCS_ACCEPT_API=1 to write the current surface into the file.
/// </summary>
[TestClass]
public class PublicApiTests
{
    private const string AcceptVariable = "YCS_ACCEPT_API";

    [TestMethod]
    public void PublicApi_MatchesBaseline()
    {
        var path = Path.Combine(SourceDirectory(), "PublicApi.txt");
        var actual = PublicApi.Render(typeof(YDoc).Assembly);

        if (Environment.GetEnvironmentVariable(AcceptVariable) == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        var expected = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : "";
        if (expected == actual)
        {
            return;
        }

        var expectedLines = WithTypes(expected);
        var actualLines = WithTypes(actual);
        var removed = expectedLines.Except(actualLines, StringComparer.Ordinal).Select(l => "- " + l);
        var added = actualLines.Except(expectedLines, StringComparer.Ordinal).Select(l => "+ " + l);
        Assert.Fail(
            $"The public API of Ycs.dll differs from tests/Ycs.Tests/PublicApi.txt:{Environment.NewLine}" +
            $"{string.Join(Environment.NewLine, removed.Concat(added))}{Environment.NewLine}" +
            $"If the change is intended, accept it with `{AcceptVariable}=1 dotnet test tests/Ycs.Tests --filter PublicApi_MatchesBaseline` " +
            "and commit PublicApi.txt (an API break before 1.0 is fine, but it should be visible in the diff).");
    }

    /// <summary>Each member line prefixed with its type's header, so a changed member says where it is.</summary>
    private static List<string> WithTypes(string api)
    {
        var lines = new List<string>();
        var type = "";
        foreach (var line in api.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith(' '))
            {
                lines.Add($"{type} :: {line.Trim()}");
            }
            else
            {
                type = line;
                lines.Add(line);
            }
        }

        return lines;
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}

/// <summary>A C#-like, one-line-per-member rendering of an assembly's public surface.</summary>
internal static class PublicApi
{
    private static readonly NullabilityInfoContext Nullability = new();

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static string Render(Assembly assembly)
    {
        var text = new StringBuilder();
        foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            text.Append(TypeHeader(type)).Append('\n');
            foreach (var member in Members(type).Order(StringComparer.Ordinal))
            {
                text.Append("    ").Append(member).Append('\n');
            }
        }

        return text.ToString();
    }

    private static string TypeHeader(Type type)
    {
        string kind;
        if (type.IsEnum)
        {
            kind = "enum";
        }
        else if (type.IsInterface)
        {
            kind = "interface";
        }
        else if (type.IsValueType)
        {
            kind = (type.IsDefined(typeof(IsReadOnlyAttribute)) ? "readonly " : "") + "struct";
        }
        else if (typeof(Delegate).IsAssignableFrom(type))
        {
            kind = "delegate";
        }
        else
        {
            kind = (type.IsAbstract && type.IsSealed ? "static " : type.IsAbstract ? "abstract " : type.IsSealed ? "sealed " : "") + "class";
        }

        var bases = new List<string>();
        if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType) && baseType != typeof(Enum)
            && baseType != typeof(MulticastDelegate))
        {
            bases.Add(Name(baseType));
        }

        if (type.IsEnum)
        {
            bases.Add(Name(Enum.GetUnderlyingType(type)));
        }
        else if (!typeof(Delegate).IsAssignableFrom(type))
        {
            // Only the interfaces this type adds, not those its base type already has.
            var inherited = type.BaseType?.GetInterfaces() ?? [];
            bases.AddRange(type.GetInterfaces().Except(inherited).Where(i => i.IsPublic || i.IsNestedPublic).Select(i => Name(i)).Order(StringComparer.Ordinal));
        }

        return $"{kind} {Name(type, qualified: true)}{Constraints(type.IsGenericTypeDefinition ? type.GetGenericArguments() : [])}{(bases.Count > 0 ? " : " + string.Join(", ", bases) : "")}";
    }

    private static IEnumerable<string> Members(Type type)
    {
        if (type.IsEnum)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                yield return $"{field.Name} = {Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture)}";
            }

            yield break;
        }

        if (typeof(Delegate).IsAssignableFrom(type))
        {
            yield return Method(type.GetMethod("Invoke")!);
            yield break;
        }

        foreach (var member in type.GetMembers(Declared))
        {
            var line = member switch
            {
                ConstructorInfo c when Visible(c) => $"{Modifiers(c)}{type.Name.Split('`')[0]}({Parameters(c.GetParameters())})",
                MethodInfo m when Visible(m) && (!m.IsSpecialName || m.Name.StartsWith("op_", StringComparison.Ordinal)) => Method(m),
                PropertyInfo p when Visible(p.GetMethod) || Visible(p.SetMethod) => Property(p),
                FieldInfo f when Visible(f) => Field(f),
                EventInfo e when Visible(e.AddMethod) => $"{Modifiers(e.AddMethod!)}event {Name(e.EventHandlerType!, Nullability.Create(e))} {e.Name}",
                Type nested when nested.IsNestedPublic || nested.IsNestedFamily || nested.IsNestedFamORAssem => null, // rendered as a type of its own
                _ => null,
            };
            if (line is not null)
            {
                yield return line;
            }
        }
    }

    private static bool Visible(MethodBase? m) => m is not null && (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly);

    private static bool Visible(FieldInfo f) => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly;

    private static string Access(MethodBase m) => m.IsPublic ? "" : "protected ";

    private static string Modifiers(MethodBase m)
    {
        var modifiers = Access(m);
        if (m.IsStatic)
        {
            return modifiers + "static ";
        }

        if (m is MethodInfo method && method.GetBaseDefinition() != method)
        {
            return modifiers + (m.IsFinal ? "sealed override " : "override ");
        }

        if (m.IsAbstract && !m.DeclaringType!.IsInterface)
        {
            return modifiers + "abstract ";
        }

        if (m.IsVirtual && !m.IsFinal && !m.DeclaringType!.IsInterface)
        {
            return modifiers + "virtual ";
        }

        return modifiers;
    }

    private static string Method(MethodInfo m)
    {
        var generics = m.IsGenericMethodDefinition ? $"<{string.Join(", ", m.GetGenericArguments().Select(a => a.Name))}>" : "";
        var returns = Name(m.ReturnType, Nullability.Create(m.ReturnParameter));
        return $"{Modifiers(m)}{returns} {m.Name}{generics}({Parameters(m.GetParameters())}){Constraints(m.IsGenericMethodDefinition ? m.GetGenericArguments() : [])}";
    }

    private static string Property(PropertyInfo p)
    {
        var accessor = Visible(p.GetMethod) ? p.GetMethod! : p.SetMethod!;
        var index = p.GetIndexParameters();
        var name = index.Length > 0 ? $"this[{Parameters(index)}]" : p.Name;
        var get = Visible(p.GetMethod) ? Access(p.GetMethod!) + "get; " : "";
        var set = Visible(p.SetMethod)
            ? Access(p.SetMethod!) + (p.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)) ? "init; " : "set; ")
            : "";
        return $"{Modifiers(accessor)}{Name(p.PropertyType, Nullability.Create(p))} {name} {{ {get}{set}}}";
    }

    private static string Field(FieldInfo f)
    {
        var access = f.IsPublic ? "" : "protected ";
        var type = Name(f.FieldType, Nullability.Create(f));
        if (f.IsLiteral)
        {
            return $"{access}const {type} {f.Name} = {Literal(f.GetRawConstantValue())}";
        }

        return $"{access}{(f.IsStatic ? "static " : "")}{(f.IsInitOnly ? "readonly " : "")}{type} {f.Name}";
    }

    private static string Parameters(ParameterInfo[] parameters) => string.Join(", ", parameters.Select(p =>
    {
        var prefix = p.IsDefined(typeof(ParamArrayAttribute)) ? "params "
            : p.ParameterType.IsByRef ? (p.IsOut ? "out " : p.IsIn ? "in " : "ref ")
            : "";
        var type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
        var defaultValue = p.HasDefaultValue ? " = " + Literal(p.RawDefaultValue) : "";
        return $"{prefix}{Name(type, Nullability.Create(p))} {p.Name}{defaultValue}";
    }));

    private static string Constraints(Type[] arguments)
    {
        var clauses = new List<string>();
        foreach (var argument in arguments)
        {
            var constraints = new List<string>();
            var attributes = argument.GenericParameterAttributes;
            if (attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint))
            {
                constraints.Add("class");
            }

            if (attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
            {
                constraints.Add("struct");
            }

            constraints.AddRange(argument.GetGenericParameterConstraints().Where(c => c != typeof(ValueType)).Select(c => Name(c)));
            if (attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint) && !attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
            {
                constraints.Add("new()");
            }

            if (constraints.Count > 0)
            {
                clauses.Add($" where {argument.Name} : {string.Join(", ", constraints)}");
            }
        }

        return string.Concat(clauses);
    }

    private static string Literal(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        char c => $"'{c}'",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };

    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(void)] = "void",
        [typeof(object)] = "object",
        [typeof(string)] = "string",
        [typeof(bool)] = "bool",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
        [typeof(decimal)] = "decimal",
        [typeof(char)] = "char",
    };

    /// <summary>A type's C# name; with nullability information, reference types read as nullable get a '?'.</summary>
    private static string Name(Type type, NullabilityInfo? nullability = null) => Name(type, nullability, qualified: false);

    private static string Name(Type type, bool qualified) => Name(type, null, qualified);

    private static string Name(Type type, NullabilityInfo? nullability, bool qualified)
    {
        var nullable = nullability?.ReadState == NullabilityState.Nullable && !type.IsValueType ? "?" : "";
        if (type.IsGenericParameter)
        {
            return type.Name + nullable;
        }

        if (Keywords.TryGetValue(type, out var keyword))
        {
            return keyword + nullable;
        }

        if (type.IsArray)
        {
            var rank = type.GetArrayRank();
            return $"{Name(type.GetElementType()!, nullability?.ElementType)}[{new string(',', rank - 1)}]{nullable}";
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Name(underlying, nullability?.GenericTypeArguments.FirstOrDefault()) + "?";
        }

        var name = type.Name.Split('`')[0];
        var owner = type.IsNested ? Name(type.DeclaringType!, qualified) + "." : qualified || type.Namespace is not "Ycs" ? type.Namespace + "." : "";
        if (!type.IsGenericType)
        {
            return owner + name + nullable;
        }

        // Generic arguments of a nested type include its declaring type's; show only its own.
        var arguments = type.GetGenericArguments();
        var own = type.IsNested ? arguments.Skip(type.DeclaringType!.GetGenericArguments().Length).ToArray() : arguments;
        if (own.Length == 0)
        {
            return owner + name + nullable;
        }

        var argumentNullability = nullability?.GenericTypeArguments;
        var names = own.Select((a, i) => Name(a, argumentNullability is { Length: > 0 } ? argumentNullability[arguments.Length - own.Length + i] : null));
        return $"{owner}{name}<{string.Join(", ", names)}>{nullable}";
    }
}
