using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using HMLLibrary;

// Tree Harvest Fix: stops the NullReferenceException in HarvestableTree.Depleted
// (seen when another player hits a tree added by More Bananas / NoMoreUselessPalms).
// It first tries to repair the tree's missing references; if that fails, the hit is ignored.
public class TreeHarvestFix : Mod
{
    const string HarmonyId = "com.bestcode.treeharvestfix";
    Harmony harmony;
    static readonly HashSet<int> reported = new HashSet<int>();

    public void Start()
    {
        harmony = new Harmony(HarmonyId);
        harmony.PatchAll(typeof(TreeHarvestFix).Assembly);
        Debug.Log("[TreeHarvestFix] loaded");
    }

    public void OnModUnload()
    {
        if (harmony != null) harmony.UnpatchAll(HarmonyId);
        reported.Clear();
        Debug.Log("[TreeHarvestFix] unloaded");
    }

    // Returns true if the tree has everything Harvest/Depleted need.
    public static bool EnsureValid(HarvestableTree tree)
    {
        if (tree == null) return false;
        if (tree.pickupItem == null) tree.pickupItem = tree.GetComponent<PickupItem>();
        var p = tree.pickupItem;
        if (p != null && p.yieldHandler == null)
        {
            p.yieldHandler = tree.GetComponent<YieldHandler>();
            if (p.yieldHandler == null) p.yieldHandler = tree.GetComponentInChildren<YieldHandler>(true);
        }
        bool ok = p != null && p.yieldHandler != null && p.yieldHandler.Yield != null;
        if (!ok && reported.Count < 50 && reported.Add(tree.GetInstanceID()))
            Debug.LogWarning("[TreeHarvestFix] ignored hit on broken tree '" + tree.name + "' at " + tree.transform.position);
        return ok;
    }
}

[HarmonyPatch(typeof(HarvestableTree), "Depleted", MethodType.Getter)]
static class Patch_TreeDepleted
{
    static bool Prefix(HarvestableTree __instance, ref bool __result)
    {
        if (TreeHarvestFix.EnsureValid(__instance)) return true;
        __result = true; // treat a broken tree as empty
        return false;
    }
}

[HarmonyPatch(typeof(HarvestableTree), "Harvest")]
static class Patch_TreeHarvest
{
    static bool Prefix(HarvestableTree __instance, ref bool __result)
    {
        if (TreeHarvestFix.EnsureValid(__instance)) return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(HarvestableTree), "OnLandmarkReset")]
static class Patch_TreeReset
{
    static bool Prefix(HarvestableTree __instance)
    {
        return TreeHarvestFix.EnsureValid(__instance);
    }
}
