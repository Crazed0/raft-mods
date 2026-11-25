using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using HMLLibrary;
using RaftModLoader;

// Stackable Chests v1.1.0 (looking at a chest shows the 3 things it holds the most of)
// Every placed chest (any block with a Storage_Small) gets a build surface on its lid, so another
// chest can be placed straight on top of it, exactly aligned. Only chests can go there.
// A chest on top stays up only while the one below exists: remove the bottom one and the game's
// own stability check drops the ones above (their items too, as with any removed chest).
// The surface copies the layer, trigger flag and build sides of the floor surface the game itself
// uses for chests, so placing on a lid works like placing on a floor.
// Console (F10): chests.status
public class StackableChests : Mod
{
    public const string Version = "1.1.0";
    public const string HarmonyId = "com.bestcode.stackablechests";
    public const string LidName = "BC_ChestLid";

    Harmony harmony;
    public static readonly List<Item_Base> chestItems = new List<Item_Base>();
    static SO_BlockQuadType lidType;
    static BlockSurface[] lidSides;
    static int lidLayer = -1;
    static bool lidTrigger;
    static bool lidSnapRot = true; // the chest on top turns with the one below, so the stack is straight
    // chest on top -> chest it stands on
    static readonly Dictionary<Block, Block> below = new Dictionary<Block, Block>();
    public static bool ready;
    static string setupProblem = "";
    static float nextScan, nextSetupTry;
    public static int lidsAdded, stackedPlaced;

    public void Start()
    {
        harmony = new Harmony(HarmonyId);
        int patched = 0;
        var broken = new List<string>();
        foreach (System.Type t in typeof(StackableChests).Assembly.GetTypes())
        {
            if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            try { harmony.CreateClassProcessor(t).Patch(); patched++; }
            catch (System.Exception e) { broken.Add(t.Name); Debug.LogError("[StackableChests] patch " + t.Name + " falhou: " + (e.InnerException ?? e).Message); }
        }
        Log("carregado v" + Version + ", patches " + patched + "/" + (patched + broken.Count) + (broken.Count > 0 ? " (FALHARAM: " + string.Join(", ", broken.ToArray()) + ")" : ""));
        TrySetup();
    }

    public void OnModUnload()
    {
        if (harmony != null) harmony.UnpatchAll(HarmonyId);
        // take the lids off every placed chest
        int n = 0;
        List<Block> blocks = BlockCreator.GetPlacedBlocks();
        if (blocks != null)
            foreach (Block b in blocks)
            {
                if (b == null) continue;
                Transform lid = b.transform.Find(LidName);
                if (lid != null) { Object.Destroy(lid.gameObject); n++; }
            }
        if (lidType != null) Object.Destroy(lidType);
        ready = false;
        Log("descarregado, " + n + " tampas removidas");
    }

    public static void Log(string msg) { Debug.Log("[StackableChests] " + msg); }
    static readonly HashSet<string> said = new HashSet<string>();
    public static void LogOnce(string msg) { if (said.Add(msg)) Log(msg); }
    static string Clean(string n) { return n == null ? "?" : n.Replace("(Clone)", "").Trim(); }

    public void Update()
    {
        if (!ready)
        {
            if (Time.unscaledTime >= nextSetupTry) { nextSetupTry = Time.unscaledTime + 3f; TrySetup(); }
            return;
        }
        if (Raft_Network.InMenuScene || !LoadSceneManager.IsGameSceneLoaded) return;
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 2f;
        List<Block> blocks = BlockCreator.GetPlacedBlocks();
        if (blocks == null) return;
        for (int i = 0; i < blocks.Count; i++)
        {
            Storage_Small s = blocks[i] as Storage_Small;
            if (s == null || !IsChest(s)) continue;
            AddLid(s);
            if (!below.ContainsKey(s)) Remember(s);
        }
        // forget chests that are gone
        var dead = new List<Block>();
        foreach (var kv in below) if (kv.Key == null) dead.Add(kv.Key);
        foreach (Block d in dead) below.Remove(d);
    }

    public static bool IsChest(Block b)
    {
        return b is Storage_Small && b.buildableItem != null && chestItems.Contains(b.buildableItem);
    }

    // Records which chest a chest stands on (if any).
    public static void Remember(Block top)
    {
        Block low = ChestBelow(top);
        if (low != null) below[top] = low;
    }

    // ---------- setup: which items are chests, and how the game's own floor surface looks ----------
    static void TrySetup()
    {
        if (ready) return;
        if (!ItemManager.ItemsLoaded) { setupProblem = "itens do jogo ainda nao carregados"; return; }
        chestItems.Clear();
        foreach (Item_Base it in ItemManager.GetAllItems())
        {
            if (it == null || it.settings_buildable == null || !it.settings_buildable.HasBuildablePrefabs) continue;
            // only the real chests: no fridge, trash can or cupboards on top of each other
            if (it.UniqueName == null || !it.UniqueName.StartsWith("Placeable_Storage_")) continue;
            Block[] bs = it.settings_buildable.GetBlockPrefabs();
            if (bs == null) continue;
            foreach (Block b in bs)
                if (b is Storage_Small) { chestItems.Add(it); break; }
        }
        if (chestItems.Count == 0) { setupProblem = "nenhum bau encontrado"; return; }

        // the floor surface chests normally go on (foundation / floor): copy its look and rules
        BlockQuad reference = null;
        foreach (BlockQuad q in Resources.FindObjectsOfTypeAll<BlockQuad>())
        {
            if (q == null || q.quadType == null || q.acceptableBuildSides == null) continue;
            if (!q.quadType.AcceptsBlock(chestItems[0])) continue;
            if (q.QuadCollider == null) continue;
            reference = q;
            if (q.transform.up.y > 0.9f) break; // prefer a flat floor
        }
        Block chestPrefab = null;
        foreach (Block b in chestItems[0].settings_buildable.GetBlockPrefabs()) if (b != null) { chestPrefab = b; break; }
        DPS dps = chestPrefab != null ? chestPrefab.dpsType : DPS.Default;
        var sides = new List<BlockSurface>();
        if (reference != null)
        {
            lidLayer = reference.gameObject.layer;
            lidTrigger = reference.QuadCollider.isTrigger;
            foreach (BlockSurface s in reference.acceptableBuildSides)
                if (s != null && s.dpsType == dps) sides.Add(new BlockSurface { surfaceType = SurfaceType.All, dpsType = s.dpsType });
            Log("superficie de referencia: " + Clean(reference.name) + " em " + Clean(reference.transform.root.name)
                + " layer=" + LayerMask.LayerToName(lidLayer) + " trigger=" + lidTrigger + " snapRot=" + reference.snapToQuadRotation + " snapPos=" + reference.snapToQuadPosition
                + " lados=" + Sides(reference.acceptableBuildSides));
        }
        else
        {
            lidLayer = LayerMask.NameToLayer("BuildQuad");
            lidTrigger = false;
            Log("AVISO: nao encontrei a superficie onde os baus se poem, uso layer BuildQuad");
        }
        if (sides.Count == 0) sides.Add(new BlockSurface { surfaceType = SurfaceType.All, dpsType = dps });
        lidSides = sides.ToArray();

        lidType = ScriptableObject.CreateInstance<SO_BlockQuadType>();
        lidType.name = "BC_ChestLidType";
        Traverse.Create(lidType).Field("acceptableBlockTypes").SetValue(new List<Item_Base>(chestItems));

        var names = new List<string>();
        foreach (Item_Base it in chestItems)
        {
            string info = it.UniqueName;
            foreach (Block b in it.settings_buildable.GetBlockPrefabs())
            {
                if (b == null) continue;
                Bounds bb;
                info += " [dps=" + b.dpsType + " snapsToQuads=" + b.snapsToQuads + " estavel=" + (b.stableComponent != null ? b.stableComponent.requiredGizmoColliders.Length + " caixas" : "nao") + (LocalBounds(b, out bb) ? " tamanho=" + bb.size.ToString("0.00") : "") + "]";
            }
            names.Add(info);
        }
        Log("baus: " + string.Join(", ", names.ToArray()));
        ready = true;
        setupProblem = "";
    }

    static string Sides(BlockSurface[] s)
    {
        if (s == null) return "-";
        var l = new List<string>();
        foreach (BlockSurface x in s) if (x != null) l.Add(x.surfaceType + "/" + x.dpsType);
        return string.Join(",", l.ToArray());
    }

    // Box of the chest's visible meshes in its own space.
    static bool LocalBounds(Block b, out Bounds bb)
    {
        bb = new Bounds();
        bool any = false;
        Matrix4x4 w2l = b.transform.worldToLocalMatrix;
        foreach (MeshRenderer r in b.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (r == null || !r.enabled) continue;
            MeshFilter mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            Bounds mb = mf.sharedMesh.bounds;
            Matrix4x4 m = w2l * mf.transform.localToWorldMatrix;
            for (int k = 0; k < 8; k++)
            {
                Vector3 c = mb.center + Vector3.Scale(mb.extents, new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1));
                Vector3 p = m.MultiplyPoint3x4(c);
                if (!any) { bb = new Bounds(p, Vector3.zero); any = true; }
                else bb.Encapsulate(p);
            }
        }
        return any;
    }

    // ---------- the lid surface ----------
    public static void AddLid(Storage_Small s)
    {
        if (!ready || s == null || !s.hasBeenPlaced || !IsChest(s)) return;
        if (s.transform.Find(LidName) != null) return;
        Bounds bb;
        if (!LocalBounds(s, out bb)) return;
        var go = new GameObject(LidName);
        go.layer = lidLayer;
        go.transform.SetParent(s.transform, false);
        go.transform.localPosition = new Vector3(bb.center.x, bb.max.y + 0.01f, bb.center.z);
        go.transform.localRotation = Quaternion.identity;
        var col = go.AddComponent<BoxCollider>();
        col.size = new Vector3(Mathf.Max(0.2f, bb.size.x * 0.9f), 0.04f, Mathf.Max(0.2f, bb.size.z * 0.9f));
        col.isTrigger = lidTrigger;
        var q = go.AddComponent<BlockQuad>();
        q.quadType = lidType;
        q.acceptableBuildSides = lidSides;
        q.snapToQuadPosition = true;
        q.snapToQuadRotation = lidSnapRot;
        lidsAdded++;
    }

    // A chest sits on another chest's lid (same spot, within 10 cm).
    public static bool OnAChest(Block b) { return ChestBelow(b) != null; }

    static Block ChestBelow(Block b)
    {
        if (!ready || b == null || !IsChest(b)) return null;
        Vector3 p = b.transform.position;
        List<Block> blocks = BlockCreator.GetPlacedBlocks();
        if (blocks == null) return null;
        for (int i = 0; i < blocks.Count; i++)
        {
            Block o = blocks[i];
            if (o == null || o == b || !(o is Storage_Small) || !o.gameObject.activeInHierarchy) continue;
            Transform lid = o.transform.Find(LidName);
            if (lid != null && (lid.position - p).sqrMagnitude < 0.01f) return o;
        }
        return null;
    }

    // Chests carry no stability check of their own, so a chest that stood on a chest only stays
    // up while that chest is still there; otherwise the game removes it like any loose block.
    public static bool StableOverride(Block b, bool vanilla)
    {
        if (!ready || !IsChest(b)) return vanilla;
        Block low;
        if (below.TryGetValue(b, out low))
        {
            if (low != null && low.gameObject.activeInHierarchy && ChestBelow(b) == low) return true;
            below.Remove(b);
            Log("bau de cima ficou sem o de baixo: cai");
            return false;
        }
        return vanilla || ChestBelow(b) != null;
    }

    public static bool IsLid(BlockQuad q) { return q != null && q.quadType != null && q.quadType == lidType; }

    [ConsoleCommand("chests.status", "Stackable Chests: state and counts.")]
    public static string Status(string[] args)
    {
        if (!ready) return "Stackable Chests ainda nao pronto: " + setupProblem;
        int chests = 0, lids = 0, stacked = 0;
        List<Block> blocks = BlockCreator.GetPlacedBlocks();
        if (blocks != null)
            foreach (Block b in blocks)
            {
                if (!(b is Storage_Small)) continue;
                chests++;
                if (b.transform.Find(LidName) != null) lids++;
                if (OnAChest(b)) stacked++;
            }
        return "baus=" + chests + " com_tampa=" + lids + " empilhados=" + stacked + " tipos_de_bau=" + chestItems.Count + " colocados_em_cima_nesta_sessao=" + stackedPlaced;
    }
}

// A newly placed chest gets its lid right away.
[HarmonyPatch(typeof(Storage_Small), "OnFinishedPlacement")]
static class Patch_ChestLidOnPlace
{
    static void Postfix(Storage_Small __instance)
    {
        StackableChests.AddLid(__instance);
        StackableChests.Remember(__instance);
        if (StackableChests.OnAChest(__instance))
        {
            StackableChests.stackedPlaced++;
            StackableChests.Log("bau posto em cima de outro: " + __instance.name.Replace("(Clone)", "").Trim());
        }
    }
}

// A chest on a chest is held by the one below (the game's own check looks for a floor under it).
[HarmonyPatch(typeof(Block), "IsStable")]
static class Patch_ChestOnChestStable
{
    static void Postfix(Block __instance, ref bool __result)
    {
        __result = StackableChests.StableOverride(__instance, __result);
    }
}

// Logs why a chest cannot go on a lid (once per reason), to tune the mod from the log.
[HarmonyPatch(typeof(BlockCreator), "CanBuildBlock")]
static class Patch_LogLidErrors
{
    static readonly AccessTools.FieldRef<BlockCreator, BlockQuad> quadRef = AccessTools.FieldRefAccess<BlockCreator, BlockQuad>("quadAtCursor");
    static BuildError last = BuildError.None;
    static float lastAt;

    static void Postfix(BlockCreator __instance, Block block, BuildError __result)
    {
        if (!StackableChests.IsLid(quadRef(__instance))) return;
        if (__result == last && Time.unscaledTime - lastAt < 10f) return;
        last = __result;
        lastAt = Time.unscaledTime;
        StackableChests.Log("bau sobre tampa: " + (__result == BuildError.None ? "pode por" : "bloqueado (" + __result + ")") + " pos=" + (block != null ? block.transform.position.ToString("0.00") : "?"));
    }
}

// Chest label: looking at a closed chest shows, under "Open", the 3 things it holds the most of.
[HarmonyPatch(typeof(Storage_Small), "OnIsRayed")]
static class Patch_ChestLabel
{
    static readonly Dictionary<Item_Base, int> sums = new Dictionary<Item_Base, int>();
    static readonly List<KeyValuePair<Item_Base, int>> sorted = new List<KeyValuePair<Item_Base, int>>();
    static int cachedFor;
    static float cachedAt;
    static string cached = "";

    static void Postfix(Storage_Small __instance)
    {
        try
        {
            if (CanvasHelper.ActiveMenu != MenuType.None || __instance.IsOpen || PlayerItemManager.IsBusy) return;
            if (!Helper.LocalPlayerIsWithinDistance(__instance.transform.position, Player.UseDistance + 0.5f)) return;
            if (__instance.buildableItem != null && __instance.buildableItem.UniqueName != null && __instance.buildableItem.UniqueName.Contains("Trash")) return;
            CanvasHelper canvas = ComponentManager<CanvasHelper>.Value;
            if (canvas == null || canvas.displayTextManager == null || !canvas.CanOpenMenu) return;
            int id = __instance.GetInstanceID();
            if (id != cachedFor || Time.unscaledTime - cachedAt > 0.5f)
            {
                cached = Label(__instance.GetInventoryReference());
                cachedFor = id;
                cachedAt = Time.unscaledTime;
            }
            if (cached.Length > 0) canvas.displayTextManager.ShowText(cached, 2, false);
        }
        catch (System.Exception e) { StackableChests.LogOnce("etiqueta falhou: " + e.Message); }
    }

    public static string Label(Inventory inv)
    {
        if (inv == null || inv.allSlots == null) return "";
        sums.Clear();
        foreach (Slot s in inv.allSlots)
        {
            if (s == null || s.IsEmpty || s.itemInstance == null || s.itemInstance.baseItem == null) continue;
            Item_Base it = s.itemInstance.baseItem;
            int have;
            sums.TryGetValue(it, out have);
            sums[it] = have + Mathf.Max(1, s.itemInstance.Amount);
        }
        if (sums.Count == 0) return "Vazio";
        sorted.Clear();
        sorted.AddRange(sums);
        sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
        var sb = new System.Text.StringBuilder();
        int n = Mathf.Min(3, sorted.Count);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(",  ");
            Item_Base it = sorted[i].Key;
            string name = it.settings_Inventory != null && !string.IsNullOrEmpty(it.settings_Inventory.DisplayName) ? it.settings_Inventory.DisplayName : it.UniqueName;
            sb.Append(name).Append(" x").Append(sorted[i].Value);
        }
        if (sorted.Count > 3) sb.Append("  (+").Append(sorted.Count - 3).Append(")");
        return sb.ToString();
    }
}
