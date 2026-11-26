using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using HMLLibrary;
using RaftModLoader;

// Breakable Big Palms: the big decorative palms on islands become choppable trees,
// like the normal palm trees, but with more hits and more drops.
// Unlike NoMoreUselessPalms it does not replace the tree: it adds the harvest
// components to the original palm, so its own hitbox is the one you chop and it
// disappears (model + hitbox) when it is depleted. Respawns when the island resets.
public class BreakableBigPalms : Mod
{
    public const string HarmonyId = "com.bestcode.breakablebigpalms";
    public const int YieldMultiplier = 3; // big palm = 3x the drops of a normal palm
    public const int HitsToFell = 8;      // axe hits to fell a big palm (any axe: 1 hit = 1 yield)
    public const float DoubleChance = 0.08f; // each hit gives 1 of its item; rarely 2 (planks: never more)
    Harmony harmony;

    public void Start()
    {
        harmony = new Harmony(HarmonyId);
        harmony.PatchAll(typeof(BreakableBigPalms).Assembly);
        // palms made breakable by an older version still carry the old (much larger) yield
        int fixedN = 0;
        foreach (YieldHandler y in Resources.FindObjectsOfTypeAll<YieldHandler>())
            if (y != null && y.gameObject.scene.IsValid() && Nerf(y)) fixedN++;
        Debug.Log("[BreakableBigPalms] loaded v1.1.0, " + fixedN + " palmeiras ja carregadas acertadas.");
    }

    // every hit gives a single item, only now and then two
    public static bool Nerf(YieldHandler yh)
    {
        if (yh == null || yh.yieldAsset == null || yh.yieldAsset.name != "BigPalmYield" || yh.yieldAsset.yieldAssets == null) return false;
        var mine = new List<Cost>();
        foreach (Cost c in yh.yieldAsset.yieldAssets)
            if (c != null && c.item != null) mine.Add(new Cost(c.item, Random.value < DoubleChance ? 2 : 1));
        SO_ItemYield own = ScriptableObject.CreateInstance<SO_ItemYield>();
        own.name = "BigPalmYield";
        own.yieldAssets = mine;
        yh.yieldAsset = own;
        // what is left of this palm keeps its count, at the new amounts
        if (yh.yield != null)
        {
            var left = new List<Cost>();
            foreach (Cost c in yh.yield)
                if (c != null && c.item != null) left.Add(new Cost(c.item, Random.value < DoubleChance ? 2 : 1));
            yh.yield = left;
        }
        return true;
    }

    public void OnModUnload()
    {
        if (harmony != null) harmony.UnpatchAll(HarmonyId);
        Debug.Log("[BreakableBigPalms] unloaded");
    }

    [ConsoleCommand("palm.debug", "Olha para uma palmeira grande e corre isto: diz o que o machado acerta")]
    public static string PalmDebug(string[] args)
    {
        Camera cam = Camera.main;
        if (cam == null) return "sem camara";
        var sb = new System.Text.StringBuilder();
        sb.Append("palmeiras grandes preparadas: " + madeCount + ", camada arvore: " + treeLayer + "\n");
        Axe axe = null;
        foreach (Axe a in Object.FindObjectsOfType<Axe>())
            if (a.playerNetwork != null && a.playerNetwork.IsLocalPlayer) { axe = a; break; }
        if (axe != null) sb.Append("machado mask: " + axe.hitmask.value + ", inclui camada arvore: " + (treeLayer >= 0 && (axe.hitmask.value & (1 << treeLayer)) != 0) + "\n");
        RaycastHit[] hits = Physics.RaycastAll(cam.transform.position, cam.transform.forward, 8f, ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
        if (hits.Length == 0) sb.Append("nada a menos de 8 m");
        for (int i = 0; i < hits.Length && i < 4; i++)
        {
            RaycastHit h = hits[i];
            var tree = h.transform.GetComponentInParent<HarvestableTree>();
            bool inMask = axe == null || (axe.hitmask.value & (1 << h.collider.gameObject.layer)) != 0;
            sb.Append(string.Format("{0:0.0}m col={1} raiz={2} tag={3} camada={4}({5}) noMachado={6} arvore={7} esgotada={8}\n",
                h.distance, h.collider.name, h.transform.name, h.transform.tag, h.collider.gameObject.layer,
                LayerMask.LayerToName(h.collider.gameObject.layer), inMask, tree != null, tree != null && tree.Depleted));
        }
        return sb.ToString();
    }

    // ---------- template taken from the normal palm tree ----------
    static bool templateSearched;
    static SO_ItemYield bigYield;
    static RandomDropper templateDropper;
    static HarvestableTree templateTree;
    static PickupItem templatePickup;
    static int treeLayer = -1; // layer of the normal palm's chop collider (the axe hitmask includes it)
    public static int madeCount;

    static bool EnsureTemplate()
    {
        if (templateSearched) return bigYield != null;
        templateSearched = true;
        Item_Base seed = ItemManager.GetItemByName("Seed_Palm");
        if (seed == null) { Debug.LogError("[BreakableBigPalms] Seed_Palm item not found"); return false; }
        foreach (Plant plant in Resources.FindObjectsOfTypeAll<Plant>())
        {
            if (plant == null || plant.item == null || plant.item.UniqueIndex != seed.UniqueIndex) continue;
            var yh = plant.GetComponent<YieldHandler>();
            if (yh == null || yh.yieldAsset == null || yh.yieldAsset.yieldAssets == null || yh.yieldAsset.yieldAssets.Count == 0) continue;
            // HitsToFell hits, each giving more so the total is still YieldMultiplier x a normal palm
            var src = new List<Cost>();
            foreach (Cost c in yh.yieldAsset.yieldAssets)
                if (c != null && c.item != null) src.Add(c);
            if (src.Count == 0) continue;
            float scale = YieldMultiplier * src.Count / (float)HitsToFell;
            var list = new List<Cost>();
            for (int i = 0; i < HitsToFell; i++)
            {
                Cost c = src[i % src.Count];
                list.Add(new Cost(c.item, Mathf.Max(1, Mathf.RoundToInt(c.amount * scale))));
            }
            bigYield = ScriptableObject.CreateInstance<SO_ItemYield>();
            bigYield.name = "BigPalmYield";
            bigYield.yieldAssets = list;
            Object.DontDestroyOnLoad(bigYield);
            templateDropper = plant.GetComponent<RandomDropper>();
            templateTree = plant.GetComponent<HarvestableTree>();
            templatePickup = plant.GetComponent<PickupItem>();
            foreach (Collider c in plant.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || c.isTrigger) continue;
                if (treeLayer < 0 || c.CompareTag("Tree")) treeLayer = c.gameObject.layer;
                if (c.CompareTag("Tree")) break;
            }
            if (treeLayer < 0) treeLayer = plant.gameObject.layer;
            Debug.Log("[BreakableBigPalms] camada das palmeiras normais: " + treeLayer + " (" + LayerMask.LayerToName(treeLayer) + ")");
            Debug.Log("[BreakableBigPalms] big palm yield: " + list.Count + " hits, " + HitsToFell + " por palmeira");
            return true;
        }
        Debug.LogError("[BreakableBigPalms] palm tree template not found");
        return false;
    }

    // A big palm root is the parent of a "BigPalm..._LOD0" object
    // (root has LODGroup + Rigidbody, children are LOD models, billboard and CollisionObjectN).
    public static bool IsBigPalmRoot(Transform t)
    {
        if (t == null || t.GetComponent<LODGroup>() == null) return false;
        for (int i = 0; i < t.childCount; i++)
        {
            string n = t.GetChild(i).name;
            if (n.StartsWith("BigPalm") && n.Contains("_LOD")) return true;
        }
        return false;
    }

    public static void MakeBreakable(Transform root)
    {
        if (root.GetComponent<HarvestableTree>() != null) { Nerf(root.GetComponent<YieldHandler>()); return; } // already done
        if (!EnsureTemplate()) return;

        GameObject go = root.gameObject;
        bool wasActive = go.activeSelf;
        go.SetActive(false); // add everything first, so Awake() runs with the fields set

        var yh = go.AddComponent<YieldHandler>();
        // every hit gives a single item, and only now and then two
        var mine = new List<Cost>();
        foreach (Cost c in bigYield.yieldAssets)
            mine.Add(new Cost(c.item, Random.value < DoubleChance ? 2 : 1));
        SO_ItemYield own = ScriptableObject.CreateInstance<SO_ItemYield>();
        own.name = "BigPalmYield";
        own.yieldAssets = mine;
        yh.yieldAsset = own;
        yh.yield = new List<Cost>(mine);

        RandomDropper dropper = null;
        if (templateDropper != null && templateDropper.randomDropperAsset != null)
        {
            dropper = go.AddComponent<RandomDropper>();
            dropper.randomDropperAsset = templateDropper.randomDropperAsset;
            dropper.amountOfItems = templateDropper.amountOfItems;
        }

        var pickup = go.AddComponent<PickupItem>();
        pickup.yieldHandler = yh;
        pickup.dropper = dropper;
        pickup.canBePickedUp = true;
        if (templatePickup != null)
        {
            pickup.pickupItemType = templatePickup.pickupItemType;
            pickup.pickupTerm = templatePickup.pickupTerm;
        }

        var net = go.AddComponent<PickupItem_Networked>();
        net.spawnType = ObjectSpawnType.GameObject;
        net.pickupItem = pickup;
        pickup.networkID = net;

        var tree = go.AddComponent<HarvestableTree>();
        tree.pickupItem = pickup;
        tree.hideOnDepleted = true;
        tree.particleSpawnPositions = new Transform[] { root };
        if (templateTree != null) tree.particleSystemName = templateTree.particleSystemName;

        var lmItem = go.AddComponent<LandmarkItem_PickupItem>();
        lmItem.connectedBehaviourID = net;
        lmItem.pickupItem = pickup;

        // The axe only chops objects tagged "Tree". The hit transform is the
        // Rigidbody root, so tag the root and its collision objects.
        // The axe raycast uses a layer mask: put the colliders on the same layer as
        // the normal palm, otherwise the axe never hits them.
        go.tag = "Tree";
        foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
        {
            c.gameObject.tag = "Tree";
            if (!c.isTrigger && treeLayer >= 0) c.gameObject.layer = treeLayer;
        }
        madeCount++;

        go.SetActive(wasActive);
    }
}

// Before a landmark (island) collects its items, turn its big palms into trees,
// so the game itself saves, networks and resets them like any island pickup.
[HarmonyPatch(typeof(Landmark), "Initialize")]
static class Patch_BigPalm_LandmarkInit
{
    static void Prefix(Landmark __instance)
    {
        if (__instance == null || __instance.initialized) return;
        var all = __instance.GetComponentsInChildren<LODGroup>(true);
        int before = BreakableBigPalms.madeCount;
        foreach (LODGroup lg in all)
            if (BreakableBigPalms.IsBigPalmRoot(lg.transform))
                BreakableBigPalms.MakeBreakable(lg.transform);
        int n = BreakableBigPalms.madeCount - before;
        if (n > 0) Debug.Log("[BreakableBigPalms] ilha " + __instance.name + ": " + n + " palmeiras grandes partiveis");
    }
}

// Worlds saved before this mod have no network index for the big palms.
// The host gives them one, like the game does for a new item.
[HarmonyPatch(typeof(RGD_Landmark), "RestoreLandmark")]
static class Patch_BigPalm_Restore
{
    static void Postfix(Landmark landmark)
    {
        if (landmark == null || !Raft_Network.IsHost || landmark.landmarkItems == null) return;
        foreach (LandmarkItem item in landmark.landmarkItems)
        {
            if (item == null || item.connectedBehaviourID == null) continue;
            if (item.connectedBehaviourID.ObjectIndex != 0) continue;
            if (!BreakableBigPalms.IsBigPalmRoot(item.transform)) continue;
            item.AssignIndexAndTrack(SaveAndLoad.GetUniqueObjectIndex());
        }
    }
}
