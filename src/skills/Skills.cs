using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using HarmonyLib;
using HMLLibrary;
using RaftModLoader;

// Skills v1.0.0: six skills that level up as you play, saved per world.
// - Pescador: catching fish. Fish pull less hard the higher the level (reads Fish Fight's fight).
// - Recolha: sea junk caught with the hook. The hook goes further and grabbing is faster.
// - Lenhador: chopping trees. Chance of a second load of wood.
// - Caçador: killing animals. More damage.
// - Mergulhador: time under water. Oxygen lasts longer.
// - Agricultor: harvesting crops. Chance of an extra crop.
// A small panel sits in the bottom right (level and XP bar per skill); K opens the details.
// Console (F10): skills | skills xp <skill> <n> | skills reset
public class Skills : Mod
{
    public const string Version = "1.2.0";
    public const string HarmonyId = "com.bestcode.skills";
    public const int MaxLevel = 20;

    public enum Id { Pescador, Recolha, Lenhador, Cacador, Mergulhador, Agricultor }
    public static readonly string[] Names = { "PESCADOR", "RECOLHA", "LENHADOR", "CAÇADOR", "MERGULHADOR", "AGRICULTOR" };
    public static readonly string[] Keys = { "pescador", "recolha", "lenhador", "cacador", "mergulhador", "agricultor" };
    public static int[] xp = new int[6];
    public static string world;
    static bool dirty;
    static float lastSave, diveAcc, fishRetry;
    static float baseOxygen = -1f;
    static Stat_Oxygen oxyStat;
    static Harmony harmony;
    static bool fishPatched;
    static readonly HashSet<string> said = new HashSet<string>();

    public void Start()
    {
        harmony = new Harmony(HarmonyId);
        int patched = 0;
        var broken = new List<string>();
        foreach (Type t in typeof(Skills).Assembly.GetTypes())
        {
            if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            try { harmony.CreateClassProcessor(t).Patch(); patched++; }
            catch (Exception e) { broken.Add(t.Name); Debug.LogError("[Skills] patch " + t.Name + " falhou: " + (e.InnerException ?? e).Message); }
        }
        TryPatchFishFight();
        Log("carregado v" + Version + ", patches " + patched + "/" + (patched + broken.Count) + (broken.Count > 0 ? " (FALHARAM: " + string.Join(", ", broken.ToArray()) + ")" : ""));
    }

    public void OnModUnload()
    {
        Save();
        RestoreOxygen();
        if (harmony != null) harmony.UnpatchAll(HarmonyId);
        BCUI_SK.SkillHud.Kill();
        Log("descarregado");
    }

    public static void Log(string m) { Debug.Log("[Skills] " + m); }
    public static void LogOnce(string m) { if (said.Add(m)) Log(m); }

    // ---------- levels ----------
    public static int Need(int level) { return Mathf.RoundToInt(40f + 30f * Mathf.Pow(level, 1.35f)); }   // xp from level to level+1
    public static int Level(int total)
    {
        int l = 0;
        while (l < MaxLevel && total >= Need(l)) { total -= Need(l); l++; }
        return l;
    }
    public static int Lv(Id s) { return Level(xp[(int)s]); }
    // xp inside the current level and what that level needs
    public static void Progress(int total, out int have, out int need)
    {
        int l = 0;
        while (l < MaxLevel && total >= Need(l)) { total -= Need(l); l++; }
        have = total; need = l >= MaxLevel ? 0 : Need(l);
    }

    public static void Add(Id s, int amount)
    {
        if (amount <= 0 || string.IsNullOrEmpty(world)) return;
        int before = Lv(s);
        xp[(int)s] += amount;
        dirty = true;
        int after = Lv(s);
        try { BCUI_SK.SkillHud.Get().Gain((int)s, amount); } catch (Exception e) { LogOnce("painel falhou: " + e.Message); }
        if (after > before)
        {
            try { BCUI_SK.Toast.Show(Names[(int)s] + " NÍVEL " + after, Bonus(s, after), null, false, 4f); } catch { }
            Log(Keys[(int)s] + " subiu para o nivel " + after);
            if (s == Id.Mergulhador) ApplyOxygen();
        }
    }

    // ---------- effects ----------
    public static float FishEase(int l) { return Mathf.Min(0.4f, l * 0.02f); }      // taken off Fish Fight's difficulty (0..1)
    public static float ThrowBoost(int l) { return 1f + l * 0.025f; }               // hook throw force
    public static float GatherCut(int l) { return 1f - l * 0.025f; }                // time to grab with the hook
    public static float WoodChance(int l) { return l * 0.025f; }
    public static float DamageBoost(int l) { return 1f + l * 0.03f; }
    public static float OxygenCut(int l) { return 1f - l * 0.03f; }
    public static float CropChance(int l) { return l * 0.03f; }

    static string Pct(float f) { return Mathf.RoundToInt(f * 100f) + "%"; }
    public static string Bonus(Id s, int l)
    {
        switch (s)
        {
            case Id.Pescador: return "Os peixes puxam " + Pct(FishEase(l)) + " menos";
            case Id.Recolha: return "Anzol vai " + Pct(ThrowBoost(l) - 1f) + " mais longe, apanha " + Pct(1f - GatherCut(l)) + " mais rápido";
            case Id.Lenhador: return Pct(WoodChance(l)) + " de hipótese de madeira a dobrar";
            case Id.Cacador: return "Dás " + Pct(DamageBoost(l) - 1f) + " mais dano aos animais";
            case Id.Mergulhador: return "Gastas " + Pct(1f - OxygenCut(l)) + " menos oxigénio";
            default: return Pct(CropChance(l)) + " de hipótese de colheita a mais";
        }
    }
    public static string HowTo(Id s)
    {
        switch (s)
        {
            case Id.Pescador: return "Apanha peixe com a cana";
            case Id.Recolha: return "Apanha tábuas, plástico e folhas com o anzol";
            case Id.Lenhador: return "Corta árvores com o machado";
            case Id.Cacador: return "Mata animais";
            case Id.Mergulhador: return "Passa tempo debaixo de água";
            default: return "Colhe o que plantaste";
        }
    }

    public static Network_Player Me()
    {
        try { return RAPI.GetLocalPlayer(); } catch { return null; }
    }

    // ---------- per-world save ----------
    static string Dir() { return Path.Combine(Application.persistentDataPath, "BestCodeSkills"); }
    static string FileFor(string w)
    {
        var sb = new StringBuilder();
        foreach (char c in w) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return Path.Combine(Dir(), sb.ToString() + ".txt");
    }
    static void Load(string w)
    {
        xp = new int[6];
        try
        {
            string f = FileFor(w);
            if (File.Exists(f))
                foreach (string line in File.ReadAllLines(f))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    int k = Array.IndexOf(Keys, line.Substring(0, i).Trim());
                    int v;
                    if (k >= 0 && int.TryParse(line.Substring(i + 1).Trim(), out v)) xp[k] = Mathf.Max(0, v);
                }
        }
        catch (Exception e) { Log("ler skills falhou: " + e.Message); }
        dirty = false;
        Log("mundo " + w + ": " + Summary());
    }
    public static void Save()
    {
        if (!dirty || string.IsNullOrEmpty(world)) return;
        try
        {
            Directory.CreateDirectory(Dir());
            var sb = new StringBuilder();
            for (int i = 0; i < 6; i++) sb.Append(Keys[i]).Append('=').Append(xp[i]).Append('\n');
            File.WriteAllText(FileFor(world), sb.ToString());
            dirty = false;
        }
        catch (Exception e) { LogOnce("guardar skills falhou: " + e.Message); }
        lastSave = Time.unscaledTime;
    }
    static string Summary()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 6; i++) sb.Append(i > 0 ? ", " : "").Append(Keys[i]).Append(' ').Append(Level(xp[i])).Append(" (").Append(xp[i]).Append("xp)");
        return sb.ToString();
    }

    // ---------- every frame ----------
    public void Update()
    {
        Network_Player me = Me();
        string w = null;
        if (me != null) { try { w = SaveAndLoad.CurrentGameFileName; } catch { } }
        if (w != world)
        {
            Save();
            RestoreOxygen();
            world = w;
            if (!string.IsNullOrEmpty(world)) { Load(world); ApplyOxygen(); }
        }
        if (string.IsNullOrEmpty(world) || me == null) { BCUI_SK.SkillHud.Hide(); return; }

        try { BCUI_SK.SkillHud.Get(); } catch (Exception e) { LogOnce("painel falhou: " + e.Message); }

        // diving: 1 xp per second fully under water, off the raft
        try
        {
            if (me.PersonController.SubmersionState == UltimateWater.SubmersionState.Full && !me.PersonController.HasRaftAsParent && !me.PersonController.airPillar)
            {
                diveAcc += Time.deltaTime;
                if (diveAcc >= 1f) { diveAcc -= 1f; Add(Id.Mergulhador, 1); }
            }
            if (oxyStat == null || oxyStat != me.Stats.stat_oxygen) ApplyOxygen();
        }
        catch (Exception e) { LogOnce("mergulho falhou: " + e.Message); }

        if (!fishPatched && Time.unscaledTime > fishRetry) { fishRetry = Time.unscaledTime + 3f; TryPatchFishFight(); }
        if (dirty && Time.unscaledTime - lastSave > 10f) Save();
    }

    static void ApplyOxygen()
    {
        Network_Player me = Me();
        if (me == null || me.Stats == null || me.Stats.stat_oxygen == null) return;
        if (oxyStat != me.Stats.stat_oxygen) { oxyStat = me.Stats.stat_oxygen; baseOxygen = oxyStat.GetOxygenLostPerSecond(); }
        oxyStat.SetOxygenLostPerSecond(baseOxygen * OxygenCut(Lv(Id.Mergulhador)));
    }
    static void RestoreOxygen()
    {
        try { if (oxyStat != null && baseOxygen > 0f) oxyStat.SetOxygenLostPerSecond(baseOxygen); } catch { }
        oxyStat = null; baseOxygen = -1f;
    }

    // ---------- Fish Fight (separate mod): ease the fight it starts ----------
    static FieldInfo diffField;
    static void TryPatchFishFight()
    {
        if (fishPatched) return;
        Type t = null;
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { t = a.GetType("FishFight+Fight", false); } catch { }
            if (t != null) break;
        }
        if (t == null) return;
        MethodInfo begin = t.GetMethod("Begin", BindingFlags.Public | BindingFlags.Static);
        diffField = t.GetField("diff");
        if (begin == null || diffField == null) { LogOnce("Fish Fight sem Begin/diff, pescador so ganha xp"); fishPatched = true; return; }
        try
        {
            harmony.Patch(begin, null, new HarmonyMethod(typeof(Skills).GetMethod("FightBegun", BindingFlags.NonPublic | BindingFlags.Static)));
            fishPatched = true;
            Log("ligado ao Fish Fight");
        }
        catch (Exception e) { fishPatched = true; Log("ligar ao Fish Fight falhou: " + e.Message); }
    }
    static float lastDiff = -1f;
    static void FightBegun(object __result)
    {
        if (__result == null || diffField == null) return;
        try
        {
            float d = (float)diffField.GetValue(__result);
            lastDiff = d;
            diffField.SetValue(__result, Mathf.Max(0f, d - FishEase(Lv(Id.Pescador))));
        }
        catch { }
    }

    // ---------- patches ----------
    // any fish that reaches the inventory (Fish Fight's catch and the game's own click)
    [HarmonyPatch(typeof(FishingRod), "PullItemsFromSea")]
    static class P_Fish
    {
        static void Postfix(FishingRod __instance)
        {
            if (__instance.playerNetwork != null && __instance.playerNetwork.IsLocalPlayer) { Add(Id.Pescador, lastDiff >= 0f ? Mathf.RoundToInt(10f + 30f * lastDiff) : 8); lastDiff = -1f; }
        }
    }

    // junk pulled in with the thrown hook
    [HarmonyPatch(typeof(ItemCollector), "AddCollectedItemsToPlayer")]
    static class P_Collect
    {
        static void Prefix(ItemCollector __instance, Network_Player collectingPlayer)
        {
            if (collectingPlayer != null && collectingPlayer.IsLocalPlayer && __instance.playerControlled && __instance.collectedItems != null)
                Add(Id.Recolha, 2 * __instance.collectedItems.Count);
        }
    }

    // grabbing with the hook (in the water, or a barrel): faster with Recolha
    static float baseGather = -1f;
    [HarmonyPatch(typeof(Hook), "HandleGathering")]
    static class P_Gather
    {
        static void Prefix(Hook __instance, PickupItem item)
        {
            if (baseGather < 0f) baseGather = __instance.gatherTime;
            __instance.gatherTime = baseGather * GatherCut(Lv(Id.Recolha));
            if (item != null && __instance.gatherTimer + Time.deltaTime >= __instance.gatherTime && __instance.playerNetwork != null && __instance.playerNetwork.IsLocalPlayer)
                pendingGrab = item;
        }
        static void Postfix(Hook __instance)
        {
            if (pendingGrab != null && __instance.gatherTimer == 0f) Add(Id.Recolha, 3);
            pendingGrab = null;
        }
    }
    static PickupItem pendingGrab;

    // the hook goes further: scale the force before it is thrown (and sent to the host)
    [HarmonyPatch(typeof(Throwable), "Animation_HookThrow")]
    static class P_Throw
    {
        static void Prefix(Throwable __instance)
        {
            if (__instance.playerNetwork == null || !__instance.playerNetwork.IsLocalPlayer) return;
            if (__instance.GetComponent<Hook>() == null) return;
            __instance.lastThrowForce *= ThrowBoost(Lv(Id.Recolha));
        }
    }

    // a tree chopped down: xp, and sometimes its wood twice
    [HarmonyPatch(typeof(HarvestableTree), "Harvest")]
    static class P_Tree
    {
        static void Prefix(HarvestableTree __instance, PlayerInventory playerInventory, out Cost __state)
        {
            __state = null;
            try
            {
                Network_Player me = Me();
                if (me == null || playerInventory != me.Inventory || __instance.Depleted) return;
                __state = __instance.pickupItem.yieldHandler.Yield[__instance.pickupItem.yieldHandler.Yield.Count - 1];
            }
            catch { }
        }
        static void Postfix(PlayerInventory playerInventory, Cost __state)
        {
            if (__state == null || __state.item == null) return;
            Add(Id.Lenhador, 6);
            if (UnityEngine.Random.value < WoodChance(Lv(Id.Lenhador)))
            {
                playerInventory.AddItem(__state.item.UniqueName, Mathf.Max(1, __state.amount));
                LogOnce("primeira madeira a dobrar: " + __state.item.UniqueName);
            }
        }
    }

    // animals: more damage from the local player, xp when it dies
    [HarmonyPatch(typeof(Network_Entity), "Damage")]
    static class P_Damage
    {
        static void Prefix(Network_Entity __instance, ref float damage, EntityType damageInflictorEntityType, bool isLocal, out bool __state)
        {
            __state = false;
            if (!isLocal || damageInflictorEntityType != EntityType.Player || __instance.GetComponentInParent<Network_Player>() != null) return;
            if (__instance.stat_health == null || __instance.IsDead) return;
            damage *= DamageBoost(Lv(Id.Cacador));
            __state = true;
        }
        static void Postfix(Network_Entity __instance, bool __state)
        {
            if (!__state || !__instance.IsDead) return;
            string n = __instance.name.ToLowerInvariant();
            Add(Id.Cacador, n.Contains("shark") ? 60 : n.Contains("bear") || n.Contains("boar") || n.Contains("butler") ? 45 : 20);
        }
    }

    // crops: xp, and sometimes one more
    [HarmonyPatch(typeof(PlantManager), "Harvest")]
    static class P_Crop
    {
        static void Prefix(PlantManager __instance, Plant plant, bool giveYield, out Item_Base __state)
        {
            __state = null;
            try
            {
                Network_Player p = __instance.playerNetwork ?? __instance.GetComponentInParent<Network_Player>();
                if (plant == null || !giveYield || p == null || !p.IsLocalPlayer) return;
                var y = plant.pickupComponent.yieldHandler;
                if (y != null && y.Yield.Count > 0) __state = y.Yield[0].item;
                else if (plant.pickupComponent.itemInstance != null) __state = plant.pickupComponent.itemInstance.baseItem;
            }
            catch { }
        }
        static void Postfix(Item_Base __state)
        {
            if (__state == null) return;
            Add(Id.Agricultor, 10);
            Network_Player me = Me();
            if (me != null && UnityEngine.Random.value < CropChance(Lv(Id.Agricultor))) me.Inventory.AddItem(__state.UniqueName, 1);
        }
    }

    // ---------- console ----------
    [ConsoleCommand("skills", "Skills: levels in this world. Syntax: skills | skills xp <skill> <n> | skills reset")]
    public static string CmdSkills(string[] args)
    {
        if (string.IsNullOrEmpty(world)) return "Entra num mundo primeiro.";
        if (args.Length >= 3 && args[0] == "xp")
        {
            int k = Array.IndexOf(Keys, args[1].ToLowerInvariant());
            int n;
            if (k < 0 || !int.TryParse(args[2], out n)) return "Uso: skills xp <" + string.Join("|", Keys) + "> <n>";
            Add((Id)k, n);
            return Names[k] + ": nivel " + Lv((Id)k) + " (" + xp[k] + " xp)";
        }
        if (args.Length >= 1 && args[0] == "reset")
        {
            xp = new int[6]; dirty = true; Save(); ApplyOxygen();
            return "Skills deste mundo a zero.";
        }
        return "Skills v" + Version + " (" + world + "): " + Summary();
    }
}

// no walking, jumping or sprinting while the panel is open
[HarmonyPatch(typeof(MyInput))]
static class P_Freeze_SK
{
    static bool Block(string id) { return (BCUI_SK.SkillHud.IsOpen) && (id == "Walk" || id == "Strafe" || id == "Jump" || id == "Sprint" || id == "Crouch"); }
    [HarmonyPatch("GetAxis")] [HarmonyPrefix] static bool Axis(string identifier, ref float __result) { if (!Block(identifier)) return true; __result = 0f; return false; }
    [HarmonyPatch("GetButton")] [HarmonyPrefix] static bool Btn(string identifier, ref bool __result) { if (!Block(identifier)) return true; __result = false; return false; }
    [HarmonyPatch("GetButtonDown")] [HarmonyPrefix] static bool Down(string identifier, ref bool __result) { if (!Block(identifier)) return true; __result = false; return false; }
}


// ---------- BestCode UI kit ----------
// Drawn with Raft's own font (ChineseRocks) and sprites (parchment plate, item slot, key chip), on
// one shared overlay canvas. Every BestCode mod carries its own copy of this kit in its own
// namespace; mods only share the canvas object and the toast column (plain Unity components and
// RectTransforms), so one mod reloading never breaks another. Source: raft-mods/src/bcui_kit.cs.txt
namespace BCUI_SK
{
    using System.Collections.Generic;
    using UnityEngine;
    using UnityEngine.UI;

    public static class Kit
    {
        public static readonly Color Cream = new Color(0.956f, 0.893f, 0.759f);
        public static readonly Color Brown = new Color(0.373f, 0.224f, 0.125f);
        public static readonly Color Dark = new Color(0.2f, 0.12f, 0.06f);
        public static readonly Color Bad = new Color(0.62f, 0.17f, 0.1f);
        public static readonly Color Good = new Color(0.27f, 0.42f, 0.12f);
        public static readonly Color Parchment = new Color(0.86f, 0.76f, 0.56f);
        public static readonly Vector2 TL = new Vector2(0f, 1f), TR = new Vector2(1f, 1f), ML = new Vector2(0f, 0.5f), MR = new Vector2(1f, 0.5f), C = new Vector2(0.5f, 0.5f);

        public static Color A(Color c, float a) { return new Color(c.r, c.g, c.b, a); }
        public static float EaseOut(float k) { k = Mathf.Clamp01(k); float i = 1f - k; return 1f - i * i * i; }
        public static float Back(float k) { k = Mathf.Clamp01(k) - 1f; return 1f + 2.70158f * k * k * k + 1.70158f * k * k; }

        static Font font;
        public static Font GameFont()
        {
            if (font != null) return font;
            foreach (Font f in Resources.FindObjectsOfTypeAll<Font>())
                if (f != null && f.name == "ChineseRocks") { font = f; return f; }
            return Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        // The game's sprites, found by name once (and again later if one was not loaded yet).
        static Dictionary<string, Sprite> sprites;
        static float scannedAt = -100f;
        static Sprite round;
        public static Sprite Get(string name)
        {
            Sprite s;
            if (sprites != null && sprites.TryGetValue(name, out s) && s != null) return s;
            if (sprites == null || Time.realtimeSinceStartup - scannedAt > 5f)
            {
                scannedAt = Time.realtimeSinceStartup;
                sprites = new Dictionary<string, Sprite>();
                foreach (Sprite x in Resources.FindObjectsOfTypeAll<Sprite>())
                    if (x != null && x.texture != null && !sprites.ContainsKey(x.name)) sprites[x.name] = x;
                if (sprites.TryGetValue(name, out s) && s != null) return s;
            }
            return Round();
        }
        public static bool IsReal(Sprite s) { return s != null && s != round; }

        // stand-in when a sprite is missing: a soft rounded rectangle
        static Sprite Round()
        {
            if (round != null) return round;
            int n = 32; float r = 10f;
            Texture2D t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = Mathf.Max(0f, Mathf.Max(r - x - 0.5f, x + 0.5f - (n - r)));
                    float dy = Mathf.Max(0f, Mathf.Max(r - y - 0.5f, y + 0.5f - (n - r)));
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f)));
                }
            t.Apply();
            round = Sprite.Create(t, new Rect(0f, 0f, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(12f, 12f, 12f, 12f));
            round.name = "BC_Round";
            return round;
        }

        public static Transform Root()
        {
            GameObject go = GameObject.Find("BC_UICanvas");
            if (go == null)
            {
                go = new GameObject("BC_UICanvas");
                go.layer = 5;
                Object.DontDestroyOnLoad(go);
                Canvas c = go.AddComponent<Canvas>();
                c.renderMode = RenderMode.ScreenSpaceOverlay;
                c.sortingOrder = 500;
                CanvasScaler cs = go.AddComponent<CanvasScaler>();
                cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                cs.referenceResolution = new Vector2(1920f, 1080f);
                cs.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                cs.matchWidthOrHeight = 1f;
            }
            return go.transform;
        }

        public static RectTransform Node(Transform parent, string name)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }
        public static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = anchor; rt.anchorMax = anchor; rt.pivot = pivot; rt.anchoredPosition = pos; rt.sizeDelta = size;
        }
        public static void Stretch(RectTransform rt, float l, float b, float r, float t)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.pivot = C;
            rt.offsetMin = new Vector2(l, b); rt.offsetMax = new Vector2(-r, -t);
        }
        public static Image Img(Transform parent, string name, Sprite s, Color c, bool sliced)
        {
            RectTransform rt = Node(parent, name);
            Image im = rt.gameObject.AddComponent<Image>();
            im.sprite = s; im.color = c; im.raycastTarget = false;
            if (sliced && s != null && s.border.sqrMagnitude > 0f) im.type = Image.Type.Sliced;
            return im;
        }
        public static Text Txt(Transform parent, string name, string text, int size, Color c, TextAnchor a, bool shadow)
        {
            RectTransform rt = Node(parent, name);
            Text t = rt.gameObject.AddComponent<Text>();
            t.font = GameFont(); t.fontSize = size; t.color = c; t.alignment = a; t.text = text;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false; t.supportRichText = false;
            if (shadow) { AddShadow(t.gameObject); AddShadow(t.gameObject); }   // the game's prompts: brown shadow, twice
            return t;
        }
        public static void AddShadow(GameObject go)
        {
            UnityEngine.UI.Shadow sh = go.AddComponent<UnityEngine.UI.Shadow>();
            sh.effectColor = Brown; sh.effectDistance = new Vector2(2f, -2f);
        }
        // the parchment plate the game uses for its buttons, stretched over rt
        public static Image Plate(RectTransform rt)
        {
            Sprite s = Get("BigButtonNormal");
            Image im = Img(rt, "Plate", s, IsReal(s) ? Color.white : Parchment, true);
            Stretch(im.rectTransform, 0f, 0f, 0f, 0f);
            return im;
        }
        // a key chip like the game's ("E", "CLIQUE"), height h; its width fits the text
        public static RectTransform Chip(Transform parent, string key, float h)
        {
            Sprite s = Get("ButtonIcon");
            Image im = Img(parent, "Key", s, IsReal(s) ? Color.white : Cream, true);
            Text t = Txt(im.rectTransform, "Label", key, Mathf.RoundToInt(h * 0.6f), Brown, TextAnchor.MiddleCenter, false);
            Stretch(t.rectTransform, 0f, 3f, 0f, 0f);
            im.rectTransform.sizeDelta = new Vector2(Mathf.Max(h, t.preferredWidth + h * 0.7f), h);
            return im.rectTransform;
        }

        // The toast column at the top centre, shared by every mod: toasts stack in it.
        public static RectTransform Toasts()
        {
            Transform r = Root();
            Transform t = r.Find("BC_Toasts");
            if (t != null) return (RectTransform)t;
            RectTransform rt = Node(r, "BC_Toasts");
            Place(rt, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -34f), new Vector2(1000f, 10f));
            return rt;
        }
    }

    // A message on a parchment plate at the top centre: drops in, stays, fades and the ones under
    // it slide up. Several (from any mod) stack in one column.
    public class Toast : MonoBehaviour
    {
        const float Gap = 8f;
        RectTransform rt, plate;
        CanvasGroup group;
        float w, h, born, life, slot;
        bool placed;

        public static Toast Show(string title, string sub, Sprite icon, bool bad, float secs)
        {
            RectTransform rt = Kit.Node(Kit.Toasts(), "BC_Toast");
            Kit.Place(rt, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(100f, 0f));
            Toast t = rt.gameObject.AddComponent<Toast>();
            t.Build(title, sub, icon, bad, secs);
            return t;
        }

        public static void ClearAll()
        {
            foreach (Toast t in Object.FindObjectsOfType<Toast>()) if (t != null) Object.Destroy(t.gameObject);
        }

        void Build(string title, string sub, Sprite icon, bool bad, float secs)
        {
            rt = (RectTransform)transform;
            born = Time.unscaledTime;
            life = secs;
            group = gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0f; group.interactable = false; group.blocksRaycasts = false;
            plate = Kit.Node(rt, "Body");
            Kit.Plate(plate);
            const float pad = 26f, top = 12f, lip = 20f;
            float iconW = icon != null ? 64f : 0f;
            Text tt = Kit.Txt(plate, "Title", title, 28, bad ? Kit.Bad : Kit.Brown, TextAnchor.UpperLeft, false);
            Text st = string.IsNullOrEmpty(sub) ? null : Kit.Txt(plate, "Sub", sub, 21, Kit.A(Kit.Brown, 0.85f), TextAnchor.UpperLeft, false);
            float textW = tt.preferredWidth;
            if (st != null) textW = Mathf.Max(textW, st.preferredWidth);
            textW = Mathf.Min(textW + 2f, 720f - iconW);
            w = Mathf.Max(240f, textW + pad * 2f + iconW);
            float x = pad + iconW + (w - (textW + pad * 2f + iconW)) * 0.5f;   // centred when the plate is wider than the text
            tt.horizontalOverflow = HorizontalWrapMode.Wrap;
            Kit.Place(tt.rectTransform, Kit.TL, Kit.TL, new Vector2(x, -top), new Vector2(textW, 32f));
            float y = top + Mathf.Max(32f, tt.preferredHeight);
            tt.rectTransform.sizeDelta = new Vector2(textW, y - top);
            if (st != null)
            {
                st.horizontalOverflow = HorizontalWrapMode.Wrap;
                Kit.Place(st.rectTransform, Kit.TL, Kit.TL, new Vector2(x, -y), new Vector2(textW, 26f));
                float sh = Mathf.Max(26f, st.preferredHeight);
                st.rectTransform.sizeDelta = new Vector2(textW, sh);
                y += sh + 2f;
            }
            h = Mathf.Max(y + lip, icon != null ? 80f : 60f);
            if (icon != null)
            {
                Sprite slotS = Kit.Get("ItemBG");
                Image slotI = Kit.Img(plate, "Slot", slotS, Kit.IsReal(slotS) ? Color.white : Kit.A(Kit.Brown, 0.35f), false);
                Kit.Place(slotI.rectTransform, Kit.TL, Kit.TL, new Vector2(x - iconW, -(h - lip) * 0.5f + 30f), new Vector2(54f, 56f));
                Image ic = Kit.Img(slotI.rectTransform, "Icon", icon, Color.white, false);
                ic.preserveAspect = true;
                Kit.Place(ic.rectTransform, Kit.C, Kit.C, new Vector2(0f, 2f), new Vector2(40f, 40f));
            }
            Kit.Place(plate, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(w, h));
            rt.SetAsLastSibling();
            Update();
        }

        void Update()
        {
            if (rt == null) return;
            float t = Time.unscaledTime - born;
            float a, s = 1f, off;
            if (t < life)
            {
                float k = t / 0.3f;
                slot = Mathf.Max(slot, Kit.EaseOut(k));
                a = Mathf.Clamp01(t / 0.18f);
                s = Mathf.Lerp(0.9f, 1f, Kit.Back(k));
                off = (1f - Kit.EaseOut(k)) * 16f;
            }
            else
            {
                float k = (t - life) / 0.3f;
                a = 1f - Mathf.Clamp01(k);
                off = Kit.EaseOut(k) * 12f;                             // drifts up as it fades
                slot = 1f - Kit.EaseOut((t - life - 0.2f) / 0.3f);      // then the ones under it close the gap
                if (t > life + 0.55f) { Destroy(gameObject); return; }
            }
            group.alpha = a;
            plate.localScale = new Vector3(s, s, 1f);
            plate.anchoredPosition = new Vector2(0f, off);
            rt.sizeDelta = new Vector2(w, (h + Gap) * slot);
            // below every toast above it in the column (any mod's: only their sizes are read)
            float y = 0f;
            Transform col = rt.parent;
            int me = rt.GetSiblingIndex();
            for (int i = 0; i < me; i++)
            {
                RectTransform o = col.GetChild(i) as RectTransform;
                if (o != null && o.gameObject.activeSelf) y += o.sizeDelta.y;
            }
            Vector2 p = rt.anchoredPosition;
            p.y = placed ? Mathf.Lerp(p.y, -y, Mathf.Clamp01(Time.unscaledDeltaTime * 14f)) : -y;
            placed = true;
            rt.anchoredPosition = p;
        }
    }
}

// ---------- Skills HUD: compact panel bottom right, details on K ----------
namespace BCUI_SK
{
    using UnityEngine;
    using UnityEngine.UI;
    using UnityEngine.InputSystem;

    // Procedural art for the panels (leather, paper, stitching, shadows). Generated once, kept for the session.
    static class SkArt
    {
        public static readonly Color Leather = new Color(0.29f, 0.17f, 0.085f);
        static Sprite soft, stitch, grad, ribbon, vig, circle, mottle;
        static Sprite[] paper = new Sprite[3];

        static Texture2D Tex(int w, int h)
        {
            Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear; t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }
        static Sprite Make(Texture2D t, Vector4 border, string name)
        {
            t.Apply();
            Sprite s = Sprite.Create(t, new Rect(0f, 0f, t.width, t.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
            s.name = name; s.hideFlags = HideFlags.HideAndDontSave;
            return s;
        }
        // signed distance to a rounded rectangle spanning 0..w x 0..h (negative inside)
        static float Box(float x, float y, float w, float h, float r)
        {
            float qx = Mathf.Abs(x - w * 0.5f) - (w * 0.5f - r);
            float qy = Mathf.Abs(y - h * 0.5f) - (h * 0.5f - r);
            float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
            return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
        }
        static float Hash(int x, int y)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }

        // soft drop shadow, 9-sliced: solid 32 px inside its edges
        public static Sprite Soft()
        {
            if (soft != null) return soft;
            int n = 96; Texture2D t = Tex(n, n);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Box(x + 0.5f - 32f, y + 0.5f - 32f, n - 64f, n - 64f, 8f);
                    float a = Mathf.Clamp01(1f - (d + 2f) / 32f);
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, a * a * (3f - 2f * a)));
                }
            return soft = Make(t, new Vector4(44f, 44f, 44f, 44f), "BC_Soft");
        }

        // dashed stitching along a rounded edge (use Image.Type.Tiled, fillCenter off)
        public static Sprite Stitch()
        {
            if (stitch != null) return stitch;
            int n = 48; Texture2D t = Tex(n, n);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Abs(Box(x + 0.5f - 4f, y + 0.5f - 4f, n - 8f, n - 8f, 10f));
                    float a = Mathf.Clamp01(1.3f - d);
                    if (x >= 16 && x < 32 && ((x - 16) % 8) >= 5) a = 0f;
                    if (y >= 16 && y < 32 && ((y - 16) % 8) >= 5) a = 0f;
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            return stitch = Make(t, new Vector4(16f, 16f, 16f, 16f), "BC_Stitch");
        }

        // horizontal fall-off, strongest in the middle (the book's spine)
        public static Sprite Grad()
        {
            if (grad != null) return grad;
            int w = 64, h = 4; Texture2D t = Tex(w, h);
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w;
                float a = Mathf.Pow(1f - Mathf.Abs(u * 2f - 1f), 2.4f);
                for (int y = 0; y < h; y++) t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
            return grad = Make(t, Vector4.zero, "BC_Grad");
        }

        // dark corners for the screen behind a panel
        public static Sprite Vignette()
        {
            if (vig != null) return vig;
            int n = 64; Texture2D t = Tex(n, n);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy) / 1.4142f;
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.SmoothStep(0f, 1f, (r - 0.2f) / 0.8f)));
                }
            return vig = Make(t, Vector4.zero, "BC_Vignette");
        }

        // a cloth bookmark with a swallowtail cut on the left
        public static Sprite Ribbon()
        {
            if (ribbon != null) return ribbon;
            int w = 96, h = 48; Texture2D t = Tex(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float cy = Mathf.Abs(y + 0.5f - h * 0.5f) / (h * 0.5f);
                    float a = Mathf.Clamp01(x + 0.5f - 18f * (1f - cy));
                    float s = 1f;
                    if (y < 3 || y >= h - 3) s = 0.78f;
                    else if (y < 6 || y >= h - 6) s = 0.9f;
                    s *= 0.95f + 0.05f * Mathf.PerlinNoise(x * 0.35f, y * 0.35f);
                    t.SetPixel(x, y, new Color(s, s, s, a));
                }
            return ribbon = Make(t, new Vector4(22f, 8f, 8f, 8f), "BC_Ribbon");
        }

        public static Sprite Circle()
        {
            if (circle != null) return circle;
            int n = 64; Texture2D t = Tex(n, n);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x + 0.5f - n * 0.5f, dy = y + 0.5f - n * 0.5f;
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(n * 0.5f - Mathf.Sqrt(dx * dx + dy * dy))));
                }
            return circle = Make(t, Vector4.zero, "BC_Circle");
        }

        // leather grain: blotches to lay over the cover in black
        public static Sprite Mottle()
        {
            if (mottle != null) return mottle;
            int n = 192; Texture2D t = Tex(n, n);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float a = 0.55f * Mathf.PerlinNoise(x * 0.045f, y * 0.045f) + 0.3f * Mathf.PerlinNoise(x * 0.21f + 40f, y * 0.21f + 40f) + 0.15f * Hash(x, y);
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            return mottle = Make(t, Vector4.zero, "BC_Mottle");
        }

        // aged paper. mode 0 = left page (spine on the right), 1 = right page, 2 = single sheet
        public static Sprite Paper(int mode)
        {
            if (paper[mode] != null) return paper[mode];
            int w = 256, h = 320; Texture2D t = Tex(w, h);
            Color baseC = new Color(0.95f, 0.89f, 0.75f), burn = new Color(0.69f, 0.51f, 0.30f);
            float seed = mode * 37.1f;
            float r = 5f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float n1 = Mathf.PerlinNoise(x * 0.02f + seed, y * 0.02f + 11f);
                    float n2 = Mathf.PerlinNoise(x * 0.11f + 70f + seed, y * 0.11f);
                    float k = 0.94f + 0.05f * n1 + 0.025f * n2 - (Hash(x + mode * 1000, y) > 0.996f ? 0.07f : 0f);
                    Color c = new Color(baseC.r * k, baseC.g * k, baseC.b * k, 1f);
                    float dl = x, dr = w - 1 - x, db = y, dt = h - 1 - y;
                    if (mode == 0) dr = 999f; else if (mode == 1) dl = 999f;
                    float d = Mathf.Min(Mathf.Min(dl, dr), Mathf.Min(db, dt));
                    float b = (1f - Mathf.SmoothStep(0f, 1f, d / 22f)) * (0.45f + 0.35f * n1 + 0.2f * n2);
                    c = Color.Lerp(c, burn, b);
                    if (mode != 2)
                    {
                        float s = mode == 0 ? (float)x / (w - 1) : 1f - (float)x / (w - 1);
                        float m = 1f - 0.22f * Mathf.Pow(s, 7f);
                        c = new Color(c.r * m, c.g * m, c.b * m, 1f);
                    }
                    // rounded outer corners
                    bool left = mode != 1, right = mode != 0;
                    float cx = -1f, cyy = -1f;
                    if (left && x < r) cx = r; else if (right && x > w - 1 - r) cx = w - 1 - r;
                    if (y < r) cyy = r; else if (y > h - 1 - r) cyy = h - 1 - r;
                    if (cx >= 0f && cyy >= 0f)
                    {
                        float ex = x - cx, ey = y - cyy;
                        c.a = Mathf.Clamp01(r - Mathf.Sqrt(ex * ex + ey * ey) + 0.5f);
                    }
                    t.SetPixel(x, y, c);
                }
            return paper[mode] = Make(t, Vector4.zero, "BC_Paper" + mode);
        }
    }

    public class SkillHud : MonoBehaviour
    {
        static SkillHud inst;
        const float W = 262f, RowH = 27f, HeadH = 34f;
        const float DW = 820f, DRowH = 86f, DHeadH = 118f;

        RectTransform box, detail, detailRoot;
        Image[] dBadge = new Image[6];
        Text[] dBadgeTx = new Text[6];
        static readonly Color[] SkillCol = {
            new Color(0.20f, 0.36f, 0.52f), new Color(0.62f, 0.45f, 0.14f), new Color(0.50f, 0.28f, 0.14f),
            new Color(0.56f, 0.18f, 0.13f), new Color(0.14f, 0.44f, 0.46f), new Color(0.30f, 0.45f, 0.14f) };
        Text[] lvl = new Text[6], dLvl = new Text[6], dBonus = new Text[6];
        RectTransform[] fill = new RectTransform[6], dFill = new RectTransform[6];
        Image[] flash = new Image[6];
        Text[] plus = new Text[6];
        float[] flashAt = new float[6];
        int[] plusSum = new int[6];
        CanvasGroup boxGroup, detailGroup;
        bool open;
        float openAt, nextRefresh;

        public static SkillHud Get()
        {
            if (inst == null)
            {
                RectTransform rt = Kit.Node(Kit.Root(), "BC_Skills");
                Kit.Stretch(rt, 0f, 0f, 0f, 0f);
                inst = rt.gameObject.AddComponent<SkillHud>();
                inst.Build();
            }
            if (!inst.gameObject.activeSelf) inst.gameObject.SetActive(true);
            return inst;
        }
        public static bool IsOpen { get { return inst != null && inst.open && inst.gameObject.activeInHierarchy; } }
        public static void Hide() { if (inst != null && inst.gameObject.activeSelf) { inst.open = false; inst.gameObject.SetActive(false); } }
        public static void Kill() { if (inst != null) Object.Destroy(inst.gameObject); inst = null; }

        static RectTransform Bar(Transform parent, string name, float h, out RectTransform fillRt)
        {
            Image bg = Kit.Img(parent, name, Kit.Get("BC_Round"), Kit.A(Kit.Dark, 0.28f), true);
            Image f = Kit.Img(bg.rectTransform, "Fill", null, Kit.Good, false);
            fillRt = f.rectTransform;
            fillRt.anchorMin = Vector2.zero; fillRt.anchorMax = new Vector2(0f, 1f); fillRt.pivot = new Vector2(0f, 0.5f);
            fillRt.offsetMin = new Vector2(2f, 2f); fillRt.offsetMax = new Vector2(-2f, -2f);
            Image shine = Kit.Img(fillRt, "Shine", null, Kit.A(Kit.Cream, 0.28f), false);
            shine.rectTransform.anchorMin = new Vector2(0f, 0.55f); shine.rectTransform.anchorMax = Vector2.one;
            shine.rectTransform.offsetMin = shine.rectTransform.offsetMax = Vector2.zero;
            bg.rectTransform.sizeDelta = new Vector2(10f, h);
            return bg.rectTransform;
        }
        static void SetFill(RectTransform f, float k) { f.anchorMax = new Vector2(Mathf.Clamp01(k), 1f); }

        void Build()
        {
            // compact panel
            box = Kit.Node(transform, "Panel");
            float h = HeadH + 6 * RowH + 14f;
            Kit.Place(box, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-22f, 22f), new Vector2(W, h));
            boxGroup = box.gameObject.AddComponent<CanvasGroup>();
            boxGroup.interactable = false; boxGroup.blocksRaycasts = false;
            // same leather and paper as the K card, just a smaller book
            Image csh = Kit.Img(box, "Shadow", SkArt.Soft(), new Color(0f, 0f, 0f, 0.6f), true);
            Kit.Place(csh.rectTransform, Kit.C, Kit.C, new Vector2(0f, -8f), new Vector2(W + 60f, h + 60f));
            Image ccover = Kit.Img(box, "Cover", Kit.Get("BC_Round"), SkArt.Leather, true);
            Kit.Stretch(ccover.rectTransform, -10f, -10f, -10f, -10f);
            Image cgrain = Kit.Img(box, "Grain", SkArt.Mottle(), new Color(0f, 0f, 0f, 0.38f), false);
            Kit.Stretch(cgrain.rectTransform, -8f, -8f, -8f, -8f);
            Image cstitch = Kit.Img(box, "Stitch", SkArt.Stitch(), Kit.A(Kit.Cream, 0.4f), false);
            cstitch.type = Image.Type.Tiled; cstitch.fillCenter = false;
            Kit.Stretch(cstitch.rectTransform, -9f, -9f, -9f, -9f);
            Image cpaper = Kit.Img(box, "Paper", SkArt.Paper(2), Color.white, false);
            Kit.Stretch(cpaper.rectTransform, 0f, 0f, 0f, 0f);
            Image crule = Kit.Img(box, "Rule", null, Kit.A(Kit.Brown, 0.4f), false);
            Kit.Place(crule.rectTransform, new Vector2(0.5f, 1f), Kit.C, new Vector2(0f, -HeadH - 3f), new Vector2(W - 32f, 2f));
            Text title = Kit.Txt(box, "Title", "SKILLS", 22, Kit.Dark, TextAnchor.MiddleLeft, false);
            Kit.Place(title.rectTransform, Kit.TL, new Vector2(0f, 0.5f), new Vector2(18f, -HeadH * 0.5f - 6f), new Vector2(120f, 26f));
            RectTransform chip = Kit.Chip(box, "K", 24f);
            Kit.Place(chip, Kit.TR, new Vector2(1f, 0.5f), new Vector2(-16f, -HeadH * 0.5f - 6f), chip.sizeDelta);
            for (int i = 0; i < 6; i++)
            {
                float y = -HeadH - 6f - RowH * i - RowH * 0.5f;
                flash[i] = Kit.Img(box, "Flash" + i, Kit.Get("BC_Round"), Kit.A(Kit.Good, 0f), true);
                Kit.Place(flash[i].rectTransform, new Vector2(0.5f, 1f), Kit.C, new Vector2(0f, y), new Vector2(W - 20f, RowH - 2f));
                Text n = Kit.Txt(box, "Name" + i, Skills.Names[i], 16, Kit.Brown, TextAnchor.MiddleLeft, false);
                Kit.Place(n.rectTransform, Kit.TL, new Vector2(0f, 0.5f), new Vector2(18f, y), new Vector2(110f, RowH));
                RectTransform bar = Bar(box, "Bar" + i, 10f, out fill[i]);
                fill[i].GetComponent<Image>().color = SkillCol[i];
                Kit.Place(bar, Kit.TL, new Vector2(0f, 0.5f), new Vector2(128f, y), new Vector2(W - 128f - 58f, 10f));
                lvl[i] = Kit.Txt(box, "Lvl" + i, "0", 17, Kit.Brown, TextAnchor.MiddleRight, false);
                Kit.Place(lvl[i].rectTransform, Kit.TR, new Vector2(1f, 0.5f), new Vector2(-18f, y), new Vector2(40f, RowH));
                plus[i] = Kit.Txt(box, "Plus" + i, "", 18, Kit.Cream, TextAnchor.MiddleRight, true);
                Kit.Place(plus[i].rectTransform, Kit.TL, new Vector2(1f, 0.5f), new Vector2(-8f, y), new Vector2(80f, RowH));
            }

            // details (K): a leather-bound card with one line per skill
            detailRoot = Kit.Node(transform, "DetailRoot");
            Kit.Stretch(detailRoot, 0f, 0f, 0f, 0f);
            detailGroup = detailRoot.gameObject.AddComponent<CanvasGroup>();
            detailGroup.interactable = false; detailGroup.blocksRaycasts = false;
            Image veil = Kit.Img(detailRoot, "Veil", null, new Color(0f, 0f, 0f, 0.3f), false);
            Kit.Stretch(veil.rectTransform, 0f, 0f, 0f, 0f);
            Image vig = Kit.Img(detailRoot, "Vignette", SkArt.Vignette(), new Color(0f, 0f, 0f, 0.55f), false);
            Kit.Stretch(vig.rectTransform, 0f, 0f, 0f, 0f);

            detail = Kit.Node(detailRoot, "Detail");
            float dh = DHeadH + 6f + 6 * DRowH + 62f;
            Kit.Place(detail, Kit.C, Kit.C, new Vector2(0f, 6f), new Vector2(DW, dh));
            Image sh = Kit.Img(detail, "Shadow", SkArt.Soft(), new Color(0f, 0f, 0f, 0.75f), true);
            Kit.Place(sh.rectTransform, Kit.C, Kit.C, new Vector2(0f, -18f), new Vector2(DW + 96f, dh + 96f));
            Image cover = Kit.Img(detail, "Cover", Kit.Get("BC_Round"), SkArt.Leather, true);
            Kit.Stretch(cover.rectTransform, -16f, -16f, -16f, -16f);
            Image grain = Kit.Img(detail, "Grain", SkArt.Mottle(), new Color(0f, 0f, 0f, 0.38f), false);
            Kit.Stretch(grain.rectTransform, -12f, -12f, -12f, -12f);
            Image stitch = Kit.Img(detail, "Stitch", SkArt.Stitch(), Kit.A(Kit.Cream, 0.4f), false);
            stitch.type = Image.Type.Tiled; stitch.fillCenter = false;
            Kit.Stretch(stitch.rectTransform, -11f, -11f, -11f, -11f);
            Image paper = Kit.Img(detail, "Paper", SkArt.Paper(2), Color.white, false);
            Kit.Stretch(paper.rectTransform, 0f, 0f, 0f, 0f);

            Text dt = Kit.Txt(detail, "Title", "SKILLS", 46, Kit.Dark, TextAnchor.MiddleCenter, false);
            Kit.Place(dt.rectTransform, new Vector2(0.5f, 1f), Kit.C, new Vector2(0f, -48f), new Vector2(DW, 52f));
            float tw = dt.preferredWidth * 0.5f + 22f;
            for (int sdir = -1; sdir <= 1; sdir += 2)
            {
                Image r = Kit.Img(detail, "TRule" + sdir, null, Kit.A(Kit.Brown, 0.45f), false);
                Kit.Place(r.rectTransform, new Vector2(0.5f, 1f), new Vector2(sdir < 0 ? 1f : 0f, 0.5f), new Vector2(sdir * (tw + 16f), -50f), new Vector2(150f, 2f));
                Image d = Kit.Img(detail, "TOrn" + sdir, Kit.Get("BC_Round"), Kit.Brown, false);
                Kit.Place(d.rectTransform, new Vector2(0.5f, 1f), Kit.C, new Vector2(sdir * tw, -50f), new Vector2(10f, 10f));
                d.rectTransform.localEulerAngles = new Vector3(0f, 0f, 45f);
            }
            Text ds = Kit.Txt(detail, "Sub", "Sobem de nível enquanto jogas. Guardadas neste mundo.", 18, Kit.A(Kit.Brown, 0.8f), TextAnchor.MiddleCenter, false);
            Kit.Place(ds.rectTransform, new Vector2(0.5f, 1f), Kit.C, new Vector2(0f, -86f), new Vector2(DW, 24f));
            Image r1 = Kit.Img(detail, "Rule1", null, Kit.A(Kit.Brown, 0.5f), false);
            Kit.Place(r1.rectTransform, new Vector2(0.5f, 1f), Kit.C, new Vector2(0f, -DHeadH + 6f), new Vector2(DW - 90f, 2f));
            Image r2 = Kit.Img(detail, "Rule2", null, Kit.A(Kit.Brown, 0.25f), false);
            Kit.Place(r2.rectTransform, new Vector2(0.5f, 1f), Kit.C, new Vector2(0f, -DHeadH + 1f), new Vector2(DW - 90f, 1f));

            for (int i = 0; i < 6; i++)
            {
                float top = -DHeadH - 6f - DRowH * i;
                float mid = top - DRowH * 0.5f;
                if (i % 2 == 1)
                {
                    Image band = Kit.Img(detail, "Band" + i, Kit.Get("BC_Round"), Kit.A(Kit.Brown, 0.075f), true);
                    Kit.Place(band.rectTransform, new Vector2(0.5f, 1f), Kit.C, new Vector2(0f, mid), new Vector2(DW - 60f, DRowH - 6f));
                }
                // level badge in the skill's colour
                Image ring = Kit.Img(detail, "Ring" + i, SkArt.Circle(), Kit.A(Kit.Dark, 0.85f), false);
                Kit.Place(ring.rectTransform, Kit.TL, Kit.C, new Vector2(78f, mid), new Vector2(62f, 62f));
                dBadge[i] = Kit.Img(detail, "Badge" + i, SkArt.Circle(), SkillCol[i], false);
                Kit.Place(dBadge[i].rectTransform, Kit.TL, Kit.C, new Vector2(78f, mid), new Vector2(52f, 52f));
                Image gloss = Kit.Img(dBadge[i].rectTransform, "Gloss", SkArt.Circle(), Kit.A(Kit.Cream, 0.2f), false);
                Kit.Place(gloss.rectTransform, Kit.C, Kit.C, new Vector2(0f, 8f), new Vector2(34f, 26f));
                dBadgeTx[i] = Kit.Txt(dBadge[i].rectTransform, "Lv", "0", 28, Kit.Cream, TextAnchor.MiddleCenter, false);
                Kit.Stretch(dBadgeTx[i].rectTransform, 0f, 2f, 0f, 0f);
                UnityEngine.UI.Shadow bs = dBadgeTx[i].gameObject.AddComponent<UnityEngine.UI.Shadow>();
                bs.effectColor = new Color(0f, 0f, 0f, 0.5f); bs.effectDistance = new Vector2(1.5f, -1.5f);

                Text n = Kit.Txt(detail, "Name" + i, Skills.Names[i], 26, Kit.Dark, TextAnchor.MiddleLeft, false);
                Kit.Place(n.rectTransform, Kit.TL, new Vector2(0f, 0.5f), new Vector2(128f, top - 22f), new Vector2(300f, 30f));
                dLvl[i] = Kit.Txt(detail, "Xp" + i, "", 19, Kit.A(Kit.Brown, 0.85f), TextAnchor.MiddleRight, false);
                Kit.Place(dLvl[i].rectTransform, Kit.TR, new Vector2(1f, 0.5f), new Vector2(-44f, top - 22f), new Vector2(300f, 26f));
                RectTransform bar = Bar(detail, "Bar" + i, 14f, out dFill[i]);
                Kit.Place(bar, Kit.TL, new Vector2(0f, 0.5f), new Vector2(128f, top - 46f), new Vector2(DW - 128f - 44f, 14f));
                bar.GetComponent<Image>().color = Kit.A(Kit.Dark, 0.16f);
                dFill[i].GetComponent<Image>().color = SkillCol[i];
                for (int q = 1; q < 4; q++)
                {
                    Image tick = Kit.Img(bar, "Q" + q, null, Kit.A(Kit.Dark, 0.18f), false);
                    tick.rectTransform.anchorMin = tick.rectTransform.anchorMax = new Vector2(q * 0.25f, 0.5f);
                    tick.rectTransform.pivot = Kit.C;
                    tick.rectTransform.anchoredPosition = Vector2.zero; tick.rectTransform.sizeDelta = new Vector2(2f, 14f);
                }
                dBonus[i] = Kit.Txt(detail, "Bonus" + i, "", 17, Kit.Dark, TextAnchor.MiddleLeft, false);
                dBonus[i].supportRichText = true;
                Kit.Place(dBonus[i].rectTransform, Kit.TL, new Vector2(0f, 0.5f), new Vector2(128f, top - 70f), new Vector2(DW - 128f - 44f, 22f));
            }

            RectTransform foot = Kit.Node(detail, "Foot");
            Kit.Place(foot, new Vector2(0.5f, 0f), Kit.C, new Vector2(0f, 34f), new Vector2(10f, 34f));
            RectTransform kc = Kit.Chip(foot, "K", 30f);
            Text kt = Kit.Txt(foot, "Close", "Fechar", 20, Kit.Brown, TextAnchor.MiddleLeft, false);
            float fw = kc.sizeDelta.x + 10f + kt.preferredWidth;
            kc.anchorMin = kc.anchorMax = Kit.C; kc.pivot = new Vector2(0f, 0.5f); kc.anchoredPosition = new Vector2(-fw * 0.5f, 0f);
            Kit.Place(kt.rectTransform, Kit.C, new Vector2(0f, 0.5f), new Vector2(-fw * 0.5f + kc.sizeDelta.x + 10f, 0f), new Vector2(kt.preferredWidth + 6f, 34f));
            detailRoot.gameObject.SetActive(false);
            Refresh();
        }

        public void Gain(int i, int amount)
        {
            if (Time.unscaledTime - flashAt[i] > 1.4f) plusSum[i] = 0;
            plusSum[i] += amount;
            flashAt[i] = Time.unscaledTime;
            plus[i].text = "+" + plusSum[i];
            nextRefresh = 0f;
        }

        void Refresh()
        {
            for (int i = 0; i < 6; i++)
            {
                int total = Skills.xp[i], have, need;
                Skills.Progress(total, out have, out need);
                int l = Skills.Level(total);
                float k = need > 0 ? (float)have / need : 1f;
                lvl[i].text = l.ToString();
                SetFill(fill[i], k);
                if (open)
                {
                    dBadgeTx[i].text = l.ToString();
                    dLvl[i].text = l >= Skills.MaxLevel || need <= 0 ? "NÍVEL MÁXIMO   |   " + total + " XP" : "NÍVEL " + l + "   |   " + have + " / " + need + " XP";
                    SetFill(dFill[i], l >= Skills.MaxLevel ? 1f : k);
                    Skills.Id s = (Skills.Id)i;
                    dBonus[i].text = "<color=#331f0f>" + (l > 0 ? Skills.Bonus(s, l) : "Ainda sem bónus") + "</color>     <color=#5f3920b0>" + Skills.HowTo(s) + "</color>";
                }
            }
        }

        void Update()
        {
            bool menu = false;
            try { menu = CanvasHelper.ActiveMenu != MenuType.None; } catch { }
            Keyboard kb = Keyboard.current;
            if (kb != null)
            {
                if (!menu && kb.kKey.wasPressedThisFrame) { open = !open; openAt = Time.unscaledTime; nextRefresh = 0f; }
                if (open && kb.escapeKey.wasPressedThisFrame) open = false;
            }
            if (menu) open = false;
            if (detailRoot.gameObject.activeSelf != open) detailRoot.gameObject.SetActive(open);
            if (open)
            {
                float a = Kit.EaseOut((Time.unscaledTime - openAt) / 0.22f);
                detailGroup.alpha = a;
                detail.localScale = Vector3.one * Mathf.LerpUnclamped(0.95f, 1f, Kit.Back((Time.unscaledTime - openAt) / 0.3f));
                detail.anchoredPosition = new Vector2(0f, 6f - 22f * (1f - a));
            }
            boxGroup.alpha = Mathf.MoveTowards(boxGroup.alpha, menu || open ? 0f : 1f, Time.unscaledDeltaTime * 6f);

            for (int i = 0; i < 6; i++)
            {
                float t = Time.unscaledTime - flashAt[i];
                float fa = flashAt[i] > 0f ? Mathf.Clamp01(1f - t / 1.2f) : 0f;
                flash[i].color = Kit.A(Kit.Good, 0.35f * fa);
                float pa = flashAt[i] > 0f ? Mathf.Clamp01(1.6f - t) : 0f;
                plus[i].color = Kit.A(Kit.Cream, pa);
                plus[i].rectTransform.anchoredPosition = new Vector2(-8f - 10f * Kit.EaseOut(t / 0.4f), plus[i].rectTransform.anchoredPosition.y);
            }
            if (Time.unscaledTime >= nextRefresh) { nextRefresh = Time.unscaledTime + 0.5f; Refresh(); }
        }
    }
}
