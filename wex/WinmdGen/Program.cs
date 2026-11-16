// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// WinmdGen: reads a WinRT metadata file (Windows.winmd) and emits C# ABI code for the runtime classes named on the
// command line: for every interface a class implements (its own, its base classes', their required interfaces) and
// for its factory, statics and composable interfaces:
//   * the IID,
//   * an ABI interface (I<Name>_Abi) whose methods take the raw ABI parameters and by default log the call and return
//     E_NOTIMPL, so an implementation overrides only what it needs,
//   * a vtable of [UnmanagedCallersOnly] thunks that dispatch to the managed object behind a CCW (Ccw.Target),
//   * a caller (<Name>_Call) with one static method per vtable slot, for calling native objects.
// Delegates named with --delegate get a caller only (Invoke at slot 3). Structs and enums used in signatures are
// emitted as blittable structs and their underlying integer types.
//
// usage: WinmdGen <Windows.winmd[;more.winmd]> <out.cs> <namespace> [--class Name]... [--interface Name]... [--delegate Name]...

using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

if (args.Length < 3) { Console.Error.WriteLine("usage: WinmdGen <winmd> <out.cs> <namespace> [--class N] [--interface N] [--delegate N]"); return 2; }
var gen = new Generator(args[0]);
for (int i = 3; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--class": gen.AddClass(args[i + 1]); break;
        case "--interface": gen.AddInterface(args[i + 1]); break;
        case "--delegate": gen.AddDelegate(args[i + 1]); break;
        default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
    }
}
File.WriteAllText(args[1], gen.Emit(args[2]));
Console.WriteLine($"{gen.InterfaceCount} interfaces, {gen.MethodCount} methods, {gen.StructCount} structs, {gen.ClassCount} classes");
return 0;

sealed class Generator
{
    /* the metadata file of the type being processed: every type is read from its own file (Windows.winmd, WinUI 2's
     * Microsoft.UI.Xaml.winmd), handles are only meaningful there */
    MetadataReader md;
    readonly Dictionary<string, (MetadataReader R, TypeDefinitionHandle H)> types = new();
    readonly SortedDictionary<string, TypeDefinitionHandle> interfaces = new(StringComparer.Ordinal);
    readonly SortedDictionary<string, TypeDefinitionHandle> delegates = new(StringComparer.Ordinal);
    readonly SortedDictionary<string, TypeDefinitionHandle> structs = new(StringComparer.Ordinal);
    readonly SortedDictionary<string, ClassInfo> classes = new(StringComparer.Ordinal);
    public int InterfaceCount => interfaces.Count;
    public int StructCount => structs.Count;
    public int ClassCount => classes.Count;
    public int MethodCount;

    sealed class ClassInfo
    {
        public List<string> Interfaces = new();      // QI table: every interface instances answer to
        public string? DefaultInterface;
        public string? Base;
        public List<string> Factories = new();       // activatable with parameters
        public List<string> Statics = new();
        public List<string> Composable = new();
        public bool Activatable;                     // default constructor (IActivationFactory.ActivateInstance)
    }

    /* paths: one or more metadata files, separated by ';' (a type defined in two files is taken from the first) */
    public Generator(string paths)
    {
        md = null!;
        foreach (var path in paths.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var reader = new PEReader(File.OpenRead(path)).GetMetadataReader();
            md ??= reader;
            foreach (var h in reader.TypeDefinitions)
            {
                var t = reader.GetTypeDefinition(h);
                types.TryAdd(reader.GetString(t.Namespace) + "." + reader.GetString(t.Name), (reader, h));
            }
        }
    }

    TypeDefinitionHandle Find(string name) =>
        types.TryGetValue(name, out var t) ? t.H : throw new InvalidOperationException($"type {name} not found");

    /* run with the metadata file of a type current */
    T In<T>(string name, Func<T> f)
    {
        var saved = md;
        if (types.TryGetValue(name, out var t)) md = t.R;
        try { return f(); }
        finally { md = saved; }
    }

    void In(string name, Action f) => In<object?>(name, () => { f(); return null; });

    string FullName(EntityHandle h) => FullName(md, h);

    static string FullName(MetadataReader md, EntityHandle h)
    {
        switch (h.Kind)
        {
        case HandleKind.TypeDefinition:
            var d = md.GetTypeDefinition((TypeDefinitionHandle)h);
            return md.GetString(d.Namespace) + "." + md.GetString(d.Name);
        case HandleKind.TypeReference:
            var r = md.GetTypeReference((TypeReferenceHandle)h);
            return md.GetString(r.Namespace) + "." + md.GetString(r.Name);
        case HandleKind.TypeSpecification:
            return "<generic>";
        }
        throw new InvalidOperationException(h.Kind.ToString());
    }

    static string Short(string full) => full[(full.LastIndexOf('.') + 1)..].Replace('`', '_');
    static string Ident(string full) => full.Replace("Windows.", "").Replace('.', '_').Replace('`', '_');

    bool IsInterface(TypeDefinitionHandle h) => (md.GetTypeDefinition(h).Attributes & System.Reflection.TypeAttributes.Interface) != 0;

    public void AddClass(string name)
    {
        if (classes.ContainsKey(name)) return;
        In(name, () => AddClassIn(name));
    }

    void AddClassIn(string name)
    {
        var info = new ClassInfo();
        classes[name] = info;
        var h = Find(name);
        var t = md.GetTypeDefinition(h);
        foreach (var ih in t.GetInterfaceImplementations())
        {
            var impl = md.GetInterfaceImplementation(ih);
            var iname = FullName(impl.Interface);
            if (iname == "<generic>") continue;
            if (info.DefaultInterface == null && HasAttribute(impl.GetCustomAttributes(), "DefaultAttribute"))
                info.DefaultInterface = iname;
            AddInterfaceTree(iname, info.Interfaces);
        }
        if (!t.BaseType.IsNil)
        {
            var b = FullName(t.BaseType);
            if (b != "System.Object")
            {
                info.Base = b;
                AddClass(b);
                foreach (var i in classes[b].Interfaces) if (!info.Interfaces.Contains(i)) info.Interfaces.Add(i);
            }
        }
        foreach (var ah in t.GetCustomAttributes())
        {
            var a = md.GetCustomAttribute(ah);
            var aname = AttributeName(a);
            if (aname != "ActivatableAttribute" && aname != "StaticAttribute" && aname != "ComposableAttribute") continue;
            var first = TypeArgument(a);
            switch (aname)
            {
            case "ActivatableAttribute":
                if (first != null) { info.Factories.Add(first); AddInterface(first); } else info.Activatable = true;
                break;
            case "StaticAttribute":
                if (first != null) { info.Statics.Add(first); AddInterface(first); }
                break;
            case "ComposableAttribute":
                if (first != null) { info.Composable.Add(first); AddInterface(first); }
                break;
            }
        }
    }

    void AddInterfaceTree(string name, List<string> into)
    {
        if (!into.Contains(name)) into.Add(name);
        AddInterface(name);
        In(name, () =>
        {
            foreach (var ih in md.GetTypeDefinition(Find(name)).GetInterfaceImplementations())
            {
                var iname = FullName(md.GetInterfaceImplementation(ih).Interface);
                if (iname != "<generic>") AddInterfaceTree(iname, into);
            }
        });
    }

    public void AddInterface(string name)
    {
        if (interfaces.ContainsKey(name)) return;
        In(name, () =>
        {
            var h = Find(name);
            if (!IsInterface(h)) throw new InvalidOperationException($"{name} is not an interface");
            interfaces[name] = h;
            foreach (var mh in md.GetTypeDefinition(h).GetMethods()) CollectTypes(md.GetMethodDefinition(mh));
        });
    }

    public void AddDelegate(string name)
    {
        if (delegates.ContainsKey(name)) return;
        In(name, () =>
        {
            var h = Find(name);
            delegates[name] = h;
            foreach (var mh in md.GetTypeDefinition(h).GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                if (md.GetString(m.Name) == "Invoke") CollectTypes(m);
            }
        });
    }

    void CollectTypes(MethodDefinition m)
    {
        var sig = m.DecodeSignature(new SigProvider(this), null);
        foreach (var p in sig.ParameterTypes.Prepend(sig.ReturnType)) NoteStruct(p);
    }

    void NoteStruct(AbiType t)
    {
        if (t.Struct == null || structs.ContainsKey(t.Struct)) return;
        In(t.Struct, () =>
        {
            var h = Find(t.Struct);
            structs[t.Struct] = h;
            foreach (var fh in md.GetTypeDefinition(h).GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                NoteStruct(f.DecodeSignature(new SigProvider(this), null));
            }
        });
    }

    /* the System.Type first argument of an attribute constructor (a serialized type name), or null */
    string? TypeArgument(CustomAttribute a)
    {
        if (a.Constructor.Kind != HandleKind.MemberReference) return null;
        var ctor = md.GetMemberReference((MemberReferenceHandle)a.Constructor);
        var sig = ctor.DecodeMethodSignature(new SigProvider(this), null);
        if (sig.ParameterTypes.Length == 0 || sig.ParameterTypes[0].Cs != "TypeName") return null;   /* System.Type */
        var b = md.GetBlobReader(a.Value);
        b.ReadUInt16();
        var name = b.ReadSerializedString();
        if (name == null) return null;
        var comma = name.IndexOf(',');
        return comma < 0 ? name : name[..comma];
    }

    bool HasAttribute(CustomAttributeHandleCollection attrs, string name) =>
        attrs.Any(a => AttributeName(md.GetCustomAttribute(a)) == name);

    string AttributeName(CustomAttribute a)
    {
        EntityHandle parent = a.Constructor.Kind == HandleKind.MemberReference
            ? md.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent
            : md.GetMethodDefinition((MethodDefinitionHandle)a.Constructor).GetDeclaringType();
        return Short(FullName(parent));
    }

    Guid InterfaceGuid(TypeDefinitionHandle h)
    {
        foreach (var ah in md.GetTypeDefinition(h).GetCustomAttributes())
        {
            var a = md.GetCustomAttribute(ah);
            if (AttributeName(a) != "GuidAttribute") continue;
            var b = md.GetBlobReader(a.Value);
            b.ReadUInt16();
            uint d1 = b.ReadUInt32(); ushort d2 = b.ReadUInt16(), d3 = b.ReadUInt16();
            var rest = b.ReadBytes(8);
            return new Guid(d1, d2, d3, rest[0], rest[1], rest[2], rest[3], rest[4], rest[5], rest[6], rest[7]);
        }
        throw new InvalidOperationException("no GUID");
    }

    // ---- signatures ----

    /* Kind: void, bool, prim, string, object, enum, struct, other (what a default property body can convert) */
    public sealed record AbiType(string Cs, string? Struct = null, bool IsArray = false, bool ByRef = false, string Kind = "prim");

    sealed class SigProvider : ISignatureTypeProvider<AbiType, object?>
    {
        readonly Generator g;
        public SigProvider(Generator g) { this.g = g; }
        public AbiType GetPrimitiveType(PrimitiveTypeCode c) => new AbiType(c switch
        {
            PrimitiveTypeCode.Void => "void",
            PrimitiveTypeCode.Boolean => "byte",
            PrimitiveTypeCode.Char => "char",
            PrimitiveTypeCode.SByte => "sbyte",
            PrimitiveTypeCode.Byte => "byte",
            PrimitiveTypeCode.Int16 => "short",
            PrimitiveTypeCode.UInt16 => "ushort",
            PrimitiveTypeCode.Int32 => "int",
            PrimitiveTypeCode.UInt32 => "uint",
            PrimitiveTypeCode.Int64 => "long",
            PrimitiveTypeCode.UInt64 => "ulong",
            PrimitiveTypeCode.Single => "float",
            PrimitiveTypeCode.Double => "double",
            _ => "IntPtr",                       // String (HSTRING), Object (IInspectable*)
        }) with { Kind = c switch { PrimitiveTypeCode.Void => "void", PrimitiveTypeCode.Boolean => "bool",
                                    PrimitiveTypeCode.String => "string", PrimitiveTypeCode.Object => "object",
                                    PrimitiveTypeCode.Char => "other", _ => "prim" } };
        public AbiType GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte kind) => Named(FullName(r, h), kind);
        public AbiType GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte kind) => Named(FullName(r, h), kind);
        AbiType Named(string name, byte kind)
        {
            /* the union metadata names some Windows.Foundation types by their .NET projections */
            switch (name)
            {
            case "System.Guid": return new("Guid") { Kind = "other" };
            case "System.TimeSpan": return new("long") { Kind = "other" };                       /* Windows.Foundation.TimeSpan */
            case "System.DateTimeOffset": return new("long") { Kind = "other" };                 /* Windows.Foundation.DateTime */
            case "System.Runtime.InteropServices.WindowsRuntime.EventRegistrationToken": return new("long") { Kind = "other" };
            case "System.Exception": return new("int") { Kind = "other" };                       /* Windows.Foundation.HResult */
            case "System.Type": return new("TypeName") { Kind = "other" };                       /* Windows.UI.Xaml.Interop.TypeName */
            }
            if (name.StartsWith("System.Numerics.")) return new(name) { Kind = "other" };        /* Windows.Foundation.Numerics */
            if (kind != (byte)SignatureTypeKind.ValueType) return new("IntPtr") { Kind = "object" };
            if (!g.types.TryGetValue(name, out var t)) return new("IntPtr") { Kind = "other" };
            var def = t.R.GetTypeDefinition(t.H);
            if (def.BaseType.IsNil) return new("IntPtr") { Kind = "other" };
            if (FullName(t.R, def.BaseType) == "System.Enum")
            {
                foreach (var fh in def.GetFields())
                {
                    var f = t.R.GetFieldDefinition(fh);
                    if (t.R.GetString(f.Name) == "value__") return f.DecodeSignature(this, null) with { Kind = "enum" };
                }
                return new("int") { Kind = "enum" };
            }
            return new(Ident(name), name) { Kind = "struct" };
        }
        public AbiType GetSZArrayType(AbiType e) => new(e.Cs, e.Struct, IsArray: true);
        public AbiType GetByReferenceType(AbiType e) => e with { ByRef = true };
        public AbiType GetGenericInstantiation(AbiType t, ImmutableArray<AbiType> a) => new("IntPtr") { Kind = "object" };
        public AbiType GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) => new("IntPtr");
        public AbiType GetGenericTypeParameter(object? c, int i) => new("IntPtr");
        public AbiType GetGenericMethodParameter(object? c, int i) => new("IntPtr");
        public AbiType GetArrayType(AbiType e, ArrayShape s) => new("IntPtr");
        public AbiType GetPointerType(AbiType e) => new(e.Cs + "*");
        public AbiType GetFunctionPointerType(MethodSignature<AbiType> s) => new("IntPtr");
        public AbiType GetModifiedType(AbiType m, AbiType u, bool req) => u;
        public AbiType GetPinnedType(AbiType e) => e;
    }

    sealed class AttrProvider : ICustomAttributeTypeProvider<object?>
    {
        public object? GetPrimitiveType(PrimitiveTypeCode c) => c;
        public object? GetSystemType() => "System.Type";
        public object? GetSZArrayType(object? e) => null;
        public object? GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => "enum";
        public object? GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => "enum";
        public object? GetTypeFromSerializedName(string n) => n;
        public PrimitiveTypeCode GetUnderlyingEnumType(object? t) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(object? t) => t as string == "System.Type";
    }

    // the ABI parameter list of one method: (C# parameter declarations, argument names)
    List<(string Decl, string Name, AbiType? Type)> AbiParams(MethodDefinition m)
    {
        var sig = m.DecodeSignature(new SigProvider(this), null);
        var names = new Dictionary<int, (string Name, bool Out)>();
        foreach (var ph in m.GetParameters())
        {
            var p = md.GetParameter(ph);
            names[p.SequenceNumber] = (md.GetString(p.Name), (p.Attributes & System.Reflection.ParameterAttributes.Out) != 0);
        }
        var list = new List<(string, string, AbiType?)>();
        for (int i = 0; i < sig.ParameterTypes.Length; i++)
        {
            var t = sig.ParameterTypes[i];
            var (name, isOut) = names.TryGetValue(i + 1, out var n) ? n : ($"p{i}", false);
            name = "@" + name;
            if (t.IsArray && t.ByRef) { list.Add(($"uint* {name}Length", name + "Length", null)); list.Add(($"{t.Cs}** {name}", name, null)); }
            else if (t.IsArray) { list.Add(($"uint {name}Length", name + "Length", null)); list.Add(($"{t.Cs}* {name}", name, null)); }
            else if (t.ByRef) list.Add(($"{t.Cs}* {name}", name, null));
            else list.Add(($"{t.Cs} {name}", name, t));
        }
        var ret = sig.ReturnType;
        if (ret.Cs != "void")
        {
            if (ret.IsArray) { list.Add(("uint* resultLength", "resultLength", null)); list.Add(($"{ret.Cs}** result", "result", null)); }
            else list.Add(($"{ret.Cs}* result", "result", ret));
        }
        return list;
    }

    List<(string Name, MethodDefinition Def)> Methods(TypeDefinitionHandle h)
    {
        var list = new List<(string, MethodDefinition)>();
        var seen = new HashSet<string>();
        foreach (var mh in md.GetTypeDefinition(h).GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            var name = md.GetString(m.Name);
            foreach (var ah in m.GetCustomAttributes())
            {
                var a = md.GetCustomAttribute(ah);
                if (AttributeName(a) == "OverloadAttribute" && a.DecodeValue(new AttrProvider()).FixedArguments[0].Value is string o) name = o;
            }
            while (!seen.Add(name)) name += "_";
            list.Add((name, m));
        }
        return list;
    }

    // ---- output ----

    public string Emit(string ns)
    {
        var s = new StringBuilder();
        s.AppendLine("// SPDX-License-Identifier: Apache-2.0");
        s.AppendLine("// Copyright 2026 WineExplorer contributors");
        s.AppendLine("// <auto-generated> by WinmdGen from Windows.winmd and WinUI 2's metadata: do not edit </auto-generated>");
        s.AppendLine("#pragma warning disable CS8981, CS0649, CS1591");
        s.AppendLine("using System.Runtime.CompilerServices;");
        s.AppendLine("using System.Runtime.InteropServices;");
        s.AppendLine($"namespace {ns};");
        s.AppendLine();
        foreach (var (name, h) in structs)
            In(name, () =>
            {
                s.AppendLine($"[StructLayout(LayoutKind.Sequential)] public struct {Ident(name)}");
                s.AppendLine("{");
                foreach (var fh in md.GetTypeDefinition(h).GetFields())
                {
                    var f = md.GetFieldDefinition(fh);
                    s.AppendLine($"    public {f.DecodeSignature(new SigProvider(this), null).Cs} {md.GetString(f.Name)};");
                }
                s.AppendLine("}");
            });
        s.AppendLine();
        s.AppendLine("public static class Iids");
        s.AppendLine("{");
        foreach (var (name, h) in interfaces)
            s.AppendLine($"    public static readonly Guid {Ident(name)} = new(\"{In(name, () => InterfaceGuid(h))}\");");
        foreach (var (name, h) in delegates)
            s.AppendLine($"    public static readonly Guid {Ident(name)} = new(\"{In(name, () => InterfaceGuid(h))}\");");
        s.AppendLine("}");
        s.AppendLine();
        foreach (var (name, h) in interfaces) In(name, () => EmitInterface(s, name, h));
        foreach (var (name, h) in delegates) In(name, () => EmitDelegate(s, name, h));
        EmitClassTable(s);
        return s.ToString();
    }

    void EmitInterface(StringBuilder s, string name, TypeDefinitionHandle h)
    {
        var id = Ident(name);
        var methods = Methods(h);
        MethodCount += methods.Count;
        s.AppendLine($"/* {name} */");
        s.AppendLine($"public unsafe interface I{id}_Abi");
        s.AppendLine("{");
        foreach (var (mname, m) in methods)
        {
            var ps = AbiParams(m);
            /* a composable factory's CreateInstance(outer, inner, instance): by default the Uno class of the same name */
            var composable = mname == "CreateInstance" && ps.Count == 3 && ps[0].Name == "@baseInterface" &&
                             ps[1].Name == "@innerInterface" && ps[2].Name == "result";
            var body = composable
                ? "=> ShimFactory.ComposeDefault(this, @baseInterface, @innerInterface, result);"
                : PropertyBody(mname, ps) ?? EventBody(mname, ps) ?? AttachedBody(mname, ps) ??
                  $"{{ Ccw.NotImplemented(\"{name}.{mname}\"); return unchecked((int)0x80004001); }}";
            s.AppendLine($"    int {mname}({string.Join(", ", ps.Select(p => p.Decl))}) {body}");
        }
        s.AppendLine("}");
        s.AppendLine($"public static unsafe class {id}_Vtbl");
        s.AppendLine("{");
        s.AppendLine($"    public static readonly Guid Iid = Iids.{id};");
        s.AppendLine($"    public static readonly IntPtr Vtbl = Create();");
        s.AppendLine($"    public static bool Implements(object o) => o is I{id}_Abi;");
        s.AppendLine("    static IntPtr Create()");
        s.AppendLine("    {");
        s.AppendLine($"        var v = (IntPtr*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof({id}_Vtbl), sizeof(IntPtr) * {6 + methods.Count});");
        s.AppendLine("        Ccw.FillInspectable(v);");
        for (int i = 0; i < methods.Count; i++)
        {
            var ps = AbiParams(methods[i].Def);
            var types = string.Join(", ", ps.Select(p => p.Decl[..p.Decl.LastIndexOf(' ')]).Prepend("IntPtr").Append("int"));
            s.AppendLine($"        v[{6 + i}] = (IntPtr)(delegate* unmanaged[Stdcall]<{types}>)&M{i};");
        }
        s.AppendLine("        return (IntPtr)v;");
        s.AppendLine("    }");
        for (int i = 0; i < methods.Count; i++)
        {
            var (mname, m) = methods[i];
            var ps = AbiParams(m);
            var decl = string.Join(", ", ps.Select(p => p.Decl).Prepend("IntPtr @this"));
            var call = string.Join(", ", ps.Select(p => p.Name));
            s.AppendLine($"    [UnmanagedCallersOnly(CallConvs = new[] {{ typeof(CallConvStdcall) }})] static int M{i}({decl})");
            s.AppendLine($"    {{ if (Ccw.Calls) Ccw.Log(\"call {name}.{mname}\"); try {{ return ((I{id}_Abi)Ccw.Target(@this)).{mname}({call}); }} catch (Exception ex__) {{ return Ccw.Fail(\"{name}.{mname}\", ex__); }} }}");
        }
        s.AppendLine("}");
        s.AppendLine($"public static unsafe class {id}_Call");
        s.AppendLine("{");
        for (int i = 0; i < methods.Count; i++)
        {
            var (mname, m) = methods[i];
            var ps = AbiParams(m);
            var types = string.Join(", ", ps.Select(p => p.Decl[..p.Decl.LastIndexOf(' ')]).Prepend("IntPtr").Append("int"));
            var decl = string.Join(", ", ps.Select(p => p.Decl).Prepend("IntPtr @this"));
            var call = string.Join(", ", ps.Select(p => p.Name).Prepend("@this"));
            s.AppendLine($"    public static int {mname}({decl}) => ((delegate* unmanaged[Stdcall]<{types}>)(*(IntPtr**)@this)[{6 + i}])({call});");
        }
        s.AppendLine("}");
        s.AppendLine();
    }

    /* a default body for a property accessor: the Uno object's property of the same name (Reflect, XamlShim) */
    static string? PropertyBody(string mname, List<(string Decl, string Name, AbiType? Type)> ps)
    {
        if (ps.Count != 1 || ps[0].Type is not { } t || mname.Length < 5) return null;
        var prop = mname[4..];
        if (mname.StartsWith("get_") && ps[0].Name == "result")
            return t.Kind switch
            {
                "string" => $"{{ *result = HString.Create((string?)Reflect.Get(this, \"{prop}\") ?? \"\"); return 0; }}",
                "bool" => $"{{ *result = (byte)(Reflect.Get(this, \"{prop}\") is true ? 1 : 0); return 0; }}",
                "prim" => $"{{ *result = ({t.Cs})System.Convert.ChangeType(Reflect.Get(this, \"{prop}\")!, typeof({t.Cs}), System.Globalization.CultureInfo.InvariantCulture); return 0; }}",
                "enum" => $"{{ *result = unchecked(({t.Cs})Reflect.GetInteger(this, \"{prop}\")); return 0; }}",
                "object" => $"{{ *result = Boxing.Box(Reflect.Get(this, \"{prop}\")); return 0; }}",
                "struct" => $"{{ *result = Reflect.GetStruct<{t.Cs}>(this, \"{prop}\"); return 0; }}",
                _ => null,
            };
        if (mname.StartsWith("put_") && ps[0].Name == "@value")
            return t.Kind switch
            {
                "string" => $"{{ Reflect.Set(this, \"{prop}\", HString.Read(@value)); return 0; }}",
                "bool" => $"{{ Reflect.Set(this, \"{prop}\", @value != 0); return 0; }}",
                "prim" => $"{{ Reflect.Set(this, \"{prop}\", @value); return 0; }}",
                "enum" => $"{{ Reflect.Set(this, \"{prop}\", (long)@value); return 0; }}",
                "object" => $"{{ Reflect.Set(this, \"{prop}\", Boxing.Unbox(@value)); return 0; }}",
                "struct" => $"{{ Reflect.SetStruct(this, \"{prop}\", @value); return 0; }}",
                _ => null,
            };
        return null;
    }

    /* a default body for an attached property of a statics interface (AutomationProperties.SetName(element, value),
     * GetName(element)): the Uno class's static SetX or GetX (Reflect, XamlShim) */
    static string? AttachedBody(string mname, List<(string Decl, string Name, AbiType? Type)> ps)
    {
        if (ps.Count != 2 || ps[0].Type is not { Kind: "object" } || ps[1].Type is not { } t || mname.Length < 4) return null;
        var prop = mname[3..];
        if (mname.StartsWith("Get") && ps[1].Name == "result")
        {
            var get = $"Reflect.GetAttached(this, \"{prop}\", {ps[0].Name})";
            return t.Kind switch
            {
                "string" => $"{{ *result = HString.Create((string?){get} ?? \"\"); return 0; }}",
                "bool" => $"{{ *result = (byte)({get} is true ? 1 : 0); return 0; }}",
                "prim" => $"{{ *result = ({t.Cs})System.Convert.ChangeType({get}!, typeof({t.Cs}), System.Globalization.CultureInfo.InvariantCulture); return 0; }}",
                "enum" => $"{{ *result = unchecked(({t.Cs})System.Convert.ToInt64({get}, System.Globalization.CultureInfo.InvariantCulture)); return 0; }}",
                "object" => $"{{ *result = Boxing.Box({get}); return 0; }}",
                "struct" => $"{{ *result = {get} is {{ }} v__ ? ({t.Cs})StructConvert.ToAbi(v__, typeof({t.Cs})) : default; return 0; }}",
                _ => null,
            };
        }
        if (mname.StartsWith("Set") && ps[1].Name == "@value")
        {
            var value = t.Kind switch
            {
                "string" => "HString.Read(@value)",
                "bool" => "@value != 0",
                "prim" => "@value",
                "enum" => "(long)@value",
                "object" => "Boxing.Unbox(@value)",
                "struct" => "@value",
                _ => null,
            };
            return value == null ? null : $"{{ Reflect.SetAttached(this, \"{prop}\", {ps[0].Name}, {value}); return 0; }}";
        }
        return null;
    }

    /* a default body for an event accessor: the Uno object's event of the same name (Events, XamlShim) */
    static string? EventBody(string mname, List<(string Decl, string Name, AbiType? Type)> ps)
    {
        if (mname.StartsWith("add_") && ps.Count == 2 && ps[0].Name == "@handler" && ps[1].Name == "result")
            return $"=> Events.Add(this, \"{mname[4..]}\", @handler, result);";
        if (mname.StartsWith("remove_") && ps.Count == 1 && ps[0].Name == "@token")
            return $"=> Events.Remove(this, \"{mname[7..]}\", @token);";
        return null;
    }

    void EmitDelegate(StringBuilder s, string name, TypeDefinitionHandle h)
    {
        var id = Ident(name);
        foreach (var mh in md.GetTypeDefinition(h).GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            if (md.GetString(m.Name) != "Invoke") continue;
            var ps = AbiParams(m);
            var types = string.Join(", ", ps.Select(p => p.Decl[..p.Decl.LastIndexOf(' ')]).Prepend("IntPtr").Append("int"));
            var decl = string.Join(", ", ps.Select(p => p.Decl).Prepend("IntPtr @this"));
            var call = string.Join(", ", ps.Select(p => p.Name).Prepend("@this"));
            s.AppendLine($"/* delegate {name} */");
            s.AppendLine($"public static unsafe class {id}_Call");
            s.AppendLine("{");
            s.AppendLine($"    public static int Invoke({decl}) => ((delegate* unmanaged[Stdcall]<{types}>)(*(IntPtr**)@this)[3])({call});");
            s.AppendLine("}");
        }
    }

    void EmitClassTable(StringBuilder s)
    {
        s.AppendLine("public static class ClassTable");
        s.AppendLine("{");
        s.AppendLine("    /* runtime class -> the vtables of every interface its instances answer to, default interface first */");
        s.AppendLine("    public static readonly Dictionary<string, (Guid Iid, IntPtr Vtbl, Func<object, bool> Implements)[]> Interfaces = new()");
        s.AppendLine("    {");
        foreach (var (name, info) in classes)
        {
            var list = info.Interfaces.OrderBy(i => i == info.DefaultInterface ? 0 : 1).Select(i => $"({Ident(i)}_Vtbl.Iid, {Ident(i)}_Vtbl.Vtbl, {Ident(i)}_Vtbl.Implements)");
            s.AppendLine($"        [\"{name}\"] = new (Guid, IntPtr, Func<object, bool>)[] {{ {string.Join(", ", list)} }},");
        }
        s.AppendLine("    };");
        s.AppendLine("    /* runtime class -> the interfaces of its activation factory (factories, statics, composable factories) */");
        s.AppendLine("    public static readonly Dictionary<string, (Guid Iid, IntPtr Vtbl, Func<object, bool> Implements)[]> Factory = new()");
        s.AppendLine("    {");
        foreach (var (name, info) in classes)
        {
            var list = info.Factories.Concat(info.Statics).Concat(info.Composable).Select(i => $"({Ident(i)}_Vtbl.Iid, {Ident(i)}_Vtbl.Vtbl, {Ident(i)}_Vtbl.Implements)");
            s.AppendLine($"        [\"{name}\"] = new (Guid, IntPtr, Func<object, bool>)[] {{ {string.Join(", ", list)} }},");
        }
        s.AppendLine("    };");
        s.AppendLine("    public static readonly HashSet<string> DefaultActivatable = new() { " +
                     string.Join(", ", classes.Where(c => c.Value.Activatable).Select(c => $"\"{c.Key}\"")) + " };");
        s.AppendLine("}");
        s.AppendLine();
        // one partial class per runtime class, mirroring the runtime class hierarchy, and one per activation factory
        foreach (var (name, info) in classes)
        {
            var own = info.Interfaces.Where(i => info.Base == null || !classes[info.Base].Interfaces.Contains(i)).Select(i => $"I{Ident(i)}_Abi");
            var bases = new[] { info.Base != null ? Ident(info.Base) : "ShimObject" }.Concat(own);
            s.AppendLine($"public partial class {Ident(name)} : {string.Join(", ", bases)}");
            s.AppendLine("{");
            s.AppendLine($"    public override string RuntimeClassName => \"{name}\";");
            s.AppendLine("}");
            var fbases = info.Factories.Concat(info.Statics).Concat(info.Composable).Select(i => $"I{Ident(i)}_Abi").Prepend("ShimFactory");
            s.AppendLine($"public partial class {Ident(name)}_Factory : {string.Join(", ", fbases)}");
            s.AppendLine("{");
            s.AppendLine($"    public override string ClassName => \"{name}\";");
            s.AppendLine("}");
        }
        s.AppendLine("public static class FactoryTable");
        s.AppendLine("{");
        s.AppendLine("    public static readonly Dictionary<string, Func<ShimFactory>> Create = new()");
        s.AppendLine("    {");
        foreach (var name in classes.Keys) s.AppendLine($"        [\"{name}\"] = () => new {Ident(name)}_Factory(),");
        s.AppendLine("    };");
        s.AppendLine("}");
    }
}
