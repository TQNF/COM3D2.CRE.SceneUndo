using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

// Fake snapshot item implementing the REAL MaidEdit.ISnapShotItem interface,
// with payload fields mimicking the real item layout (backing-free public
// fields stand in for the k__BackingField payload fields).
public class FakeItem : MaidEdit.ISnapShotItem
{
    public ulong id { get; set; }
    public MaidEdit.ISnapShotItem.ItemType itemType { get; set; }
    public MaidEdit.ISnapShotItem.Priority priority { get; set; }
    public MPN mpn { get; set; }
    public bool enabled { get; set; }
    public MaidEdit.ISnapShotItem dependencyItem { get; set; }
    public List<MaidEdit.ISnapShotItem> childrenItems;
    public IReadOnlyList<MaidEdit.ISnapShotItem> children { get { return childrenItems; } }

    // payload (like SnapShotItemEditMenu / Parameter / *DependentColor)
    public ulong partsId;
    public int value;
    public string colorPresetId;
    public byte[] colorPresetData;
    public List<byte[]> transformObjectData;
    public float hairLength;

    public bool Apply(MaidEdit.MaidEditManager manager) { return true; }
    public void OnBeforeSerialize() { }
    public void OnAfterDeserialize() { }
}

// second fake type for the type-mismatch test
public class FakeOther : MaidEdit.ISnapShotItem
{
    public ulong id { get; set; }
    public MaidEdit.ISnapShotItem.ItemType itemType { get; set; }
    public MaidEdit.ISnapShotItem.Priority priority { get; set; }
    public MPN mpn { get; set; }
    public bool enabled { get; set; }
    public MaidEdit.ISnapShotItem dependencyItem { get; set; }
    public IReadOnlyList<MaidEdit.ISnapShotItem> children { get { return null; } }
    public int toothMPN;
    public bool Apply(MaidEdit.MaidEditManager manager) { return false; }
    public void OnBeforeSerialize() { }
    public void OnAfterDeserialize() { }
}

public static class Driver
{
    const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static string ManagedDir;

    static Assembly Resolve(object s, ResolveEventArgs e)
    {
        string n = new AssemblyName(e.Name).Name;
        string f = Path.Combine(ManagedDir, n + ".dll");
        if (File.Exists(f)) return Assembly.LoadFrom(f);
        string bep = Path.Combine(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(ManagedDir)), "BepInEx"), "core");
        string f2 = Path.Combine(bep, n + ".dll");
        if (File.Exists(f2)) return Assembly.LoadFrom(f2);
        return null;
    }

    static void FSet(Type t, object o, string n, object v) { t.GetField(n, BF).SetValue(o, v); }

    class R { public List<string> L = new List<string>(); public void C(string name, bool ok, string detail) { L.Add((ok ? "PASS" : "FAIL") + " | " + name + (detail.Length > 0 ? " | " + detail : "")); } }

    static MethodInfo itemsEqM;

    static List<MaidEdit.ISnapShotItem> L(params MaidEdit.ISnapShotItem[] items) { return new List<MaidEdit.ISnapShotItem>(items); }

    static bool Eq(IReadOnlyList<MaidEdit.ISnapShotItem> a, IReadOnlyList<MaidEdit.ISnapShotItem> b)
    {
        return (bool)itemsEqM.Invoke(null, new object[] { a, b });
    }

    static int idc;
    static FakeItem Mk(ulong partsId, int value, string presetId, byte[] presetData, MPN mpn)
    {
        FakeItem f = new FakeItem();
        f.id = (ulong)System.Threading.Interlocked.Increment(ref idc); // random-like ids
        f.mpn = mpn;
        f.enabled = true;
        f.partsId = partsId;
        f.value = value;
        f.colorPresetId = presetId;
        f.colorPresetData = presetData;
        f.transformObjectData = new List<byte[]> { new byte[] { 1, 2, 3 } };
        f.hairLength = 0.5f;
        return f;
    }

    public static string[] Run(string pluginDll, string managedDir)
    {
        ManagedDir = managedDir;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        R r = new R();

        Assembly asmCS = Assembly.LoadFrom(Path.Combine(managedDir, "Assembly-CSharp.dll"));
        Assembly plugin = Assembly.LoadFrom(pluginDll);
        Type pt = plugin.GetType("CRESceneUndo.SceneUndoPlugin");
        Type histT = pt.GetNestedType("History", BF);
        Type propT = pt.GetNestedType("PropSnap", BF);
        r.C("types resolved", pt != null && histT != null && propT != null, "");

        // ------- comparator tests -------
        itemsEqM = pt.GetMethod("ItemsEqual", BF);
        MethodInfo lightEqM = pt.GetMethod("LightEquals", BF);
        r.C("methods resolved", itemsEqM != null && lightEqM != null, "");

        FakeItem a1 = Mk(100, 1, "p1", new byte[] { 9, 9 }, MPN.wear);
        FakeItem a2 = Mk(100, 1, "p1", new byte[] { 9, 9 }, MPN.wear); // same payload, different random id
        r.C("T1 equal trees (random ids) -> true", Eq(L(a1), L(a2)), "");

        FakeItem b1 = Mk(100, 1, "p1", new byte[] { 9, 9 }, MPN.wear);
        FakeItem b2 = Mk(200, 1, "p1", new byte[] { 9, 9 }, MPN.wear);
        r.C("T2 diff partsId -> false", !Eq(L(b1), L(b2)), "");

        FakeItem c1 = Mk(100, 1, "p1", new byte[] { 9, 9 }, MPN.wear);
        FakeItem c2 = Mk(100, 2, "p1", new byte[] { 9, 9 }, MPN.wear);
        r.C("T3 diff value -> false", !Eq(L(c1), L(c2)), "");

        FakeItem d1 = Mk(100, 1, "p1", new byte[] { 9, 9 }, MPN.wear);
        FakeItem d2 = Mk(100, 1, "p1", new byte[] { 9, 8 }, MPN.wear);
        r.C("T4 diff colorPresetData -> false", !Eq(L(d1), L(d2)), "");

        FakeItem e1 = Mk(100, 1, "p1", new byte[] { 9, 9 }, MPN.wear);
        FakeItem e2 = Mk(100, 1, "p2", new byte[] { 9, 9 }, MPN.wear);
        r.C("T5 diff colorPresetId -> false", !Eq(L(e1), L(e2)), "");

        FakeItem f1 = Mk(100, 1, "p1", new byte[] { 9, 9 }, MPN.wear);
        FakeItem f2 = Mk(100, 1, "p1", new byte[] { 9, 9 }, MPN.wear);
        f2.transformObjectData[0] = new byte[] { 1, 2, 4 };
        r.C("T6 diff transformObjectData -> false", !Eq(L(f1), L(f2)), "");

        // children: same payload new objects + random ids -> true
        FakeItem g1 = Mk(100, 1, "p1", new byte[] { 9, 9 }, MPN.wear);
        g1.childrenItems = new List<MaidEdit.ISnapShotItem> { Mk(500, 5, "cp", new byte[] { 7 }, MPN.skirt) };
        FakeItem g2 = Mk(100, 1, "p1", new byte[] { 9, 9 }, MPN.wear);
        g2.childrenItems = new List<MaidEdit.ISnapShotItem> { Mk(500, 5, "cp", new byte[] { 7 }, MPN.skirt) };
        r.C("T7 equal children (new objects) -> true", Eq(L(g1), L(g2)), "");

        g2.childrenItems[0] = Mk(501, 5, "cp", new byte[] { 7 }, MPN.skirt);
        r.C("T8 diff child partsId -> false", !Eq(L(g1), L(g2)), "");

        FakeItem h1 = Mk(100, 1, "p1", new byte[] { 9, 9 }, MPN.wear);
        FakeItem h2 = Mk(100, 1, "p1", new byte[] { 9, 9 }, MPN.skirt);
        r.C("T9 diff mpn -> false", !Eq(L(h1), L(h2)), "");

        FakeItem i1 = Mk(100, 1, "p1", new byte[] { 9, 9 }, MPN.wear);
        FakeItem i2 = Mk(100, 1, "p1", new byte[] { 9, 9 }, MPN.wear);
        i2.enabled = false;
        r.C("T10 diff enabled -> false", !Eq(L(i1), L(i2)), "");

        FakeItem j1 = Mk(100, 1, "p1", new byte[] { 9, 9 }, MPN.wear);
        FakeOther j2 = new FakeOther();
        j2.mpn = MPN.wear; j2.enabled = true; j2.toothMPN = 0;
        r.C("T11 diff concrete type -> false", !Eq(L(j1), L(j2)), "");

        r.C("T12 diff list count -> false", !Eq(L(a1, a1), L(a2)), "");
        r.C("T13 both null lists -> true", Eq(null, null), "");
        r.C("T13b one null list -> false", !Eq(L(a1), null), "");

        // hairLength float payload
        FakeItem k1 = Mk(100, 1, "p1", new byte[] { 9, 9 }, MPN.wear);
        FakeItem k2 = Mk(100, 1, "p1", new byte[] { 9, 9 }, MPN.wear);
        k2.hairLength = 0.6f;
        r.C("T14 diff hairLength -> false", !Eq(L(k1), L(k2)), "");

        // ------- LightEquals (kept from v1.x core) -------
        Type dictT = typeof(Dictionary<,>).MakeGenericType(typeof(int), propT);
        Func<string, ulong, int, object> mkLight = delegate(string file, ulong rid, int val)
        {
            object d = Activator.CreateInstance(dictT);
            object p = Activator.CreateInstance(propT);
            FSet(propT, p, "FileName", file);
            FSet(propT, p, "Rid", rid);
            FSet(propT, p, "Value", val);
            dictT.GetMethod("Add").Invoke(d, new object[] { 1, p });
            return d;
        };
        r.C("T15 equal lights -> true", (bool)lightEqM.Invoke(null, new object[] { mkLight("a.menu", 42, 5), mkLight("a.menu", 42, 5) }), "");
        r.C("T16 diff file -> false", !(bool)lightEqM.Invoke(null, new object[] { mkLight("a.menu", 42, 5), mkLight("b.menu", 42, 5) }), "");
        r.C("T17 diff value -> false", !(bool)lightEqM.Invoke(null, new object[] { mkLight("a.menu", 42, 5), mkLight("a.menu", 42, 6) }), "");

        // ------- History core invariants (List<ISnapShotItem> entries) -------
        FieldInfo listF = histT.GetField("list", BF);
        object h = Activator.CreateInstance(histT);
        MethodInfo recM = histT.GetMethod("Record", BF);
        MethodInfo clearM = histT.GetMethod("Clear", BF);
        MethodInfo undoM = histT.GetMethod("Undo", BF);
        MethodInfo redoM = histT.GetMethod("Redo", BF);
        List<MaidEdit.ISnapShotItem> sA = L(Mk(1, 1, null, null, MPN.wear));
        List<MaidEdit.ISnapShotItem> sB = L(Mk(2, 2, null, null, MPN.wear));
        List<MaidEdit.ISnapShotItem> sC = L(Mk(3, 3, null, null, MPN.wear));
        recM.Invoke(h, new object[] { sA });
        recM.Invoke(h, new object[] { sB });
        recM.Invoke(h, new object[] { sC });
        r.C("T18 record x3", ((System.Collections.ICollection)listF.GetValue(h)).Count == 3, "");
        object[] aOut = new object[1];
        bool ur = (bool)undoM.Invoke(h, aOut);
        r.C("T19 undo1 -> B", ur && object.ReferenceEquals(aOut[0], sB), "");
        ur = (bool)undoM.Invoke(h, aOut);
        r.C("T20 undo2 -> A (baseline)", ur && object.ReferenceEquals(aOut[0], sA), "");
        ur = (bool)undoM.Invoke(h, aOut);
        r.C("T21 undo3 -> false", !ur, "");
        ur = (bool)redoM.Invoke(h, aOut);
        r.C("T22 redo -> B", ur && object.ReferenceEquals(aOut[0], sB), "");
        ur = (bool)undoM.Invoke(h, aOut);
        r.C("T23 undo -> A again", ur && object.ReferenceEquals(aOut[0], sA), "");
        List<MaidEdit.ISnapShotItem> sD = L(Mk(4, 4, null, null, MPN.wear));
        recM.Invoke(h, new object[] { sD });  // discards redo tail
        r.C("T24 record trims redo tail", ((System.Collections.ICollection)listF.GetValue(h)).Count == 2, "");
        ur = (bool)redoM.Invoke(h, aOut);
        r.C("T25 redo after trim -> false", !ur, "");
        clearM.Invoke(h, null);
        r.C("T26 clear", ((System.Collections.ICollection)listF.GetValue(h)).Count == 0, "");
        Exception threw = null;
        try { recM.Invoke(h, new object[] { sD }); }
        catch (TargetInvocationException tie) { threw = tie.InnerException; }
        catch (Exception ex) { threw = ex; }
        r.C("T27 record after clear no throw (problem-4 scenario)", threw == null, threw == null ? "" : threw.ToString());
        ur = (bool)undoM.Invoke(h, aOut);
        r.C("T28 single entry undo false", !ur, "");

        histT.GetField("MaxEntries", BF).SetValue(h, 3);
        for (int i = 0; i < 5; i++) recM.Invoke(h, new object[] { L(Mk((ulong)(10 + i), i, null, null, MPN.wear)) });
        r.C("T29 MaxEntries trim", ((System.Collections.ICollection)listF.GetValue(h)).Count == 3, "");

        return r.L.ToArray();
    }
}
