using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;

namespace Tokenmetry.PluginValidator;

/// <summary>
/// 契约包与当前源码程序集的比对结果。<see cref="Differences"/> 为空即同步。
/// </summary>
public sealed record ContractSyncReport(IReadOnlyList<string> Differences)
{
    /// <summary>包内 DLL 与新源码的公开 API 一致。</summary>
    public bool InSync => Differences.Count == 0;
}

/// <summary>
/// 比对 <c>plugin-sdk/contract</c> 里的契约包与当前源码程序集的公开 API。
///
/// 为什么比 API 而不是比字节：插件作者编译时绑定包内 DLL，运行时绑定宿主提供的
/// 同版本程序集（插件包禁止携带契约 DLL）。只要公开 API 与程序集版本一致，两者就能
/// 互相替换；而不同 SDK / 不同提交编出的 PE 字节本来就会不同（InformationalVersion
/// 里还带提交号），逐字节比较会在每次提交后误报。
/// </summary>
public static class ContractSyncVerifier
{
    /// <summary>契约 DLL 在包内的固定相对路径。</summary>
    public const string PackageAssemblyPath = "lib/net10.0/Tokenmetry.Plugin.Abstractions.dll";

    private static readonly BindingFlags DeclaredPublic =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>
    /// 打开 <paramref name="packagePath"/>，取出契约 DLL，与 <paramref name="sourceAssembly"/>
    /// （当前源码编译产物）比对公开 API。
    /// </summary>
    public static ContractSyncReport Verify(string packagePath, Assembly sourceAssembly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(sourceAssembly);

        var packageAssemblyBytes = ReadPackageAssembly(packagePath);
        if (packageAssemblyBytes is null)
        {
            return new ContractSyncReport([$"missing from package: {PackageAssemblyPath}"]);
        }

        var context = new AssemblyLoadContext("tokenmetry-contract-sync", isCollectible: true);
        try
        {
            using var stream = new MemoryStream(packageAssemblyBytes, writable: false);
            Assembly packageAssembly;
            try
            {
                packageAssembly = context.LoadFromStream(stream);
            }
            catch (BadImageFormatException exception)
            {
                return new ContractSyncReport([$"package assembly is not loadable: {exception.Message}"]);
            }

            IReadOnlyList<string> actual;
            try
            {
                actual = Describe(packageAssembly);
            }
            catch (ReflectionTypeLoadException exception)
            {
                var details = string.Join(
                    "; ",
                    exception.LoaderExceptions
                        .Select(loaderException => loaderException?.Message)
                        .Where(message => message is not null));
                return new ContractSyncReport([$"package assembly types are not readable: {details}"]);
            }

            return new ContractSyncReport(Diff(Describe(sourceAssembly), actual));
        }
        finally
        {
            context.Unload();
        }
    }

    private static byte[]? ReadPackageAssembly(string packagePath)
    {
        using var stream = File.OpenRead(packagePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry(PackageAssemblyPath);
        if (entry is null)
        {
            return null;
        }

        using var entryStream = entry.Open();
        using var buffer = new MemoryStream();
        entryStream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// 把程序集的公开 API 展开成一行一条的稳定清单；两份清单用多重集比较。
    /// 只描述公开可见的内容：类型、基类、接口、枚举值、字段、属性、事件、构造与方法。
    /// </summary>
    private static List<string> Describe(Assembly assembly)
    {
        var nullability = new NullabilityInfoContext();
        var name = assembly.GetName();
        var lines = new List<string>
        {
            $"assembly {name.Name} version={name.Version}",
        };

        foreach (var type in assembly.GetExportedTypes().OrderBy(TypeName, StringComparer.Ordinal))
        {
            lines.Add($"type {TypeName(type)} {TypeKind(type)}");

            if (type.BaseType is { } baseType && baseType != typeof(object))
            {
                lines.Add($"  base {TypeName(baseType)}");
            }

            foreach (var implemented in type.GetInterfaces()
                         .Where(i => i.IsPublic || i.IsNestedPublic)
                         .Select(TypeName)
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(i => i, StringComparer.Ordinal))
            {
                lines.Add($"  implements {implemented}");
            }

            if (type.IsEnum)
            {
                lines.Add($"  underlying {TypeName(Enum.GetUnderlyingType(type))}");
                foreach (var value in type.GetFields(BindingFlags.Public | BindingFlags.Static)
                             .Where(f => f.IsLiteral)
                             .OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    var raw = value.GetRawConstantValue();
                    lines.Add(raw is null
                        ? $"  enum-value {value.Name}=null"
                        : $"  enum-value {value.Name}={Convert.ToInt64(raw, CultureInfo.InvariantCulture)}");
                }

                continue;
            }

            foreach (var field in type.GetFields(DeclaredPublic)
                         .Where(f => !f.IsSpecialName)
                         .OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                lines.Add($"  field {FieldSignature(field, nullability)}");
            }

            foreach (var property in type.GetProperties(DeclaredPublic)
                         .OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                lines.Add($"  property {PropertySignature(property, nullability)}");
            }

            foreach (var @event in type.GetEvents(DeclaredPublic)
                         .OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                lines.Add($"  event {TypeName(@event.EventHandlerType!)} {@event.Name}");
            }

            foreach (var constructor in type.GetConstructors(DeclaredPublic)
                         .OrderBy(MethodOrderKey, StringComparer.Ordinal))
            {
                lines.Add($"  ctor {ParameterList(constructor.GetParameters(), nullability)}");
            }

            foreach (var method in type.GetMethods(DeclaredPublic)
                         .Where(m => !IsAccessor(m))
                         .OrderBy(MethodOrderKey, StringComparer.Ordinal))
            {
                lines.Add($"  method {MethodSignature(method, nullability)}");
            }
        }

        return lines;
    }

    private static IReadOnlyList<string> Diff(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual)
    {
        var expectedCounts = Count(expected);
        var actualCounts = Count(actual);
        var differences = new List<string>();

        foreach (var line in expectedCounts.Keys
                     .Union(actualCounts.Keys, StringComparer.Ordinal)
                     .OrderBy(line => line, StringComparer.Ordinal))
        {
            expectedCounts.TryGetValue(line, out var expectedCount);
            actualCounts.TryGetValue(line, out var actualCount);
            if (expectedCount == actualCount)
            {
                continue;
            }

            differences.Add(actualCount < expectedCount
                ? $"missing from package: {line}"
                : $"unexpected in package: {line}");
        }

        return differences;
    }

    private static Dictionary<string, int> Count(IReadOnlyList<string> lines)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            counts.TryGetValue(line, out var count);
            counts[line] = count + 1;
        }

        return counts;
    }

    private static string TypeKind(Type type)
    {
        if (type.IsEnum)
        {
            return "enum";
        }

        if (type.IsValueType)
        {
            return "struct";
        }

        if (type.IsInterface)
        {
            return "interface";
        }

        if (type.IsAbstract && type.IsSealed)
        {
            return "static class";
        }

        return type.IsAbstract ? "abstract class" : "class";
    }

    private static string FieldSignature(FieldInfo field, NullabilityInfoContext nullability)
    {
        var modifiers = field.IsLiteral
            ? "const "
            : field.IsInitOnly
                ? "readonly "
                : string.Empty;
        var isStatic = field.IsStatic ? "static " : string.Empty;
        var suffix = NullabilitySuffix(field.FieldType, nullability.Create(field).ReadState);
        var value = field.IsLiteral
            ? $" = {Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture) ?? "null"}"
            : string.Empty;

        return $"{isStatic}{modifiers}{TypeName(field.FieldType)}{suffix} {field.Name}{value}";
    }

    private static string PropertySignature(PropertyInfo property, NullabilityInfoContext nullability)
    {
        var accessors = new List<string>();
        if (property.GetMethod is not null)
        {
            accessors.Add("get");
        }

        if (property.SetMethod is { } setter)
        {
            accessors.Add(IsInitOnly(setter) ? "init" : "set");
        }

        var isStatic = property.GetMethod?.IsStatic == true || property.SetMethod?.IsStatic == true;
        var suffix = NullabilitySuffix(property.PropertyType, nullability.Create(property).ReadState);

        return $"{(isStatic ? "static " : string.Empty)}{TypeName(property.PropertyType)}{suffix} " +
               $"{property.Name} {{ {string.Join("; ", accessors)}; }}";
    }

    private static string MethodSignature(MethodInfo method, NullabilityInfoContext nullability)
    {
        var modifiers = method.IsStatic
            ? "static "
            : method.IsAbstract
                ? "abstract "
                : method.IsVirtual
                    ? "virtual "
                    : string.Empty;
        var generic = method.IsGenericMethodDefinition
            ? $"<{string.Join(", ", method.GetGenericArguments().Select(argument => argument.Name))}>"
            : string.Empty;
        var suffix = NullabilitySuffix(
            method.ReturnType, nullability.Create(method.ReturnParameter).ReadState);

        return $"{modifiers}{TypeName(method.ReturnType)}{suffix} {method.Name}{generic}" +
               ParameterList(method.GetParameters(), nullability);
    }

    private static string ParameterList(ParameterInfo[] parameters, NullabilityInfoContext nullability)
    {
        if (parameters.Length == 0)
        {
            return "()";
        }

        var rendered = parameters.Select(parameter =>
        {
            var modifier = parameter.IsOut
                ? "out "
                : parameter.ParameterType.IsByRef
                    ? "ref "
                    : string.Empty;
            var suffix = NullabilitySuffix(
                parameter.ParameterType,
                nullability.Create(parameter).ReadState);
            return $"{modifier}{TypeName(parameter.ParameterType)}{suffix} {parameter.Name}";
        });

        return $"({string.Join(", ", rendered)})";
    }

    private static string MethodOrderKey(MethodBase method)
    {
        var generic = method.IsGenericMethodDefinition
            ? $"<{method.GetGenericArguments().Length}>"
            : string.Empty;
        return $"{method.Name}{generic}({string.Join(",", method.GetParameters().Select(p => TypeName(p.ParameterType)))})";
    }

    private static bool IsAccessor(MethodInfo method) =>
        method.IsSpecialName &&
        (method.Name.StartsWith("get_", StringComparison.Ordinal)
         || method.Name.StartsWith("set_", StringComparison.Ordinal)
         || method.Name.StartsWith("add_", StringComparison.Ordinal)
         || method.Name.StartsWith("remove_", StringComparison.Ordinal));

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers()
            .Any(modifier => string.Equals(
                modifier.FullName,
                "System.Runtime.CompilerServices.IsExternalInit",
                StringComparison.Ordinal));

    private static string NullabilitySuffix(Type type, NullabilityState state) =>
        state == NullabilityState.Nullable && !type.IsValueType ? "?" : string.Empty;

    /// <summary>
    /// 输出不带程序集限定名的类型名。跨 <see cref="AssemblyLoadContext"/> 比较时，
    /// <see cref="Type.FullName"/> 会把版本与加载上下文写进泛型实参，必须自己拼。
    /// </summary>
    private static string TypeName(Type type)
    {
        if (type.IsByRef)
        {
            return TypeName(type.GetElementType()!) + "&";
        }

        if (type.IsPointer)
        {
            return TypeName(type.GetElementType()!) + "*";
        }

        if (type.IsArray)
        {
            return TypeName(type.GetElementType()!) + "[]";
        }

        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        var name = type.IsNested
            ? TypeName(type.DeclaringType!) + "+" + type.Name
            : type.Namespace is { Length: > 0 } ns
                ? ns + "." + type.Name
                : type.Name;

        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
        {
            name = name[..tick];
        }

        if (type.IsGenericType)
        {
            name += "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">";
        }

        return name;
    }
}
