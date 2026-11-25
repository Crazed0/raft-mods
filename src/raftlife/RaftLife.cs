using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using HMLLibrary;
using RaftModLoader;

// Raft Life v1.1.0: a shark hit several times in a row backs off for a while before coming back.
// Raft Life v1.0.0: small touches that should feel like part of the game.
// 1) Inventory full: the game already drops what does not fit in a box, but throws it forward
//    from the camera; with the hook at the edge of the raft that is straight into the sea. Now a
//    box from a full inventory lands beside the player, on the raft.
// 2) Rain: while it rains, water tanks with open sky above them slowly fill with fresh water
//    (host only; the game's own tank message keeps every player in sync).
// 3) Wounded shark: the game already makes the shark let go of the raft (or swim past you) when
//    hit, but it is back at once. Hit it 3 times within 15 s and it dives away and leaves you
//    alone for 40-70 s (host decides; the game syncs the shark to everyone).
// Console (F10): life.status | life.rain <units per minute, 0 = off> | life.shark <hits, 0 = off>
public class RaftLife : Mod
{
    public const string Version = "1.1.0";
    public const string HarmonyId = "com.bestcode.raftlife";
    const string PrefRain = "RaftLife.RainPerMin";
    const string PrefShark = "RaftLife.SharkHits";
    public static int sharkHits = 3;          // hits within SharkWindow that make it back off
    const float SharkWindow = 15f;
    public static int sharksFled, attacksHeldBack;
    class SharkMemo { public readonly List<float> hits = new List<float>(); public float fleeUntil; }
    static readonly Dictionary<AI_StateMachine_Shark, SharkMemo> sharks = new Dictionary<AI_StateMachine_Shark, SharkMemo>();

    Harmony harmony;
    public static int adding;              // >0 while the local player's inventory is adding items
    public static int boxesBeside;
    public static float rainPerMin = 12f;  // tank units per minute while it rains
    const float RainTick = 5f;
    static float nextRain, nextTankScan;
    static readonly List<Tank> tanks = new List<Tank>();
    static readonly AccessTools.FieldRef<Tank, bool> placedRef = AccessTools.FieldRefAccess<Tank, bool>("hasBeenPlaced");
    public static bool raining;
    public static int tanksOpen, tanksCovered;
    public static float rainAdded;
    static readonly HashSet<string> said = new HashSet<string>();

    public void Start()
    {
        rainPerMin = PlayerPrefs.GetFloat(PrefRain, 12f);
        sharkHits = PlayerPrefs.GetInt(PrefShark, 3);
        harmony = new Harmony(HarmonyId);
        int patched = 0;
        var broken = new List<string>();
        foreach (Type t in typeof(RaftLife).Assembly.GetTypes())
        {
            if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            try { harmony.CreateClassProcessor(t).Patch(); patched++; }
            catch (Exception e) { broken.Add(t.Name); Debug.LogError("[RaftLife] patch " + t.Name + " falhou: " + (e.InnerException ?? e).Message); }
        }
        Log("carregado v" + Version + ", patches " + patched + "/" + (patched + broken.Count) + (broken.Count > 0 ? " (FALHARAM: " + string.Join(", ", broken.ToArray()) + ")" : "") + ", chuva " + rainPerMin + "/min, tubarao foge com " + sharkHits + " golpes");
    }

    public void OnModUnload()
    {
        if (harmony != null) harmony.UnpatchAll(HarmonyId);
        Log("descarregado");
    }

    public static void Log(string msg) { Debug.Log("[RaftLife] " + msg); }
    public static void LogOnce(string msg) { if (said.Add(msg)) Log(msg); }

    // ---------- inventory full: box beside the player ----------
    // Called (through the patch on Helper.DropItem) only for drops made while the local inventory
    // is adding items, i.e. what did not fit. Normal drops (Q, dragging out) are untouched.
    public static void Beside(ItemInstance item, ref Vector3 position, ref Vector3 direction, bool onRaft)
    {
        Network_Player me = ComponentManager<Network_Player>.Value;
        if (me == null) return;
        Vector3 fwd = direction; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.01f) { fwd = me.transform.forward; fwd.y = 0f; }
        fwd.Normalize();
        Vector3 side = Vector3.Cross(Vector3.up, fwd);
        Vector3 basePos = me.transform.position;
        // behind, then to either side, then behind-diagonal: the first spot with raft under it
        Vector3[] tries = { -fwd, side, -side, (-fwd + side).normalized, (-fwd - side).normalized };
        Vector3 chosen = Vector3.zero;
        bool found = false;
        if (onRaft)
            foreach (Vector3 d in tries)
                if (FloorUnder(basePos + d * 0.6f)) { chosen = d; found = true; break; }
        if (found)
        {
            position = basePos + chosen * 0.6f + Vector3.up * 0.3f;
            direction = chosen * 0.15f;   // the game pushes with direction*100 + up*200: a small hop
        }
        else
        {
            position = basePos + Vector3.up * 0.3f;
            direction = Vector3.zero;     // straight up and down, at the player's feet
        }
        boxesBeside++;
        Log("inventario cheio: " + (item != null && item.baseItem != null ? item.baseItem.UniqueName + " x" + item.Amount : "?") + " numa caixa ao lado do jogador" + (found ? "" : " (aos pes)"));
    }

    static bool FloorUnder(Vector3 p)
    {
        RaycastHit hit;
        if (!Physics.Raycast(p + Vector3.up * 1.2f, Vector3.down, out hit, 2.6f, ~0, QueryTriggerInteraction.Ignore)) return false;
        if (hit.collider.GetComponentInParent<Network_Player>() != null) return false;
        return hit.collider.GetComponentInParent<Block>() != null;
    }

    // ---------- wounded shark backs off ----------
    public static bool Fleeing(AI_StateMachine_Shark s)
    {
        SharkMemo m;
        return s != null && sharks.TryGetValue(s, out m) && Time.time < m.fleeUntil;
    }

    public static void SharkHit(AI_StateMachine_Shark s, float damage, EntityType by)
    {
        if (!Raft_Network.IsHost || sharkHits <= 0 || s == null || damage <= 0f || by != EntityType.Player) return;
        if (s.currentState == s.deadState || s.currentState == s.decayState) return;
        if (s.networkBehaviour != null && s.networkBehaviour.networkEntity != null && s.networkBehaviour.networkEntity.IsDead) return;
        SharkMemo m;
        if (!sharks.TryGetValue(s, out m))
        {
            var dead = new List<AI_StateMachine_Shark>();
            foreach (var k in sharks.Keys) if (k == null) dead.Add(k);
            foreach (var k in dead) sharks.Remove(k);
            m = new SharkMemo();
            sharks[s] = m;
        }
        if (Time.time < m.fleeUntil) return;
        m.hits.RemoveAll(t => Time.time - t > SharkWindow);
        m.hits.Add(Time.time);
        if (m.hits.Count < sharkHits) return;
        m.hits.Clear();
        float secs = UnityEngine.Random.Range(40f, 70f);
        m.fleeUntil = Time.time + secs;
        sharksFled++;
        s.SetSearchBlockProgressCooldown(secs + 10f);
        AI_State away = s.GetDiveOrRetreatState(5f);
        if (away != null) s.ChangeState(away);
        else if (s.circulationDeepState != null) s.ChangeState(s.circulationDeepState);
        Log("tubarao ferido afasta-se durante " + Mathf.RoundToInt(secs) + " s");
    }

    // ---------- rain fills open-air water tanks ----------
    public void Update()
    {
        if (Raft_Network.InMenuScene || !LoadSceneManager.IsGameSceneLoaded || !Raft_Network.IsHost) return;
        if (Time.time < nextRain) return;
        nextRain = Time.time + RainTick;
        WeatherManager wm = ComponentManager<WeatherManager>.Value;
        Weather w = wm != null ? wm.GetCurrentWeather() : null;
        bool now = w != null && w.so_weather != null && w.so_weather.uniqueWeatherType == UniqueWeatherType.Rain;
        if (now != raining)
        {
            raining = now;
            if (now) { rainAdded = 0f; Log("comecou a chover: tanques de agua a ceu aberto vao enchendo"); }
            else Log("parou de chover (agua da chuva nos tanques: " + rainAdded.ToString("0.#") + ")");
        }
        if (!raining || rainPerMin <= 0f) return;
        try { Rain(); } catch (Exception e) { LogOnce("chuva falhou: " + e.Message); }
    }

    static void RefreshTanks()
    {
        tanks.Clear();
        foreach (Tank t in UnityEngine.Object.FindObjectsOfType<Tank>())
            if (t != null && t.tankFiltrationType == FuelValue_FiltrationType.FV_WATERTANK && placedRef(t)) tanks.Add(t);
    }

    static void Rain()
    {
        if (Time.time >= nextTankScan) { nextTankScan = Time.time + 30f; RefreshTanks(); }
        float amount = rainPerMin * RainTick / 60f;
        tanksOpen = tanksCovered = 0;
        foreach (Tank t in tanks)
        {
            if (t == null || !t.gameObject.activeInHierarchy) continue;
            if (!OpenSky(t)) { tanksCovered++; continue; }
            tanksOpen++;
            if (t.IsFull) continue;
            float add = Mathf.Min(amount, t.maxCapacity - t.CurrentTankAmount);
            if (add <= 0f) continue;
            t.ModifyTankRPC(add, null);
            rainAdded += add;
            LogOnce("chuva a encher: " + Name(t) + " (capacidade " + t.maxCapacity + ")");
        }
    }

    // Nothing solid straight above the top of the tank's block.
    public static bool OpenSky(Tank t)
    {
        Block b = t.GetComponentInParent<Block>();
        Transform root = b != null ? b.transform : t.transform;
        Bounds bounds = new Bounds(root.position, Vector3.zero);
        bool any = false;
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>())
        {
            if (r == null || !r.enabled) continue;
            if (!any) { bounds = r.bounds; any = true; } else bounds.Encapsulate(r.bounds);
        }
        Vector3 from = new Vector3(bounds.center.x, bounds.max.y + 0.2f, bounds.center.z);
        RaycastHit hit;
        if (!Physics.Raycast(from, Vector3.up, out hit, 80f, ~0, QueryTriggerInteraction.Ignore)) return true;
        return hit.collider.GetComponentInParent<Network_Player>() != null;   // a player standing on it is not a roof
    }

    static string Name(Tank t)
    {
        Block b = t.GetComponentInParent<Block>();
        string n = b != null && b.buildableItem != null ? b.buildableItem.UniqueName : t.name;
        return n.Replace("(Clone)", "").Trim();
    }

    public static string Status()
    {
        RefreshTanks();
        var parts = new List<string>();
        foreach (Tank t in tanks)
            if (t != null) parts.Add(Name(t) + " " + t.CurrentTankAmount.ToString("0.#") + "/" + t.maxCapacity + (OpenSky(t) ? " ceu aberto" : " coberto"));
        return "chuva=" + (raining ? "sim" : "nao") + " ritmo=" + rainPerMin + "/min agua_da_chuva=" + rainAdded.ToString("0.#") + " caixas_ao_lado=" + boxesBeside + " tubarao_foge_com=" + (sharkHits > 0 ? sharkHits + "_golpes" : "off") + " tubaroes_fugiram=" + sharksFled + " ataques_travados=" + attacksHeldBack
            + " tanques_de_agua=" + tanks.Count + (parts.Count > 0 ? " [" + string.Join("; ", parts.ToArray()) + "]" : "") + (Raft_Network.IsHost ? "" : " (a chuva so corre no anfitriao)");
    }

    [ConsoleCommand("life.status", "Raft Life: rain, water tanks, boxes dropped beside you.")]
    public static string CmdStatus(string[] args) { string s = Status(); Log("estado: " + s); return s; }

    [ConsoleCommand("life.shark", "Hits within 15 s that make a shark back off for a while (host). Syntax: life.shark <0-10>, 0 = off")]
    public static string CmdShark(string[] args)
    {
        int v;
        if (args.Length < 1 || !int.TryParse(args[0], out v)) return "tubarao foge com " + sharkHits + " golpes. Uso: life.shark <0-10>";
        sharkHits = Mathf.Clamp(v, 0, 10);
        PlayerPrefs.SetInt(PrefShark, sharkHits);
        PlayerPrefs.Save();
        Log("tubarao foge com " + sharkHits + " golpes");
        return sharkHits > 0 ? "tubarao foge com " + sharkHits + " golpes" : "tubarao ferido: desligado";
    }

    [ConsoleCommand("life.rain", "Water tank units per minute while it rains (host). Syntax: life.rain <0-120>, 0 = off")]
    public static string CmdRain(string[] args)
    {
        float v;
        if (args.Length < 1 || !float.TryParse(args[0].Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return "chuva: " + rainPerMin + "/min. Uso: life.rain <0-120>";
        rainPerMin = Mathf.Clamp(v, 0f, 120f);
        PlayerPrefs.SetFloat(PrefRain, rainPerMin);
        PlayerPrefs.Save();
        Log("chuva " + rainPerMin + "/min");
        return "chuva " + rainPerMin + "/min";
    }
}

// The local player's inventory is adding items: what does not fit is dropped from inside these.
[HarmonyPatch(typeof(PlayerInventory), "AddItem", new Type[] { typeof(string), typeof(int) })]
static class Patch_AddByName
{
    static void Prefix() { RaftLife.adding++; }
    static Exception Finalizer(Exception __exception) { RaftLife.adding--; return __exception; }
}

[HarmonyPatch(typeof(PlayerInventory), "AddItem", new Type[] { typeof(ItemInstance), typeof(bool) })]
static class Patch_AddInstance
{
    static void Prefix() { RaftLife.adding++; }
    static Exception Finalizer(Exception __exception) { RaftLife.adding--; return __exception; }
}

[HarmonyPatch(typeof(Helper), "DropItem", new Type[] { typeof(ItemInstance), typeof(Vector3), typeof(Vector3), typeof(bool) })]
static class Patch_FullInventoryDrop
{
    static void Prefix(ItemInstance item, ref Vector3 position, ref Vector3 direction, bool parentedToRaft)
    {
        if (RaftLife.adding <= 0) return;
        try { RaftLife.Beside(item, ref position, ref direction, parentedToRaft); }
        catch (Exception e) { RaftLife.LogOnce("caixa ao lado falhou: " + e.Message); }
    }
}

// The shark took damage: count player hits; enough in a short time and it backs off.
[HarmonyPatch(typeof(AI_StateMachine_Shark), "OnDamageTaken")]
static class Patch_SharkHit
{
    static void Postfix(AI_StateMachine_Shark __instance, float damage, EntityType damageType)
    {
        try { RaftLife.SharkHit(__instance, damage, damageType); }
        catch (Exception e) { RaftLife.LogOnce("tubarao ferido falhou: " + e.Message); }
    }
}

// While it backs off it stays deep and does not go for the raft or for a player in the water.
[HarmonyPatch(typeof(AI_StateMachine), "ChangeState", new Type[] { typeof(AI_State) })]
static class Patch_SharkKeepsAway
{
    static bool Prefix(AI_StateMachine __instance, ref AI_State newState)
    {
        if (newState == null || !Raft_Network.IsHost) return true;
        AI_StateMachine_Shark s = __instance as AI_StateMachine_Shark;
        if (s == null || !RaftLife.Fleeing(s)) return true;
        if (newState == s.circulationSurfaceState && s.circulationDeepState != null) { newState = s.circulationDeepState; return true; }
        if (newState == s.biteRaftState || newState == s.wayPointChaseState || newState is AI_State_Attack_Entity_Shark)
        {
            RaftLife.attacksHeldBack++;
            // from a dive (or anywhere else) it goes to swim around deep; if it already is, stay
            if (s.circulationDeepState != null && __instance.currentState != s.circulationDeepState) { newState = s.circulationDeepState; return true; }
            return false;
        }
        return true;
    }
}
