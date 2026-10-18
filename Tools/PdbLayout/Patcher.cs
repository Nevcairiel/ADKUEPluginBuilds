// Patcher: turns the ABI differences between a reference PDB (built against the SDK headers) and the ARK devkit
// PDBs into edits of the SDK headers.
//
//   PdbLayout patch <refpdb> <sdkRoot> <engineRootInRefPdb> <report> <arkpdb>...
//
// Handled automatically: inserted/removed data members and bitfields, inserted/removed introduced virtual
// functions, inserted/removed/renumbered enumerators, access changes of methods, SLATE_BEGIN_ARGS arguments.
// Everything the patcher cannot place is written to the report as MANUAL so it can be done by hand.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

static class Patcher
{
    public static HashSet<string> LoadExclusions(ref string[] args)
    {
        var set = new HashSet<string>();
        int i = Array.IndexOf(args, "--exclude");
        if (i < 0) return set;
        foreach (var l in File.ReadAllLines(args[i + 1]))
        {
            var t = l.Split('#')[0].Trim();
            if (t.Length > 0) set.Add(t);
        }
        args = args.Where((_, k) => k != i && k != i + 1).ToArray();
        return set;
    }

    public static int Run(string[] args)
    {
        var excluded = LoadExclusions(ref args);
        var refPdb = args[1];
        var sdkRoot = Path.GetFullPath(args[2]);
        var refEngineRoot = args[3].TrimEnd('\\', '/');
        var reportPath = args[4];
        MacroIndex.Root = sdkRoot;
        var refDb = TypeDb.Open(refPdb);
        var arks = args.Skip(5).Select(p => (Path.GetFileNameWithoutExtension(p), TypeDb.Open(p))).ToList();
        var locations = SourceLocations.Load(refPdb, refDb);

        var report = new StringBuilder();
        var files = new Dictionary<string, HeaderFile>(StringComparer.OrdinalIgnoreCase);
        int patchedTypes = 0, manual = 0;

        foreach (var uniq in refDb.CompleteUniqueNames().OrderBy(x => x, StringComparer.Ordinal))
        {
            var (mod, b) = arks.FirstOrDefault(x => x.Item2.Has(uniq));
            if (b == null) continue;
            var ta = refDb.Describe(uniq);
            var tb = b.Describe(uniq);
            if (ta == null || tb == null || ta.Name.Contains("<unnamed") || ta.Name.Contains("<lambda")) continue;
            var ops = Ops.Compute(ta, tb);
            if (ops.Count == 0) continue;

            var typeReport = new StringBuilder();
            typeReport.AppendLine($"[{mod}] {ta.Kind} {ta.Name}: sizeof {ta.Size} -> {tb.Size}");
            if (excluded.Contains(ta.Name))
            {
                typeReport.AppendLine("  EXCLUDED (Config\\AbiExclusions.txt)");
                report.Append(typeReport).AppendLine(); continue;
            }
            if (!locations.TryGetValue(uniq, out var loc))
            {
                typeReport.AppendLine("  MANUAL: no source location in reference PDB");
                foreach (var op in ops) typeReport.AppendLine("    " + op);
                manual++; report.Append(typeReport).AppendLine(); continue;
            }
            var path = loc.File;
            if (path.StartsWith(refEngineRoot, StringComparison.OrdinalIgnoreCase)) path = Path.Combine(sdkRoot, path.Substring(refEngineRoot.Length).TrimStart('\\', '/'));
            else if (!path.StartsWith(sdkRoot, StringComparison.OrdinalIgnoreCase))
            {
                typeReport.AppendLine($"  MANUAL: defined outside the engine ({loc.File}:{loc.Line})");
                foreach (var op in ops) typeReport.AppendLine("    " + op);
                manual++; report.Append(typeReport).AppendLine(); continue;
            }
            typeReport.AppendLine($"  at {path}:{loc.Line}");
            if (!files.TryGetValue(path, out var hf)) files[path] = hf = new HeaderFile(path);

            if (ta.Name.Contains('<'))
            {
                typeReport.AppendLine("  MANUAL: template instantiation, patch the template by hand");
                foreach (var op in ops) typeReport.AppendLine("    " + op);
                manual++; report.Append(typeReport).AppendLine(); continue;
            }
            var results = hf.Apply(ta, loc.Line, ops);
            foreach (var r in results) typeReport.AppendLine("  " + r);
            if (results.Any(r => r.StartsWith("MANUAL"))) manual++;
            patchedTypes++;
            report.Append(typeReport).AppendLine();
        }
        foreach (var f in files.Values) f.Save();
        File.WriteAllText(reportPath, report.ToString());
        Console.WriteLine($"patched {patchedTypes} types in {files.Values.Count(f => f.Changed)} headers, {manual} need manual work -> {reportPath}");
        return 0;
    }
}

static class Prober
{
    // probe <refpdb> <sdkRoot> <engineRootInRefPdb> <exclusionsFile>
    public static int Run(string[] args)
    {
        var refPdb = args[1];
        var sdkRoot = Path.GetFullPath(args[2]);
        var refEngineRoot = args[3].TrimEnd('\\', '/');
        var names = File.ReadAllLines(args[4]).Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0).ToHashSet();
        MacroIndex.Root = sdkRoot;
        var db = TypeDb.Open(refPdb);
        var locations = SourceLocations.Load(refPdb, db);
        var files = new Dictionary<string, HeaderFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var uniq in db.CompleteUniqueNames())
        {
            var t = db.Describe(uniq);
            if (t == null || !names.Contains(t.Name)) continue;
            names.Remove(t.Name);
            if (t.Kind == "enum") { Console.WriteLine($"  {t.Name}: enum, not probed"); continue; }
            if (!locations.TryGetValue(uniq, out var loc)) { Console.WriteLine($"  {t.Name}: no location"); continue; }
            var path = loc.File.StartsWith(refEngineRoot, StringComparison.OrdinalIgnoreCase) ? Path.Combine(sdkRoot, loc.File.Substring(refEngineRoot.Length).TrimStart('\\', '/')) : loc.File;
            if (!files.TryGetValue(path, out var hf)) files[path] = hf = new HeaderFile(path);
            bool poly = t.Members.Any(m => m.Kind == "vfptr" || (m.Kind == "method" && m.Virtual));
            Console.WriteLine($"  {t.Name}: {hf.InjectProbe(t, loc.Line, poly)}{(poly ? " (+virtual)" : "")}");
        }
        foreach (var n in names) Console.WriteLine($"  {n}: not referenced by the plugin's PDB");
        foreach (var f in files.Values) f.Save();
        return 0;
    }
}

// ------------------------------------------------------------------------------------------------------------
// Source locations of UDTs (LF_UDT_MOD_SRC_LINE / LF_UDT_SRC_LINE + string table)
// ------------------------------------------------------------------------------------------------------------
sealed record SourceLocation(string File, int Line);

static class SourceLocations
{
    public static Dictionary<string, SourceLocation> Load(string pdb, TypeDb db)
    {
        var tool = TypeDb.PdbUtil;
        var strings = new Dictionary<int, string>();
        foreach (var l in RunLines(tool, $"dump --string-table \"{pdb}\""))
        {
            var m = Regex.Match(l, @"^\s*(\d+) \| '(.*)'$");
            if (m.Success) strings[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value;
        }
        var result = new Dictionary<string, SourceLocation>();
        foreach (var l in RunLines(tool, $"dump --ids \"{pdb}\""))
        {
            var m = Regex.Match(l, @"udt = 0x([0-9A-F]+),(?: mod = \d+,)? file = (\d+), line = (\d+)");
            if (!m.Success) continue;
            var uniq = db.UniqueNameOf(Convert.ToInt32(m.Groups[1].Value, 16));
            if (uniq == null || !strings.TryGetValue(int.Parse(m.Groups[2].Value), out var file)) continue;
            result.TryAdd(uniq, new SourceLocation(file, int.Parse(m.Groups[3].Value)));
        }
        return result;
    }

    static IEnumerable<string> RunLines(string tool, string args)
    {
        var psi = new ProcessStartInfo(tool, args) { RedirectStandardOutput = true, UseShellExecute = false };
        using var p = Process.Start(psi);
        string line;
        while ((line = p.StandardOutput.ReadLine()) != null) yield return line;
        p.WaitForExit();
    }
}

// ------------------------------------------------------------------------------------------------------------
// Edit operations
// ------------------------------------------------------------------------------------------------------------
sealed class Op
{
    public string Kind;          // insert-data, remove-data, insert-virtual, remove-virtual, insert-enum, remove-enum, set-enum, access
    public Member Member;        // ARK member (insert/access) or reference member (remove)
    public Member Anchor;        // reference member after which to insert (null = before first of its kind)
    public Member Successor;     // next reference member following the inserted one (fallback placement)
    public Member Replaces;      // insert-data: removed reference member with the same name (type changed in place)
    public Member Virtualizes;   // insert-virtual: non-virtual reference method that became virtual
    public Member OverloadOf;    // insert-virtual: existing virtual overload with the same name
    public bool OverloadFirst;   // ... and the new overload precedes it in the vtable
    public long Value;           // enum value
    public override string ToString() => $"{Kind} {(Member == null ? "" : TypeLayout.Format(Member))}{(Anchor != null ? $"   after {Anchor.Name}" : Kind.StartsWith("insert") ? "   at start" : "")}";
}

static class Ops
{
    static bool IsData(Member m) => m.Kind is "data" or "bitfield";
    static string DataKey(Member m) => m.Kind == "bitfield" ? $"{m.Type} {m.Name}:{m.BitCount}" : $"{m.Type} {m.Name}";
    static string VirtKey(Member m) => m.Name + m.Signature;

    public static List<Op> Compute(TypeLayout a, TypeLayout b)
    {
        var ops = new List<Op>();
        // ---- data members (access ignored: MSVC lays out members in declaration order regardless of access)
        Sequence(a.Members.Where(IsData).ToList(), b.Members.Where(IsData).ToList(), DataKey, "data", ops);
        // ---- introduced virtual functions in vtable order
        // (__vecDelDtor is compiler generated and has no declaration to anchor on)
        Sequence(a.Members.Where(m => m.Kind == "method" && m.Intro && m.Name != "__vecDelDtor").OrderBy(m => m.VSlot).ToList(),
                 b.Members.Where(m => m.Kind == "method" && m.Intro && m.Name != "__vecDelDtor").OrderBy(m => m.VSlot).ToList(), VirtKey, "virtual", ops);
        // ---- enumerators
        if (a.Kind == "enum")
        {
            var ea = a.Members.Where(m => m.Kind == "enumerator").Select(Enumerator.Parse).ToList();
            var eb = b.Members.Where(m => m.Kind == "enumerator").Select(Enumerator.Parse).ToList();
            var aByName = ea.ToDictionary(e => e.Name, e => e);
            var bNames = eb.Select(e => e.Name).ToHashSet();
            Enumerator prevCommon = null;
            foreach (var e in eb)
            {
                if (aByName.TryGetValue(e.Name, out var old))
                {
                    if (old.Value != e.Value) ops.Add(new Op { Kind = "set-enum", Member = new Member { Kind = "enumerator", Name = e.Name }, Value = e.Value });
                    prevCommon = old;
                }
                else ops.Add(new Op { Kind = "insert-enum", Member = new Member { Kind = "enumerator", Name = e.Name }, Anchor = prevCommon == null ? null : new Member { Name = prevCommon.Name }, Value = e.Value });
            }
            foreach (var e in ea) if (!bNames.Contains(e.Name)) ops.Add(new Op { Kind = "remove-enum", Member = new Member { Kind = "enumerator", Name = e.Name } });
        }
        // ---- members whose type changed: replace in place; methods that became virtual: move the declaration
        foreach (var ins in ops.Where(o => o.Kind == "insert-data"))
            ins.Replaces = ops.FirstOrDefault(r => r.Kind == "remove-data" && r.Member.Name == ins.Member.Name)?.Member;
        var nonVirtual = a.Members.Where(m => m.Kind == "method" && !m.Virtual).GroupBy(VirtKey).ToDictionary(g => g.Key, g => g.First());
        foreach (var ins in ops.Where(o => o.Kind == "insert-virtual"))
        {
            if (nonVirtual.TryGetValue(VirtKey(ins.Member), out var nv)) ins.Virtualizes = nv;
            // MSVC groups overloaded virtuals and lays them out in reverse declaration order
            var sibling = b.Members.FirstOrDefault(m => m.Kind == "method" && m.Intro && m.Name == ins.Member.Name && m.Signature != ins.Member.Signature
                                                        && a.Members.Any(r => r.Kind == "method" && r.Name == m.Name && r.Signature == m.Signature));
            if (sibling != null) { ins.OverloadOf = sibling; ins.OverloadFirst = ins.Member.VSlot < sibling.VSlot; }
        }
        // removals first: replacements are placed after the removed declaration
        var ordered = ops.OrderBy(o => o.Kind.StartsWith("remove") ? 0 : 1).ToList();   // stable
        ops.Clear(); ops.AddRange(ordered);
        // ---- access / virtual changes of methods present in both
        var am = a.Members.Where(m => m.Kind == "method" && !m.CompilerGenerated).GroupBy(VirtKey).ToDictionary(g => g.Key, g => g.First());
        foreach (var m in b.Members.Where(m => m.Kind == "method" && !m.CompilerGenerated))
            if (am.TryGetValue(VirtKey(m), out var o) && (o.Access != m.Access || o.Static != m.Static))
                ops.Add(new Op { Kind = "access", Member = m, Anchor = o });
        return ops;
    }

    static void Sequence(List<Member> x, List<Member> y, Func<Member, string> key, string what, List<Op> ops)
    {
        var xs = x.Select(key).ToList(); var ys = y.Select(key).ToList();
        if (xs.SequenceEqual(ys)) return;
        int n = xs.Count, m = ys.Count;
        var t = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--) for (int j = m - 1; j >= 0; j--) t[i, j] = xs[i] == ys[j] ? t[i + 1, j + 1] + 1 : Math.Max(t[i + 1, j], t[i, j + 1]);
        int a = 0, b = 0; Member lastCommon = null;
        while (a < n || b < m)
        {
            if (a < n && b < m && xs[a] == ys[b]) { lastCommon = x[a]; a++; b++; }
            else if (b < m && (a == n || t[a, b + 1] >= t[a + 1, b]))
            {
                // successor: next reference element that is kept (common to both sequences)
                Member succ = null;
                var remainingArk = ys.Skip(b + 1).ToHashSet();
                for (int k = a; k < n && succ == null; k++) if (remainingArk.Contains(xs[k])) succ = x[k];
                ops.Add(new Op { Kind = "insert-" + what, Member = y[b], Anchor = lastCommon, Successor = succ }); b++;
            }
            else { ops.Add(new Op { Kind = "remove-" + what, Member = x[a] }); a++; }
        }
    }
}

sealed class Enumerator
{
    public string Name; public long Value;
    public static Enumerator Parse(Member m)
    {
        var i = m.Name.LastIndexOf(" = ", StringComparison.Ordinal);
        return new Enumerator { Name = m.Name[..i], Value = long.Parse(m.Name[(i + 3)..]) };
    }
}

// ------------------------------------------------------------------------------------------------------------
// C++ spelling of PDB type names
// ------------------------------------------------------------------------------------------------------------
static class CppType
{
    public static string Clean(string t)
    {
        string prev;
        do
        {
            prev = t;
            t = t.Replace(" __cdecl", "").Replace("(void)", "()");
            t = Regex.Replace(t, @"\benum (?=[A-Za-z_])", "");
            t = Regex.Replace(t, @",TSizedDefaultAllocator<32> >", ">");
            t = Regex.Replace(t, @",FDefaultDelegateUserPolicy>", ">");
            t = Regex.Replace(t, @",FWeakObjectPtr>", ">");
            t = Regex.Replace(t, @"\b(TSharedPtr|TSharedRef|TWeakPtr|TSharedFromThis)<(.+?),1>", "$1<$2>");
            t = Regex.Replace(t, @"\b(TSharedPtr|TSharedRef|TWeakPtr|TSharedFromThis)<(.+?),0>", "$1<$2, ESPMode::NotThreadSafe>");
            t = Regex.Replace(t, @",FDefaultSetAllocator,TDefaultMapHashableKeyFuncs<[^<>]*(?:<[^<>]*>[^<>]*)*,0> >", ">");
            t = Regex.Replace(t, @",DefaultKeyFuncs<[^<>]*(?:<[^<>]*>[^<>]*)*,0>,FDefaultSetAllocator>", ">");
            t = Regex.Replace(t, @"\bUE::Math::TVector2<double>", "FVector2D");
            t = Regex.Replace(t, @"\bUE::Math::TVector<double>", "FVector");
            t = Regex.Replace(t, @"\bUE::Math::TRotator<double>", "FRotator");
            t = Regex.Replace(t, @"\bUE::Math::TQuat<double>", "FQuat");
            t = Regex.Replace(t, @"\bUE::Math::TTransform<double>", "FTransform");
            t = Regex.Replace(t, @"\bUE::Math::TBox<double>", "FBox");
            t = Regex.Replace(t, @"\bUE::Math::TMatrix<double>", "FMatrix");
            t = Regex.Replace(t, @"\bUE::Math::TIntPoint<int>", "FIntPoint");
            t = Regex.Replace(t, @"\bunsigned __int64\b", "uint64");
            t = Regex.Replace(t, @"\b__int64\b", "int64");
            t = Regex.Replace(t, @"\bunsigned char\b", "uint8");
            t = Regex.Replace(t, @"\bunsigned short\b", "uint16");
            t = Regex.Replace(t, @"\bunsigned int\b", "uint32");
            t = Regex.Replace(t, @"\bwchar_t\b", "TCHAR");
            t = t.Replace(" >", ">").Replace(" const &", " const&").Replace(" const *", " const*");
        } while (t != prev);
        return t;
    }

    // Declaration of a data member; handles pointer-to-function and array types.
    public static string Declare(string type, string name)
    {
        type = Clean(type);
        var arr = Regex.Match(type, @"^(.*)\[(\d+)\]$");
        if (arr.Success) return $"{arr.Groups[1].Value} {name}[{arr.Groups[2].Value}]";
        var fp = Regex.Match(type, @"^(.*) \(\*\)\((.*)\)$");
        if (fp.Success) return $"{fp.Groups[1].Value} (*{name})({fp.Groups[2].Value})";
        return $"{type} {name}";
    }

    // "(args) const -> ret" -> pieces
    public static (string args, bool isConst, string ret) SplitSignature(string sig)
    {
        int depth = 0, close = -1;
        for (int i = 0; i < sig.Length; i++)
        {
            if (sig[i] == '(') depth++;
            else if (sig[i] == ')') { depth--; if (depth == 0) { close = i; break; } }
        }
        var args = sig[1..close];
        var rest = sig[(close + 1)..];
        bool isConst = rest.TrimStart().StartsWith("const");
        var arrow = rest.IndexOf("-> ", StringComparison.Ordinal);
        var ret = arrow >= 0 ? rest[(arrow + 3)..] : "";
        return (args, isConst, ret);
    }

    public static int ArgCount(string args)
    {
        if (args.Trim().Length == 0) return 0;
        int depth = 0, n = 1;
        foreach (var c in args) { if (c is '<' or '(') depth++; else if (c is '>' or ')') depth--; else if (c == ',' && depth == 0) n++; }
        return n;
    }

    public static string MethodDecl(Member m)
    {
        var (args, isConst, ret) = SplitSignature(m.Signature);
        var argList = SplitArgs(args).Select(Clean);
        return $"{(m.Static ? "static " : "")}{(m.Virtual ? "virtual " : "")}{Clean(ret)} {m.Name}({string.Join(", ", argList)}){(isConst ? " const" : "")}{(m.Pure ? " = 0" : "")};";
    }

    public static List<string> SplitArgs(string args)
    {
        var res = new List<string>(); int depth = 0; var cur = new StringBuilder();
        foreach (var c in args)
        {
            if (c is '<' or '(') depth++; else if (c is '>' or ')') depth--;
            if (c == ',' && depth == 0) { res.Add(cur.ToString().Trim()); cur.Clear(); } else cur.Append(c);
        }
        if (cur.ToString().Trim().Length > 0) res.Add(cur.ToString().Trim());
        return res;
    }
}

// ------------------------------------------------------------------------------------------------------------
// Header file editing
// ------------------------------------------------------------------------------------------------------------
sealed class Stmt
{
    public int Start, End;          // [Start, End) in file text
    public string Text;             // code without comments
    public string Access;
    public string Kind;             // access, macro, data, function, type, other, preproc
    public List<string> Names = new();
    public bool Virtual;
    public int ArgCount = -1;
    public string MacroName;
    public List<string> MacroArgs;
}

// Index of all object/function-like macro definitions in the SDK, used to know which access specifier a macro
// leaves behind when it is expanded inside a class body.
static class MacroIndex
{
    static Dictionary<string, string> bodies;
    static readonly Dictionary<string, string> cache = new();
    public static string Root;

    static void Load()
    {
        bodies = new Dictionary<string, string>();
        if (Root == null) return;
        var def = new Regex(@"^\s*#\s*define\s+([A-Za-z_]\w*)(\([^)]*\))?(.*)$");
        foreach (var f in Directory.EnumerateFiles(Root, "*.*", SearchOption.AllDirectories))
        {
            if (!(f.EndsWith(".h") || f.EndsWith(".inl") || f.EndsWith(".hpp"))) continue;
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                var m = def.Match(lines[i]);
                if (!m.Success) continue;
                var sb = new StringBuilder(m.Groups[3].Value);
                while (lines[i].TrimEnd().EndsWith("\\") && i + 1 < lines.Length) { i++; sb.Append('\n').Append(lines[i]); }
                bodies.TryAdd(m.Groups[1].Value, sb.ToString());
            }
        }
    }

    // access keyword in effect after expanding the macro, or null if it does not change access
    public static string TrailingAccess(string name, int depth = 0)
    {
        if (bodies == null) Load();
        if (cache.TryGetValue(name, out var c)) return c;
        string result = null;
        if (depth < 8 && bodies.TryGetValue(name, out var body))
        {
            // walk tokens; the last access specifier or nested macro that changes access wins
            foreach (Match t in Regex.Matches(body, @"\b(public|protected|private)\s*:(?!:)|\b([A-Z_][A-Z0-9_]{3,})\b"))
            {
                if (t.Groups[1].Success) result = t.Groups[1].Value;
                else if (t.Groups[2].Value != name && TrailingAccess(t.Groups[2].Value, depth + 1) is string n) result = n;
            }
        }
        cache[name] = result;
        return result;
    }
}

sealed class HeaderFile
{
    readonly string path;
    string text;
    readonly List<(int pos, int order, string insert, int removeLen)> edits = new();
    int editOrder;
    public bool Changed => edits.Count > 0;

    public HeaderFile(string path) { this.path = path; text = File.ReadAllText(path); }

    public void Save()
    {
        if (edits.Count == 0) return;
        var sb = new StringBuilder(text);
        foreach (var e in edits.OrderByDescending(e => e.pos).ThenByDescending(e => e.order))
        {
            if (e.removeLen > 0) sb.Remove(e.pos, e.removeLen);
            if (e.insert != null) sb.Insert(e.pos, e.insert);
        }
        var fi = new FileInfo(path);
        if (fi.IsReadOnly) fi.IsReadOnly = false;
        File.WriteAllText(path, sb.ToString());
    }

    void Insert(int pos, string s) => edits.Add((pos, editOrder++, s, 0));

    int LineStart(int line)
    {
        int l = 1, i = 0;
        while (l < line && i < text.Length) { if (text[i] == '\n') l++; i++; }
        return i;
    }
    int LineStartOf(int pos) { while (pos > 0 && text[pos - 1] != '\n') pos--; return pos; }
    int LineEndOf(int pos) { while (pos < text.Length && text[pos] != '\n') pos++; return pos; }   // index of '\n' (or EOF)
    string IndentOf(int pos) { var s = LineStartOf(pos); int e = s; while (e < text.Length && text[e] is ' ' or '\t') e++; return text[s..e]; }

    // ---- locating the type body ---------------------------------------------------------------------------
    // Returns the index of the '{' opening the body of the type defined at/after 'line', or for SLATE_BEGIN_ARGS
    // the position right after the macro's argument list.
    (int open, bool slateArgs) FindBody(TypeLayout t, int line)
    {
        int pos = LineStart(line);
        var shortName = t.Name.Contains("::") ? t.Name[(t.Name.LastIndexOf("::") + 2)..] : t.Name;
        if (shortName == "FArguments")
        {
            var m = new Regex(@"SLATE_BEGIN_ARGS\s*\(").Match(text, LineStartOf(pos));
            if (m.Success && m.Index - pos < 2000)
            {
                int p = MatchParen(m.Index + m.Length - 1);
                return (p + 1, true);
            }
        }
        // find "class|struct|union|enum ... Name" then the next '{' before any ';'
        var re = new Regex(@"\b(class|struct|union|enum)\b[^;{]*?\b" + Regex.Escape(shortName) + @"\b");
        var mm = re.Match(text, LineStartOf(pos));
        if (!mm.Success || mm.Index - pos > 4000) return (-1, false);
        int i = mm.Index + mm.Length;
        int depthParen = 0;
        while (i < text.Length)
        {
            i = SkipTrivia(i);
            if (i >= text.Length) break;
            char c = text[i];
            if (c == '(') depthParen++;
            else if (c == ')') depthParen--;
            else if (c == '{' && depthParen == 0) return (i, false);
            else if (c == ';' && depthParen == 0) return (-1, false);
            i++;
        }
        return (-1, false);
    }

    int MatchParen(int open)
    {
        char o = text[open], c = o == '(' ? ')' : o == '{' ? '}' : ']';
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            i = SkipTrivia(i);
            if (i >= text.Length) break;
            if (text[i] == o) depth++;
            else if (text[i] == c) { depth--; if (depth == 0) return i; }
            else if (text[i] == '"' || text[i] == '\'') i = SkipLiteral(i) - 1;
        }
        return -1;
    }

    // skip whitespace and comments; returns index of next significant char (does not skip preprocessor lines)
    int SkipTrivia(int i)
    {
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/') { i = LineEndOf(i); continue; }
            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*') { var e = text.IndexOf("*/", i + 2, StringComparison.Ordinal); i = e < 0 ? text.Length : e + 2; continue; }
            break;
        }
        return i;
    }

    int SkipLiteral(int i)
    {
        char q = text[i];
        if (q == '"' && i > 0 && text[i - 1] == 'R')
        {
            var delimEnd = text.IndexOf('(', i);
            var delim = text[(i + 1)..delimEnd];
            var end = text.IndexOf(")" + delim + "\"", delimEnd, StringComparison.Ordinal);
            return end + delim.Length + 2;
        }
        i++;
        while (i < text.Length && text[i] != q) { if (text[i] == '\\') i++; i++; }
        return i + 1;
    }

    bool AtLineStartIgnoringWs(int i) { int s = LineStartOf(i); for (int k = s; k < i; k++) if (!char.IsWhiteSpace(text[k])) return false; return true; }

    // ---- statement scanner for a class body ------------------------------------------------------------------
    List<Stmt> Scan(int bodyStart, bool isStruct, bool slateArgs, out int bodyEnd)
    {
        var stmts = new List<Stmt>();
        string access = isStruct ? "public" : "private";
        int i = bodyStart + (slateArgs ? 0 : 1);
        bodyEnd = -1;
        if (slateArgs)
        {
            // skip the member initializer list and the constructor body "{}"
            i = SkipTrivia(i);
            if (i < text.Length && text[i] == ':')
            {
                int d = 0;
                for (; i < text.Length; i++)
                {
                    i = SkipTrivia(i);
                    if (text[i] == '(' ) d++;
                    else if (text[i] == ')') d--;
                    else if (text[i] == '{' && d == 0) break;
                }
            }
            i = SkipTrivia(i);
            if (i < text.Length && text[i] == '{') i = MatchParen(i) + 1;
            access = "public";
        }
        while (i < text.Length)
        {
            i = SkipTrivia(i);
            if (i >= text.Length) break;
            char c = text[i];
            if (c == '#' && AtLineStartIgnoringWs(i))
            {
                int e = LineEndOf(i);
                while (e < text.Length && text[i..e].TrimEnd('\r', ' ', '\t').EndsWith("\\")) e = LineEndOf(e + 1);
                stmts.Add(new Stmt { Start = i, End = e, Kind = "preproc", Access = access, Text = text[i..e].Trim() });
                i = e; continue;
            }
            if (c == '}' && !slateArgs) { bodyEnd = i; break; }
            if (slateArgs && Regex.IsMatch(text.AsSpan(i, Math.Min(32, text.Length - i)).ToString(), @"^SLATE_END_ARGS\b")) { bodyEnd = i; break; }
            // nested FArguments struct of a Slate widget: skip to SLATE_END_ARGS(), access is unaffected
            if (!slateArgs)
            {
                var sm = Regex.Match(text.AsSpan(i, Math.Min(40, text.Length - i)).ToString(), @"^SLATE_(BEGIN|USER)_ARGS\b");
                if (sm.Success)
                {
                    var em = new Regex(@"\bSLATE_END_ARGS\s*\(\s*\)").Match(text, i);
                    if (!em.Success) break;
                    stmts.Add(new Stmt { Start = i, End = em.Index + em.Length, Kind = "type", Access = access, Text = "SLATE_ARGS" });
                    i = em.Index + em.Length;
                    continue;
                }
            }
            var st = ReadStatement(i, access);
            if (st == null) break;
            if (st.Kind == "access") access = st.Access;
            // macros can change the access level: GENERATED_BODY() of a UCLASS ends with 'private:', the legacy
            // GENERATED_*_BODY() macros with 'public:'; other macros (DECLARE_CLASS, SLATE_DECLARE_WIDGET, ...)
            // are looked up in the SDK's #defines
            if (st.Kind == "macro")
            {
                if (st.MacroName == "GENERATED_BODY") { if (!isStruct) access = "private"; }
                else if (st.MacroName is "GENERATED_UCLASS_BODY" or "GENERATED_BODY_LEGACY" or "GENERATED_USTRUCT_BODY" or "GENERATED_UINTERFACE_BODY" or "GENERATED_IINTERFACE_BODY") access = "public";
                else if (MacroIndex.TrailingAccess(st.MacroName) is string a) access = a;
            }
            stmts.Add(st);
            i = st.End;
        }
        return stmts;
    }

    static readonly HashSet<string> AccessWords = new() { "public", "protected", "private" };
    static readonly Regex AllCapsMacro = new(@"^[A-Z_][A-Z0-9_]*$");

    Stmt ReadStatement(int start, string access)
    {
        var code = new StringBuilder();
        int i = start, paren = 0, angle = 0;
        bool sawEquals = false;
        string firstWord = null;
        while (i < text.Length)
        {
            int j = SkipTrivia(i);
            if (j > i) { code.Append(' '); i = j; }
            if (i >= text.Length) break;
            char c = text[i];
            if (c == '#' && AtLineStartIgnoringWs(i))
            {
                // preprocessor directive inside a declaration (e.g. #if in an initializer list): skip it
                int e = LineEndOf(i);
                while (e < text.Length && text[i..e].TrimEnd('\r', ' ', '\t').EndsWith("\\")) e = LineEndOf(e + 1);
                code.Append(' '); i = e; continue;
            }
            if (c == '"' || c == '\'') { int e = SkipLiteral(i); code.Append(text, i, e - i); i = e; continue; }
            if (char.IsLetter(c) || c == '_')
            {
                int e = i; while (e < text.Length && (char.IsLetterOrDigit(text[e]) || text[e] == '_')) e++;
                var word = text[i..e];
                firstWord ??= word;
                code.Append(word); i = e;
                // access specifier
                if (AccessWords.Contains(word) && code.ToString().Trim() == word)
                {
                    int k = SkipTrivia(i);
                    if (k < text.Length && text[k] == ':' && (k + 1 >= text.Length || text[k + 1] != ':'))
                        return new Stmt { Start = start, End = k + 1, Kind = "access", Access = word, Text = word + ":" };
                }
                // object-like macro alone on its line, e.g. PRAGMA_ENABLE_DEPRECATION_WARNINGS
                if (AllCapsMacro.IsMatch(word) && code.ToString().Trim() == word)
                {
                    int k = SkipTrivia(i);
                    bool newLine = text.IndexOf('\n', i, Math.Max(0, Math.Min(k, text.Length) - i)) >= 0;
                    if (newLine && k < text.Length && text[k] != '(' && text[k] != ':' && text[k] != ';')
                        return new Stmt { Start = start, End = i, Kind = "macro", Access = access, Text = word, MacroName = word, MacroArgs = new List<string>() };
                }
                continue;
            }
            if (c == '(') { paren++; code.Append(c); i++; continue; }
            if (c == ')')
            {
                paren--; code.Append(c); i++;
                if (paren == 0 && angle == 0)
                {
                    // macro statement without semicolon: IDENT(...) followed by a new line and something that does not continue a declaration
                    var soFar = code.ToString().Trim();
                    var mm = Regex.Match(soFar, @"^([A-Z_][A-Z0-9_]*)\s*\((.*)\)$", RegexOptions.Singleline);
                    if (mm.Success)
                    {
                        int k = SkipTrivia(i);
                        bool newLine = text.IndexOf('\n', i, Math.Max(0, k - i)) >= 0;
                        string next = k < text.Length ? text[k].ToString() : "";
                        if (newLine && next != ";" && next != "{" && next != ":" && next != "=" && !text.AsSpan(k).StartsWith("const") && !text.AsSpan(k).StartsWith("override") && !text.AsSpan(k).StartsWith("->"))
                            return MakeMacro(start, i, soFar, mm, access);
                        if (k < text.Length && text[k] == ';') return MakeMacro(start, k + 1, soFar, mm, access);
                    }
                }
                continue;
            }
            if (c == '<' && paren == 0 && !sawEquals) { angle++; code.Append(c); i++; continue; }
            if (c == '>' && paren == 0 && !sawEquals && angle > 0) { angle--; code.Append(c); i++; continue; }
            if (c == '=' && paren == 0 && angle == 0)
            {
                // not an initializer: comparison operators and operator overload names (operator=, operator==, ...)
                bool isOperator = (i + 1 < text.Length && text[i + 1] == '=') || (i > 0 && "=!<>+-*/%&|^".IndexOf(text[i - 1]) >= 0)
                                  || Regex.IsMatch(code.ToString(), @"\boperator\s*\S*$");
                if (!isOperator) sawEquals = true;
                code.Append(c); i++; continue;
            }
            if (c == '{' && paren == 0)
            {
                int close = MatchParen(i);
                var soFar = code.ToString().Trim();
                code.Append(" {}");
                i = close + 1;
                bool isFunctionBody = Regex.IsMatch(soFar, @"\)\s*(const|override|final|noexcept|volatile|&|&&|\s|->\s*[\w:<>,\s\*&]+)*$") && !sawEquals;
                bool isType = firstWord is "class" or "struct" or "union" or "enum" or "namespace";
                if (isFunctionBody && !isType)
                {
                    int k = SkipTrivia(i);
                    if (k < text.Length && text[k] == ';') i = k + 1;
                    return Classify(start, i, code.ToString(), access);
                }
                continue;  // type body or brace initializer: statement continues until ';'
            }
            if (c == ';' && paren == 0) { code.Append(';'); i++; return Classify(start, i, code.ToString(), access); }
            if (c == '}' && paren == 0) return null;   // unbalanced
            code.Append(c); i++;
        }
        return null;
    }

    Stmt MakeMacro(int start, int end, string code, Match m, string access)
    {
        var args = CppType.SplitArgs(m.Groups[2].Value);
        var st = new Stmt { Start = start, End = end, Kind = "macro", Access = access, Text = code, MacroName = m.Groups[1].Value, MacroArgs = args };
        if (st.MacroName.StartsWith("SLATE_") && args.Count >= 2) st.Names.Add("_" + args[1].Trim());
        else if (st.MacroName.StartsWith("SLATE_") && args.Count == 1) st.Names.Add("_" + args[0].Trim());
        if (st.MacroName is "SLATE_DEFAULT_SLOT" or "SLATE_NAMED_SLOT") { st.Names.Clear(); st.Names.Add("_" + args[^1].Trim()); }
        return st;
    }

    static readonly Regex Ident = new(@"[A-Za-z_]\w*");

    Stmt Classify(int start, int end, string code, string access)
    {
        var st = new Stmt { Start = start, End = end, Access = access, Text = code.Trim() };
        var t = st.Text;
        var first = Ident.Match(t).Value;
        if (first is "typedef" or "using" or "friend" or "static_assert") { st.Kind = "other"; return st; }
        if (first is "class" or "struct" or "union" or "enum")
        {
            st.Kind = t.Contains('{') ? "type" : "other";
            return st;
        }
        // strip leading template<...>
        if (first == "template")
        {
            int d = 0, k = t.IndexOf('<');
            for (; k < t.Length; k++) { if (t[k] == '<') d++; else if (t[k] == '>') { d--; if (d == 0) break; } }
            t = t[(k + 1)..].Trim();
        }
        // strip leading attribute-like macros: UE_DEPRECATED(...), [[...]], ALLCAPS(...)
        while (true)
        {
            var mm = Regex.Match(t, @"^(?:\[\[[^\]]*\]\]|[A-Z_][A-Z0-9_]*\s*\((?>[^()]+|\((?<d>)|\)(?<-d>))*(?(d)(?!))\))\s*");
            if (!mm.Success || mm.Length == 0) break;
            t = t[mm.Length..];
        }
        st.Virtual = Regex.IsMatch(t, @"\bvirtual\b|\boverride\b");
        // find first top-level '(' preceded by an identifier (function) - only before any '=' / '{'
        int paren = 0, angle = 0;
        for (int k = 0; k < t.Length; k++)
        {
            char c = t[k];
            if (c == '<') angle++;
            else if (c == '>' && angle > 0) angle--;
            else if (c == '=' && angle == 0 && paren == 0) break;
            else if (c == '(' && angle == 0 && paren == 0)
            {
                var before = t[..k].TrimEnd();
                if (before.EndsWith("*") || before.EndsWith("&")) break;  // function pointer data member handled below
                var nm = Regex.Match(before, @"(operator\s*\S+|~?[A-Za-z_]\w*)$");
                if (nm.Success && !AllCapsMacro.IsMatch(nm.Value) || nm.Success && nm.Value.StartsWith("operator"))
                {
                    if (Regex.IsMatch(t.AsSpan(k).ToString(), @"^\(\s*\*")) break; // void (*Fn)(...)
                    st.Kind = "function";
                    st.Names.Add(nm.Value);
                    int close = k, d = 0;
                    for (; close < t.Length; close++) { if (t[close] == '(') d++; else if (t[close] == ')') { d--; if (d == 0) break; } }
                    st.ArgCount = CppType.ArgCount(t[(k + 1)..close] == "void" ? "" : t[(k + 1)..close]);
                    return st;
                }
            }
            if (c == '(') paren++; else if (c == ')') paren--;
        }
        // data member(s)
        st.Kind = "data";
        if (Regex.IsMatch(t, @"^\s*(static|constexpr|static\s+constexpr|inline\s+static)\b")) st.Kind = "static";
        var body = t.TrimEnd(';').Trim();
        foreach (var decl in SplitTopLevel(body, ','))
        {
            var d = decl;
            int cut = IndexOfTopLevel(d, "={[:");
            if (cut >= 0) d = d[..cut];
            var fpm = Regex.Match(d, @"\(\s*\*\s*(\w+)\s*\)");
            if (fpm.Success) { st.Names.Add(fpm.Groups[1].Value); continue; }
            var nm = Regex.Match(d.TrimEnd(), @"([A-Za-z_]\w*)$");
            if (nm.Success) st.Names.Add(nm.Value);
        }
        return st;
    }

    static IEnumerable<string> SplitTopLevel(string s, char sep)
    {
        int d = 0, last = 0;
        for (int k = 0; k < s.Length; k++)
        {
            char c = s[k];
            if (c is '<' or '(' or '{' or '[') d++;
            else if (c is '>' or ')' or '}' or ']') d--;
            else if (c == sep && d == 0) { yield return s[last..k]; last = k + 1; }
        }
        yield return s[last..];
    }

    static int IndexOfTopLevel(string s, string chars)
    {
        int d = 0;
        for (int k = 0; k < s.Length; k++)
        {
            char c = s[k];
            if (c is '<' or '(') d++;
            else if (c is '>' or ')') d--;
            else if (d == 0 && chars.IndexOf(c) >= 0)
            {
                if (c == ':' && k + 1 < s.Length && s[k + 1] == ':') { k++; continue; }
                if (c == ':' && k > 0 && s[k - 1] == ':') continue;
                return k;
            }
        }
        return -1;
    }

    // Layout-independence probe: inject a data member (and a virtual function for polymorphic types) at the start
    // of the type body without changing any line numbers.
    public string InjectProbe(TypeLayout t, int line, bool polymorphic)
    {
        var (open, slateArgs) = FindBody(t, line);
        if (open < 0 || slateArgs) return "body not found";
        int pos = open + 1;
        var gen = new Regex(@"\G\s*GENERATED_\w*BODY\w*\s*\(\s*\)").Match(text, open + 1);
        if (gen.Success) pos = gen.Index + gen.Length;   // keep UHT's GENERATED_*BODY() first
        else
        {
            var stmts = Scan(open, t.Kind != "class", false, out int bodyEnd);
            var first = stmts.FirstOrDefault(s => s.Kind != "preproc");
            if (first != null && first.Kind == "macro" && first.MacroName.StartsWith("GENERATED_")) pos = first.End;
        }
        var probe = " uint8 ArkLayoutProbe_Data[64];" + (polymorphic ? " virtual void ArkLayoutProbe_Virtual();" : "");
        Insert(pos, probe);
        return "probed";
    }

    // debugging aid: PdbLayout scan <header> <line> <kind> <name>
    public void Dump(string kind, string name, int line)
    {
        var t = new TypeLayout { Kind = kind, Name = name };
        var (open, slateArgs) = FindBody(t, line);
        Console.WriteLine($"body at {open} slate={slateArgs}");
        if (open < 0) return;
        var stmts = Scan(open, kind != "class", slateArgs, out int bodyEnd);
        foreach (var s in stmts)
        {
            var txt = s.Text.Length > 100 ? s.Text[..100] + "..." : s.Text;
            Console.WriteLine($"{s.Start,7} {s.Kind,-8} {s.Access,-9} [{string.Join(",", s.Names)}] {txt.Replace("\n", " ").Replace("\r", "")}");
        }
        Console.WriteLine($"body end: {bodyEnd}");
        if (bodyEnd < 0 && stmts.Count > 0)
        {
            int at = stmts[^1].End;
            Console.WriteLine("stopped near: " + text.Substring(at, Math.Min(300, text.Length - at)));
        }
    }

    // ---- applying ops ------------------------------------------------------------------------------------
    public List<string> Apply(TypeLayout t, int line, List<Op> ops)
    {
        var results = new List<string>();
        var (open, slateArgs) = FindBody(t, line);
        if (open < 0) { results.Add("MANUAL: type body not found"); results.AddRange(ops.Select(o => "  " + o)); return results; }
        if (t.Kind == "enum") return ApplyEnum(open, ops);
        var stmts = Scan(open, t.Kind != "class", slateArgs, out int bodyEnd);
        if (bodyEnd < 0) { results.Add("MANUAL: could not parse type body"); results.AddRange(ops.Select(o => "  " + o)); return results; }

        // insertion points accumulate so that consecutive inserts after the same anchor keep their order
        foreach (var op in ops)
        {
            try { results.Add(ApplyOne(t, op, stmts, open, bodyEnd, slateArgs)); }
            catch (Exception e) { results.Add($"MANUAL: {op}  ({e.Message})"); }
        }
        return results;
    }

    Stmt FindData(List<Stmt> stmts, string name) => stmts.FirstOrDefault(s => (s.Kind is "data" or "macro") && s.Names.Contains(name));

    Stmt FindFunction(List<Stmt> stmts, Member m)
    {
        var (args, isConst, _) = CppType.SplitSignature(m.Signature);
        int n = CppType.ArgCount(args);
        var cands = stmts.Where(s => s.Kind == "function" && s.Names.Contains(m.Name)).ToList();
        var exact = cands.Where(s => s.ArgCount == n).ToList();
        if (exact.Count > 1) exact = exact.Where(s => Regex.IsMatch(s.Text, @"\)\s*const\b") == isConst).DefaultIfEmpty(exact[0]).ToList();
        return exact.FirstOrDefault() ?? (cands.Count == 1 ? cands[0] : null);
    }

    // position right after a statement (end of its line when only trivia follows)
    int AfterStmt(Stmt s)
    {
        int e = s.End;
        int le = LineEndOf(e);
        var rest = text[e..le];
        if (Regex.IsMatch(rest, @"^\s*(//.*)?\r?$")) return le;   // insert before the '\n' => new line starts after it
        return e;
    }

    string ApplyOne(TypeLayout t, Op op, List<Stmt> stmts, int open, int bodyEnd, bool slateArgs)
    {
        switch (op.Kind)
        {
            case "insert-data":
            {
                string decl;
                if (slateArgs) decl = SlateArgDecl(op.Member);
                else if (op.Member.Kind == "bitfield") decl = $"{CppType.Clean(op.Member.Type)} {op.Member.Name} : {op.Member.BitCount};";
                else decl = CppType.Declare(op.Member.Type, op.Member.Name) + ";";
                if (op.Replaces != null && FindData(stmts, op.Replaces.Name) is Stmt old)
                {
                    // same member, new type: put the new declaration right after the (removed) old one
                    int lineAfter = AfterStmt(old);
                    if (lineAfter < text.Length && text[lineAfter] == '\n') lineAfter++;
                    Insert(lineAfter, $"{IndentOf(old.Start)}{decl} // [ARK] type changed\r\n");
                    return $"replaced {op.Replaces.Name} by {decl}";
                }
                var (pos, indent, accessHere) = DataInsertPoint(op, stmts, open, bodyEnd, slateArgs);
                InsertDecl(pos, indent, decl, slateArgs ? null : op.Member.Access, accessHere);
                return $"inserted {decl}";
            }
            case "remove-data":
            {
                var s = FindData(stmts, op.Member.Name) ?? throw new Exception($"declaration of {op.Member.Name} not found");
                if (s.Names.Count > 1) throw new Exception("multi-declarator statement");
                RemoveStmt(s, stmts);
                return $"removed data {op.Member.Name}";
            }
            case "insert-virtual":
            {
                var decl = CppType.MethodDecl(op.Member);
                string virtualized = "";
                if (op.Virtualizes != null && FindFunction(stmts, op.Virtualizes) is Stmt orig)
                {
                    // ARK made an existing method virtual: drop the old declaration, keep its export macro
                    var api = Regex.Match(orig.Text, @"\b[A-Z][A-Z0-9_]*_API\b");
                    if (api.Success) decl = api.Value + " " + decl;
                    RemoveStmt(orig, stmts);
                    virtualized = " (was non-virtual)";
                }
                int pos; string indent; string accessHere;
                if (op.OverloadOf != null && FindFunction(stmts, op.OverloadOf) is Stmt sib)
                {
                    // vtable order of overloads = reverse declaration order
                    if (op.OverloadFirst) { pos = AfterStmt(sib); }
                    else { pos = LineStartOf(LeadingMacrosStart(sib, stmts)) - 1; }
                    InsertDecl(pos, IndentOf(sib.Start), decl, op.Member.Access, sib.Access);
                    return $"inserted {decl}{virtualized} (overload of {op.OverloadOf.Name}, {(op.OverloadFirst ? "declared after" : "declared before")} it)";
                }
                if (op.Anchor != null)
                {
                    var a = FindFunction(stmts, op.Anchor) ?? throw new Exception($"anchor virtual {op.Anchor.Name} not found");
                    pos = AfterStmt(a); indent = IndentOf(a.Start); accessHere = a.Access;
                }
                else
                {
                    var firstVirt = stmts.FirstOrDefault(s => s.Kind == "function" && s.Virtual);
                    if (firstVirt == null) { pos = LineStartOf(bodyEnd); indent = IndentOf(open) + "\t"; accessHere = stmts.Count > 0 ? stmts[^1].Access : (t.Kind == "class" ? "private" : "public"); InsertDecl(pos - 1, indent, decl, op.Member.Access, accessHere); return $"inserted {decl} (end of class)"; }
                    pos = LineStartOf(LeadingMacrosStart(firstVirt, stmts)) - 1; indent = IndentOf(firstVirt.Start); accessHere = firstVirt.Access;
                }
                InsertDecl(pos, indent, decl, op.Member.Access, accessHere);
                return $"inserted {decl}{virtualized}";
            }
            case "remove-virtual":
            {
                var s = FindFunction(stmts, op.Member) ?? throw new Exception($"virtual {op.Member.Name} not found");
                RemoveStmt(s, stmts);
                return $"removed virtual {op.Member.Name}{op.Member.Signature}";
            }
            case "access":
            {
                var s = FindFunction(stmts, op.Member) ?? throw new Exception($"method {op.Member.Name} not found");
                if (op.Member.Static != op.Anchor.Static) throw new Exception("static-ness changed");
                var indent = IndentOf(s.Start);
                var ls = LineStartOf(LeadingMacrosStart(s, stmts));
                Insert(ls, $"{op.Member.Access}: // [ARK] was {op.Anchor.Access}\r\n");
                int after = AfterStmt(s);
                if (after < text.Length && text[after] == '\n' && after > 0 && text[after - 1] == '\r') after--;   // not between \r and \n
                Insert(after, $"\r\n{s.Access}:");
                return $"access of {op.Member.Name} {op.Anchor.Access} -> {op.Member.Access}";
            }
        }
        throw new Exception("unsupported op " + op.Kind);
    }

    (int pos, string indent, string access) DataInsertPoint(Op op, List<Stmt> stmts, int open, int bodyEnd, bool slateArgs)
    {
        if (op.Anchor != null && op.Anchor.Kind is "data" or "bitfield")
        {
            var a = FindData(stmts, op.Anchor.Name);
            if (a != null) return (AfterStmt(a), IndentOf(a.Start), a.Access);
            // anchor declared by a macro: place before the successor, or at the end of the body
            var s = op.Successor != null ? FindData(stmts, op.Successor.Name) : null;
            if (s != null) return (LineStartOf(LeadingMacrosStart(s, stmts)) - 1, IndentOf(s.Start), s.Access);
            if (op.Successor != null) throw new Exception($"anchor {op.Anchor.Name} and successor {op.Successor.Name} not found");
            var lastStmt = stmts.LastOrDefault();
            return (LineStartOf(bodyEnd) - 1, lastStmt != null ? IndentOf(lastStmt.Start) : IndentOf(open) + "\t", lastStmt?.Access ?? "private");
        }
        // before the first data member (with its attached macros)
        var first = stmts.FirstOrDefault(s => s.Kind == "data" || slateArgs && s.Kind == "macro" && s.Names.Count > 0);
        if (first != null)
        {
            var ls = LineStartOf(LeadingMacrosStart(first, stmts));
            return (ls - 1, IndentOf(first.Start), first.Access);
        }
        var last = stmts.LastOrDefault();
        return (LineStartOf(bodyEnd) - 1, IndentOf(open) + "\t", last?.Access ?? "private");
    }

    int LeadingMacrosStart(Stmt s, List<Stmt> stmts)
    {
        int idx = stmts.IndexOf(s), start = s.Start;
        while (idx > 0 && stmts[idx - 1].Kind == "macro" && stmts[idx - 1].MacroName is "UPROPERTY" or "UFUNCTION" or "UE_DEPRECATED" or "UMETA")
        { idx--; start = stmts[idx].Start; }
        return start;
    }

    void InsertDecl(int pos, string indent, string decl, string access, string accessHere)
    {
        // pos is the index of a '\n' (or the char before a line start): insert a new line after it
        var sb = new StringBuilder();
        bool switchAccess = access != null && accessHere != null && access != accessHere;
        if (pos < 0) pos = 0;
        // we insert *before* the newline character at pos so the inserted text forms new lines after the current one
        if (pos < text.Length && text[pos] == '\n' && pos > 0 && text[pos - 1] == '\r') pos--;
        if (switchAccess) sb.Append($"\r\n{access}:");
        sb.Append($"\r\n{indent}{decl} // [ARK]");
        if (switchAccess) sb.Append($"\r\n{accessHere}:");
        Insert(pos, sb.ToString());
    }

    void RemoveStmt(Stmt s, List<Stmt> stmts)
    {
        var start = LineStartOf(LeadingMacrosStart(s, stmts));
        var end = AfterStmt(s);
        if (end < text.Length && text[end] == '\n') end++;
        Insert(start, "#if 0 // [ARK] removed\r\n");
        Insert(end, "#endif // [ARK]\r\n");
    }

    string SlateArgDecl(Member m)
    {
        var name = m.Name.StartsWith("_") ? m.Name[1..] : m.Name;
        var type = CppType.Clean(m.Type);
        var attr = Regex.Match(type, @"^TAttribute<(.*)>$");
        if (attr.Success) return $"SLATE_ATTRIBUTE({attr.Groups[1].Value}, {name})";
        if (type.StartsWith("TDelegate<") || type.StartsWith("F") && type.EndsWith("Delegate")) return $"SLATE_EVENT({type}, {name})";
        var style = Regex.Match(type, @"^const (F\w+Style)\*$");
        if (style.Success) return $"SLATE_STYLE_ARGUMENT({style.Groups[1].Value}, {name})";
        return $"SLATE_ARGUMENT({type}, {name})";
    }

    // ---- enums -----------------------------------------------------------------------------------------------
    List<string> ApplyEnum(int open, List<Op> ops)
    {
        var results = new List<string>();
        int close = MatchParen(open);
        // split enumerators: entries separated by top-level commas
        var entries = new List<(int start, int end, string name)>();
        int i = open + 1, entryStart = -1, depth = 0;
        for (; i < close; i++)
        {
            i = SkipTrivia(i);
            if (i >= close) break;
            char c = text[i];
            if (c == '#' && AtLineStartIgnoringWs(i)) { i = LineEndOf(i); continue; }
            if (entryStart < 0) entryStart = i;
            if (c is '(' or '{' or '<') depth++;
            else if (c is ')' or '}' or '>') depth--;
            else if (c == ',' && depth == 0) { entries.Add(MakeEntry(entryStart, i)); entryStart = -1; }
            else if (c == '"' || c == '\'') i = SkipLiteral(i) - 1;
        }
        if (entryStart >= 0) entries.Add(MakeEntry(entryStart, LastSignificantBefore(close) + 1));

        foreach (var op in ops)
        {
            var nm = op.Member.Name;
            switch (op.Kind)
            {
                case "set-enum":
                {
                    var e = entries.FirstOrDefault(x => x.name == nm);
                    if (e.name == null) { results.Add($"MANUAL: enumerator {nm} not found (set {op.Value})"); break; }
                    var entryText = text[e.start..e.end];
                    var eq = Regex.Match(entryText, @"^(\w+)\s*(=\s*(?:(?!UMETA\().)*?)?\s*(UMETA\(.*\))?\s*$", RegexOptions.Singleline);
                    var valueText = FormatEnumValue(op.Value);
                    // aliases / computed values (X = Y - 1) follow the renumbered enumerators automatically
                    if (eq.Success && eq.Groups[2].Success && Regex.IsMatch(eq.Groups[2].Value, @"(?<![0-9A-Za-z_])[A-Za-z_]\w*") && !Regex.IsMatch(eq.Groups[2].Value, @"^=\s*[0-9][0-9A-Fa-fxXuUlL]*(\s*<<\s*[0-9]+[uUlL]*)?\s*$"))
                    {
                        results.Add($"kept computed value of {nm} ({eq.Groups[2].Value.Trim()}), ARK value {valueText}");
                        break;
                    }
                    if (eq.Success && eq.Groups[2].Success)
                    {
                        edits.Add((e.start + eq.Groups[2].Index, editOrder++, $"= {valueText} /* [ARK] */", eq.Groups[2].Value.TrimEnd().Length));
                    }
                    else Insert(e.start + nm.Length, $" = {valueText} /* [ARK] */");
                    results.Add($"set {nm} = {valueText}");
                    break;
                }
                case "insert-enum":
                {
                    int pos; string indent;
                    if (op.Anchor != null)
                    {
                        var a = entries.FirstOrDefault(x => x.name == op.Anchor.Name);
                        if (a.name == null) { results.Add($"MANUAL: anchor enumerator {op.Anchor.Name} not found for {nm}"); continue; }
                        // after the anchor's comma (end of line)
                        int comma = a.end < close && text[a.end] == ',' ? a.end + 1 : a.end;
                        if (comma == a.end) Insert(a.end, ",");
                        pos = LineEndOf(comma); if (pos > 0 && text[pos - 1] == '\r') pos--;
                        if (!Regex.IsMatch(text[comma..pos], @"^\s*(//.*)?$")) pos = comma;
                        indent = IndentOf(a.start);
                    }
                    else
                    {
                        var f = entries.FirstOrDefault();
                        pos = f.name == null ? open + 1 : LineStartOf(f.start) - 1;
                        if (pos > 0 && text[pos] == '\n' && text[pos - 1] == '\r') pos--;
                        indent = f.name == null ? "\t" : IndentOf(f.start);
                    }
                    Insert(pos, $"\r\n{indent}{nm} = {FormatEnumValue(op.Value)}, // [ARK]");
                    results.Add($"inserted enumerator {nm} = {op.Value}");
                    break;
                }
                case "remove-enum":
                {
                    var e = entries.FirstOrDefault(x => x.name == nm);
                    if (e.name == null) { results.Add($"MANUAL: enumerator {nm} to remove not found"); break; }
                    int ls = LineStartOf(e.start), le = LineEndOf(e.end);
                    if (!Regex.IsMatch(text[ls..e.start], @"^\s*$")) { results.Add($"MANUAL: enumerator {nm} not on its own line"); break; }
                    Insert(ls, "#if 0 // [ARK] removed\r\n");
                    Insert(le + 1, "#endif // [ARK]\r\n");
                    results.Add($"removed enumerator {nm}");
                    break;
                }
                default: results.Add($"MANUAL: {op}"); break;
            }
        }
        return results;
    }

    (int, int, string) MakeEntry(int s, int e)
    {
        while (e > s && char.IsWhiteSpace(text[e - 1])) e--;
        var m = Regex.Match(text[s..e], @"^[A-Za-z_]\w*");
        return (s, e, m.Value);
    }

    int LastSignificantBefore(int close)
    {
        int k = close - 1;
        while (k > 0)
        {
            // skip back over whitespace and line comments
            if (char.IsWhiteSpace(text[k])) { k--; continue; }
            int ls = LineStartOf(k);
            var line = text[ls..(k + 1)];
            var cm = line.IndexOf("//", StringComparison.Ordinal);
            if (cm >= 0) { k = ls + cm - 1; continue; }
            break;
        }
        return k;
    }

    static string FormatEnumValue(long v)
    {
        if (v < 0) return v == long.MinValue ? "0x8000000000000000" : v.ToString();
        if (v >= 0x10 && (v & (v - 1)) == 0) return $"0x{v:X}";
        return v.ToString();
    }
}
