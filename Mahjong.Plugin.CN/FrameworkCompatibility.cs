using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Mahjong.Plugin.CN;

/// <summary>Metadata-only compatibility contract. Never invokes native methods or reads game memory.</summary>
internal static class FrameworkCompatibility
{
    internal const string Profile = "cn-20260915-api15-v1";
    private const string Resource = "Mahjong.Plugin.CN.framework-contract.json";
    private static readonly HashSet<string> Assemblies = new(StringComparer.Ordinal)
    {
        "Dalamud", "Dalamud.Common", "Dalamud.Bindings.ImGui", "FFXIVClientStructs", "InteropGenerator.Runtime",
    };
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static readonly Lazy<string?> CachedError = new(() => CheckLoaded());
    internal static string? Error => CachedError.Value;

    internal sealed record Contract(int Schema, string Profile, TypeContract[] Types);
    internal sealed record TypeContract(string Assembly, string Name, string[] Required);

    internal static Contract ReadContract()
    {
        using var stream = typeof(FrameworkCompatibility).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidDataException("Missing framework contract.");
        return JsonSerializer.Deserialize<Contract>(stream) ?? throw new InvalidDataException("Empty framework contract.");
    }

    internal static string? CheckLoaded()
    {
        try
        {
            return Validate(ReadContract(), entry =>
            {
                var type = Type.GetType($"{entry.Name}, {entry.Assembly}", throwOnError: false);
                return type is null ? null : Describe(type, includeAllMembers: true);
            });
        }
        catch (Exception ex) { return $"VERSION_CONTRACT：无法核对框架接口（{ex.GetType().Name}）；已停止读取和操作。"; }
    }

    internal static string? Validate(Contract contract, Func<TypeContract, IReadOnlyCollection<string>?> describe)
    {
        if (contract.Schema != 1 || contract.Profile != Profile || contract.Types.Length == 0)
            return "VERSION_CONTRACT：兼容性依据缺失或格式不支持。";
        foreach (var entry in contract.Types)
        {
            var actual = describe(entry);
            if (actual is null) return $"VERSION_CONTRACT：缺少框架类型 {entry.Name}。";
            var available = actual.ToHashSet(StringComparer.Ordinal);
            var missing = entry.Required.FirstOrDefault(x => !available.Contains(x));
            if (missing is not null)
                return $"VERSION_CONTRACT：{entry.Name} 的接口或结构已改变（{missing}）；需要适配此更新。";
        }
        return null;
    }

    // Maintainer export and regression coverage use the plugin's real CLR references.
    // The shipped contract is fixed, reviewed and embedded; never regenerated on a user's machine.
    internal static Contract CaptureRequired(Assembly plugin)
    {
        using var stream = File.OpenRead(plugin.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var types = new Dictionary<Type, HashSet<string>>();
        bool Relevant(Type t) => Assemblies.Contains(t.Assembly.GetName().Name ?? "");
        bool FrameworkReference(EntityHandle scope)
        {
            if (scope.Kind == HandleKind.TypeSpecification)
            {
                var blob = metadata.GetBlobReader(metadata.GetTypeSpecification((TypeSpecificationHandle)scope).Signature);
                var code = blob.ReadSignatureTypeCode();
                if (code == SignatureTypeCode.GenericTypeInstance) code = blob.ReadSignatureTypeCode();
                return code == SignatureTypeCode.TypeHandle && FrameworkReference(blob.ReadTypeHandle());
            }
            while (scope.Kind == HandleKind.TypeReference) scope = metadata.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
            return scope.Kind == HandleKind.AssemblyReference &&
                Assemblies.Contains(metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)scope).Name));
        }
        void AddType(Type t)
        {
            if (t.HasElementType) { AddType(t.GetElementType()!); return; }
            if (t.IsGenericParameter || !Relevant(t)) return;
            if (t.IsConstructedGenericType)
            {
                foreach (var arg in t.GenericTypeArguments) AddType(arg);
                t = t.GetGenericTypeDefinition();
            }
            if (types.ContainsKey(t)) return;
            types[t] = Describe(t, includeAllMembers: false).ToHashSet(StringComparer.Ordinal);
            // By-value nested layouts and enum values affect offsets and meaning too.
            if (NativeLayout(t) && t.IsValueType)
                foreach (var f in t.GetFields(Declared).Where(f => !f.IsStatic))
                {
                    if (f.FieldType.IsValueType) AddType(f.FieldType);
                    if (f.FieldType.IsPointer && f.FieldType.GetElementType()!.Name.EndsWith("VirtualTable", StringComparison.Ordinal))
                        AddType(f.FieldType.GetElementType()!);
                }
        }
        foreach (var handle in metadata.TypeReferences)
        {
            if (!FrameworkReference(handle)) continue;
            AddType(plugin.ManifestModule.ResolveType(MetadataTokens.GetToken(handle)));
        }
        foreach (var handle in metadata.MemberReferences)
        {
            if (!FrameworkReference(metadata.GetMemberReference(handle).Parent)) continue;
            var member = plugin.ManifestModule.ResolveMember(MetadataTokens.GetToken(handle));
            if (member?.DeclaringType is not { } type || !Relevant(type)) continue;
            AddType(type);
            if (type.IsConstructedGenericType)
            {
                type = type.GetGenericTypeDefinition();
                member = type.GetMembers(Declared).First(m => m.MetadataToken == member.MetadataToken);
            }
            foreach (var line in DescribeMember(member)) types[type].Add(line);
        }
        return new(1, Profile, types.OrderBy(p => p.Key.Assembly.GetName().Name, StringComparer.Ordinal)
            .ThenBy(p => p.Key.FullName, StringComparer.Ordinal)
            .Select(p => new TypeContract(p.Key.Assembly.GetName().Name!, p.Key.FullName!, p.Value.Order(StringComparer.Ordinal).ToArray())).ToArray());
    }

    internal static IReadOnlyCollection<string> Describe(Type type, bool includeAllMembers)
    {
        var lines = new HashSet<string>(StringComparer.Ordinal)
        {
            $"type:{(type.IsEnum ? "enum" : type.IsValueType ? "struct" : type.IsInterface ? "interface" : "class")}:{Name(type.BaseType)}",
        };
        bool native = NativeLayout(type);
        if (native && type.IsValueType && !type.IsEnum)
        {
            var layout = type.StructLayoutAttribute!;
            lines.Add($"layout:{layout.Value}:{layout.Size}:{layout.Pack}");
            if (layout.Value == LayoutKind.Sequential)
                lines.Add("sequential-fields:" + string.Join(",", type.GetFields(Declared).Where(f => !f.IsStatic)
                    .OrderBy(f => f.MetadataToken).Select(f => f.Name)));
            foreach (var field in type.GetFields(Declared).Where(f => !f.IsStatic))
                foreach (var line in DescribeMember(field)) lines.Add(line);
            foreach (var attribute in type.GetCustomAttributesData().Where(a =>
                         a.AttributeType.FullName == "System.Runtime.CompilerServices.InlineArrayAttribute"))
                lines.Add("inline-array:" + string.Join(";", attribute.ConstructorArguments.Select(Argument)));
        }
        if (type.IsEnum)
        {
            lines.Add($"enum:{Name(Enum.GetUnderlyingType(type))}");
            foreach (var field in type.GetFields(Declared).Where(f => f.IsLiteral))
                lines.Add($"constant:{field.Name}:{Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture)}");
        }
        if (includeAllMembers)
            foreach (var member in type.GetMembers(Declared))
                foreach (var line in DescribeMember(member)) lines.Add(line);
        return lines;
    }

    private static IEnumerable<string> DescribeMember(MemberInfo member)
    {
        string? signature = member switch
        {
            FieldInfo f => $"field:{f.Name}:{Name(f.FieldType)}:{f.IsStatic}:{f.GetCustomAttribute<FieldOffsetAttribute>()?.Value.ToString(CultureInfo.InvariantCulture) ?? "sequential"}",
            MethodBase m => $"method:{m.Name}`{(m.IsGenericMethod ? m.GetGenericArguments().Length : 0)}:{m.IsStatic}:{(m is MethodInfo mi ? Name(mi.ReturnType) : "void")}({string.Join(",", m.GetParameters().Select(p => Name(p.ParameterType)))})",
            _ => null,
        };
        if (signature is null) yield break;
        yield return signature;
        foreach (var attribute in member.GetCustomAttributesData().Where(a =>
                     a.AttributeType.Namespace == "InteropGenerator.Runtime.Attributes"))
            yield return $"attribute:{signature}:{attribute.AttributeType.Name}:{string.Join(";", attribute.ConstructorArguments.Select(Argument))}:" +
                string.Join(";", attribute.NamedArguments.OrderBy(a => a.MemberName, StringComparer.Ordinal).Select(a => a.MemberName + "=" + Argument(a.TypedValue)));
    }

    private static string Argument(CustomAttributeTypedArgument a) => a.Value is IEnumerable<CustomAttributeTypedArgument> items
        ? "[" + string.Join(",", items.Select(Argument)) + "]"
        : a.Value is Type t ? Name(t) : Convert.ToString(a.Value, CultureInfo.InvariantCulture) ?? "null";

    private static bool NativeLayout(Type type) => type.Assembly.GetName().Name is "FFXIVClientStructs" or "Dalamud.Bindings.ImGui";

    private static string Name(Type? type)
    {
        if (type is null) return "none";
        if (type.IsFunctionPointer) return "fnptr:" + string.Join(",", type.GetFunctionPointerCallingConventions().Select(Name)) +
            ":" + Name(type.GetFunctionPointerReturnType()) + "(" + string.Join(",", type.GetFunctionPointerParameterTypes().Select(Name)) + ")";
        if (type.IsGenericParameter) return (type.DeclaringMethod is null ? "!" : "!!") + type.GenericParameterPosition;
        if (type.HasElementType) return Name(type.GetElementType()) + (type.IsPointer ? "*" : type.IsByRef ? "&" : "[" + new string(',', type.GetArrayRank() - 1) + "]");
        if (type.IsGenericType) return type.GetGenericTypeDefinition().FullName + "<" + string.Join(",", type.GetGenericArguments().Select(Name)) + ">";
        return type.FullName ?? type.Name;
    }
}
