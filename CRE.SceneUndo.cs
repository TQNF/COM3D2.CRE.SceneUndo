using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace CRESceneUndo
{
    // Ctrl+Z / Ctrl+Y undo-redo for CR EditSystem (NEW EDIT MODE).
    //
    // v2.0: built entirely on the game's own edit-snapshot system:
    //   - capture: MaidEditManager.CreateSnapShotItem() (the same call the
    //     official EditResetDataManager makes when the edit scene opens)
    //   - restore: MaidEdit.SnapShot.Apply() (the same call behind every
    //     per-panel "reset" button: MultiSliderManager.ResetValue,
    //     SlotItemMountController.ResetSelectedSlot, ...)
    // Snapshot items are pure value objects (menu selection by partsId,
    // slider values, color presets, unit transforms, hair lengths, ...) and
    // Apply() never mutates them, so live item trees are stored in history.
    //
    // v1.x (hand-rolled MaidProp/22-color/ApplyPreset snapshots) missed the
    // set_* costume metadata slots (excluded by ApplyPreset's All filter)
    // and the edit-parameter state (body sliders etc.) - both are ordinary
    // snapshot items here.
    [BepInPlugin("org.cre.sceneundo", "CRE SceneUndo", "2.0.0")]
    public class SceneUndoPlugin : BaseUnityPlugin
    {
        // ---------------- history ----------------
        // Invariants (learned from EditSceneUndo's original cursor bug):
        // cursor == -1 means "at tip"; Clear() ALWAYS resets the cursor;
        // Record() keeps the currently-viewed entry and discards only the
        // redo tail.
        private sealed class History
        {
            private readonly List<List<MaidEdit.ISnapShotItem>> list =
                new List<List<MaidEdit.ISnapShotItem>>();
            private int cursor = -1;
            public int MaxEntries = 50;

            public int Count { get { return list.Count; } }
            public int Cursor { get { return cursor; } }

            public void Record(List<MaidEdit.ISnapShotItem> s)
            {
                if (cursor >= 0 && cursor + 1 < list.Count)
                    list.RemoveRange(cursor + 1, list.Count - cursor - 1);
                cursor = -1;
                list.Add(s);
                if (list.Count > MaxEntries)
                    list.RemoveRange(1, list.Count - MaxEntries);
            }

            public void Clear()
            {
                list.Clear();
                cursor = -1;
            }

            public bool Undo(out List<MaidEdit.ISnapShotItem> s)
            {
                s = null;
                if (cursor < 0)
                {
                    if (list.Count == 0) return false;
                    cursor = list.Count - 1;
                }
                cursor--;
                if (cursor < 0)
                {
                    cursor = 0;
                    return false;
                }
                s = list[cursor];
                return true;
            }

            public bool Redo(out List<MaidEdit.ISnapShotItem> s)
            {
                s = null;
                if (cursor < 0) return false;
                cursor++;
                if (cursor >= list.Count)
                {
                    cursor = list.Count - 1;
                    return false;
                }
                s = list[cursor];
                return true;
            }
        }

        // per-frame cheap sensor: raw MaidProp fields (items + sliders)
        private sealed class PropSnap
        {
            public string FileName;
            public ulong Rid;
            public int Value;
        }

        private const string Version = "2.1.0";

        private ManualLogSource log;
        private readonly History hist = new History();
        private List<MaidEdit.ISnapShotItem> prev;   // last recorded item tree
        private Dictionary<int, PropSnap> prevLight; // light sensor baseline
        private Maid prevMaid;
        private object prevHead;                     // boxed maidHeadType
        private bool applying;
        private bool resettle;                        // re-baseline after apply
        private bool mouseWasDown;
        private int frameCounter;
        private int[] mpnValues;
        private int lastErrFrame = -1000;

        private ConfigEntry<KeyCode> cfgUndoKey;
        private ConfigEntry<KeyCode> cfgRedoKey;
        private ConfigEntry<bool> cfgRequireCtrl;
        private ConfigEntry<int> cfgMaxHistory;
        private ConfigEntry<int> cfgPollFrames;

        // head-type access (nested enum type is not public, read boxed)
        private static readonly FieldInfo fiFaceManager =
            typeof(MaidEdit.MaidEditManager).GetField("faceManager");
        private static readonly PropertyInfo piMaidHeadType =
            typeof(MaidEdit.FaceManager).GetProperty("maidHeadType",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        private void Awake()
        {
            log = Logger;
            cfgUndoKey = Config.Bind("Keys", "Undo", KeyCode.Z, "Undo key (combined with Control)");
            cfgRedoKey = Config.Bind("Keys", "Redo", KeyCode.Y, "Redo key (combined with Control)");
            cfgRequireCtrl = Config.Bind("Keys", "RequireControl", true, "Require Control modifier for undo/redo");
            cfgMaxHistory = Config.Bind("History", "MaxEntries", 50, "Max recorded undo entries");
            cfgPollFrames = Config.Bind("General", "PollFrames", 180,
                "Periodic deep-check interval in frames (0 = only mouse-release/light-sensor triggers)");
            hist.MaxEntries = Mathf.Max(2, cfgMaxHistory.Value);
            mpnValues = (int[])Enum.GetValues(typeof(MPN));
            log.LogInfo("CRE SceneUndo " + Version + " loaded. Ctrl+Z = undo, Ctrl+Y = redo (official snapshot path).");
        }

        private void Update()
        {
            try { UpdateCore(); }
            catch (Exception e) { ThrottledError("Update: " + e); }
        }

        private void UpdateCore()
        {
            SceneEdit.SceneEditManager sem = SceneEditReferencer.SceneEditManager;
            if (sem == null || sem.EditMaidManager == null) { ResetAll(); return; }
            MaidEdit.MaidEditManager em = sem.EditMaidManager;
            Maid maid = em.maid;
            if (maid == null) { ResetAll(); return; }

            if (!ReferenceEquals(prevMaid, maid))
            {
                hist.Clear();
                prev = null;
                prevLight = null;
                prevHead = null;
                prevMaid = maid;
            }

            // busy: official apply coroutine in progress / async prop loading
            if (maid.IsSnapShotApplyBusy || maid.IsAllProcPropBusy) return;

            if (applying)
            {
                // waiting for the apply completion callback (60s self-heal)
                if (Time.frameCount - applyStartFrame < 3600) return;
                applying = false;
            }

            if (resettle)
            {
                // after an undo/redo finished: re-baseline to the ACTUAL
                // current state without recording (imperfect restores must
                // not spawn phantom entries or kill the redo tail)
                resettle = false;
                try
                {
                    List<MaidEdit.ISnapShotItem> cur = em.CreateSnapShotItem();
                    if (cur != null && cur.Count > 0)
                    {
                        prev = cur;
                        prevLight = CaptureLight(maid);
                    }
                }
                catch (Exception e) { ThrottledError("resettle: " + e); }
                return;
            }

            bool mouseDown = Input.GetMouseButton(0);
            bool releaseEdge = mouseWasDown && !mouseDown;
            mouseWasDown = mouseDown;
            if (mouseDown) return;

            bool needBaseline = prev == null;

            bool lightChanged = false;
            if (!needBaseline && prevLight != null)
            {
                try { lightChanged = !LightEquals(CaptureLight(maid), prevLight); }
                catch (Exception e) { ThrottledError("light: " + e); }
            }

            frameCounter++;
            int poll = cfgPollFrames.Value;
            bool periodic = poll > 0 && (frameCounter % poll) == 0;

            if (needBaseline || lightChanged || releaseEdge || periodic)
            {
                try { HandleTrigger(em, maid); }
                catch (Exception e) { ThrottledError("capture: " + e); }
            }

            if (UndoPressed())
            {
                try { DoUndo(em); } catch (Exception e) { ThrottledError("undo: " + e); }
                return;
            }
            if (RedoPressed())
            {
                try { DoRedo(em); } catch (Exception e) { ThrottledError("redo: " + e); }
            }
        }

        private void HandleTrigger(MaidEdit.MaidEditManager em, Maid maid)
        {
            // head-type (2.0 <-> 3.0) switch = history boundary: the official
            // reset system also restores head-dependent categories from
            // separately saved data, old-head items are not applicable
            object head = ReadHeadType(em);
            if (head != null)
            {
                if (prevHead == null) prevHead = head;
                else if (!object.Equals(prevHead, head))
                {
                    hist.Clear();
                    prev = null;
                    prevLight = null;
                    prevHead = head;
                    log.LogInfo("Head type changed - history cleared.");
                }
            }

            List<MaidEdit.ISnapShotItem> cur = em.CreateSnapShotItem();
            prevLight = CaptureLight(maid);
            if (cur == null || cur.Count == 0) return;

            if (prev == null)
            {
                prev = cur;
                hist.Record(cur);   // baseline = state at edit entry
                return;
            }
            if (!ItemsEqual(cur, prev))
            {
                hist.Record(cur);
                prev = cur;
                log.LogInfo("Recorded entry " + (hist.Count - 1) + " (" + cur.Count + " items).");
            }
        }

        private bool UndoPressed()
        {
            if (!Input.GetKeyDown(cfgUndoKey.Value)) return false;
            if (cfgRequireCtrl.Value && !Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl)) return false;
            return true;
        }

        private bool RedoPressed()
        {
            if (!Input.GetKeyDown(cfgRedoKey.Value)) return false;
            if (cfgRequireCtrl.Value && !Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl)) return false;
            return true;
        }

        private void DoUndo(MaidEdit.MaidEditManager em)
        {
            List<MaidEdit.ISnapShotItem> s;
            if (!hist.Undo(out s)) { log.LogInfo("Undo: nothing to undo."); return; }
            log.LogInfo("Undo -> entry " + hist.Cursor + " / " + (hist.Count - 1) + " (" + (s == null ? 0 : s.Count) + " items)");
            ApplyItems(em, s);
        }

        private void DoRedo(MaidEdit.MaidEditManager em)
        {
            List<MaidEdit.ISnapShotItem> s;
            if (!hist.Redo(out s)) { log.LogInfo("Redo: nothing to redo."); return; }
            log.LogInfo("Redo -> entry " + hist.Cursor + " / " + (hist.Count - 1) + " (" + (s == null ? 0 : s.Count) + " items)");
            ApplyItems(em, s);
        }

        private int applyStartFrame;

        private void ApplyItems(MaidEdit.MaidEditManager em, List<MaidEdit.ISnapShotItem> items)
        {
            if (items == null || items.Count == 0) return;
            applying = true;
            applyStartFrame = Time.frameCount;
            bool ok = false;
            try { ok = MaidEdit.SnapShot.Apply(em, items, OnApplyComplete); }
            catch (Exception e) { ThrottledError("apply: " + e); }
            if (!ok)
            {
                applying = false;
                resettle = true;
            }
        }

        // runs inside the official ApplyCoroutine when everything is done
        private void OnApplyComplete()
        {
            applying = false;
            resettle = true;
            // official post-apply refresh (EditResetDataManager::LoadAllSnapShot
            // callback does exactly this pair): rebuild the open category panel
            // node (sliders/menus/values) + custom view
            try
            {
                CategoryPanelController cpc = SceneEditReferencer.CategoryPanelController;
                if (cpc != null)
                {
                    cpc.ReloadEditcategoryPanel();
                    cpc.ReloadCustomView();
                }
            }
            catch (Exception e) { ThrottledError("ui reload: " + e); }
            // movable panels (color palette / placement panels)
            try
            {
                EditMoveablePanelReloader r = SceneEditReferencer.EditMoveablePanelReloader;
                if (r != null) r.ReloadPanel();
            }
            catch (Exception e) { ThrottledError("ui reload2: " + e); }
        }

        private void ResetAll()
        {
            hist.Clear();
            prev = null;
            prevLight = null;
            prevMaid = null;
            prevHead = null;
            resettle = false;
        }

        // ---------------- light sensor ----------------

        private Dictionary<int, PropSnap> CaptureLight(Maid maid)
        {
            Dictionary<int, PropSnap> props = new Dictionary<int, PropSnap>(mpnValues.Length);
            int[] vals = mpnValues;
            for (int i = 0; i < vals.Length; i++)
            {
                int v = vals[i];
                if (v == 0) continue; // null_mpn
                MaidProp mp = maid.GetProp((MPN)v);
                if (mp == null) continue;
                PropSnap ps = new PropSnap();
                ps.FileName = mp.strFileName;
                ps.Rid = mp.nFileNameRID;
                ps.Value = mp.value;
                props[v] = ps;
            }
            return props;
        }

        private static bool LightEquals(Dictionary<int, PropSnap> a, Dictionary<int, PropSnap> b)
        {
            if (a.Count != b.Count) return false;
            foreach (KeyValuePair<int, PropSnap> kv in a)
            {
                PropSnap o;
                if (!b.TryGetValue(kv.Key, out o)) return false;
                if (kv.Value.FileName != o.FileName) return false;
                if (!kv.Value.Rid.Equals(o.Rid)) return false;
                if (kv.Value.Value != o.Value) return false;
            }
            return true;
        }

        // ---------------- head type ----------------

        private static object ReadHeadType(MaidEdit.MaidEditManager em)
        {
            try
            {
                if (fiFaceManager == null || piMaidHeadType == null) return null;
                object fm = fiFaceManager.GetValue(em);
                if (fm == null) return null;
                return piMaidHeadType.GetValue(fm, null);
            }
            catch { return null; }
        }

        // ---------------- snapshot comparison ----------------
        // Snapshot item ids are random per create (Guid hash) and link fields
        // are structural, so compare payload fields only, recursively.

        private static readonly HashSet<string> SkipFields = new HashSet<string>
        {
            "<id>k__BackingField",
            "<itemType>k__BackingField",
            "<priority>k__BackingField",
            "<dependencyItem>k__BackingField",
            "<childrenItems>k__BackingField"
        };

        internal static bool ItemsEqual(IReadOnlyList<MaidEdit.ISnapShotItem> a, IReadOnlyList<MaidEdit.ISnapShotItem> b)
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!ItemEqual(a[i], b[i])) return false;
            }
            return true;
        }

        internal static bool ItemEqual(MaidEdit.ISnapShotItem x, MaidEdit.ISnapShotItem y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x == null || y == null) return false;
            Type xt = x.GetType();
            if (xt != y.GetType()) return false;
            if (x.mpn != y.mpn) return false;
            if (x.enabled != y.enabled) return false;
            for (Type t = xt; t != null && t != typeof(object); t = t.BaseType)
            {
                FieldInfo[] fs = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                for (int i = 0; i < fs.Length; i++)
                {
                    FieldInfo f = fs[i];
                    if (SkipFields.Contains(f.Name)) continue;
                    // link fields are structural: children compared via
                    // .children recursion, dependency skipped entirely
                    if (f.FieldType == typeof(MaidEdit.ISnapShotItem)) continue;
                    if (f.FieldType == typeof(List<MaidEdit.ISnapShotItem>)) continue;
                    if (!ValueEqual(f.GetValue(x), f.GetValue(y))) return false;
                }
            }
            return ChildrenEqual(x.children, y.children);
        }

        private static bool ValueEqual(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            byte[] ba = a as byte[];
            if (ba != null)
            {
                byte[] bb = b as byte[];
                if (bb == null || ba.Length != bb.Length) return false;
                for (int i = 0; i < ba.Length; i++)
                {
                    if (ba[i] != bb[i]) return false;
                }
                return true;
            }
            List<byte[]> la = a as List<byte[]>;
            if (la != null)
            {
                List<byte[]> lb = b as List<byte[]>;
                if (lb == null || la.Count != lb.Count) return false;
                for (int i = 0; i < la.Count; i++)
                {
                    if (!ValueEqual(la[i], lb[i])) return false;
                }
                return true;
            }
            if (a.GetType() != b.GetType()) return false;
            return object.Equals(a, b);
        }

        private static bool ChildrenEqual(IReadOnlyList<MaidEdit.ISnapShotItem> a, IReadOnlyList<MaidEdit.ISnapShotItem> b)
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!ItemEqual(a[i], b[i])) return false;
            }
            return true;
        }

        private void ThrottledError(string msg)
        {
            if (Time.frameCount - lastErrFrame < 300) return;
            lastErrFrame = Time.frameCount;
            log.LogError("SceneUndo " + msg);
        }
    }
}
