using System.Reflection;

namespace CadMcp.Core;

/// <summary>
/// Late-bound access to optional vendor APIs (Civil 3D, Map 3D) that are not referenced at compile time.
/// Vendor classes hide inherited members with <c>new</c> (for example a set-only <c>Name</c>), declare
/// <c>uint</c> or <c>ref</c> parameters and overload methods, so plain <see cref="Type.GetProperty(string)"/>
/// and exact type checks are not reliable.
/// </summary>
public static class VendorReflection
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    /// <summary>The public readable, non-indexed instance property declared closest to the runtime type.</summary>
    public static PropertyInfo? Readable(Type type, string name) => Find(type, name, p => p.GetMethod is { IsPublic: true });

    /// <summary>The public writable, non-indexed instance property declared closest to the runtime type.</summary>
    public static PropertyInfo? Writable(Type type, string name) => Find(type, name, p => p.SetMethod is { IsPublic: true });

    private static PropertyInfo? Find(Type type, string name, Func<PropertyInfo, bool> usable)
    {
        for (var current = type; current is not null; current = current.BaseType)
            foreach (var property in current.GetProperties(Declared))
                if (property.Name == name && property.GetIndexParameters().Length == 0 && usable(property)) return property;
        return null;
    }

    /// <summary>
    /// A readable message for a vendor exception. Map 3D exceptions often have an empty Message and carry
    /// the reason in ErrorCode; reflection wraps the real exception in TargetInvocationException.
    /// </summary>
    public static string Describe(Exception error)
    {
        while (error is TargetInvocationException { InnerException: { } inner }) error = inner;
        string message = error.Message;
        var code = TryGet(error, "ErrorCode") ?? TryGet(error, "ErrorStatus");
        if (string.IsNullOrWhiteSpace(message)) message = error.GetType().Name;
        return code is null || message.Contains(code.ToString() ?? "", StringComparison.Ordinal) ? message : message + " (" + code + ")";
    }

    /// <summary>Reads a property; returns null when it is absent or its getter fails.</summary>
    public static object? TryGet(object instance, string name)
    {
        try { return Readable(instance.GetType(), name)?.GetValue(instance); }
        catch (Exception e) when (e is TargetInvocationException or InvalidOperationException or NotSupportedException or ArgumentException) { return null; }
    }

    /// <summary>Bounded JSON-safe scalar: short text, finite number, Boolean or enum name.</summary>
    public static object? Scalar(object? value) => value switch
    {
        string s => s.Length <= 500 ? s : s[..500],
        double d => double.IsFinite(d) ? d : null,
        float f => float.IsFinite(f) ? f : null,
        bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal => value,
        Enum e => e.ToString(),
        _ => null
    };

    /// <summary>Scalar properties by name, skipping absent, failing and non-scalar values.</summary>
    public static Dictionary<string, object> Scalars(object instance, IEnumerable<string> names)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var name in names)
            if (Scalar(TryGet(instance, name)) is { } value) result[name] = value;
        return result;
    }

    /// <summary>
    /// Scalars of objects returned by parameterless statistics methods, such as Civil surface
    /// GetGeneralProperties/GetTinProperties. Missing or failing methods are reported, not thrown.
    /// </summary>
    public static Dictionary<string, object> Statistics(object instance, IEnumerable<string> methods)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var name in methods)
        {
            var method = instance.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            if (method is null) continue;
            try
            {
                var value = method.Invoke(instance, null);
                if (value is null) continue;
                var scalars = value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is { IsPublic: true })
                    .Select(p => p.Name).Distinct().Take(60);
                result[name] = Scalars(value, scalars);
            }
            catch (TargetInvocationException e) { result[name] = new { error = Describe(e) }; }
        }
        return result;
    }

    /// <summary>
    /// Converts an argument to a parameter type without losing information. Score 0 is an exact match,
    /// 1 a lossless numeric conversion (for example int literal to uint).
    /// </summary>
    public static bool TryConvert(object? value, Type target, out object? converted, out int score)
    {
        if (target.IsByRef) target = target.GetElementType()!;
        converted = value; score = 0;
        if (value is null) return !target.IsValueType || Nullable.GetUnderlyingType(target) is not null;
        if (target.IsInstanceOfType(value)) return true;
        var underlying = Nullable.GetUnderlyingType(target) ?? target;
        if (underlying.IsInstanceOfType(value)) return true;
        if (!IsNumber(value.GetType()) || !IsNumber(underlying)) return false;
        bool integralSource = IsIntegral(value.GetType());
        if (IsIntegral(underlying) && !integralSource) return false;
        try { converted = Convert.ChangeType(value, underlying, System.Globalization.CultureInfo.InvariantCulture); score = 1; return true; }
        catch (OverflowException) { return false; }
    }

    private static bool IsIntegral(Type t) => t == typeof(sbyte) || t == typeof(byte) || t == typeof(short) || t == typeof(ushort) ||
        t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong);
    private static bool IsNumber(Type t) => IsIntegral(t) || t == typeof(float) || t == typeof(double) || t == typeof(decimal);

    /// <summary>
    /// Invokes the single best overload of a static (instanceOrType is a Type) or instance method.
    /// Values written by ref/out parameters are copied back into <paramref name="args"/>.
    /// </summary>
    public static object? Invoke(object instanceOrType, string method, params object?[] args)
    {
        var type = instanceOrType as Type ?? instanceOrType.GetType();
        var flags = BindingFlags.Public | (instanceOrType is Type ? BindingFlags.Static : BindingFlags.Instance);
        var candidates = new List<(MethodInfo Method, object?[] Arguments, int Score)>();
        foreach (var candidate in type.GetMethods(flags).Where(m => m.Name == method && m.GetParameters().Length == args.Length && !m.ContainsGenericParameters))
        {
            var parameters = candidate.GetParameters(); var converted = new object?[args.Length]; int total = 0; bool usable = true;
            for (int i = 0; i < args.Length && usable; i++)
            {
                usable = TryConvert(args[i], parameters[i].ParameterType, out converted[i], out int score);
                total += score;
            }
            if (usable) candidates.Add((candidate, converted, total));
        }
        if (candidates.Count == 0) throw new CadFault("VERTICAL_API_UNAVAILABLE", type.FullName + "." + method + " has no supported signature for the supplied arguments");
        int best = candidates.Min(c => c.Score);
        var chosen = candidates.Where(c => c.Score == best).ToArray();
        if (chosen.Length != 1) throw new CadFault("VERTICAL_API_UNAVAILABLE", type.FullName + "." + method + " is ambiguous for the supplied arguments");
        try
        {
            var result = chosen[0].Method.Invoke(instanceOrType is Type ? null : instanceOrType, chosen[0].Arguments);
            var parameters = chosen[0].Method.GetParameters();
            for (int i = 0; i < args.Length; i++) if (parameters[i].ParameterType.IsByRef) args[i] = chosen[0].Arguments[i];
            return result;
        }
        catch (TargetInvocationException e) { throw new CadFault("VERTICAL_API_ERROR", Describe(e)); }
    }

    /// <summary>Sets a writable property, converting numbers losslessly; rejects unknown or incompatible properties.</summary>
    public static void Set(object instance, string name, object? value)
    {
        var property = Writable(instance.GetType(), name);
        if (property is null || !TryConvert(value, property.PropertyType, out var converted, out _))
            throw new CadFault("VERTICAL_PROPERTY_UNAVAILABLE", name);
        try { property.SetValue(instance, converted); }
        catch (TargetInvocationException e) { throw new CadFault("VERTICAL_API_ERROR", Describe(e)); }
    }

    /// <summary>Reads an indexer with one parameter, such as Records[int] or Tables[string].</summary>
    public static object Index(object instance, object index)
    {
        foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var parameters = property.GetIndexParameters();
            if (parameters.Length != 1 || property.GetMethod is not { IsPublic: true }) continue;
            if (!TryConvert(index, parameters[0].ParameterType, out var converted, out _)) continue;
            try { return property.GetValue(instance, [converted]) ?? throw new CadFault("VERTICAL_INDEX_UNAVAILABLE", instance.GetType().Name); }
            catch (TargetInvocationException e) { throw new CadFault("VERTICAL_API_ERROR", Describe(e)); }
        }
        throw new CadFault("VERTICAL_INDEX_UNAVAILABLE", instance.GetType().Name);
    }

    /// <summary>
    /// A neutral argument for an optional-looking API parameter: false, numeric zero of the exact parameter
    /// type (a Map "offset" is uint) or the declared default. Other parameters must come from the caller.
    /// </summary>
    public static bool TryDefault(ParameterInfo parameter, out object? value)
    {
        var type = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
        value = null;
        if (type == typeof(bool)) { value = false; return true; }
        if (IsNumber(type)) { value = Convert.ChangeType(0, type, System.Globalization.CultureInfo.InvariantCulture); return true; }
        if (parameter.HasDefaultValue) { value = parameter.DefaultValue; return true; }
        return false;
    }
}
