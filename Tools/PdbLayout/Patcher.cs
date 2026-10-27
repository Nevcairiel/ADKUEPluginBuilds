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

    // --ark-classes Config\ArkClasses.txt: generated ARK-only classes, included when a type is re-based onto them
    public static void LoadArkClasses(ref string[] args)
    {
        int i = Array.IndexOf(args, "--ark-classes");
        if (i < 0) return;
        foreach (var l in File.ReadAllLines(args[i + 1]))
        {
            var f = l.Split('#')[0].Split('|').Select(x => x.Trim()).ToArray();
            if (f.Length < 4) continue;
            var pub = f[0].Replace('/', '\\');
            var k = pub.IndexOf("\\Public\\", StringComparison.OrdinalIgnoreCase);
            var inc = (k >= 0 ? pub[(k + 8)..] : Path.GetFileName(pub)).Replace('\\', '/');
            foreach (var t in f[3].Split(' ', StringSplitOptions.RemoveEmptyEntries)) HeaderFile.ArkClassIncludes[t] = inc;
        }
        args = args.Where((_, k) => k != i && k != i + 1).ToArray();
    }

    public static int Run(string[] args)
    {
        var excluded = LoadExclusions(ref args);
        LoadArkClasses(ref args);
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
        var refPdb = args[1];
        var sdkRoot = Path.GetFullPath(args[2]);
        var refEngineRoot = args[3].TrimEnd('\\', '/');
        var reportPath = args[4];
        MacroIndex.Root = sdkRoot;
        // several reference PDBs ('a;b;c', all built against the same SDK headers): every type is patched once, from the
        // first reference PDB that has it
        var refs = refPdb.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => { var d = TypeDb.Open(p); return (db: d, locations: SourceLocations.Load(p, d)); }).ToList();
        var refDb = refs[0].db;
        var arks = args.Skip(5).Select(p => (Path.GetFileNameWithoutExtension(p), TypeDb.Open(p))).ToList();
        var lookupCache = new Dictionary<string, TypeLayout>();
        Ops.Lookup = n => lookupCache.TryGetValue(n, out var c) ? c
            : lookupCache[n] = arks.Select(x => x.Item2.Describe(n)).FirstOrDefault(x => x != null) ?? refs.Select(r => r.db.Describe(n)).FirstOrDefault(x => x != null);
        // the SDK header defining an engine type (by-value members inserted into other headers need it)
        HeaderFile.HeaderOf = n => refs.Select(r => r.db.UniqueNameOfType(n) is string u && r.locations.TryGetValue(u, out var l) ? l.File : null)
            .Where(f => f != null && f.StartsWith(refEngineRoot, StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.Combine(sdkRoot, f.Substring(refEngineRoot.Length).TrimStart('\\', '/'))).FirstOrDefault();

        var report = new StringBuilder();
        var files = new Dictionary<string, HeaderFile>(StringComparer.OrdinalIgnoreCase);
        int patchedTypes = 0, manual = 0;

        var allTypes = refs.SelectMany(r => r.db.CompleteUniqueNames().Select(u => (u, r))).GroupBy(x => x.u).Select(g => g.First());
        foreach (var (uniq, r) in allTypes.OrderBy(x => x.u, StringComparer.Ordinal))
        {
            var (mod, b) = arks.FirstOrDefault(x => x.Item2.Has(uniq));
            if (b == null) continue;
            refDb = r.db;
            var locations = r.locations;
            var ta = refDb.Describe(uniq);
            var tb = b.Describe(uniq);
            if (tb != null) tb = Differ.WithRenames(tb);   // members the SDK keeps under their vanilla name
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
            foreach (var line in results) typeReport.AppendLine("  " + line);
            if (results.Any(x => x.StartsWith("MANUAL"))) manual++;
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
    // probe <refpdb>[;<refpdb>...] <sdkRoot> <engineRootInRefPdb> <exclusionsFile> <arkpdb>... [--renames <file>]
    // Several PDBs (the modules of one plugin): every excluded type is probed once, located via the first PDB using it.
    // The probe goes where the SDK layout starts to differ from the devkit's (first devkit PDB defining the type), so
    // the plugin may use the part both agree on. Exclusions whose comment contains "[no probe]" are skipped.
    public static int Run(string[] args)
    {
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
        var sdkRoot = Path.GetFullPath(args[2]);
        var refEngineRoot = args[3].TrimEnd('\\', '/');
        var lines = File.ReadAllLines(args[4]).Select(l => (Name: l.Split('#')[0].Trim(), NoProbe: l.Contains("[no probe]"))).Where(l => l.Name.Length > 0).ToList();
        var names = lines.Select(l => l.Name).ToHashSet();
        foreach (var l in lines.Where(l => l.NoProbe)) { names.Remove(l.Name); Console.WriteLine($"  {l.Name}: [no probe]"); }
        var arks = args.Skip(5).Select(TypeDb.Open).ToList();
        MacroIndex.Root = sdkRoot;
        var files = new Dictionary<string, HeaderFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var refPdb in args[1].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
        var db = TypeDb.Open(refPdb);
        var locations = SourceLocations.Load(refPdb, db);
        foreach (var uniq in db.CompleteUniqueNames())
        {
            var t = db.Describe(uniq);
            if (t == null || !names.Contains(t.Name)) continue;
            names.Remove(t.Name);
            if (t.Kind == "enum") { Console.WriteLine($"  {t.Name}: enum, not probed"); continue; }
            if (!locations.TryGetValue(uniq, out var loc)) { Console.WriteLine($"  {t.Name}: no location"); continue; }
            var path = loc.File.StartsWith(refEngineRoot, StringComparison.OrdinalIgnoreCase) ? Path.Combine(sdkRoot, loc.File.Substring(refEngineRoot.Length).TrimStart('\\', '/')) : loc.File;
            if (!Path.GetFullPath(path).StartsWith(sdkRoot, StringComparison.OrdinalIgnoreCase)) { Console.WriteLine($"  {t.Name}: outside the SDK ({loc.File}), not probed"); continue; }
            bool poly = t.Members.Any(m => m.Kind == "vfptr" || (m.Kind == "method" && m.Virtual));
            // without a devkit counterpart: the whole type (data member and virtual function at the start)
            var dk = arks.FirstOrDefault(x => x.Has(uniq))?.Describe(uniq);
            var (first, atEnd, virtuals) = dk != null ? Differ.ProbePoint(t, dk) : (null, false, true);
            bool data = dk == null || first != null || atEnd;
            virtuals &= poly;
            if (!data && !virtuals) { Console.WriteLine($"  {t.Name}: same layout as the devkit, not probed"); continue; }
            if (!files.TryGetValue(path, out var hf)) files[path] = hf = new HeaderFile(path);
            Console.WriteLine($"  {t.Name}: {hf.InjectProbe(t, loc.Line, data, first, atEnd, virtuals)}");
        }
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
    public Member ReplacesVirtual; // insert-virtual: removed overload of the same name (signature changed in place)
    public bool OverloadFirst;   // ... and the new overload precedes it in the vtable
    public long Value;           // enum value
    public List<string> Bases;   // set-bases: the devkit's base classes in declaration order
    public override string ToString() => Kind == "set-bases" ? $"set-bases {string.Join(", ", Bases)}"
        : $"{Kind} {(Member == null ? "" : TypeLayout.Format(Member))}{(Anchor != null ? $"   after {Anchor.Name}" : Kind.StartsWith("insert") ? "   at start" : "")}";
}

static class Ops
{
    static bool IsData(Member m) => m.Kind is "data" or "bitfield";
    static string DataKey(Member m) => m.Kind == "bitfield" ? $"{m.Type} {m.Name}:{m.BitCount}" : $"{m.Type} {m.Name}";
    static string VirtKey(Member m) => m.Name + m.Signature;

    // Empty bases (no data, no vtable, e.g. ARK's PrimalLiveTunablesMixin<T>) do not affect the layout and are
    // left out by the generated ARK classes; they are ignored when comparing base lists.
    public static Func<string, TypeLayout> Lookup;
    static bool IsEmptyBase(Member m) => Lookup?.Invoke(m.Type) is TypeLayout t && t.Size == 1
        && !t.Members.Any(x => x.Kind is "data" or "bitfield" or "vfptr" or "base");
    static bool IsOverride(Member m) => m.Kind == "method" && m.Virtual && !m.Intro && !m.CompilerGenerated && !m.Name.StartsWith("~")
        && m.Name is not ("__vecDelDtor" or "_getUObject");

    public static List<Op> Compute(TypeLayout a, TypeLayout b)
    {
        var ops = new List<Op>();
        // ---- base classes (ARK inserted new bases, e.g. AActor : UPrimalActor, and reordered interfaces)
        if (a.Kind != "enum")
        {
            var ba = a.Members.Where(m => m.Kind == "base" && !IsEmptyBase(m)).Select(m => m.Type).ToList();
            var bb = b.Members.Where(m => m.Kind == "base" && !IsEmptyBase(m)).Select(m => m.Type).ToList();
            if (!ba.SequenceEqual(bb)) ops.Add(new Op { Kind = "set-bases", Bases = bb });
        }
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
            // ARK changed the underlying type of some enums (e.g. ETeleportType : uint8)
            if (a.Size != b.Size && b.Size > 0) ops.Add(new Op { Kind = "set-underlying", Value = b.Size });
        }
        // ---- members whose type changed: replace in place; methods that became virtual: move the declaration
        foreach (var ins in ops.Where(o => o.Kind == "insert-data"))
        {
            // retyped in place: same name, other type, and still after the same kept member (else it moved)
            var old = ops.FirstOrDefault(r => r.Kind == "remove-data" && r.Member.Name == ins.Member.Name && r.Member.Type != ins.Member.Type)?.Member;
            if (old == null) continue;
            var aData = a.Members.Where(IsData).ToList();
            var bAll = b.Members.Where(IsData).ToList();
            // predecessors that exist on both sides (members only one side has, e.g. new ones, do not count)
            var aNames = aData.Select(m => m.Name).ToHashSet(); var bNames = bAll.Select(m => m.Name).ToHashSet();
            int oi = aData.IndexOf(old), bi = bAll.IndexOf(ins.Member);
            var oldPrev = aData.Take(Math.Max(0, oi)).LastOrDefault(m => bNames.Contains(m.Name))?.Name;
            var newPrev = bAll.Take(Math.Max(0, bi)).LastOrDefault(m => aNames.Contains(m.Name))?.Name;
            if (oldPrev == newPrev) ins.Replaces = old;
        }
        // a new member without a kept predecessor follows its devkit predecessor if that one exists by name in the
        // reference (with another type): e.g. LastRenderTimeOnScreen after the retyped LastRenderTime
        var bData = b.Members.Where(IsData).ToList();
        foreach (var ins in ops.Where(o => o.Kind == "insert-data" && o.Replaces == null))
        {
            int k = bData.IndexOf(ins.Member);
            if (k <= 0) continue;
            var prev = bData[k - 1];
            var refPrev = a.Members.FirstOrDefault(m => IsData(m) && m.Name == prev.Name);
            if (refPrev != null && (ins.Anchor == null || ops.Any(o => o.Kind == "insert-data" && o.Member == prev && o.Replaces != null))) { ins.Anchor = refPrev; continue; }
            // further back: new members that follow a retyped member (through other new ones) are placed after it too
            var aKeys = a.Members.Where(IsData).Select(DataKey).ToHashSet();
            for (int j = k - 1; j >= 0; j--)
            {
                var retyped = ops.FirstOrDefault(o => o.Kind == "insert-data" && o.Member == bData[j] && o.Replaces != null);
                if (retyped != null) { ins.Anchor = retyped.Replaces; break; }
                if (aKeys.Contains(DataKey(bData[j]))) break;   // reached a kept member: the normal anchor is right
            }
        }
        var nonVirtual = a.Members.Where(m => m.Kind == "method" && !m.Virtual).GroupBy(VirtKey).ToDictionary(g => g.Key, g => g.First());
        foreach (var ins in ops.Where(o => o.Kind == "insert-virtual"))
        {
            if (nonVirtual.TryGetValue(VirtKey(ins.Member), out var nv)) ins.Virtualizes = nv;
            else
            {
                // ARK replaced a non-virtual method by a virtual one with another signature (e.g. a different return
                // type): same name and argument count, and the devkit no longer has the old one
                var (insArgs, insConst, _) = CppType.SplitSignature(ins.Member.Signature);
                var old = a.Members.FirstOrDefault(m => m.Kind == "method" && !m.Virtual && !m.Static && m.Name == ins.Member.Name
                    && CppType.ArgCount(CppType.SplitSignature(m.Signature).args) == CppType.ArgCount(insArgs)
                    && !b.Members.Any(x => x.Kind == "method" && x.Name == m.Name && x.Signature == m.Signature));
                if (old != null) ins.Virtualizes = old;
            }
            // MSVC groups overloaded virtuals and lays them out in reverse declaration order
            var sibling = b.Members.FirstOrDefault(m => m.Kind == "method" && m.Intro && m.Name == ins.Member.Name && m.Signature != ins.Member.Signature
                                                        && a.Members.Any(r => r.Kind == "method" && r.Name == m.Name && r.Signature == m.Signature));
            if (sibling != null) { ins.OverloadOf = sibling; ins.OverloadFirst = ins.Member.VSlot < sibling.VSlot; }
            // an overload whose signature changed (the old one is removed): the group stays where it was declared
            // (a single, not overloaded virtual follows its vtable neighbours instead: ARK also moved some of them)
            if (ins.Virtualizes == null && b.Members.Count(m => m.Kind == "method" && m.Intro && m.Name == ins.Member.Name) > 1)
                ins.ReplacesVirtual = ops.FirstOrDefault(r => r.Kind == "remove-virtual" && r.Member.Name == ins.Member.Name)?.Member;
        }
        // several new overloads of one name after the same anchor: declared in reverse vtable order (MSVC lays out
        // overloads in reverse declaration order)
        foreach (var grp in ops.Where(o => o.Kind == "insert-virtual" && o.OverloadOf == null && o.Virtualizes == null)
                               .GroupBy(o => (o.Member.Name, o.ReplacesVirtual ?? o.Anchor)).Where(g => g.Count() > 1).ToList())
        {
            var idx = grp.Select(o => ops.IndexOf(o)).OrderBy(i => i).ToList();
            var rev = grp.Reverse().ToList();
            for (int k = 0; k < idx.Count; k++) ops[idx[k]] = rev[k];
        }
        // removals first: replacements are placed after the removed declaration
        var ordered = ops.OrderBy(o => o.Kind.StartsWith("remove") ? 0 : 1).ToList();   // stable
        ops.Clear(); ops.AddRange(ordered);
        // ---- overrides of base class virtuals: they decide the vtable entries of classes derived in a plugin
        // (a missing override silently calls the base implementation, a removed one is an unresolved external)
        var virtA = a.Members.Where(m => m.Kind == "method" && m.Virtual).Select(VirtKey).ToHashSet();
        var virtB = b.Members.Where(m => m.Kind == "method" && m.Virtual).Select(VirtKey).ToHashSet();
        foreach (var m in b.Members.Where(IsOverride)) if (!virtA.Contains(VirtKey(m))) ops.Add(new Op { Kind = "insert-override", Member = m });
        foreach (var m in a.Members.Where(IsOverride)) if (!virtB.Contains(VirtKey(m))) ops.Add(new Op { Kind = "remove-override", Member = m });
        // a virtual the devkit introduces in a new base class (e.g. CheckForErrors in UPrimalActor) is removed from the
        // introduced virtuals here but still overridden: declare it again as an override
        foreach (var rv in ops.Where(o => o.Kind == "remove-virtual").ToList())
        {
            var ov = b.Members.FirstOrDefault(m => IsOverride(m) && VirtKey(m) == VirtKey(rv.Member));
            if (ov != null) ops.Add(new Op { Kind = "insert-override", Member = ov });
        }
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
    // a module export macro (not a function-like macro such as SLATE_DECLARE_WIDGET_API(...))
    const string ApiMacro = @"\b[A-Z][A-Z0-9_]*_API\b(?!\s*\()";
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
        // at the same position: the end of a removed block, then inserted declarations, then the start of the next
        // removed block (declarations inserted between two removed members stay outside both '#if 0' blocks)
        static int Rank(string s) => s == null ? 1 : s.StartsWith("#endif // [ARK]") ? 0 : s.StartsWith("#if 0 // [ARK] removed") ? 2 : 1;
        foreach (var e in edits.OrderByDescending(e => e.pos).ThenByDescending(e => Rank(e.insert)).ThenByDescending(e => e.order))
        {
            if (e.removeLen > 0) sb.Remove(e.pos, e.removeLen);
            if (e.insert != null) sb.Insert(e.pos, e.insert);
        }
        var fi = new FileInfo(path);
        if (fi.IsReadOnly) fi.IsReadOnly = false;
        File.WriteAllText(path, sb.ToString());
    }

    void Insert(int pos, string s) => edits.Add((pos, editOrder++, s, 0));
    void Replace(int pos, int len, string s) => edits.Add((pos, editOrder++, s, len));

    // ARK-only classes generated by Tools\New-ArkClassHeaders.ps1: type name -> include path ("Ark/PrimalActor.h")
    public static Dictionary<string, string> ArkClassIncludes = new();

    // the base clause of the class whose body starts at 'open': ": public A, public B" -> devkit bases
    string SetBases(TypeLayout t, int open, List<string> bases)
    {
        var shortName = t.Name.Contains("::") ? t.Name[(t.Name.LastIndexOf("::") + 2)..] : t.Name;
        int from = Math.Max(0, open - 4000);
        var head = Regex.Matches(text[from..open], @"\b(class|struct)\b[^;{}]*?\b" + Regex.Escape(shortName) + @"\b(\s*final)?").LastOrDefault()
                   ?? throw new Exception("class head not found");
        int nameEnd = from + head.Index + head.Length;
        var clause = text[nameEnd..open];
        var colon = clause.IndexOf(':');
        var newClause = " : " + string.Join(", ", bases.Select(b => "public " + CppType.Clean(b)));
        if (colon < 0) Insert(nameEnd, newClause + " // [ARK] bases");
        else
        {
            // keep everything after the base list (comments, line break before '{')
            int end = clause.Length; while (end > 0 && char.IsWhiteSpace(clause[end - 1])) end--;
            Replace(nameEnd, end, newClause + " // [ARK] bases (was" + Regex.Replace(clause[..end], @"\s+", " ") + ")");
        }
        // generated ARK-only bases need their header
        foreach (var b in bases)
            if (ArkClassIncludes.TryGetValue(b, out var inc)) AddInclude(inc, "base class");
        return $"bases -> {string.Join(", ", bases)}";
    }

    // type name -> SDK header defining it (set by Patcher.Run)
    public static Func<string, string> HeaderOf;
    readonly HashSet<string> addedIncludes = new(StringComparer.OrdinalIgnoreCase);

    // #include before the .generated.h include (which must stay last), else after the last #include
    void AddInclude(string inc, string why)
    {
        if (text.Contains($"\"{inc}\"") || !addedIncludes.Add(inc)) return;
        var gen = Regex.Match(text, @"^#include\s+""[^""]+\.generated\.h"".*$", RegexOptions.Multiline);
        if (gen.Success) Insert(gen.Index, $"#include \"{inc}\" // [ARK] {why}\r\n");
        else
        {
            var last = Regex.Matches(text, @"^#include\s+.*$", RegexOptions.Multiline).LastOrDefault() ?? throw new Exception("no #include to anchor on");
            Insert(LineEndOf(last.Index) + 1, $"#include \"{inc}\" // [ARK] {why}\r\n");
        }
    }

    // a data member of this class/struct/union/enum type held by value needs the type's definition: its ARK-only
    // header or the engine header defining it, as included by other modules (path below Public\ or Classes\)
    void IncludeDefinitionOf(string type)
    {
        var name = CppType.Clean(type);
        if (!Regex.IsMatch(name, @"^[A-Za-z_][\w:]*$") || Ops.Lookup?.Invoke(type) is not TypeLayout tl || tl.Kind is not ("class" or "struct" or "union" or "enum")) return;
        if (ArkClassIncludes.TryGetValue(name, out var ark)) { AddInclude(ark, "member type"); return; }
        var header = HeaderOf?.Invoke(name);
        if (header == null || string.Equals(Path.GetFullPath(header), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return;
        // only real headers (FString is defined in UnrealString.h.inl, which UnrealString.h includes with its macros)
        var m = Regex.Match(header.Replace('/', '\\'), @"\\(?:Public|Classes)\\(.+\.h)$", RegexOptions.IgnoreCase);
        if (m.Success) AddInclude(m.Groups[1].Value.Replace('\\', '/'), "member type");
    }

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
                    // "virtual float F() const PURE_VIRTUAL(F, return 0.f;)": a function whose body is the macro
                    var pv = Regex.Match(soFar, @"\)[^()]*\bPURE_VIRTUAL\s*\(.*\)$", RegexOptions.Singleline);
                    if (pv.Success)
                    {
                        int k = SkipTrivia(i);
                        if (k < text.Length && text[k] == ';') i = k + 1;
                        return Classify(start, i, soFar[..(pv.Index + 1)] + Regex.Replace(soFar[(pv.Index + 1)..], @"\bPURE_VIRTUAL\s*\(.*\)$", "{}", RegexOptions.Singleline), access);
                    }
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
                // "class X* Get() const { ... }" is a function with an elaborated return type, not a nested type
                bool isType = !isFunctionBody && firstWord is "class" or "struct" or "union" or "enum" or "namespace";
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
        // "class AActor* Target;" is a data member with an elaborated type specifier, not a forward declaration
        if (first is "class" or "struct" && !t.Contains('{') && !t.Contains('(') && Regex.IsMatch(t, @"^(class|struct)\s+[\w:]+(\s*<[^;]*>)?[\s*&]+[A-Za-z_]\w*(\s*\[[^\]]*\])?\s*(=[^;]*)?;?$"))
            t = st.Text = t[(first.Length)..].TrimStart();
        // "class X* Get() const { ... }": a function with an elaborated return type
        else if (first is "class" or "struct" && Regex.IsMatch(t, @"^(class|struct)\s+[\w:]+(\s*<[^;{(]*>)?[\s*&]+[A-Za-z_]\w*\s*\("))
            t = t[(first.Length)..].TrimStart();
        else if (first is "class" or "struct" or "union" or "enum")
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

    // Layout-independence probe, without changing any line numbers: a data member before 'before' (with its
    // UPROPERTY; a base, vfptr or null: at the start of the type body, atEnd: before its closing brace) and a virtual
    // function at the start of the body.
    public string InjectProbe(TypeLayout t, int line, bool data, Member before, bool atEnd, bool virtualFn)
    {
        var (open, slateArgs) = FindBody(t, line);
        if (open < 0 || slateArgs) return "body not found";
        var stmts = Scan(open, t.Kind != "class", false, out int bodyEnd);
        int start = open + 1;
        var gen = new Regex(@"\G\s*GENERATED_\w*BODY\w*\s*\(\s*\)").Match(text, open + 1);
        if (gen.Success) start = gen.Index + gen.Length;   // keep UHT's GENERATED_*BODY() first
        else
        {
            var first = stmts.FirstOrDefault(s => s.Kind != "preproc");
            if (first != null && first.Kind == "macro" && first.MacroName.StartsWith("GENERATED_")) start = first.End;
        }
        var done = new List<string>();
        if (data)
        {
            int pos = start; string where = "at start";
            if (atEnd)
            {
                if (bodyEnd < 0) return "body end not found";
                pos = bodyEnd; where = "at end";
            }
            else if (before != null && before.Kind is "data" or "bitfield")
            {
                if (FindData(stmts, before.Name) is Stmt s) { pos = LeadingMacrosStart(s, stmts); where = $"before {before.Name}"; }
                else where = $"at start ({before.Name} not found)";
            }
            Insert(pos, " uint8 ArkLayoutProbe_Data[64]; ");
            done.Add("data " + where);
        }
        if (virtualFn) { Insert(start, " virtual void ArkLayoutProbe_Virtual();"); done.Add("virtual"); }
        return "probed: " + string.Join(", ", done);
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

    // the module export macro for members of a class that is not exported as a whole (null if it is, or unknown)
    string ClassApi(int open, int bodyEnd)
    {
        if (Regex.IsMatch(text[Math.Max(0, open - 400)..open], @"\b(class|struct)\s+[A-Z][A-Z0-9_]*_API\b")) return null;
        var api = Regex.Match(text[Math.Max(0, open - 400)..bodyEnd], ApiMacro);
        return api.Success ? api.Value : null;
    }

    Stmt FindData(List<Stmt> stmts, string name) => stmts.FirstOrDefault(s => (s.Kind is "data" or "macro") && s.Names.Contains(name));

    Stmt FindFunction(List<Stmt> stmts, Member m)
    {
        var (args, isConst, _) = CppType.SplitSignature(m.Signature);
        int n = CppType.ArgCount(args);
        var cands = stmts.Where(s => s.Kind == "function" && s.Names.Contains(m.Name)).ToList();
        var exact = cands.Where(s => s.ArgCount == n).ToList();
        if (exact.Count > 1) exact = exact.Where(s => Regex.IsMatch(s.Text, @"\)\s*const\b") == isConst).DefaultIfEmpty(exact[0]).ToList();
        if (exact.Count > 1)
        {
            // overloads with the same number of arguments: the one whose declaration names the argument types
            // first (outer template, e.g. TArray vs TArrayView) and last identifier of every argument type
            var typeNames = CppType.SplitArgs(args).Select(CppType.Clean)
                .Select(a => Regex.Matches(a, @"[A-Za-z_]\w*").Select(x => x.Value).Where(w => w is not ("const" or "unsigned" or "signed")).ToList())
                .SelectMany(w => w.Count == 0 ? new string[0] : new[] { w[0], w[^1] }.Distinct()).ToList();
            exact = exact.OrderByDescending(s => typeNames.Count(tn => Regex.IsMatch(s.Text, $@"\b{Regex.Escape(tn)}(8|16|32|64)?\b"))).ToList();   // int -> int32
        }
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
                if (!slateArgs && op.Member.Kind == "data")
                {
                    // a type nested in this class: unqualified if the header declares it; ARK-only nested and unnamed
                    // types become opaque storage of the same size and alignment
                    var nested = Regex.Match(CppType.Clean(op.Member.Type), $@"^{Regex.Escape(t.Name)}::(\w+)$");
                    bool opaque = op.Member.Type.Contains("<unnamed");
                    if (nested.Success)
                    {
                        if (Regex.IsMatch(text[open..bodyEnd], $@"\b{Regex.Escape(nested.Groups[1].Value)}\b"))
                            decl = CppType.Declare(nested.Groups[1].Value, op.Member.Name) + ";";
                        else opaque = true;
                    }
                    if (opaque && Ops.Lookup?.Invoke(op.Member.Type) is TypeLayout ot)
                        decl = $"alignas({Generator.AlignOf(Ops.Lookup, op.Member.Type, 0)}) uint8 {op.Member.Name}[{ot.Size}];";
                    else if (!nested.Success) IncludeDefinitionOf(op.Member.Type);
                }
                if (op.Replaces != null && FindData(stmts, op.Replaces.Name) is Stmt old)
                {
                    // same member, new type: put the new declaration right after the (removed) old one, keeping what is
                    // not part of the layout but inline code relies on: 'mutable' and a simple default initializer
                    if (Regex.IsMatch(old.Text, @"^\s*mutable\b")) decl = "mutable " + decl;
                    var init = Regex.Match(old.Text, @"=\s*(-?[\d.]+f?|nullptr|true|false)\s*;?\s*$");
                    if (init.Success && op.Member.Kind == "data") decl = decl.TrimEnd(';') + " = " + init.Groups[1].Value + ";";
                    int lineAfter = AfterStmt(old);
                    if (lineAfter < text.Length && text[lineAfter] == '\n') lineAfter++;
                    Insert(lineAfter, $"{IndentOf(old.Start)}{decl} // [ARK] type changed\r\n");
                    return $"replaced {op.Replaces.Name} by {decl}";
                }
                // anchor declared in several preprocessor branches (#if ... #else): follow each of them
                var anchors = op.Anchor != null && op.Anchor.Kind is "data" or "bitfield"
                    ? stmts.Where(s => s.Kind is "data" or "macro" && s.Names.Contains(op.Anchor.Name)).ToList() : new List<Stmt>();
                if (anchors.Count > 1)
                {
                    foreach (var a in anchors) InsertDecl(AfterStmt(a), IndentOf(a.Start), decl, slateArgs ? null : op.Member.Access, a.Access);
                    return $"inserted {decl} (after each of {anchors.Count} declarations of {op.Anchor.Name})";
                }
                // anchor removed (#if 0) and replaced: follow the replacement, i.e. go after the #endif of the removal
                if (anchors.Count == 1 && removed.Contains(anchors[0]))
                {
                    int lineAfter = AfterStmt(anchors[0]);
                    if (lineAfter < text.Length && text[lineAfter] == '\n') lineAfter++;
                    Insert(lineAfter, $"{IndentOf(anchors[0].Start)}{decl} // [ARK]\r\n");
                    return $"inserted {decl} (after the replaced {op.Anchor.Name})";
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
                // An RPC / BlueprintNativeEvent wrapper that sits between UHT generated virtuals (or replaces one) must be
                // declared as a UFUNCTION; one that ARK added outside of them can be declared explicitly.
                bool isWrapper = op.Member.Name.EndsWith("_Implementation") || op.Member.Name.EndsWith("_Validate");
                bool anchorIsWrapper = op.Anchor != null && (op.Anchor.Name.EndsWith("_Implementation") || op.Anchor.Name.EndsWith("_Validate"))
                                       && FindFunction(stmts, op.Anchor) == null;
                if (isWrapper && anchorIsWrapper)
                    throw new Exception("generated by UHT from a UFUNCTION: edit the UFUNCTION declaration (vtable order = reverse declaration order)");
                var decl = CppType.MethodDecl(op.Member);
                string virtualized = "";
                if (op.Virtualizes != null && FindFunction(stmts, op.Virtualizes) is Stmt orig)
                {
                    // ARK made an existing method virtual: drop the old declaration, keep its export macro
                    var api = Regex.Match(orig.Text, ApiMacro);
                    if (api.Success) decl = api.Value + " " + decl;
                    RemoveStmt(orig, stmts);
                    virtualized = " (was non-virtual)";
                }
                // new virtuals of a class that is not exported as a whole are exported individually (Super:: calls)
                if (!Regex.IsMatch(decl, ApiMacro) && ClassApi(open, bodyEnd) is string classMacro) decl = classMacro + " " + decl;
                int pos; string indent; string accessHere;
                if (op.ReplacesVirtual != null && FindFunction(stmts, op.ReplacesVirtual) is Stmt prevSig)
                {
                    // signature changed in place: after the removed declaration (its #endif), keeping its export macro
                    var api = Regex.Match(prevSig.Text, ApiMacro);
                    if (api.Success && !Regex.IsMatch(decl, ApiMacro)) decl = api.Value + " " + decl;
                    int lineAfter = AfterStmt(prevSig);
                    if (lineAfter < text.Length && text[lineAfter] == '\n') lineAfter++;
                    Insert(lineAfter, $"{IndentOf(prevSig.Start)}{decl} // [ARK] signature changed\r\n");
                    return $"inserted {decl} (in place of {op.ReplacesVirtual.Name}{op.ReplacesVirtual.Signature})";
                }
                if (op.OverloadOf != null && FindFunction(stmts, op.OverloadOf) is Stmt sib)
                {
                    // vtable order of overloads = reverse declaration order
                    if (op.OverloadFirst) { pos = AfterStmt(sib); }
                    else { pos = LineStartOf(LeadingMacrosStart(sib, stmts)) - 1; }
                    InsertDecl(pos, IndentOf(sib.Start), decl, op.Member.Access, sib.Access);
                    return $"inserted {decl}{virtualized} (overload of {op.OverloadOf.Name}, {(op.OverloadFirst ? "declared after" : "declared before")} it)";
                }
                // virtuals UHT declares in GENERATED_BODY (RPC / BlueprintNativeEvent wrappers, interface boilerplate)
                // come first in the vtable: following one of them means being the first declared virtual
                var anchorDecl = op.Anchor != null ? FindFunction(stmts, op.Anchor) : null;
                bool anchorGenerated = op.Anchor != null && anchorDecl == null
                    && (op.Anchor.Name.EndsWith("_Implementation") || op.Anchor.Name.EndsWith("_Validate") || op.Anchor.Name == "_getUObject");
                if (op.Anchor != null && !anchorGenerated)
                {
                    var a = anchorDecl ?? throw new Exception($"anchor virtual {op.Anchor.Name} not found");
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
            case "set-bases":
                return SetBases(t, open, op.Bases);
            case "insert-override":
            {
                // at the end of the class body; the module's export macro if the class is not exported as a whole
                if (FindFunction(stmts, op.Member) is Stmt nonVirtual && !nonVirtual.Virtual)
                {
                    // ARK overrides a method the class declares non-virtual (e.g. an inline accessor that implements an
                    // ARK interface): make that declaration virtual, keeping its body and export macro
                    Insert(nonVirtual.Start, "virtual /* [ARK] */ ");
                    return $"made {op.Member.Name} virtual (override)";
                }
                var decl = CppType.MethodDecl(op.Member);
                if (ClassApi(open, bodyEnd) is string api) decl = api + " " + decl;
                var last = stmts.LastOrDefault();
                InsertDecl(LineStartOf(bodyEnd) - 1, last != null ? IndentOf(last.Start) : IndentOf(open) + "\t", decl, op.Member.Access, last?.Access ?? (t.Kind == "class" ? "private" : "public"));
                return $"inserted override {decl}";
            }
            case "remove-override":
            {
                var s = FindFunction(stmts, op.Member) ?? throw new Exception($"override {op.Member.Name} not found");
                RemoveStmt(s, stmts);
                return $"removed override {op.Member.Name}{op.Member.Signature}";
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

    readonly HashSet<Stmt> removed = new();
    void RemoveStmt(Stmt s, List<Stmt> stmts)
    {
        removed.Add(s);
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
            if (op.Kind == "set-underlying")
            {
                var u = op.Value switch { 1 => "uint8", 2 => "uint16", 8 => "uint64", _ => "int32" };
                int from = Math.Max(0, open - 600);
                var head = Regex.Matches(text[from..open], @"\benum\s+(?:class\s+|struct\s+)?[A-Za-z_]\w*(\s*:\s*[\w:]+)?\s*$").LastOrDefault();
                if (head == null) { results.Add($"MANUAL: enum head not found (underlying type {u})"); continue; }
                if (head.Groups[1].Success) Replace(from + head.Groups[1].Index, head.Groups[1].Length, $" : {u} /* [ARK] was{head.Groups[1].Value.Replace(":", "")} */");
                else Insert(from + head.Index + head.Value.TrimEnd().Length, $" : {u} /* [ARK] */");
                results.Add($"underlying type -> {u}");
                continue;
            }
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
