// Vergleicht zwei Baeume mit Cosmos-Assemblies (Paket E1-D): etwa die NuGet-Pakete 3.0.97 aus dem NuGet-Cache gegen
// die entpackten Pakete eines Quellbaus aus dem Branch cosmos. Laeuft mit dem vorhandenen .NET-10-SDK als
// dateibasierte App, ohne weitere Werkzeuge (monodis laesst Methoden neuerer Assemblies still aus):
//
//   dotnet run scripts/compare-cosmos-dlls.cs -- <A> <B> [--show <n>]
//
// Je DLL gleichen relativen Pfads ein Urteil:
//   gleich         bytegleich
//   bauangaben     gleich, nachdem die Angaben maskiert sind, die bei gleicher Quelle und gleicher PathMap von Bau zu
//                  Bau oder mit dem Quell-Commit wechseln: COFF-Zeitstempel, PE-Pruefsumme, Debug-Verzeichnis
//                  (PDB-Pfad, -GUID, -Pruefsumme), MVID und "+<40 Hex-Zeichen>" (Commit in InformationalVersion und
//                  Win32-Versionsressource). Maskiert wird an Ort und Stelle; das geht, weil ein Commit-SHA immer
//                  40 Zeichen hat.
//   IL-gleich      gleiche Typen, Felder, Methoden, Methodenkoerper (IL mit aufgeloesten Token, Lokale, Ausnahmebereiche),
//                  Attribut-Zeichenketten und Assemblyverweise; nur Reihenfolge oder Nummern der Metadaten weichen ab.
//   VERSCHIEDEN    mit Hinweisen, was abweicht.
// Ende 0, wenn keine Datei VERSCHIEDEN ist und beide Seiten dieselben DLLs haben; IL-gleich zaehlt als Gleichstand.
using System.Collections.Immutable;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

if (args.Length < 2)
{
    Console.Error.WriteLine("Aufruf: dotnet run scripts/compare-cosmos-dlls.cs -- <A> <B> [--show <n>]");
    return 2;
}
string rootA = Path.GetFullPath(args[0]), rootB = Path.GetFullPath(args[1]);
int show = args.Length >= 4 && args[2] == "--show" ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 10;
var filesA = ListDlls(rootA);
var filesB = ListDlls(rootB);
int same = 0, build = 0, semantic = 0, differ = 0;
foreach (string name in filesA.Keys.Intersect(filesB.Keys).Order(StringComparer.Ordinal))
{
    byte[] a = File.ReadAllBytes(filesA[name]), b = File.ReadAllBytes(filesB[name]);
    if (a.AsSpan().SequenceEqual(b)) { same++; Console.WriteLine($"gleich       {name}"); continue; }
    if (Masking.Mask(a).AsSpan().SequenceEqual(Masking.Mask(b))) { build++; Console.WriteLine($"bauangaben   {name}"); continue; }
    var findings = Assembly.Compare(a, b);
    if (findings.Count == 0) { semantic++; Console.WriteLine($"IL-gleich    {name}"); continue; }
    differ++;
    Console.WriteLine($"VERSCHIEDEN  {name} ({a.Length} / {b.Length} Byte, {findings.Count} Hinweise)");
    foreach (string line in findings.Take(show)) Console.WriteLine("    " + line);
}
var onlyA = filesA.Keys.Except(filesB.Keys).Order(StringComparer.Ordinal).ToList();
var onlyB = filesB.Keys.Except(filesA.Keys).Order(StringComparer.Ordinal).ToList();
foreach (string name in onlyA) Console.WriteLine($"nur in A     {name}");
foreach (string name in onlyB) Console.WriteLine($"nur in B     {name}");
Console.WriteLine($"Summe: gleich {same}, bauangaben {build}, IL-gleich {semantic}, VERSCHIEDEN {differ}, nur A {onlyA.Count}, nur B {onlyB.Count}");
return differ == 0 && onlyA.Count == 0 && onlyB.Count == 0 ? 0 : 1;

static Dictionary<string, string> ListDlls(string root) =>
    Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path).Replace('\\', '/'), path => path, StringComparer.Ordinal);

static class Masking
{
    // Kopie mit genullten Bauangaben (siehe Kopf). Ohne verwaltete Metadaten bleibt nur der Bytevergleich.
    public static byte[] Mask(byte[] data)
    {
        byte[] copy = (byte[])data.Clone();
        using var pe = new PEReader(ImmutableArray.Create(data));
        var headers = pe.PEHeaders;
        Array.Clear(copy, headers.CoffHeaderStartOffset + 4, 4);   // TimeDateStamp
        Array.Clear(copy, headers.PEHeaderStartOffset + 64, 4);    // CheckSum, gleiche Lage bei PE32 und PE32+
        var debug = headers.PEHeader!.DebugTableDirectory;
        if (debug.Size > 0 && headers.TryGetDirectoryOffset(debug, out int table))
        {
            // Je Eintrag 28 Byte mit Zeitstempel bei +4; die Daten (CodeView, PDB-Pruefsumme) ganz nullen.
            for (int at = table; at + 28 <= table + debug.Size; at += 28) Array.Clear(copy, at + 4, 4);
            foreach (var entry in pe.ReadDebugDirectory())
                if (entry.DataPointer > 0 && entry.DataPointer + entry.DataSize <= copy.Length)
                    Array.Clear(copy, entry.DataPointer, entry.DataSize);
        }
        if (pe.HasMetadata)
        {
            var md = pe.GetMetadataReader();
            int index = MetadataTokens.GetHeapOffset(md.GetModuleDefinition().Mvid);
            if (index > 0)
                Array.Clear(copy, headers.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Guid) + (index - 1) * 16, 16);
        }
        // Der Commit steht als UTF-8 (InformationalVersion) und als UTF-16 (Win32-Versionsressource).
        for (int width = 1; width <= 2; width++)
            for (int at = 0; at + 41 * width <= copy.Length; at++)
                if (copy[at] == (byte)'+' && (width == 1 || copy[at + 1] == 0) && IsHexRun(copy, at + width, 40, width))
                {
                    Array.Clear(copy, at + width, 40 * width);
                    at += 40 * width;
                }
        return copy;
    }

    // 40 Hex-Ziffern ab start im Abstand width (1 = UTF-8, 2 = UTF-16LE), nicht von einer weiteren Ziffer gefolgt.
    private static bool IsHexRun(byte[] data, int start, int count, int width)
    {
        for (int i = 0; i < count; i++)
            if (!IsHexAt(data, start + i * width, width)) return false;
        int next = start + count * width;
        return next + width > data.Length || !IsHexAt(data, next, width);
    }

    private static bool IsHexAt(byte[] data, int at, int width) =>
        data[at] is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' && (width == 1 || data[at + 1] == 0);
}

static class Assembly
{
    private static readonly Dictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes).GetFields()
        .Select(field => (OpCode)field.GetValue(null)!).ToDictionary(code => unchecked((ushort)code.Value));

    // Leere Liste: inhaltlich gleich (Urteil IL-gleich). Sonst je Abweichung eine Zeile.
    public static List<string> Compare(byte[] a, byte[] b)
    {
        using var peA = new PEReader(ImmutableArray.Create(a));
        using var peB = new PEReader(ImmutableArray.Create(b));
        if (!peA.HasMetadata || !peB.HasMetadata) return ["keine verwalteten Metadaten, Bytes verschieden"];
        var viewA = new View(peA);
        var viewB = new View(peB);
        var findings = new List<string>();
        Diff(findings, "Assemblyverweis", viewA.References(), viewB.References());
        Diff(findings, "Attribut", viewA.AttributeStrings(), viewB.AttributeStrings());
        Diff(findings, "Feld", viewA.Fields(), viewB.Fields());
        var bodiesA = viewA.Bodies();
        var bodiesB = viewB.Bodies();
        Diff(findings, "Methode", bodiesA.Keys.ToHashSet(StringComparer.Ordinal), bodiesB.Keys.ToHashSet(StringComparer.Ordinal));
        foreach (string key in bodiesA.Keys.Intersect(bodiesB.Keys).Order(StringComparer.Ordinal))
            if (bodiesA[key] != bodiesB[key]) findings.Add($"IL anders: {key}: {FirstDifference(bodiesA[key], bodiesB[key])}");
        return findings;
    }

    private static void Diff(List<string> findings, string kind, HashSet<string> a, HashSet<string> b)
    {
        findings.AddRange(a.Except(b).Order(StringComparer.Ordinal).Select(value => $"{kind} nur in A: {value}"));
        findings.AddRange(b.Except(a).Order(StringComparer.Ordinal).Select(value => $"{kind} nur in B: {value}"));
    }

    private static string FirstDifference(string a, string b)
    {
        string[] linesA = a.Split('\n'), linesB = b.Split('\n');
        int at = 0;
        while (at < linesA.Length && at < linesB.Length && linesA[at] == linesB[at]) at++;
        string left = at < linesA.Length ? linesA[at] : "<Ende>", right = at < linesB.Length ? linesB[at] : "<Ende>";
        return $"Zeile {at + 1}: A '{left}' / B '{right}'";
    }

    // Lesesicht auf eine Assembly, die Token in Namen aufloest, damit Metadaten-Reihenfolge keine Rolle spielt.
    private sealed class View(PEReader pe)
    {
        private readonly MetadataReader md = pe.GetMetadataReader();
        private readonly Names names = new(pe.GetMetadataReader());

        public HashSet<string> References() =>
            md.AssemblyReferences.Select(handle => md.GetAssemblyReference(handle))
                .Select(reference => $"{md.GetString(reference.Name)} {reference.Version}").ToHashSet(StringComparer.Ordinal);

        // Druckbare Teile der Attributwerte, etwa GeneratedCode("<Generator>", "<Version>"); der Commit ist maskiert.
        public HashSet<string> AttributeStrings()
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var handle in md.CustomAttributes)
            {
                var attribute = md.GetCustomAttribute(handle);
                string owner = names.Entity(attribute.Constructor);
                byte[] blob = md.GetBlobBytes(attribute.Value);
                string text = Encoding.ASCII.GetString(blob.Select(value => value is >= 32 and < 127 ? value : (byte)' ').ToArray());
                foreach (string part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (part.Length >= 4) result.Add($"{owner}: {Regex.Replace(part, "\\+[0-9a-f]{40}", "+<commit>")}");
            }
            return result;
        }

        public HashSet<string> Fields() =>
            md.FieldDefinitions.Select(handle => names.Entity(handle)).ToHashSet(StringComparer.Ordinal);

        public Dictionary<string, string> Bodies()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var handle in md.MethodDefinitions)
            {
                string key = names.Entity(handle);
                for (int n = 2; result.ContainsKey(key); n++) key = $"{names.Entity(handle)}#{n}";
                int rva = md.GetMethodDefinition(handle).RelativeVirtualAddress;
                result[key] = rva == 0 ? "<ohne Koerper>" : Body(pe.GetMethodBody(rva));
            }
            return result;
        }

        private string Body(MethodBodyBlock body)
        {
            var text = new StringBuilder();
            text.Append("maxstack ").Append(body.MaxStack).Append(" init ").Append(body.LocalVariablesInitialized).Append('\n');
            if (!body.LocalSignature.IsNil)
            {
                var signature = md.GetStandaloneSignature(body.LocalSignature);
                text.Append("locals ").Append(string.Join(", ", signature.DecodeLocalSignature(names, null))).Append('\n');
            }
            foreach (var region in body.ExceptionRegions)
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $"region {region.Kind} try {region.TryOffset}+{region.TryLength} handler {region.HandlerOffset}+{region.HandlerLength}")
                    .Append(region.CatchType.IsNil ? "" : " catch " + names.Entity(region.CatchType)).Append('\n');
            var reader = body.GetILReader();
            while (reader.RemainingBytes > 0)
            {
                text.Append(reader.Offset).Append(": ");
                ushort value = reader.ReadByte();
                if (value == 0xFE) value = (ushort)(0xFE00 | reader.ReadByte());
                if (!OpCodesByValue.TryGetValue(value, out var code)) { text.Append(System.Globalization.CultureInfo.InvariantCulture, $"?{value:x}\n"); continue; }
                text.Append(code.Name);
                switch (code.OperandType)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget: text.Append(' ').Append(reader.ReadSByte()); break;
                    case OperandType.ShortInlineI: text.Append(' ').Append(reader.ReadSByte()); break;
                    case OperandType.ShortInlineVar: text.Append(' ').Append(reader.ReadByte()); break;
                    case OperandType.InlineVar: text.Append(' ').Append(reader.ReadUInt16()); break;
                    case OperandType.InlineBrTarget: case OperandType.InlineI: text.Append(' ').Append(reader.ReadInt32()); break;
                    case OperandType.InlineI8: text.Append(' ').Append(reader.ReadInt64()); break;
                    case OperandType.ShortInlineR: text.Append(' ').Append(reader.ReadSingle()); break;
                    case OperandType.InlineR: text.Append(' ').Append(reader.ReadDouble()); break;
                    case OperandType.InlineSwitch:
                        int count = reader.ReadInt32();
                        for (int i = 0; i < count; i++) text.Append(' ').Append(reader.ReadInt32());
                        break;
                    case OperandType.InlineString:
                        text.Append(" \"").Append(md.GetUserString(MetadataTokens.UserStringHandle(reader.ReadInt32() & 0xFFFFFF))).Append('"');
                        break;
                    default:   // InlineField, InlineMethod, InlineTok, InlineType, InlineSig
                        text.Append(' ').Append(names.Token(reader.ReadInt32()));
                        break;
                }
                text.Append('\n');
            }
            return text.ToString();
        }
    }

    // Namen fuer Typen, Member und Signaturen; zugleich Anbieter fuer den SignatureDecoder.
    private sealed class Names(MetadataReader md) : ISignatureTypeProvider<string, object?>
    {
        public string Token(int token)
        {
            if ((token >> 24) == 0x11) return "sig " + Signature(md.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle(token & 0xFFFFFF)));
            return Entity(MetadataTokens.EntityHandle(token));
        }

        private string Signature(StandaloneSignature signature) =>
            signature.GetKind() == StandaloneSignatureKind.Method ? Method(signature.DecodeMethodSignature(this, null)) : "locals";

        public string Entity(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition: return GetTypeFromDefinition(md, (TypeDefinitionHandle)handle, 0);
                case HandleKind.TypeReference: return GetTypeFromReference(md, (TypeReferenceHandle)handle, 0);
                case HandleKind.TypeSpecification: return md.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null);
                case HandleKind.FieldDefinition:
                    var field = md.GetFieldDefinition((FieldDefinitionHandle)handle);
                    return $"{GetTypeFromDefinition(md, field.GetDeclaringType(), 0)}::{md.GetString(field.Name)} : {field.DecodeSignature(this, null)}";
                case HandleKind.MethodDefinition:
                    var method = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                    return $"{GetTypeFromDefinition(md, method.GetDeclaringType(), 0)}::{md.GetString(method.Name)}{Method(method.DecodeSignature(this, null))}";
                case HandleKind.MemberReference:
                    var member = md.GetMemberReference((MemberReferenceHandle)handle);
                    string parent = member.Parent.IsNil ? "" : Entity(member.Parent);
                    string shape = member.GetKind() == MemberReferenceKind.Method
                        ? Method(member.DecodeMethodSignature(this, null)) : " : " + member.DecodeFieldSignature(this, null);
                    return $"{parent}::{md.GetString(member.Name)}{shape}";
                case HandleKind.MethodSpecification:
                    var spec = md.GetMethodSpecification((MethodSpecificationHandle)handle);
                    return $"{Entity(spec.Method)}<{string.Join(",", spec.DecodeSignature(this, null))}>";
                default: return handle.Kind.ToString();
            }
        }

        private static string Method(MethodSignature<string> signature) =>
            $"<{signature.GenericParameterCount}>({string.Join(",", signature.ParameterTypes)}) : {signature.ReturnType}";

        public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', shape.Rank - 1)}]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr" + Method(signature);
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => $"{genericType}<{string.Join(",", typeArguments)}>";
        public string GetGenericMethodParameter(object? context, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? context, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => $"{unmodifiedType} {(isRequired ? "modreq" : "modopt")}({modifier})";
        public string GetPinnedType(string elementType) => elementType + " pinned";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, context);

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeDefinition(handle);
            string name = reader.GetString(type.Name);
            var outer = type.GetDeclaringType();
            return outer.IsNil ? Qualified(reader.GetString(type.Namespace), name) : GetTypeFromDefinition(reader, outer, 0) + "/" + name;
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeReference(handle);
            string name = reader.GetString(type.Name);
            return type.ResolutionScope.Kind == HandleKind.TypeReference
                ? GetTypeFromReference(reader, (TypeReferenceHandle)type.ResolutionScope, 0) + "/" + name
                : Qualified(reader.GetString(type.Namespace), name);
        }

        private static string Qualified(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
    }
}
