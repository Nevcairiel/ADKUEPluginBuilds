// PdbLayout - reconstructs C++ class layouts (bases, members, bitfields, statics, virtual functions with
// vtable slots) from a PDB's type stream, using the textual output of `llvm-pdbutil dump --types`.
//
//   PdbLayout layout <pdb|types.txt> <TypeName>...            print the canonical layout of each type
//   PdbLayout diff   <reference pdb> <devkit pdb> <TypeName>...   print what changed between the two
//
// The dump of a .pdb is cached next to it in %PDBLAYOUT_CACHE% (default: %TEMP%\PdbLayoutCache).
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: PdbLayout layout <pdb> <type>... | diff <refpdb> <arkpdb> <type>... | difffile <refpdb> <arkpdb> <typesfile>"); return 2; }
        try
        {
            switch (args[0])
            {
                case "layout":
                {
                    var db = TypeDb.Open(args[1]);
                    foreach (var t in args.Skip(2)) Console.WriteLine(db.Describe(t)?.ToText() ?? $"// {t}: not found\n");
                    return 0;
                }
                case "patch":
                    return Patcher.Run(args);
                case "probe":
                    return Prober.Run(args);
                case "genclass":
                    return Generator.Run(args);
                case "gentypes":
                    return Generator.RunTypes(args);
                case "scan":
                    MacroIndex.Root = args.Length > 5 ? args[5] : null;
                    new HeaderFile(args[1]).Dump(args[3], args[4], int.Parse(args[2]));
                    return 0;
                case "audit":
                {
                    // audit <refpdb> <report> <arkpdb>... [--renames <file>] : every type of the reference PDB is looked up
                    // in the devkit PDBs (first one that defines it wins) and checked for changes of its own.
                    // --renames: data members that keep their vanilla name in the SDK ("<Type> <DevkitName> <SdkName>")
                    int ri = Array.IndexOf(args, "--renames");
                    if (ri >= 0)
                    {
                        foreach (var l in File.ReadAllLines(args[ri + 1]))
                        {
                            var f = l.Split('#')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (f.Length == 3) Differ.Renames[(f[0], f[1])] = f[2];
                        }
                        args = args.Where((_, k) => k != ri && k != ri + 1).ToArray();
                    }
                    var a = TypeDb.Open(args[1]);
                    var arks = args.Skip(3).Select(p => (Path.GetFileNameWithoutExtension(p), TypeDb.Open(p))).ToList();
                    using var w = new StreamWriter(args[2]);
                    int changed = 0, missing = 0, total = 0;
                    foreach (var uniq in a.CompleteUniqueNames().OrderBy(x => x, StringComparer.Ordinal))
                    {
                        var (mod, b) = arks.FirstOrDefault(x => x.Item2.Has(uniq));
                        total++;
                        if (b == null) { missing++; continue; }
                        var ta = a.Describe(uniq); var tb = b.Describe(uniq);
                        var d = Differ.Audit(ta, tb);
                        if (d.Length > 0) { changed++; w.Write($"[{mod}] " + d); }
                    }
                    Console.WriteLine($"audited {total} types: {changed} with own changes, {missing} not present in the devkit PDBs");
                    return 0;
                }
                case "diff":
                case "difffile":
                {
                    var a = TypeDb.Open(args[1]);
                    var b = TypeDb.Open(args[2]);
                    var names = args[0] == "diff" ? args.Skip(3) : File.ReadAllLines(args[3]).Where(l => l.Trim().Length > 0);
                    foreach (var t in names) Console.Write(Differ.Diff(t, a.Describe(t), b.Describe(t)));
                    return 0;
                }
            }
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        return 2;
    }
}

sealed class Member
{
    public string Kind;      // base, vbase, data, bitfield, static, vfptr, method, nested
    public string Name;
    public string Type;
    public long Offset = -1;
    public int BitOffset = -1, BitCount = -1;
    public string Access;
    public bool Virtual, Intro, Pure, Static, CompilerGenerated;
    public long VSlot = -1;   // byte offset into the vtable for intro virtuals
    public string Signature;  // for methods: canonical signature
    public string Key => Kind switch
    {
        "method" => $"method {Name}{Signature}",
        "nested" => $"nested {Name}",
        _ => $"{Kind} {Name}",
    };
}

sealed class TypeLayout
{
    public string Name, Kind;
    public long Size;
    public List<Member> Members = new();
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Kind} {Name}  // sizeof {Size} (0x{Size:X})");
        foreach (var m in Members) sb.AppendLine("  " + Format(m));
        return sb.ToString();
    }
    public static string Format(Member m) => m.Kind switch
    {
        "base" or "vbase" => $"{m.Kind,-8} +0x{m.Offset:X4} {m.Access} {m.Type}",
        "data" => $"data     +0x{m.Offset:X4} {m.Access,-9} {m.Type} {m.Name};",
        "bitfield" => $"bitfield +0x{m.Offset:X4} {m.Access,-9} {m.Type} {m.Name} : {m.BitCount};  // bit {m.BitOffset}",
        "static" => $"static         {m.Access,-9} static {m.Type} {m.Name};",
        "vfptr" => $"vfptr    +0x{m.Offset:X4}",
        "nested" => $"nested         {m.Name}",
        "enumerator" => $"enumerator     {m.Name}",
        "method" => $"method   {(m.VSlot >= 0 ? $"[vt+0x{m.VSlot:X3}]" : "         ")} {m.Access,-9} {(m.Static ? "static " : "")}{(m.Virtual ? "virtual " : "")}{m.Name}{m.Signature}{(m.Pure ? " = 0" : "")}{(m.Intro ? "  // intro" : "")}{(m.CompilerGenerated ? "  // compiler-generated" : "")}",
        _ => m.Kind,
    };
}

sealed class TypeDb
{
    // Record index -> byte offset of its header line in the dump file
    readonly Dictionary<int, long> offsets = new();
    // Complete UDT definitions by display name and unique name
    readonly Dictionary<string, int> completeByName = new();
    readonly Dictionary<string, int> completeByUnique = new();
    readonly FileStream file;
    readonly Dictionary<int, Rec> cache = new();

    sealed class Rec { public int Index; public string Kind; public string Name; public List<string> Lines = new(); }

    static readonly Regex Header = new(@"^\s+0x([0-9A-F]+) \| (LF_\w+) \[size = \d+\](?: `(.*)`)?\s*$", RegexOptions.Compiled);

    // llvm-pdbutil: LLVM_PDBUTIL is set by Common.ps1 (Initialize-PdbLayout); standalone runs use PATH
    public static string PdbUtil => Environment.GetEnvironmentVariable("LLVM_PDBUTIL") ?? "llvm-pdbutil.exe";

    public static TypeDb Open(string path)
    {
        string dump = path;
        if (path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
        {
            var cacheDir = Environment.GetEnvironmentVariable("PDBLAYOUT_CACHE") ?? Path.Combine(Path.GetTempPath(), "PdbLayoutCache");
            Directory.CreateDirectory(cacheDir);
            var fi = new FileInfo(path);
            dump = Path.Combine(cacheDir, $"{Path.GetFileNameWithoutExtension(path)}.{fi.Length:X}.{fi.LastWriteTimeUtc.Ticks:X}.types.txt");
            if (!File.Exists(dump))
            {
                var tool = PdbUtil;
                Console.WriteLine($"[PdbLayout] dumping {path} ...");
                var psi = new ProcessStartInfo(tool, $"dump --types \"{path}\"") { RedirectStandardOutput = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                using (var o = File.Create(dump + ".tmp")) p.StandardOutput.BaseStream.CopyTo(o);
                p.WaitForExit();
                if (p.ExitCode != 0) throw new Exception($"llvm-pdbutil failed on {path}");
                File.Move(dump + ".tmp", dump, true);
            }
        }
        return new TypeDb(dump);
    }

    TypeDb(string dump)
    {
        file = new FileStream(dump, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        // Index pass
        long pos = 0;
        int pendingIndex = -1; string pendingName = null; bool pendingUdt = false; string pendingUnique = null;
        foreach (var (line, off) in ReadLines(file, 0))
        {
            var m = Header.Match(line);
            if (m.Success)
            {
                int idx = Convert.ToInt32(m.Groups[1].Value, 16);
                offsets[idx] = off;
                var kind = m.Groups[2].Value;
                pendingUdt = kind is "LF_CLASS" or "LF_STRUCTURE" or "LF_UNION" or "LF_ENUM" or "LF_INTERFACE";
                pendingIndex = idx; pendingName = m.Groups[3].Value; pendingUnique = null;
                continue;
            }
            if (!pendingUdt) continue;
            var t = line.TrimStart();
            if (t.StartsWith("unique name: `")) pendingUnique = t[14..^1];
            else if (t.StartsWith("field list: ") || t.StartsWith("vtable: "))
            {
                if (!t.Contains("field list: <no type>"))
                {
                    completeByName.TryAdd(pendingName, pendingIndex);
                    if (pendingUnique != null) completeByUnique.TryAdd(pendingUnique, pendingIndex);
                }
                pendingUdt = false;
            }
            pos = off;
        }
    }

    static IEnumerable<(string, long)> ReadLines(FileStream fs, long start)
    {
        fs.Position = start;
        var buf = new byte[1 << 20];
        var lineBuf = new List<byte>(512);
        long lineStart = start, cur = start;
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                byte c = buf[i];
                cur++;
                if (c == (byte)'\n')
                {
                    int len = lineBuf.Count; if (len > 0 && lineBuf[len - 1] == '\r') len--;
                    yield return (Encoding.UTF8.GetString(lineBuf.GetRange(0, len).ToArray()), lineStart);
                    lineBuf.Clear(); lineStart = cur;
                }
                else lineBuf.Add(c);
            }
        }
        if (lineBuf.Count > 0) yield return (Encoding.UTF8.GetString(lineBuf.ToArray()), lineStart);
    }

    Rec Get(int index)
    {
        if (cache.TryGetValue(index, out var r)) return r;
        if (!offsets.TryGetValue(index, out var off)) return null;
        r = new Rec { Index = index };
        bool first = true;
        foreach (var (line, _) in ReadLines(file, off))
        {
            if (first)
            {
                var m = Header.Match(line); r.Kind = m.Groups[2].Value; r.Name = m.Groups[3].Value; first = false; continue;
            }
            if (Header.IsMatch(line) || line.Length == 0) break;
            r.Lines.Add(line.Trim());
        }
        cache[index] = r;
        return r;
    }

    static readonly Regex TypeRef = new(@"0x([0-9A-F]+)(?: \(([^)]*(?:\([^)]*\))?[^)]*)\))?", RegexOptions.Compiled);

    static string Prop(Rec r, string key)
    {
        foreach (var l in r.Lines)
        {
            int i = l.IndexOf(key + " = ", StringComparison.Ordinal);
            if (i < 0) continue;
            var rest = l[(i + key.Length + 3)..];
            int depth = 0, end = 0;
            for (; end < rest.Length; end++)
            {
                if (rest[end] == '(') depth++;
                else if (rest[end] == ')') depth--;
                else if (rest[end] == ',' && depth == 0) break;
            }
            return rest[..end].Trim();
        }
        return null;
    }

    static int ParseIndex(string s, out string simpleName)
    {
        simpleName = null;
        if (s == null || s.StartsWith("<no type>")) return -1;
        var m = TypeRef.Match(s);
        if (!m.Success) return -1;
        if (m.Groups[2].Success) simpleName = m.Groups[2].Value;
        return Convert.ToInt32(m.Groups[1].Value, 16);
    }

    int Resolve(int idx)
    {
        // forward reference -> complete definition
        var r = Get(idx);
        if (r == null) return idx;
        if (r.Kind is "LF_CLASS" or "LF_STRUCTURE" or "LF_UNION" or "LF_ENUM" or "LF_INTERFACE")
        {
            var fl = r.Lines.FirstOrDefault(l => l.Contains("field list: "));
            if (fl != null && fl.Contains("field list: <no type>"))
            {
                var u = r.Lines.FirstOrDefault(l => l.StartsWith("unique name: `"));
                if (u != null && completeByUnique.TryGetValue(u[14..^1], out var c)) return c;
                if (completeByName.TryGetValue(r.Name, out c)) return c;
            }
        }
        return idx;
    }

    public long SizeOf(int idx, string simple)
    {
        if (idx < 0x1000) return SimpleSize(simple ?? "");
        var r = Get(Resolve(idx));
        if (r == null) return 0;
        switch (r.Kind)
        {
            case "LF_POINTER": return 8;
            case "LF_MODIFIER": { var p = Prop(r, "referent"); int i = ParseIndex(p, out var s); return SizeOf(i, s); }
            case "LF_ARRAY": { var l = r.Lines[0]; var m = Regex.Match(l, @"size: (\d+)"); return long.Parse(m.Groups[1].Value); }
            case "LF_ENUM": { var l = r.Lines.First(x => x.Contains("underlying type: ")); int i = ParseIndex(l[(l.IndexOf("underlying type: ") + 17)..], out var s); return SizeOf(i, s); }
            case "LF_BITFIELD": { int i = ParseIndex(Prop(r, "type"), out var s); return SizeOf(i, s); }
            default:
            {
                var l = r.Lines.LastOrDefault(x => x.Contains("sizeof "));
                if (l == null) return 0;
                return long.Parse(Regex.Match(l, @"sizeof (\d+)").Groups[1].Value);
            }
        }
    }

    static long SimpleSize(string s)
    {
        if (s.EndsWith("*")) return 8;
        return s switch
        {
            "bool" or "char" or "signed char" or "unsigned char" or "char8_t" or "__int8" or "unsigned __int8" => 1,
            "short" or "unsigned short" or "wchar_t" or "char16_t" => 2,
            "int" or "unsigned" or "long" or "unsigned long" or "float" or "char32_t" or "HRESULT" => 4,
            "__int64" or "unsigned __int64" or "double" => 8,
            _ => 0,
        };
    }

    // Produces a C++-ish spelling of a type. 'decl' is spliced in for function pointers / arrays.
    public string Name(int idx, string simple)
    {
        if (idx < 0) return "<none>";
        if (idx < 0x1000) return simple switch { "unsigned" => "unsigned int", null => $"<simple 0x{idx:X}>", _ => simple };
        var r = Get(idx);
        if (r == null) return $"<0x{idx:X}>";
        switch (r.Kind)
        {
            case "LF_CLASS": case "LF_STRUCTURE": case "LF_UNION": case "LF_ENUM": case "LF_INTERFACE": return r.Name;
            case "LF_MODIFIER":
            {
                var mods = Prop(r, "modifiers") ?? "";
                int i = ParseIndex(Prop(r, "referent"), out var s);
                var inner = Name(i, s);
                var q = string.Join(" ", mods.Split('|').Select(x => x.Trim()).Where(x => x is "const" or "volatile"));
                return q.Length > 0 ? $"{q} {inner}" : inner;
            }
            case "LF_POINTER":
            {
                int i = ParseIndex(Prop(r, "referent"), out var s);
                var mode = Prop(r, "mode") ?? "pointer";
                var opts = Prop(r, "opts") ?? "";
                string suffix = mode switch { "ref" => "&", "rvalue ref" => "&&", _ => "*" };
                var target = Get(i);
                string inner;
                if (target != null && (target.Kind == "LF_PROCEDURE"))
                    inner = FuncSpelling(target, "(" + suffix + ")");
                else if (target != null && target.Kind == "LF_MFUNCTION")
                    inner = FuncSpelling(target, "(" + Name(ParseIndex(Prop(target, "class type"), out _), null) + "::" + suffix + ")");
                else if (mode.StartsWith("pointer to member data"))
                    inner = Name(i, s) + " " + Name(ParseIndex(Prop(r, "class type") ?? "", out _), null) + "::*";
                else
                    inner = Name(i, s) + suffix;
                if (opts.Contains("const")) inner += " const";
                return inner;
            }
            case "LF_ARRAY":
            {
                var m = Regex.Match(r.Lines[0], @"size: (\d+), index type: .*, element type: (0x[0-9A-F]+(?: \([^)]*\))?)");
                long total = long.Parse(m.Groups[1].Value);
                int e = ParseIndex(m.Groups[2].Value, out var es);
                long esz = SizeOf(e, es);
                return $"{Name(e, es)}[{(esz > 0 ? total / esz : 0)}]";
            }
            case "LF_PROCEDURE": return FuncSpelling(r, "");
            case "LF_MFUNCTION": return FuncSpelling(r, "");
            case "LF_BITFIELD": { int i = ParseIndex(Prop(r, "type"), out var s); return Name(i, s); }
            default: return $"<{r.Kind} 0x{idx:X}>";
        }
    }

    string ArgList(int listIdx)
    {
        var r = Get(listIdx);
        if (r == null) return "";
        var args = new List<string>();
        foreach (var l in r.Lines)
        {
            int i = ParseIndex(l, out var s);
            if (i >= 0) args.Add(Name(i, s));
        }
        return string.Join(", ", args);
    }

    string FuncSpelling(Rec f, string declarator)
    {
        int ri = ParseIndex(Prop(f, "return type"), out var rs);
        int li = ParseIndex(Prop(f, "param list"), out _);
        return $"{Name(ri, rs)} {declarator}({ArgList(li)})";
    }

    // Method signature in the form "(args) const -> ret"
    (string sig, bool isStatic) MethodSig(int mfuncIdx)
    {
        var f = Get(mfuncIdx);
        if (f == null) return ("(?)", false);
        int ri = ParseIndex(Prop(f, "return type"), out var rs);
        int li = ParseIndex(Prop(f, "param list"), out _);
        var thisType = Prop(f, "this type");
        bool isStatic = thisType == null || thisType.StartsWith("<no type>");
        bool isConst = false;
        if (!isStatic)
        {
            var tp = Get(ParseIndex(thisType, out _));
            if (tp != null && tp.Kind == "LF_POINTER")
            {
                var target = Get(ParseIndex(Prop(tp, "referent"), out _));
                if (target != null && target.Kind == "LF_MODIFIER" && (Prop(target, "modifiers") ?? "").Contains("const")) isConst = true;
            }
        }
        var ret = Name(ri, rs);
        var opts = Prop(f, "options") ?? "";
        bool isCtor = opts.Contains("constructor");
        return ($"({ArgList(li)}){(isConst ? " const" : "")}{(isCtor ? "" : " -> " + ret)}", isStatic);
    }

    public IEnumerable<string> CompleteUniqueNames() => completeByUnique.Keys;

    public string UniqueNameOf(int idx)
    {
        var r = Get(idx);
        if (r == null) return null;
        var u = r.Lines.FirstOrDefault(l => l.StartsWith("unique name: `"));
        return u != null ? u[14..^1] : r.Name;
    }
    public bool Has(string name) => completeByUnique.ContainsKey(name) || completeByName.ContainsKey(name);
    public string UniqueNameOfType(string name) => completeByName.TryGetValue(name, out var idx) ? UniqueNameOf(idx) : null;

    public TypeLayout Describe(string name)
    {
        int idx;
        if (!completeByName.TryGetValue(name, out idx) && !completeByUnique.TryGetValue(name, out idx)) return null;
        var r = Get(idx);
        var t = new TypeLayout { Name = r.Name, Kind = r.Kind switch { "LF_CLASS" => "class", "LF_STRUCTURE" => "struct", "LF_UNION" => "union", "LF_ENUM" => "enum", _ => r.Kind } };
        if (r.Kind == "LF_ENUM") t.Size = SizeOf(idx, null);
        else
        {
            var sl = r.Lines.LastOrDefault(x => x.Contains("sizeof "));
            t.Size = sl == null ? 0 : long.Parse(Regex.Match(sl, @"sizeof (\d+)").Groups[1].Value);
        }
        var flLine = r.Lines.First(l => l.Contains("field list: "));
        int fl = ParseIndex(flLine[(flLine.IndexOf("field list: ") + 12)..], out _);
        foreach (var flIdx in FieldLists(fl)) ParseFieldList(flIdx, t, r.Kind == "LF_ENUM");
        return t;
    }

    IEnumerable<int> FieldLists(int fl)
    {
        while (fl >= 0)
        {
            yield return fl;
            var r = Get(fl);
            int next = -1;
            foreach (var l in r.Lines) if (l.StartsWith("- LF_INDEX continuation = ")) next = ParseIndex(l[26..], out _);
            fl = next;
        }
    }

    static readonly Regex EntryHead = new(@"^- (LF_\w+)(?: \[(.*)\])?(?: (.*))?$", RegexOptions.Compiled);

    void ParseFieldList(int flIdx, TypeLayout t, bool isEnum)
    {
        var r = Get(flIdx);
        for (int i = 0; i < r.Lines.Count; i++)
        {
            var m = EntryHead.Match(r.Lines[i]);
            if (!m.Success) continue;
            var kind = m.Groups[1].Value;
            var inl = m.Groups[2].Value;
            var tail = m.Groups[3].Value;
            string next = i + 1 < r.Lines.Count && !r.Lines[i + 1].StartsWith("- ") ? r.Lines[i + 1] : "";
            string F(string src, string key)
            {
                var mm = Regex.Match(src, Regex.Escape(key) + @" = (`[^`]*`|0x[0-9A-F]+(?: \((?:[^()]|\([^()]*\))*\))?|[^,\]]+)");
                return mm.Success ? mm.Groups[1].Value.Trim('`') : null;
            }
            switch (kind)
            {
                case "LF_ENUMERATE":
                    t.Members.Add(new Member { Kind = "enumerator", Name = inl });
                    break;
                case "LF_BCLASS":
                case "LF_VBCLASS":
                case "LF_IVBCLASS":
                {
                    int bi = ParseIndex(F(next, "type"), out _);
                    long off = long.TryParse(F(next, "offset"), out var o) ? o : 0;
                    t.Members.Add(new Member { Kind = kind == "LF_BCLASS" ? "base" : "vbase", Type = Name(bi, null), Offset = off, Access = F(next, "attrs") });
                    break;
                }
                case "LF_VFUNCTAB":
                    t.Members.Add(new Member { Kind = "vfptr", Offset = 0 });
                    break;
                case "LF_MEMBER":
                {
                    var nm = F(inl, "name");
                    int ti = ParseIndex(F(inl, "Type"), out var ts);
                    long off = long.Parse(F(inl, "offset"));
                    var acc = F(inl, "attrs");
                    var tr = ti >= 0x1000 ? Get(ti) : null;
                    if (tr != null && tr.Kind == "LF_BITFIELD")
                    {
                        var bm = Regex.Match(tr.Lines[0], @"type = (0x[0-9A-F]+(?: \([^)]*\))?), bit offset = (\d+), # bits = (\d+)");
                        int bt = ParseIndex(bm.Groups[1].Value, out var bts);
                        t.Members.Add(new Member { Kind = "bitfield", Name = nm, Type = Name(bt, bts), Offset = off, BitOffset = int.Parse(bm.Groups[2].Value), BitCount = int.Parse(bm.Groups[3].Value), Access = acc });
                    }
                    else t.Members.Add(new Member { Kind = "data", Name = nm, Type = Name(ti, ts), Offset = off, Access = acc });
                    break;
                }
                case "LF_STMEMBER":
                {
                    int ti = ParseIndex(F(inl, "type"), out var ts);
                    t.Members.Add(new Member { Kind = "static", Name = F(inl, "name"), Type = Name(ti, ts), Access = F(inl, "attrs") });
                    break;
                }
                case "LF_NESTTYPE":
                    t.Members.Add(new Member { Kind = "nested", Name = F(inl, "name") });
                    break;
                case "LF_ONEMETHOD":
                    AddMethod(t, F(inl, "name"), next);
                    break;
                case "LF_METHOD":
                {
                    var nm = F(inl, "name");
                    int li = ParseIndex(F(inl, "overload list"), out _);
                    var ml = Get(li);
                    foreach (var l in ml.Lines)
                        if (l.StartsWith("- Method [")) AddMethod(t, nm, l);
                    break;
                }
            }
        }
    }

    void AddMethod(TypeLayout t, string name, string props)
    {
        var tm = Regex.Match(props, @"type = (0x[0-9A-F]+)");
        var vm = Regex.Match(props, @"vftable offset = (-?\d+)");
        var am = Regex.Match(props, @"attrs = ([^\]]*)");
        var attrs = am.Success ? am.Groups[1].Value : "";
        var (sig, isStatic) = MethodSig(Convert.ToInt32(tm.Groups[1].Value, 16));
        var acc = attrs.Split(' ')[0];
        t.Members.Add(new Member
        {
            Kind = "method", Name = name, Signature = sig, Access = acc,
            Virtual = attrs.Contains("virtual"), Intro = attrs.Contains("intro"), Pure = attrs.Contains("pure"),
            Static = attrs.Contains("static") || (isStatic && !attrs.Contains("virtual")), CompilerGenerated = attrs.Contains("compiler-generated"),
            VSlot = vm.Success ? long.Parse(vm.Groups[1].Value) : -1,
        });
    }
}

static class Differ
{
    public static string Diff(string name, TypeLayout a, TypeLayout b)
    {
        var sb = new StringBuilder();
        if (a == null || b == null) { sb.AppendLine($"=== {name}: {(a == null ? "missing in reference" : "")} {(b == null ? "missing in devkit" : "")}"); return sb.ToString(); }
        var aKeys = a.Members.Where(Relevant).ToList();
        var bKeys = b.Members.Where(Relevant).ToList();
        var lines = new List<string>();
        // data layout: compare by key, report offset changes as well
        var aMap = aKeys.GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.First());
        var bMap = bKeys.GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var m in bKeys) if (!aMap.ContainsKey(m.Key)) lines.Add("+ " + TypeLayout.Format(m));
        foreach (var m in aKeys) if (!bMap.ContainsKey(m.Key)) lines.Add("- " + TypeLayout.Format(m));
        foreach (var m in aKeys)
        {
            if (!bMap.TryGetValue(m.Key, out var o)) continue;
            var fa = TypeLayout.Format(m); var fb = TypeLayout.Format(o);
            if (m.Kind is "data" or "bitfield" or "base" or "static" ? fa != fb : (m.Kind == "method" && (m.VSlot != o.VSlot || m.Access != o.Access || m.Virtual != o.Virtual)))
                lines.Add($"~ {fa}\n    => {fb}");
        }
        if (a.Size != b.Size || lines.Count > 0)
        {
            sb.AppendLine($"=== {a.Kind} {name}: sizeof {a.Size} -> {b.Size} ({b.Size - a.Size:+#;-#;0})");
            foreach (var l in lines) sb.AppendLine("  " + l);
        }
        return sb.ToString();
    }

    static bool Relevant(Member m) => !(m.Kind == "method" && m.CompilerGenerated && !m.Virtual);

    // Changes that originate in the type itself (not merely offsets shifted by a changed base/member type).
    public static readonly Dictionary<(string type, string member), string> Renames = new();

    // empty bases (no data, no vtable, e.g. ARK's PrimalLiveTunablesMixin<T>) share their offset with a member
    // (empty base optimization) and do not affect the layout
    static TypeLayout WithoutEmptyBases(TypeLayout t)
    {
        var empty = t.Members.Where(m => m.Kind == "base" && m.Offset > 0
            && t.Members.Any(o => o != m && o.Kind is "data" or "bitfield" or "base" && o.Offset == m.Offset)).ToHashSet();
        if (empty.Count == 0) return t;
        return new TypeLayout { Name = t.Name, Kind = t.Kind, Size = t.Size, Members = t.Members.Where(m => !empty.Contains(m)).ToList() };
    }

    // data members the SDK keeps under their vanilla name (Config\AbiRenames.txt)
    public static TypeLayout WithRenames(TypeLayout t)
    {
        if (!t.Members.Any(m => m.Kind is "data" or "bitfield" && Renames.ContainsKey((t.Name, m.Name)))) return t;
        return new TypeLayout { Name = t.Name, Kind = t.Kind, Size = t.Size, Members = t.Members.Select(m =>
            m.Kind is "data" or "bitfield" && Renames.TryGetValue((t.Name, m.Name), out var n)
                ? new Member { Kind = m.Kind, Name = n, Type = m.Type, Offset = m.Offset, BitOffset = m.BitOffset, BitCount = m.BitCount, Access = m.Access } : m).ToList() };
    }

    // members the SDK declares as opaque storage (uint8 Name[N], for ARK-only nested or unnamed types) match the
    // devkit's member of the same name and offset when that one occupies the same N bytes (up to alignment padding)
    static TypeLayout WithOpaqueMembers(TypeLayout a, TypeLayout b)
    {
        Member Counterpart(Member m)
        {
            var arr = m.Kind == "data" ? Regex.Match(m.Type, @"^unsigned char\[(\d+)\]$") : Match.Empty;
            if (!arr.Success) return null;
            var o = b.Members.FirstOrDefault(x => x.Kind == "data" && x.Name == m.Name && x.Offset == m.Offset && x.Type != m.Type);
            if (o == null) return null;
            long end = m.Offset + long.Parse(arr.Groups[1].Value);
            long next = b.Members.Where(x => x.Kind is "data" or "bitfield" or "base" && x.Offset > o.Offset).Select(x => x.Offset).DefaultIfEmpty(b.Size).Min();
            return next >= end && next <= (end + 7) / 8 * 8 ? o : null;
        }
        if (!a.Members.Any(m => Counterpart(m) != null)) return a;
        return new TypeLayout { Name = a.Name, Kind = a.Kind, Size = a.Size, Members = a.Members.Select(m =>
            Counterpart(m) is Member o ? new Member { Kind = m.Kind, Name = m.Name, Type = o.Type, Offset = m.Offset, Access = m.Access } : m).ToList() };
    }

    // access is not part of the key: MSVC lays out members in declaration order regardless of access
    static string D(Member m) => m.Kind switch
    {
        // offsets are part of the key: a member with the right name but at another offset is an ABI difference too
        "base" or "vbase" => $"+0x{m.Offset:X4} {m.Kind} {m.Type}",
        "vfptr" => "vfptr",
        "bitfield" => $"+0x{m.Offset:X4}.{m.BitOffset} {m.Type} {m.Name} : {m.BitCount}",
        _ => $"+0x{m.Offset:X4} {m.Type} {m.Name}",
    };
    static bool IsData(Member m) => m.Kind is "base" or "vbase" or "vfptr" or "data" or "bitfield";
    static List<string> IntroVirtuals(TypeLayout t) =>
        t.Members.Where(m => m.Kind == "method" && m.Intro).OrderBy(m => m.VSlot).Select(m => $"{m.Name}{m.Signature}").ToList();

    // Where the layout-independence probe goes: the first data item (base, vfptr, member) of the SDK layout that
    // differs from the devkit's (null when one is a prefix of the other), whether the devkit only appends members or
    // grows (AtEnd) and whether the introduced virtuals differ.
    public static (Member First, bool AtEnd, bool Virtuals) ProbePoint(TypeLayout a, TypeLayout b)
    {
        a = WithOpaqueMembers(WithoutEmptyBases(a), b);
        b = WithRenames(WithoutEmptyBases(b));
        var x = a.Members.Where(IsData).ToList();
        var y = b.Members.Where(IsData).Select(D).ToList();
        int i = 0;
        while (i < x.Count && i < y.Count && D(x[i]) == y[i]) i++;
        var first = i < x.Count ? x[i] : null;
        return (first, first == null && (y.Count > x.Count || a.Size != b.Size), !IntroVirtuals(a).SequenceEqual(IntroVirtuals(b)));
    }

    public static string Audit(TypeLayout a, TypeLayout b)
    {
        a = WithOpaqueMembers(WithoutEmptyBases(a), b);
        b = WithRenames(WithoutEmptyBases(b));
        var sections = new List<string>();
        void Seq(string title, List<string> x, List<string> y)
        {
            if (x.SequenceEqual(y)) return;
            sections.Add($"  {title}:\n" + string.Join("\n", Lcs(x, y).Select(l => "    " + l)));
        }
        // ARK-only types generated with opaque storage (ArkData*, Tools\New-ArkClassHeaders.ps1 / ArkTypes.h): their
        // layout is right when the bases and the size match the devkit
        bool opaque = a.Members.Any(m => m.Kind == "data" && m.Name.StartsWith("ArkData"));
        static bool IsBase(Member m) => m.Kind is "base" or "vbase" or "vfptr";
        if (opaque)
        {
            // opaque structs without bases (ArkTypes.h, e.g. dynamic delegates): size and polymorphism decide the layout
            bool aBases = a.Members.Any(IsBase), bPoly = b.Members.Any(m => m.Kind == "vfptr");
            if (aBases || bPoly) Seq("bases", a.Members.Where(IsBase).Select(D).ToList(), b.Members.Where(IsBase).Select(D).ToList());
            if (a.Size != b.Size) sections.Add($"  size:\n    - {a.Size}\n    + {b.Size}");
        }
        else if (a.Kind == "enum" && a.Size != b.Size)
        {
            // underlying type changed (members of this enum type move)
            sections.Add($"  size:\n    - {a.Size}\n    + {b.Size}");
        }
        else Seq("data", a.Members.Where(IsData).Select(D).ToList(), b.Members.Where(IsData).Select(D).ToList());
        Seq("introduced virtuals (vtable order)", IntroVirtuals(a), IntroVirtuals(b));
        Seq("enumerators", a.Members.Where(m => m.Kind == "enumerator").Select(m => m.Name).ToList(), b.Members.Where(m => m.Kind == "enumerator").Select(m => m.Name).ToList());
        // methods: signature/access/virtual changes of methods existing in both, removed and added methods
        var am = a.Members.Where(m => m.Kind == "method" && !m.CompilerGenerated).GroupBy(m => m.Name + m.Signature).ToDictionary(g => g.Key, g => g.First());
        var bm = b.Members.Where(m => m.Kind == "method" && !m.CompilerGenerated).GroupBy(m => m.Name + m.Signature).ToDictionary(g => g.Key, g => g.First());
        var ml = new List<string>();
        foreach (var (k, m) in am)
        {
            if (!bm.TryGetValue(k, out var o)) ml.Add("    - " + TypeLayout.Format(m));
            else if (m.Access != o.Access || m.Virtual != o.Virtual || m.Static != o.Static) ml.Add($"    ~ {TypeLayout.Format(m)}\n      => {TypeLayout.Format(o)}");
        }
        foreach (var (k, m) in bm) if (!am.ContainsKey(k)) ml.Add("    + " + TypeLayout.Format(m));
        if (ml.Count > 0) sections.Add("  methods:\n" + string.Join("\n", ml));
        var st = a.Members.Where(m => m.Kind == "static").Select(m => $"{m.Access} {m.Type} {m.Name}").ToList();
        var stb = b.Members.Where(m => m.Kind == "static").Select(m => $"{m.Access} {m.Type} {m.Name}").ToList();
        if (!st.ToHashSet().SetEquals(stb)) sections.Add("  statics:\n" + string.Join("\n", st.Except(stb).Select(s => "    - " + s).Concat(stb.Except(st).Select(s => "    + " + s))));
        if (sections.Count == 0 && a.Size == b.Size) return "";
        return $"{a.Kind} {a.Name}: sizeof {a.Size} -> {b.Size}\n" + string.Join("\n", sections) + "\n\n";
    }

    // Minimal LCS-based line diff
    static IEnumerable<string> Lcs(List<string> x, List<string> y)
    {
        int n = x.Count, m = y.Count;
        var t = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--) for (int j = m - 1; j >= 0; j--) t[i, j] = x[i] == y[j] ? t[i + 1, j + 1] + 1 : Math.Max(t[i + 1, j], t[i, j + 1]);
        int a = 0, b = 0; var res = new List<string>();
        while (a < n || b < m)
        {
            if (a < n && b < m && x[a] == y[b]) { res.Add("  " + x[a]); a++; b++; }
            else if (b < m && (a == n || t[a, b + 1] >= t[a + 1, b])) { res.Add("+ " + y[b]); b++; }
            else { res.Add("- " + x[a]); a++; }
        }
        // collapse long unchanged runs
        var outp = new List<string>();
        for (int i = 0; i < res.Count; i++)
        {
            bool near = false;
            for (int k = Math.Max(0, i - 2); k <= Math.Min(res.Count - 1, i + 2); k++) if (!res[k].StartsWith("  ")) near = true;
            if (near) outp.Add(res[i]);
            else if (outp.Count == 0 || outp[^1] != "  ...") outp.Add("  ...");
        }
        return outp;
    }
}
