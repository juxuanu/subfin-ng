#:package System.Reflection.MetadataLoadContext@10.0.0
// Binary-compatibility check: does every external member/type referenced by a compiled
// plugin DLL exist (same name + signature) in a set of host assemblies?
// usage: dotnet run abicheck.cs <plugin.dll> <hostDir> [<runtimeDir>...]
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

var pluginPath = args[0];
var probe = args.Skip(1).SelectMany(d => Directory.GetFiles(d, "*.dll"))
    .GroupBy(Path.GetFileName).Select(g => g.First()).ToList();
using var mlc = new MetadataLoadContext(new PathAssemblyResolver(probe), "System.Private.CoreLib");
var hostAsms = probe.Select(p => { try { return mlc.LoadFromAssemblyPath(p); } catch { return null; } })
    .Where(a => a != null).ToList();

using var pe = new PEReader(File.OpenRead(pluginPath));
var md = pe.GetMetadataReader();
var prov = new NameProvider(md);

string TypeRefName(TypeReferenceHandle h)
{
    var tr = md.GetTypeReference(h);
    var name = md.GetString(tr.Name);
    var ns = md.GetString(tr.Namespace);
    if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
        return TypeRefName((TypeReferenceHandle)tr.ResolutionScope) + "+" + name;
    return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
}
string ScopeAsm(TypeReferenceHandle h)
{
    var tr = md.GetTypeReference(h);
    return tr.ResolutionScope.Kind switch
    {
        HandleKind.AssemblyReference => md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name),
        HandleKind.TypeReference => ScopeAsm((TypeReferenceHandle)tr.ResolutionScope),
        _ => "?",
    };
}
// Resolve a type in the assembly the plugin references (MetadataLoadContext follows type
// forwarders); only fall back to searching everything when that assembly isn't available.
Type? FindType(string fullName, string? asmName = null)
{
    if (asmName != null)
    {
        try { var t = mlc.LoadFromAssemblyName(asmName).GetType(fullName, false); if (t != null) return t; } catch { }
    }
    foreach (var a in hostAsms)
    {
        try { var t = a!.GetType(fullName, false); if (t != null) return t; } catch { }
    }
    return null;
}
static string N(Type t)
{
    if (t.IsGenericParameter) return (t.DeclaringMethod != null ? "!!" : "!") + t.GenericParameterPosition;
    if (t.IsByRef) return N(t.GetElementType()!) + "&";
    if (t.IsPointer) return N(t.GetElementType()!) + "*";
    if (t.IsSZArray) return N(t.GetElementType()!) + "[]";
    if (t.IsArray) return N(t.GetElementType()!) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
    if (t.IsConstructedGenericType)
        return N(t.GetGenericTypeDefinition()) + "<" + string.Join(",", t.GetGenericArguments().Select(N)) + ">";
    return t.FullName ?? t.Name;
}
IEnumerable<Type> Hierarchy(Type t)
{
    for (var c = t; c != null; c = c.BaseType) yield return c;
    foreach (var i in t.GetInterfaces()) yield return i;
}
const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

var problems = new List<string>();
int checkedMembers = 0, checkedTypes = 0;

// 1. every type reference must resolve
foreach (var h in md.TypeReferences)
{
    var name = TypeRefName(h);
    if (name.StartsWith("<")) continue;
    checkedTypes++;
    if (FindType(name, ScopeAsm(h)) == null) problems.Add($"TYPE MISSING   [{ScopeAsm(h)}] {name}");
}

// 2. every member reference must resolve with identical signature
foreach (var h in md.MemberReferences)
{
    var mr = md.GetMemberReference(h);
    string parentName;
    string? parentAsm = null;
    switch (mr.Parent.Kind)
    {
        case HandleKind.TypeReference: parentName = TypeRefName((TypeReferenceHandle)mr.Parent); parentAsm = ScopeAsm((TypeReferenceHandle)mr.Parent); break;
        case HandleKind.TypeSpecification:
            var spec = md.GetTypeSpecification((TypeSpecificationHandle)mr.Parent).DecodeSignature(prov, null);
            parentName = spec.Contains('<') ? spec[..spec.IndexOf('<')] : spec;
            if (parentName.EndsWith("[]")) continue; // array pseudo-methods (Get/Set/Address)
            break;
        default: continue; // members of the plugin's own types
    }
    var type = FindType(parentName, parentAsm);
    if (type == null) continue; // already reported as TYPE MISSING (or a plugin-local type)
    var name = md.GetString(mr.Name);
    checkedMembers++;

    if (mr.GetKind() == MemberReferenceKind.Field)
    {
        var ft = mr.DecodeFieldSignature(prov, null);
        var ok = Hierarchy(type).SelectMany(t => t.GetFields(All)).Any(f => f.Name == name && N(f.FieldType) == ft);
        if (!ok) problems.Add($"FIELD MISSING  {parentName}::{name} : {ft}");
        continue;
    }

    var sig = mr.DecodeMethodSignature(prov, null);
    var want = $"{sig.ReturnType} {name}<{sig.GenericParameterCount}>({string.Join(",", sig.ParameterTypes)})";
    var candidates = Hierarchy(type)
        .SelectMany(t => name == ".ctor" ? t.GetConstructors(All).Cast<MethodBase>() : t.GetMethods(All).Where(m => m.Name == name))
        .ToList();
    string Sig(MethodBase m) =>
        $"{(m is MethodInfo mi ? N(mi.ReturnType) : "System.Void")} {name}<{(m.IsGenericMethodDefinition ? m.GetGenericArguments().Length : 0)}>({string.Join(",", m.GetParameters().Select(p => N(p.ParameterType)))})";
    if (!candidates.Any(m => Sig(m) == want))
    {
        var have = candidates.Select(Sig).Distinct().ToList();
        problems.Add($"METHOD CHANGED {parentName}::{want}\n{(have.Count == 0 ? "                 (no member with that name)" : string.Join("\n", have.Select(s => "                 now: " + s)))}");
    }
}

// 3. plugin types implementing host interfaces / abstract members must still satisfy them
using var pluginMlcCtx = new MetadataLoadContext(new PathAssemblyResolver(probe.Append(pluginPath)), "System.Private.CoreLib");
var pluginAsm = pluginMlcCtx.LoadFromAssemblyPath(pluginPath);
Type[] pluginTypes;
try { pluginTypes = pluginAsm.GetTypes(); }
catch (ReflectionTypeLoadException e)
{
    pluginTypes = e.Types.Where(t => t != null).ToArray()!;
    foreach (var le in e.LoaderExceptions) problems.Add($"TYPE LOAD      {le?.Message}");
}
foreach (var t in pluginTypes.Where(t => t.IsClass && !t.IsAbstract))
{
    foreach (var iface in t.GetInterfaces().Where(i => i.Assembly != pluginAsm))
    {
        foreach (var im in iface.GetMethods().Where(m => m.IsAbstract))
        {
            var impl = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(m => (m.Name == im.Name || m.Name.EndsWith("." + im.Name))
                          && m.GetParameters().Select(p => N(p.ParameterType)).SequenceEqual(im.GetParameters().Select(p => N(p.ParameterType))));
            if (!impl) problems.Add($"NOT IMPLEMENTED {t.FullName} : {N(iface)}::{im.Name}({string.Join(",", im.GetParameters().Select(p => N(p.ParameterType)))})");
        }
    }
    for (var b = t.BaseType; b != null && b.Assembly != pluginAsm; b = b.BaseType)
    {
        foreach (var am in b.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(m => m.IsAbstract))
        {
            var impl = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(m => m.Name == am.Name && !m.IsAbstract && m.DeclaringType!.Assembly == pluginAsm);
            if (!impl) problems.Add($"NOT IMPLEMENTED {t.FullName} : abstract {N(b)}::{am.Name}");
        }
    }
}

Console.WriteLine($"checked {checkedTypes} type refs, {checkedMembers} member refs, {pluginTypes.Length} plugin types");
Console.WriteLine(problems.Count == 0 ? "NO INCOMPATIBILITIES" : $"{problems.Count} PROBLEM(S):");
foreach (var p in problems.Distinct()) Console.WriteLine("  " + p);

sealed class NameProvider(MetadataReader md) : ISignatureTypeProvider<string, object?>
{
    public string GetPrimitiveType(PrimitiveTypeCode c) => c switch
    {
        PrimitiveTypeCode.Void => "System.Void", PrimitiveTypeCode.Boolean => "System.Boolean", PrimitiveTypeCode.Char => "System.Char",
        PrimitiveTypeCode.SByte => "System.SByte", PrimitiveTypeCode.Byte => "System.Byte", PrimitiveTypeCode.Int16 => "System.Int16",
        PrimitiveTypeCode.UInt16 => "System.UInt16", PrimitiveTypeCode.Int32 => "System.Int32", PrimitiveTypeCode.UInt32 => "System.UInt32",
        PrimitiveTypeCode.Int64 => "System.Int64", PrimitiveTypeCode.UInt64 => "System.UInt64", PrimitiveTypeCode.Single => "System.Single",
        PrimitiveTypeCode.Double => "System.Double", PrimitiveTypeCode.String => "System.String", PrimitiveTypeCode.IntPtr => "System.IntPtr",
        PrimitiveTypeCode.UIntPtr => "System.UIntPtr", PrimitiveTypeCode.Object => "System.Object", PrimitiveTypeCode.TypedReference => "System.TypedReference",
        _ => c.ToString(),
    };
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k)
    {
        var td = r.GetTypeDefinition(h);
        var n = r.GetString(td.Name);
        if (td.IsNested) return GetTypeFromDefinition(r, td.GetDeclaringType(), k) + "+" + n;
        var ns = r.GetString(td.Namespace);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k)
    {
        var tr = r.GetTypeReference(h);
        var n = r.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference) return GetTypeFromReference(r, (TypeReferenceHandle)tr.ResolutionScope, k) + "+" + n;
        var ns = r.GetString(tr.Namespace);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }
    public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) => r.GetTypeSpecification(h).DecodeSignature(this, c);
    public string GetSZArrayType(string e) => e + "[]";
    public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', s.Rank - 1) + "]";
    public string GetByReferenceType(string e) => e + "&";
    public string GetPointerType(string e) => e + "*";
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
    public string GetGenericTypeParameter(object? c, int i) => "!" + i;
    public string GetGenericMethodParameter(object? c, int i) => "!!" + i;
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetModifiedType(string m, string u, bool req) => u;
    public string GetPinnedType(string e) => e;
}
