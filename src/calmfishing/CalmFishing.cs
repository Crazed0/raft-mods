using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using HMLLibrary;
using RaftModLoader;

// Calm Fishing v1.7.0 (storms: the sea brings more floating things while a storm lasts, anchored or sailing; seagulls circle over fish schools)
// Calm Fishing v1.6.0 (fish schools: now and then, with the raft stopped, a school of fish splashes near a player for a few minutes; fishing in it the fish bite faster and sometimes a second fish comes up; the host sends the spot to everyone)
// Calm Fishing v1.5.0 (natural drift: anchored, one sea current carries the items in straight lines past and under the raft, nothing is pulled to the raft edge; sailing: items stay still as in the normal game; at an island they wash up on its coast; scatter now sent to the other players too, before it was host-only; items that passed everyone are recycled)
// Calm Fishing v1.4.7 (the item-spread patch targeted a method that does not exist on that class and stopped every later patch: calm waves and the weather hook were off since 1.4.3; patches now applied one by one)
// Calm Fishing v1.4.6 (items never follow players: at an island they wait on its coast; items wait at the real raft edge, never stuck out in the water; 3x fewer near islands, not while sailing past them; per-player counts without overlap)
// Calm Fishing v1.4.3 (more spawns, spread out, farther; drift.rate / drift.spawn)
// Calm Fishing v1.4.2 (only despawn far from every player, distance via drift.dist; default limit 80)
// Calm Fishing v1.4.1 (sailing: items no longer chase the raft, they slide into its lane)
// Calm Fishing v1.4.0
// v1.4.0: always on (anchored, stopped or sailing): the mod alone spawns the sea items near the players.
//         On the raft they wait at the raft edge, off the raft (island / swimming) they go to the player or his shore.
// Calm Fishing v1.3.2
// v1.3.2: per-player quota (limit / players), spawn direction that avoids land per player,
//         items waiting too long are recycled so fresh ones keep coming.
// Calm Fishing v1.3.1
// v1.3.1: the mod spawns the items itself while anchored (the game spawner never fired), from the open-sea side, 40 m away.
// Calm Fishing v1.3.0
// v1.3.0: items spawn near every online player (split evenly) and despawn only when far from all players.
// 1) While the raft is anchored or stopped, floating items keep spawning upstream and a sea
//    current carries them past and under the raft (hook or item nets). A limit caps how many
//    floating items can exist at once, so they never pile up and cause lag.
// 2) Calmer waves: blends the game's flat "anti-bob" water profile into the weather's waves.
// 3) Logs everything (weather, waves, drift on/off, items spawned / at the edge / caught, limit).
// Console (F10): drift <m/s>|off  drift.max <n>  drift.log on|off  drift.status  waves <0-100>
public class CalmFishing : Mod
{
    public const string Version = "1.7.1";
    public const string HarmonyId = "com.bestcode.calmfishing";
    const string PrefDrift = "CalmFishing.Drift";
    const string PrefCalm = "CalmFishing.Calm";
    const string PrefMax = "CalmFishing.Max2";
    const string PrefDist = "CalmFishing.Dist";
    const string PrefRate = "CalmFishing.Rate";
    const string PrefStorm = "CalmFishing.Storm";
    const string PrefSpawn = "CalmFishing.Spawn";
    public static float spawnRate = 2.5f;   // x the game's spawn rate
    public static float spawnDist = 70f;    // batches appear around this distance from the player
    public const float Spread = 25f;        // each item is scattered up to this far from its batch point
    const string PrefLog = "CalmFishing.Log";
    const string PrefCreative = "CalmFishing.Creative";
    // Creative has no floating items in the game itself, so the mod stays off there unless this
    // test switch is on (drift.criativo on). It changes nothing in normal worlds.
    public static bool creativeTest;

    public static float driftSpeed = 1.5f;  // m/s
    public static float calm = 0.7f;        // 0..1
    public static int maxItems = 80;        // floating items (planks + items) while drifting
    public static bool verbose = true;      // per-item logs
    public const float EdgeMargin = 0.6f;
    public static bool IslandCoastFromRaft = false; // players on the raft keep the items at the raft edge   // items wait this far outside the raft's edge

    Harmony harmony;

    static CalmFishing self;

    public void Start()
    {
        self = this;
        driftSpeed = PlayerPrefs.GetFloat(PrefDrift, 1.5f);
        calm = Mathf.Clamp01(PlayerPrefs.GetFloat(PrefCalm, 0.7f));
        maxItems = Mathf.Clamp(PlayerPrefs.GetInt(PrefMax, 80), 5, 300);
        despawnDist = Mathf.Clamp(PlayerPrefs.GetFloat(PrefDist, 100f), 30f, 500f);
        spawnRate = Mathf.Clamp(PlayerPrefs.GetFloat(PrefRate, 2.5f), 0.5f, 10f);
        stormFactor = Mathf.Clamp(PlayerPrefs.GetFloat(PrefStorm, 2f), 1f, 4f);
        spawnDist = Mathf.Clamp(PlayerPrefs.GetFloat(PrefSpawn, 70f), 20f, 200f);
        verbose = PlayerPrefs.GetInt(PrefLog, 1) == 1;
        creativeTest = PlayerPrefs.GetInt(PrefCreative, 0) == 1;
        schoolsOn = PlayerPrefs.GetInt(PrefSchools, 1) == 1;
        try { SubscribeToNetworkChannel(NetSlug); } catch (System.Exception e) { Log("canal de rede falhou: " + e.Message); }
        harmony = new Harmony(HarmonyId);
        // one patch class at a time: PatchAll stops at the first broken patch and skips the rest
        // (until 1.4.7 a bad SpawnItem target left calm waves, net logs and the weather hook off)
        int patched = 0;
        var broken = new List<string>();
        foreach (System.Type t in typeof(CalmFishing).Assembly.GetTypes())
        {
            if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            try { harmony.CreateClassProcessor(t).Patch(); patched++; }
            catch (System.Exception e) { broken.Add(t.Name); Debug.LogError("[CalmFishing] patch " + t.Name + " falhou: " + (e.InnerException ?? e).Message); }
        }
        WeatherManager.OnWeatherChanged = (System.Action<Weather>)System.Delegate.Combine(WeatherManager.OnWeatherChanged, new System.Action<Weather>(OnWeatherChanged));
        Log("carregado v" + Version + ", patches " + patched + "/" + (patched + broken.Count) + (broken.Count > 0 ? " (FALHARAM: " + string.Join(", ", broken.ToArray()) + ")" : "") + ". Corrente " + (driftSpeed > 0f ? driftSpeed + " m/s" : "desligada") + ", ondas acalmadas " + Mathf.RoundToInt(calm * 100f) + "%, limite " + maxItems + " itens, despawn a " + despawnDist + " m, logs " + (verbose ? "on" : "off"));
    }

    public void OnModUnload()
    {
        WeatherManager.OnWeatherChanged = (System.Action<Weather>)System.Delegate.Remove(WeatherManager.OnWeatherChanged, new System.Action<Weather>(OnWeatherChanged));
        if (harmony != null) harmony.UnpatchAll(HarmonyId);
        EndSchool();
        ClearGulls();
        Log("descarregado");
    }

    public static void Log(string msg) { Debug.Log("[CalmFishing] " + msg); }
    public static void LogV(string msg) { if (verbose) Log(msg); }

    static string Clean(string n) { return n == null ? "?" : n.Replace("(Clone)", "").Trim(); }

    // ---------- weather / waves ----------
    static void OnWeatherChanged(Weather w)
    {
        if (w == null || w.so_weather == null) return;
        string type = w.so_weather.uniqueWeatherType.ToString();
        float wind = w.so_weather.waterProfile != null ? w.so_weather.waterProfile.Data.WindSpeed : 0f;
        bool storm = w.so_weather.uniqueWeatherType == UniqueWeatherType.BigWaves || w.so_weather.uniqueWeatherType == UniqueWeatherType.SnowRough;
        Log("tempo mudou: " + type + (storm ? " (TEMPESTADE)" : "") + ", vento " + wind.ToString("0.0")
            + (Raft_Network.IsHost && calm > 0f ? " -> acalmado " + Mathf.RoundToInt(calm * 100f) + "%" : ""));
    }

    public static float lastRawWind = -1f, lastCalmWind;
    public static void OnWaveTarget(float rawWind, float calmedWind)
    {
        lastCalmWind = calmedWind;
        if (lastRawWind < 0f || Mathf.Abs(rawWind - lastRawWind) >= 2f)
        {
            lastRawWind = rawWind;
            Log("ondas: vento " + rawWind.ToString("0.0") + " -> " + calmedWind.ToString("0.0") + " (calma " + Mathf.RoundToInt(calm * 100f) + "%)");
        }
    }

    // ---------- drift state ----------
    public static bool DriftActive(Raft raft)
    {
        if (driftSpeed <= 0f || raft == null) return false;
        var gm = GameModeValueManager.GetCurrentGameModeValue();
        if (gm == null) return false;
        if (IsCreative(gm)) return creativeTest;
        return gm.enviromentalVariables.spawnObjects;
    }

    static bool IsCreative(SO_GameModeValue gm) { return gm.raftSpecificVariables.isRaftAlwaysAnchored || !gm.enviromentalVariables.spawnObjects; }

    public static string OffReason(Raft raft)
    {
        if (raft == null) return "sem jangada";
        if (driftSpeed <= 0f) return "desligado com 'drift off'";
        var gm = GameModeValueManager.GetCurrentGameModeValue();
        if (gm != null && IsCreative(gm) && !creativeTest) return "modo criativo (o jogo nao poe itens no mar); para testar: drift.criativo on";
        return "";
    }

    public static bool Stopped(Raft raft) { return raft != null && (raft.IsAnchored || !raft.Moving); }

    public static int FloatingCount(ObjectSpawnerManager mgr)
    {
        if (mgr == null) return 0;
        int n = 0;
        if (mgr.plankSpawner != null && mgr.plankSpawner.spawnedObjects != null) n += mgr.plankSpawner.spawnedObjects.Count;
        if (mgr.itemSpawner != null && mgr.itemSpawner.spawnedObjects != null) n += mgr.itemSpawner.spawnedObjects.Count;
        return n;
    }

    public static bool IsOverLand(Vector3 pos, Raft raft)
    {
        RaycastHit hit;
        var from = new Vector3(pos.x, 80f, pos.z);
        if (!Physics.Raycast(from, Vector3.down, out hit, 90f, ~0, QueryTriggerInteraction.Ignore)) return false;
        if (hit.point.y <= 0.3f) return false;
        if (raft != null && hit.transform.IsChildOf(raft.transform)) return false;
        if (hit.transform.GetComponentInParent<PickupItem_Networked>() != null) return false;
        return true;
    }

    public static int spawnedWhileDrifting, blockedByLand, blockedByLimit, caughtByNet;
    public static bool limitReached;

    public static string Status()
    {
        Raft raft = ComponentManager<Raft>.Value;
        ObjectSpawnerManager mgr = ComponentManager<ObjectSpawnerManager>.Value;
        if (raft == null || mgr == null) return "Fora do jogo.";
        WeatherManager wm = ComponentManager<WeatherManager>.Value;
        Weather w = wm != null ? wm.GetCurrentWeather() : null;
        string weather = w != null && w.so_weather != null ? w.so_weather.uniqueWeatherType.ToString() : "?";
        return "host=" + Raft_Network.IsHost + " jangada=" + (raft.IsAnchored ? "ancorada" : raft.Moving ? "a_navegar" : "parada")
            + " mod_ligado=" + DriftActive(raft) + (DriftActive(raft) ? "" : " (porque: " + OffReason(raft) + ")") + (creativeTest ? " criativo_teste=on" : "") + " itens=" + FloatingCount(mgr) + "/" + EffMax() + " (total, nao por jogador)"
            + (IslandMode ? " modo=ilha(3x menos, normal " + maxItems + ")" : " modo=mar")
            + " na_costa=" + atEdge.Count + " corrente=" + Mathf.RoundToInt(Mathf.Atan2(flow.x, flow.z) * Mathf.Rad2Deg) + "graus" + " criados=" + spawnedWhileDrifting + " rede=" + caughtByNet
            + " bloq_terra=" + blockedByLand + " bloq_limite=" + blockedByLimit
            + " ilha=" + (island != null ? Clean(island.name) : "nenhuma") + PlayersInfo() + SpawnerInfo(mgr.itemSpawner)
            + " quota=" + Quota() + "/jogador ritmo=x" + spawnRate + " spawn_a=" + spawnDist + "m despawn=" + despawnDist + "m removidos=" + removedFar
            + " spawns=" + spawnCalls + (lastSpawnProblem.Length > 0 ? " problema=" + lastSpawnProblem : "")
            + " tempo=" + weather + " ondas=" + (WaterRoughnessManager.Roughness * WaterRoughnessManager.maxRoughness).ToString("0.0") + " tempestade=" + (Storming() ? "sim x" + stormFactor.ToString("0.#") : "nao") + SchoolStatus();
    }

    static string SpawnerInfo(ObjectSpawner s)
    {
        var r = s as ObjectSpawner_RaftDirection;
        if (r == null) return "";
        return " spawner(max=" + r.maxSpawnedObjects + " dist=" + r.spawnDistanceFromRaft.ToString("0") + " remove=" + r.removeDistanceFromRaft.ToString("0") + " auto=" + r.removeItemsAutomaticly + ")";
    }

    static float nextStatusLog, nextScan;
    static bool wasActive;
    static readonly Dictionary<int, string> known = new Dictionary<int, string>();
    static readonly HashSet<int> seen = new HashSet<int>();
    static readonly List<int> gone = new List<int>();
    public static readonly HashSet<int> atEdge = new HashSet<int>();
    static readonly Dictionary<int, float> bornAt = new Dictionary<int, float>();

    public void Update()
    {
        if (Raft_Network.InMenuScene || !LoadSceneManager.IsGameSceneLoaded) { wasActive = false; known.Clear(); atEdge.Clear(); return; }
        Raft raft = ComponentManager<Raft>.Value;
        ObjectSpawnerManager mgr = ComponentManager<ObjectSpawnerManager>.Value;
        if (raft == null || mgr == null) return;

        bool active = DriftActive(raft);
        if (active != wasActive)
        {
            wasActive = active;
            if (active) Log("LIGADO: itens do mar sempre perto dos jogadores, limite " + maxItems);
            else Log("desligado (" + (driftSpeed <= 0f ? "drift off" : "modo criativo ou sem itens no mar") + "), jogo normal");
            if (!active) atEdge.Clear();
        }

        if (Time.unscaledTime >= nextStatusLog)
        {
            nextStatusLog = Time.unscaledTime + 60f;
            Log("estado: " + Status());
        }

        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + 0.5f;
            ScanItems(mgr, raft);
        }

        int pc = Players().Count;
        if (pc != lastPlayerCount)
        {
            lastPlayerCount = pc;
            Log("jogadores online: " + pc + (pc > 1 ? " -> itens repartidos por todos" : ""));
        }

        Schools(raft, active);
        if (!active) { island = null; return; }
        if (Time.unscaledTime >= nextIslandCheck)
        {
            nextIslandCheck = Time.unscaledTime + 3f;
            FindIsland(raft);
            if (Raft_Network.IsHost) RemoveFarItems(mgr, raft);
        }
        RefreshHull(raft);
        float dt = Time.deltaTime;
        UpdateFlow(raft, dt);
        Move(mgr.plankSpawner, raft, dt);
        Move(mgr.itemSpawner, raft, dt);
        if (Raft_Network.IsHost)
        {
            Drive(mgr.plankSpawner as ObjectSpawner_RaftDirection, 0, raft);
            Drive(mgr.itemSpawner as ObjectSpawner_RaftDirection, 1, raft);
        }
    }

    // ---------- own spawning (host) ----------
    public const float SpawnDist = 40f;        // items appear this far upstream (game uses 120)
    static readonly float[] nextSpawn = new float[2];
    public static string lastSpawnProblem = "";
    public static int spawnCalls;

    // From where the items come: away from the nearby island (open sea), else upstream (-Z).
    public static Vector3 SpawnDir(Raft raft)
    {
        if (raft != null && !Stopped(raft))
        {
            Vector3 v = Raft.direction; v.y = 0f;
            return v.sqrMagnitude > 0.01f ? v.normalized : Vector3.forward;
        }
        if (island != null && raft != null)
        {
            Vector3 d = raft.transform.position - island.transform.position; d.y = 0f;
            if (d.sqrMagnitude > 1f) return d.normalized;
        }
        return Vector3.back;
    }

    // A batch: upstream of the player with the fewest items, scattered in a strip across the
    // current (SpreadSide) and along it (SpreadAlong). Each item goes through SpawnItemNetwork, so
    // the other players get the exact same position (until 1.5.0 the scatter was host-only).
    public const float SpreadSide = 12f, SpreadAlong = 20f;
    static void Drive(ObjectSpawner_RaftDirection sp, int idx, Raft raft)
    {
        if (sp == null) { lastSpawnProblem = "spawner em falta"; return; }
        if (!sp.gameObject.activeInHierarchy) { lastSpawnProblem = "spawner inativo"; return; }
        if (Time.time < nextSpawn[idx]) return;
        if (sp.currentSettings == null && sp.spawnSettingsAsset != null) sp.currentSettings = sp.spawnSettingsAsset.GetSettings(Mathf.Max(1, Players().Count));
        if (sp.currentSettings == null) { lastSpawnProblem = "sem definicoes de spawn"; nextSpawn[idx] = Time.time + 5f; return; }
        float delay = sp.currentSettings.spawnRateInterval.GetRandomValue();
        if (delay <= 0f) delay = 4f;
        nextSpawn[idx] = Time.time + delay / (spawnRate * (IslandMode ? IslandFactor : 1f) * StormMul());
        ObjectSpawnerManager mgr = ComponentManager<ObjectSpawnerManager>.Value;
        if (FloatingCount(mgr) >= EffMax()) { blockedByLimit++; return; }
        float dist = spawnDist * Random.Range(0.7f, 1.3f);
        Vector3 dir;
        Network_Player p = PickSpawnPlayer(raft, dist, out dir);
        if (p == null) { lastSpawnProblem = "quota cheia ou sem agua livre"; return; }
        Vector3 centre = p.transform.position + dir * dist;
        centre.y = sp.transform.position.y;
        Vector3 side = Vector3.Cross(dir, Vector3.up).normalized;
        int n = Mathf.Max(1, sp.currentSettings.spawnAmount.GetRandomValue());
        int made = 0;
        try
        {
            for (int i = 0; i < n; i++)
            {
                if (FloatingCount(mgr) >= EffMax()) { blockedByLimit++; break; }
                SpawnableFloatingObject obj = sp.GetObjectToSpawn();
                if (obj == null) continue;
                bool ok = false;
                Vector3 pos = centre;
                for (int k = 0; k < 4 && !ok; k++)
                {
                    pos = centre + side * Random.Range(-SpreadSide, SpreadSide) + dir * Random.Range(-SpreadAlong, SpreadAlong);
                    ok = !IsOverLand(pos, raft);
                }
                if (!ok) { blockedByLand++; continue; }
                if (sp.SpawnItemNetwork(pos, obj) != null) { made++; spawnedWhileDrifting++; }
            }
            spawnCalls++;
            lastSpawnProblem = "";
        }
        catch (System.Exception e) { lastSpawnProblem = e.GetType().Name + ": " + e.Message; Log("erro a criar itens: " + e); }
        if (made > 0) LogV(made + " itens novos para " + PlayerName(p) + " a ~" + dist.ToString("0") + " m");
    }

    // ---------- players ----------
    public const float PlayerShare = 60f;   // items within this of a player count as his
    public const float NearRaft = 12f;      // a player this close to the raft edge counts as on the raft
    public const float PlayerStop = 2.5f;   // items stop this close to a player off the raft
    public static readonly List<Network_Player> players = new List<Network_Player>();
    static int playersFrame = -1;

    public static List<Network_Player> Players()
    {
        if (playersFrame == Time.frameCount) return players;
        playersFrame = Time.frameCount;
        players.Clear();
        Raft_Network net = ComponentManager<Raft_Network>.Value;
        if (net != null && net.remoteUsers != null)
            foreach (Network_Player pl in net.remoteUsers.Values)
                if (pl != null && pl.gameObject.activeInHierarchy) players.Add(pl);
        return players;
    }

    static int lastPlayerCount = -1;

    // ---------- island ----------
    public const float IslandRange = 250f; // an island this close to the raft attracts the items
    public static float despawnDist = 100f; // items farther than this from EVERY player are removed (host)
    public const float FarRemoveUnused = 150f;
    // never closer than where new items appear, or they would vanish right after spawning
    public static float EffectiveDespawn() { return Mathf.Max(despawnDist, spawnDist * 1.3f + Spread + 10f); }   // items farther than this are removed (host) so new ones spawn
    static Landmark island;
    static float nextIslandCheck;

    // Near an island (raft or any player within IslandRange) there are 3x fewer items, spawned 3x slower.
    // While the raft is sailing only a player really at the island counts (within IslandRangeSailing),
    // so just passing by an island does not switch to island mode.
    public const float IslandFactor = 1f / 3f;
    public const float IslandRangeSailing = 100f;
    public static bool IslandMode { get { return island != null; } }
    public static int EffMax()
    {
        int m = IslandMode ? Mathf.Max(3, Mathf.RoundToInt(maxItems * IslandFactor)) : maxItems;
        return StormMul() > 1f ? Mathf.RoundToInt(m * 1.5f) : m;   // a storm brings more, then it settles
    }

    // ---------- storms ----------
    // While a storm lasts the sea carries more stuff: items come stormFactor x as often (anchored
    // or sailing) and the item limit is 50% higher. When it passes it goes back to normal.
    public static float stormFactor = 2f;
    static float stormCheckAt;
    static bool stormNow;
    public static bool Storming()
    {
        if (Time.time < stormCheckAt) return stormNow;
        stormCheckAt = Time.time + 2f;
        WeatherManager wm = ComponentManager<WeatherManager>.Value;
        Weather w = wm != null ? wm.GetCurrentWeather() : null;
        bool st = w != null && w.so_weather != null && (w.so_weather.uniqueWeatherType == UniqueWeatherType.BigWaves || w.so_weather.uniqueWeatherType == UniqueWeatherType.SnowRough);
        if (st != stormNow)
        {
            stormNow = st;
            if (Raft_Network.IsHost)
                Log(st ? "tempestade: o mar traz mais coisas (x" + stormFactor.ToString("0.#") + ", limite +50%)" : "a tempestade passou: itens voltam ao ritmo normal");
        }
        return st;
    }
    public static float StormMul() { return stormFactor > 1f && Storming() ? stormFactor : 1f; }

    static void FindIsland(Raft raft)
    {
        Landmark best = null;
        bool sailing = !raft.IsAnchored && raft.Moving;
        float range = sailing ? IslandRangeSailing : IslandRange;
        float bestD = range;
        List<Landmark> all = WorldManager.AllLandmarks;
        var pl = Players();
        if (all != null)
        {
            Vector3 rp = raft.transform.position;
            for (int i = 0; i < all.Count; i++)
            {
                Landmark l = all[i];
                if (l == null || !l.gameObject.activeInHierarchy) continue;
                Vector3 lp = l.transform.position;
                float d = sailing ? float.MaxValue : lp.DistanceXZ(rp);
                for (int k = 0; k < pl.Count; k++) d = Mathf.Min(d, lp.DistanceXZ(pl[k].transform.position));
                if (d < bestD) { bestD = d; best = l; }
            }
        }
        if (best != island)
        {
            island = best;
            if (best != null) Log("ilha perto: " + Clean(best.name) + " a " + bestD.ToString("0") + " m -> modo ilha, limite " + EffMax() + " itens (3x menos)");
            else Log("sem ilha perto -> limite normal " + maxItems + " itens");
        }
    }

    public const float ItemLife = 240f; // items not picked up after this many seconds are recycled
    static readonly List<PickupItem_Networked> toRemove = new List<PickupItem_Networked>();
    static readonly List<PickupItem_Networked> all = new List<PickupItem_Networked>();
    static int removedFar, removedOld, removedQuota;

    public static int Quota()
    {
        int n = Mathf.Max(1, Players().Count);
        return Mathf.Max(3, Mathf.CeilToInt(EffMax() / (float)n));
    }

    static float Age(PickupItem_Networked it)
    {
        float t;
        return bornAt.TryGetValue(it.GetInstanceID(), out t) ? Time.time - t : 0f;
    }

    static void RemoveFarItems(ObjectSpawnerManager mgr, Raft raft)
    {
        toRemove.Clear();
        all.Clear();
        if (mgr.plankSpawner != null && mgr.plankSpawner.spawnedObjects != null) all.AddRange(mgr.plankSpawner.spawnedObjects);
        if (mgr.itemSpawner != null && mgr.itemSpawner.spawnedObjects != null) all.AddRange(mgr.itemSpawner.spawnedObjects);
        var pl = Players();
        int far = 0, over = 0, passed = 0;
        var keepList = new List<KeyValuePair<float, PickupItem_Networked>>();
        // downstream = where the current takes the items (stopped) or behind the raft (sailing)
        Vector3 down = Stopped(raft) ? flow : -SpawnDir(raft);

        foreach (var it in all)
        {
            if (it == null) continue;
            Vector3 p = it.transform.position;
            float od = float.MaxValue, past = float.MaxValue;
            for (int i = 0; i < pl.Count; i++)
            {
                Vector3 pp = pl[i].transform.position;
                od = Mathf.Min(od, pp.DistanceXZ(p));
                Vector3 rel = p - pp; rel.y = 0f;
                past = Mathf.Min(past, Vector3.Dot(rel, down));
            }
            if (pl.Count == 0) od = raft.transform.position.DistanceXZ(p);
            if (od > EffectiveDespawn()) { toRemove.Add(it); far++; continue; }
            // went past everyone: free the slot so a new one comes from upstream
            if (pl.Count > 0 && past > PassedDist && od > KeepNear) { toRemove.Add(it); passed++; continue; }
            keepList.Add(new KeyValuePair<float, PickupItem_Networked>(od, it));
        }
        // over the limit (e.g. just arrived at an island, limit 5x lower): drop the extras farthest
        // from every player, never the ones close to someone (being fished / about to be)
        int limit = EffMax();
        if (keepList.Count > limit + 2)
        {
            keepList.Sort((a, b) => b.Key.CompareTo(a.Key));
            for (int k = 0; k < keepList.Count - limit && keepList[k].Key > KeepNear; k++) { toRemove.Add(keepList[k].Value); over++; }
        }
        if (toRemove.Count == 0) return;
        for (int i = 0; i < toRemove.Count; i++) PickupObjectManager.RemovePickupItemNetwork(toRemove[i]);
        removedFar += far + passed; removedQuota += over;
        LogV("removidos " + toRemove.Count + " itens (" + far + " a mais de " + EffectiveDespawn().ToString("0") + " m de todos, " + passed + " ja passaram por todos, " + over + " acima do limite " + limit + ")");
    }

    // Host: which player gets the next batch: the one under his quota with the fewest items near
    // him. The batch comes from open water around him (tries 8 directions, first the open sea).
    public static Network_Player PickSpawnPlayer(Raft raft, float dist, out Vector3 dir)
    {
        dir = SpawnDir(raft);
        var pl = Players();
        int quota = Quota();
        Network_Player best = null;
        Vector3 bestDir = dir;
        int bestN = int.MaxValue;
        int[] counts = OwnedCounts();
        for (int i = 0; i < pl.Count; i++)
        {
            Vector3 pp = pl[i].transform.position;
            int n = counts[i];
            if (n >= quota) continue;
            if (n > bestN || (n == bestN && Random.value < 0.5f)) continue;
            Vector3 d;
            if (!OpenWaterDir(pp, raft, dist, out d)) continue;
            bestN = n; best = pl[i]; bestDir = d;
        }
        dir = bestDir;
        return best;
    }

    static bool OpenWaterDir(Vector3 from, Raft raft, float dist, out Vector3 dir)
    {
        Vector3 baseDir = SpawnDir(raft);
        for (int k = 0; k < 8; k++)
        {
            dir = Quaternion.Euler(0f, (k % 2 == 0 ? 1 : -1) * ((k + 1) / 2) * 45f, 0f) * baseDir;
            if (!IsOverLand(from + dir * dist, raft)) return true;
        }
        dir = baseDir;
        return false;
    }

    public const float KeepNear = 20f; // items this close to a player are never removed for the limit

    // Each item counts once, for its nearest player (if within PlayerShare). Two players on the
    // same raft no longer both count the same items.
    static int[] owned = new int[0];
    public static int[] OwnedCounts()
    {
        var pl = Players();
        if (owned.Length != pl.Count) owned = new int[pl.Count];
        for (int i = 0; i < owned.Length; i++) owned[i] = 0;
        ObjectSpawnerManager mgr = ComponentManager<ObjectSpawnerManager>.Value;
        if (mgr == null) return owned;
        CountOwned(mgr.plankSpawner, pl);
        CountOwned(mgr.itemSpawner, pl);
        return owned;
    }

    static void CountOwned(ObjectSpawner s, List<Network_Player> pl)
    {
        if (s == null || s.spawnedObjects == null) return;
        foreach (var it in s.spawnedObjects)
        {
            if (it == null) continue;
            Vector3 p = it.transform.position;
            int best = -1; float bd = PlayerShare;
            for (int i = 0; i < pl.Count; i++) { float d = pl[i].transform.position.DistanceXZ(p); if (d < bd) { bd = d; best = i; } }
            if (best >= 0) owned[best]++;
        }
    }

    public static string PlayersInfo()
    {
        var pl = Players();
        int[] n = OwnedCounts();
        var sb = new System.Text.StringBuilder(" jogadores=" + pl.Count);
        for (int i = 0; i < pl.Count; i++) sb.Append(" [" + PlayerName(pl[i]) + ": " + n[i] + " itens]");
        return sb.ToString();
    }

    public static string PlayerName(Network_Player p)
    {
        return p.IsLocalPlayer ? "eu" : "outro jogador";
    }

    // Logs items that appear / disappear (works on host and clients).
    static void ScanItems(ObjectSpawnerManager mgr, Raft raft)
    {
        seen.Clear();
        Scan(mgr.plankSpawner, raft);
        Scan(mgr.itemSpawner, raft);
        gone.Clear();
        foreach (var kv in known) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
        for (int i = 0; i < gone.Count; i++)
        {
            int id = gone[i];
            if (wasActive) LogV("item saiu da agua: " + known[id] + " (apanhado/rede/removido)");
            known.Remove(id);
            bornAt.Remove(id);
            atEdge.Remove(id);
            coast.Remove(id);
        }
    }

    static void Scan(ObjectSpawner spawner, Raft raft)
    {
        if (spawner == null || spawner.spawnedObjects == null) return;
        List<PickupItem_Networked> items = spawner.spawnedObjects;
        for (int i = 0; i < items.Count; i++)
        {
            PickupItem_Networked item = items[i];
            if (item == null) continue;
            int id = item.GetInstanceID();
            seen.Add(id);
            if (!known.ContainsKey(id))
            {
                string name = Clean(item.name);
                known[id] = name;
                bornAt[id] = Time.time;
                if (wasActive)
                    LogV("item novo no mar: " + name + " a " + item.transform.position.DistanceXZ(raft.transform.position).ToString("0") + " m");
            }
        }
    }

    // ---------- raft hull (real edge, from the placed foundation blocks) ----------
    public const float EdgeReach = 1.9f;   // an item this close (XZ) to a foundation block centre is at the edge
    public const float OffRaftDist = 5f;   // a player farther than this from every block is off the raft
    static readonly List<Vector3> hull = new List<Vector3>(); // local positions (raft space) of the lowest blocks
    static Vector3 hullCentre;
    static float hullRadius = 2f;
    static float nextHull;
    static Transform raftT;

    static void RefreshHull(Raft raft)
    {
        if (Time.unscaledTime < nextHull && raftT != null) return;
        nextHull = Time.unscaledTime + 1f;
        GameManager gm = SingletonGeneric<GameManager>.Singleton;
        raftT = gm != null && gm.raftTransform != null ? gm.raftTransform : raft.transform;
        hull.Clear();
        List<Block> blocks = BlockCreator.GetPlacedBlocks();
        if (blocks == null) return;
        float minY = float.MaxValue;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] == null) continue;
            float y = raftT.InverseTransformPoint(((Component)blocks[i]).transform.position).y;
            if (y < minY) minY = y;
        }
        Vector3 sum = Vector3.zero;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] == null) continue;
            Vector3 lp = raftT.InverseTransformPoint(((Component)blocks[i]).transform.position);
            if (lp.y > minY + 0.8f) continue; // only the floor that touches the water
            lp.y = 0f;
            hull.Add(lp);
            sum += lp;
        }
        if (hull.Count == 0) return;
        hullCentre = sum / hull.Count;
        float r = 0f;
        for (int i = 0; i < hull.Count; i++) r = Mathf.Max(r, (hull[i] - hullCentre).magnitude);
        hullRadius = r + 0.75f;
    }

    // XZ distance from a world point to the nearest foundation block.
    public static float HullDist(Vector3 world)
    {
        if (raftT == null || hull.Count == 0) return float.MaxValue;
        Vector3 lp = raftT.InverseTransformPoint(world); lp.y = 0f;
        float best = float.MaxValue;
        for (int i = 0; i < hull.Count; i++)
        {
            float dx = hull[i].x - lp.x, dz = hull[i].z - lp.z;
            float d = dx * dx + dz * dz;
            if (d < best) best = d;
        }
        return Mathf.Sqrt(best);
    }

    // ---------- current ----------
    // Stopped/anchored: one sea current moves every item in a straight line, like the raft
    // sailing through them. It comes from open water (SpawnDir), turns slowly (5 deg/s) when that
    // changes, items pass by and under the raft, nets catch them. Nothing is pulled to the raft.
    // Sailing: items do not move at all (as in the normal game, the raft moves).
    // Owner off the raft at an island: the items float to the island and wash up on its coast.
    public const float TurnRate = 5f;      // deg/s the current turns
    public const float PassedDist = 30f;   // items this far downstream of every player are recycled (host)
    static Vector3 flow = Vector3.forward;
    static bool flowInit;
    static readonly Dictionary<int, byte> coast = new Dictionary<int, byte>(); // 0 open water, 1 near land (slow), 2 on the coast (stop)

    public static Vector3 Flow { get { return flow; } }

    static void UpdateFlow(Raft raft, float dt)
    {
        Vector3 want = -SpawnDir(raft);
        if (!flowInit) { flow = want; flowInit = true; return; }
        flow = Vector3.RotateTowards(flow, want, TurnRate * Mathf.Deg2Rad * dt, 0f);
        flow.y = 0f;
        if (flow.sqrMagnitude < 0.01f) flow = want;
        flow.Normalize();
    }

    // 0.85..1.15 from the network index, so host and clients move each item the same way
    static float Factor(PickupItem_Networked it)
    {
        uint h = it.ObjectIndex * 2654435761u;
        return 0.85f + ((h >> 24) & 255u) / 255f * 0.3f;
    }

    static void Move(ObjectSpawner spawner, Raft raft, float dt)
    {
        if (spawner == null || spawner.spawnedObjects == null) return;
        List<PickupItem_Networked> items = spawner.spawnedObjects;
        bool sailing = !Stopped(raft);
        var pl = Players();

        for (int i = 0; i < items.Count; i++)
        {
            PickupItem_Networked item = items[i];
            if (item == null) continue;
            Transform t = item.transform;
            Vector3 p = t.position;
            int id = item.GetInstanceID();

            Network_Player owner = null;
            float od = float.MaxValue;
            for (int k = 0; k < pl.Count; k++) { float dd = pl[k].transform.position.DistanceXZ(p); if (dd < od) { od = dd; owner = pl[k]; } }
            bool ownerAtIsland = owner != null && island != null
                && HullDist(owner.transform.position) > OffRaftDist
                && owner.transform.position.DistanceXZ(island.transform.position) <= IslandRange;

            Vector3 dir;
            if (ownerAtIsland)
            {
                dir = island.transform.position - p; dir.y = 0f;
                float m = dir.magnitude;
                if (m < 1f) continue;
                dir /= m;
            }
            else if (sailing) { atEdge.Remove(id); coast.Remove(id); continue; }
            else dir = flow;

            // land ahead: checked every 6th frame per item (raycasts are not free)
            byte state;
            if (!coast.TryGetValue(id, out state) || (Time.frameCount + id) % 6 == 0)
            {
                state = IsOverLand(p, raft) || IsOverLand(p + dir * 1.5f, raft) ? (byte)2 : IsOverLand(p + dir * 4f, raft) ? (byte)1 : (byte)0;
                coast[id] = state;
            }
            if (state == 2)
            {
                if (atEdge.Add(id)) LogV("item deu a costa" + (island != null ? " da ilha " + Clean(island.name) : "") + ": " + Clean(item.name));
                continue;
            }
            atEdge.Remove(id);
            float f = Factor(item);
            float speed = driftSpeed * f * (state == 1 ? 0.5f : 1f);
            t.position = p + dir * speed * dt;
            t.Rotate(0f, (f - 1f) * 40f * dt, 0f, Space.World); // slow, uneven turn as it floats
        }
    }

    // ---------- commands ----------
    [ConsoleCommand("drift", "Speed items come to the raft while anchored. Syntax: drift <m/s> | drift off")]
    public static string SetDrift(string[] args)
    {
        if (args.Length < 1) return "Corrente " + (driftSpeed > 0f ? driftSpeed + " m/s" : "desligada") + ". Usa: drift <m/s> | drift off";
        string a = args[0].ToLower();
        if (a == "status") return Status();
        float v;
        if (a == "off") v = 0f;
        else if (!float.TryParse(a.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) || v < 0f || v > 10f)
            return "Valor invalido. Numero entre 0 e 10, ex.: drift 1.5";
        driftSpeed = v;
        PlayerPrefs.SetFloat(PrefDrift, v);
        PlayerPrefs.Save();
        Log("corrente mudada para " + (v > 0f ? v + " m/s" : "off"));
        return "Corrente " + (v > 0f ? v + " m/s" : "desligada") + ". (O outro jogador deve usar o mesmo valor.)";
    }

    [ConsoleCommand("drift.status", "Shows the Calm Fishing state (items, limit, weather, waves).")]
    public static string DriftStatus(string[] args) { return Status(); }

    [ConsoleCommand("drift.max", "Max floating items (anti-lag). Syntax: drift.max <5-300>")]
    public static string SetMax(string[] args)
    {
        int v;
        if (args.Length < 1) return "Limite " + maxItems + " itens. Usa: drift.max <5-300>";
        if (!int.TryParse(args[0], out v) || v < 5 || v > 300) return "Valor invalido. Usa 5 a 300, ex.: drift.max 80";
        maxItems = v;
        PlayerPrefs.SetInt(PrefMax, v);
        PlayerPrefs.Save();
        Log("limite de itens mudado para " + v);
        return "Limite " + v + " itens.";
    }

    [ConsoleCommand("drift.dist", "Items farther than this from every player despawn. Syntax: drift.dist <30-500>")]
    public static string SetDist(string[] args)
    {
        float v;
        if (args.Length < 1) return "Despawn a " + despawnDist + " m de todos os jogadores. Usa: drift.dist <30-500>";
        if (!float.TryParse(args[0], out v) || v < 30f || v > 500f) return "Valor invalido. Usa 30 a 500, ex.: drift.dist 100";
        despawnDist = v;
        PlayerPrefs.SetFloat(PrefDist, v);
        PlayerPrefs.Save();
        Log("distancia de despawn mudada para " + v + " m");
        return "Itens a mais de " + v + " m de todos os jogadores desaparecem.";
    }

    [ConsoleCommand("drift.rate", "Spawn rate multiplier (more = more items). Syntax: drift.rate <0.5-10>")]
    public static string SetRate(string[] args)
    {
        float v;
        if (args.Length < 1) return "Ritmo de spawn x" + spawnRate + ". Usa: drift.rate <0.5-10>";
        if (!float.TryParse(args[0].Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) || v < 0.5f || v > 10f) return "Valor invalido. Usa 0.5 a 10, ex.: drift.rate 2.5";
        spawnRate = v;
        PlayerPrefs.SetFloat(PrefRate, v);
        PlayerPrefs.Save();
        return "Ritmo de spawn x" + v + ".";
    }

    [ConsoleCommand("drift.spawn", "How far from the players new items appear. Syntax: drift.spawn <20-200>")]
    public static string SetSpawn(string[] args)
    {
        float v;
        if (args.Length < 1) return "Itens novos aparecem a ~" + spawnDist + " m. Usa: drift.spawn <20-200>";
        if (!float.TryParse(args[0], out v) || v < 20f || v > 200f) return "Valor invalido. Usa 20 a 200, ex.: drift.spawn 70";
        spawnDist = v;
        PlayerPrefs.SetFloat(PrefSpawn, v);
        PlayerPrefs.Save();
        string warn = v >= despawnDist ? " Aviso: maior que o despawn (" + despawnDist + " m), sobe o drift.dist." : "";
        return "Itens novos aparecem a ~" + v + " m." + warn;
    }

    [ConsoleCommand("drift.log", "Per-item logs on/off. Syntax: drift.log on | drift.log off")]
    public static string SetLog(string[] args)
    {
        if (args.Length < 1) return "Logs por item " + (verbose ? "on" : "off") + ". Usa: drift.log on | off";
        verbose = args[0].ToLower() != "off";
        PlayerPrefs.SetInt(PrefLog, verbose ? 1 : 0);
        PlayerPrefs.Save();
        return "Logs por item " + (verbose ? "on" : "off") + ".";
    }

    [ConsoleCommand("drift.criativo", "Test switch: run the mod in creative too (host). Syntax: drift.criativo on | off")]
    public static string SetCreative(string[] args)
    {
        if (args.Length < 1) return "Teste no criativo " + (creativeTest ? "on" : "off") + ". Usa: drift.criativo on | off";
        creativeTest = args[0].ToLower() != "off";
        PlayerPrefs.SetInt(PrefCreative, creativeTest ? 1 : 0);
        PlayerPrefs.Save();
        Log("teste no criativo " + (creativeTest ? "on" : "off"));
        return "Teste no criativo " + (creativeTest ? "on: os itens passam a aparecer no mar deste mundo." : "off.");
    }

    // ---------- fish schools ----------
    // Now and then (raft stopped) the host puts a school of fish in open water near a player. It
    // shows as fish splashing at the surface for a few minutes. Fishing inside it the fish bite
    // faster and sometimes a second fish comes up. The host sends the spot to everyone.
    public const string NetSlug = "calm-fishing";
    const string PrefSchools = "CalmFishing.Schools";
    public static bool schoolsOn = true;
    public const float SchoolRadius = 6f;
    public static Vector3 schoolPos;
    public static float schoolUntil;          // Time.time
    public static bool SchoolActive { get { return Time.time < schoolUntil; } }
    static float nextSchoolTry = -1f, nextSchoolSync, nextSplash;
    static GameObject schoolFx;
    static GameObject schoolPrefab;
    static bool prefabSearched;
    public static int schoolsMade, schoolFastBites, schoolExtraFish;

    public static bool InSchool(Vector3 p)
    {
        if (!SchoolActive) return false;
        float dx = p.x - schoolPos.x, dz = p.z - schoolPos.z;
        return dx * dx + dz * dz <= (SchoolRadius + 2f) * (SchoolRadius + 2f);
    }

    static void Schools(Raft raft, bool active)
    {
        if (SchoolActive) SchoolVisuals();
        else if (schoolFx != null) { Object.Destroy(schoolFx); schoolFx = null; }
        if (!Raft_Network.IsHost) return;
        if (!active || !schoolsOn) return;
        if (nextSchoolTry < 0f) nextSchoolTry = Time.time + Random.Range(90f, 180f);
        if (SchoolActive)
        {
            if (Time.time >= nextSchoolSync) { nextSchoolSync = Time.time + 15f; SendSchool(); }
            return;
        }
        if (Time.time < nextSchoolTry) return;
        nextSchoolTry = Time.time + 30f;            // retry soon if no good spot now
        if (!Stopped(raft)) return;                 // sailing: the raft leaves fish behind anyway
        var pl = Players();
        if (pl.Count == 0) return;
        Network_Player p = pl[Random.Range(0, pl.Count)];
        Vector3 spot;
        if (!SchoolSpot(p.transform.position, raft, out spot)) return;
        float dur = Random.Range(150f, 240f);
        StartSchool(spot, dur);
        nextSchoolTry = Time.time + dur + Random.Range(240f, 480f);
        Log("cardume perto de " + PlayerName(p) + " a " + p.transform.position.DistanceXZ(spot).ToString("0") + " m, durante " + Mathf.RoundToInt(dur) + " s");
        SendSchool();
    }

    static bool SchoolSpot(Vector3 near, Raft raft, out Vector3 spot)
    {
        for (int k = 0; k < 12; k++)
        {
            float a = Random.Range(0f, Mathf.PI * 2f);
            float d = Random.Range(12f, 24f);
            spot = new Vector3(near.x + Mathf.Cos(a) * d, 0f, near.z + Mathf.Sin(a) * d);
            if (IsOverLand(spot, raft)) continue;
            if (HullDist(spot) < SchoolRadius + 3f) continue;   // not under the raft
            return true;
        }
        spot = Vector3.zero;
        return false;
    }

    public static void StartSchool(Vector3 pos, float seconds)
    {
        bool isNew = !SchoolActive || (pos - schoolPos).sqrMagnitude > 1f;
        schoolPos = new Vector3(pos.x, 0f, pos.z);
        schoolUntil = Time.time + seconds;
        if (isNew)
        {
            schoolsMade++;
            if (schoolFx != null) { Object.Destroy(schoolFx); schoolFx = null; }
            if (!Raft_Network.IsHost) Log("cardume a " + DistToMe(schoolPos).ToString("0") + " m de ti, durante " + Mathf.RoundToInt(seconds) + " s");
        }
    }

    public static void EndSchool()
    {
        schoolUntil = 0f;
        if (schoolFx != null) { Object.Destroy(schoolFx); schoolFx = null; }
    }

    static float DistToMe(Vector3 p)
    {
        Network_Player me = ComponentManager<Network_Player>.Value;
        return me != null ? me.transform.position.DistanceXZ(p) : -1f;
    }

    static void SendSchool()
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        string msg = SchoolActive
            ? "school " + schoolPos.x.ToString("0.00", ci) + " " + schoolPos.z.ToString("0.00", ci) + " " + (schoolUntil - Time.time).ToString("0", ci)
            : "school off";
        try { if (self != null) self.SendNetworkMessage(msg, Target.Other, Steamworks.EP2PSend.k_EP2PSendReliable, NetSlug); }
        catch (System.Exception e) { LogOnce("envio do cardume falhou: " + e.Message); }
    }

    public override bool OnNetworkMessage(object message, Network_UserId from, string modslug)
    {
        if (modslug != NetSlug) return false;
        string s = message as string;
        if (s == null || !s.StartsWith("school")) return false;
        if (Raft_Network.IsHost) return true;
        string[] a = s.Split(' ');
        if (a.Length >= 2 && a[1] == "off") { EndSchool(); return true; }
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        float x, z, secs;
        if (a.Length >= 4 && float.TryParse(a[1], System.Globalization.NumberStyles.Float, ci, out x)
            && float.TryParse(a[2], System.Globalization.NumberStyles.Float, ci, out z)
            && float.TryParse(a[3], System.Globalization.NumberStyles.Float, ci, out secs))
            StartSchool(new Vector3(x, 0f, z), secs);
        return true;
    }

    // Fish splashing at the surface, and the game's own swimming-fish effect if it has one.
    static void SchoolVisuals()
    {
        float d = DistToMe(schoolPos);
        if (d < 0f || d > 150f) return;
        if (schoolFx == null)
        {
            if (!prefabSearched)
            {
                prefabSearched = true;
                foreach (FishSchool f in Resources.FindObjectsOfTypeAll<FishSchool>())
                    if (f != null) { schoolPrefab = f.gameObject; break; }
                Log(schoolPrefab != null ? "efeito de cardume do jogo: " + Clean(schoolPrefab.name) : "o jogo nao tem efeito de cardume carregado: so salpicos");
            }
            if (schoolPrefab != null)
            {
                try
                {
                    schoolFx = Object.Instantiate(schoolPrefab, schoolPos + Vector3.down * 0.6f, Quaternion.identity);
                    schoolFx.name = "BC_FishSchool";
                    schoolFx.SetActive(true);
                }
                catch (System.Exception e) { LogOnce("efeito de cardume falhou: " + e.Message); schoolPrefab = null; }
            }
        }
        try { SchoolGulls(); } catch (System.Exception e) { LogOnce("gaivotas falharam: " + e.Message); gullPrefab = null; gullSearched = true; }
        if (Time.time < nextSplash) return;
        nextSplash = Time.time + Random.Range(0.25f, 0.7f);
        Vector2 c = Random.insideUnitCircle * SchoolRadius;
        try { ParticleManager.PlaySystem("WaterSplash_Hook", new Vector3(schoolPos.x + c.x, 0.05f, schoolPos.z + c.y)); }
        catch (System.Exception e) { LogOnce("salpico do cardume falhou: " + e.Message); }
    }

    // ---------- seagulls circling over the school ----------
    // Copies of the game's seagull with all its game logic taken off (no AI, no network, no
    // pecking): just the model and its flying animation, circling over the school. Every player
    // makes their own, so they cost nothing on the network. They fly off when the school ends.
    const int GullCount = 3;
    static GameObject gullPrefab;
    static bool gullSearched;
    static float nextGullAt;
    static readonly List<BC_Gull> gulls = new List<BC_Gull>();

    static void SchoolGulls()
    {
        gulls.RemoveAll(x => x == null || x.leaving);
        if (gulls.Count >= GullCount || Time.time < nextGullAt) return;
        nextGullAt = Time.time + Random.Range(2f, 6f);   // they arrive one by one
        if (!gullSearched)
        {
            gullSearched = true;
            Network_Host_Entities he = ComponentManager<Network_Host_Entities>.Value;
            if (he != null && he.seagullPrefab != null) gullPrefab = he.seagullPrefab.gameObject;
            if (gullPrefab == null)
                foreach (Seagull sg in Resources.FindObjectsOfTypeAll<Seagull>())
                    if (sg != null) { gullPrefab = sg.gameObject; break; }
            Log(gullPrefab != null ? "gaivotas sobre os cardumes: modelo " + Clean(gullPrefab.name) : "sem modelo de gaivota: cardumes sem gaivotas");
        }
        if (gullPrefab == null) return;
        BC_Gull g = MakeGull(gulls.Count);
        if (g == null) { gullPrefab = null; return; }
        gulls.Add(g);
    }

    // Removes every script from the copy in dependency order: a script another one still needs
    // ([RequireComponent], e.g. Seagull -> Network_Entity) waits for the next pass, so Unity never
    // refuses a removal (that used to print "Can't remove ... because ... depends on it").
    static void StripScripts(GameObject go)
    {
        for (int pass = 0; pass < 12; pass++)
        {
            MonoBehaviour[] mbs = go.GetComponentsInChildren<MonoBehaviour>(true);
            if (mbs.Length == 0) return;
            bool removed = false;
            foreach (MonoBehaviour mb in mbs)
            {
                if (mb == null || Required(mb)) continue;
                Object.DestroyImmediate(mb);
                removed = true;
            }
            if (!removed)
            {
                // a dependency loop: switch the rest off instead of fighting Unity
                foreach (MonoBehaviour mb in mbs) if (mb != null) mb.enabled = false;
                return;
            }
        }
    }

    // true when another component on the same object declares [RequireComponent] for c's type
    static bool Required(Component c)
    {
        System.Type t = c.GetType();
        foreach (Component o in c.GetComponents<Component>())
        {
            if (o == null || o == c) continue;
            foreach (object a in o.GetType().GetCustomAttributes(typeof(RequireComponent), true))
            {
                RequireComponent rc = (RequireComponent)a;
                if ((rc.m_Type0 != null && rc.m_Type0.IsAssignableFrom(t)) ||
                    (rc.m_Type1 != null && rc.m_Type1.IsAssignableFrom(t)) ||
                    (rc.m_Type2 != null && rc.m_Type2.IsAssignableFrom(t))) return true;
            }
        }
        return false;
    }

    static BC_Gull MakeGull(int i)
    {
        GameObject holder = new GameObject("BC_GullHolder");
        holder.SetActive(false);                         // nothing on the copy wakes up
        GameObject go = null;
        try
        {
            go = Object.Instantiate(gullPrefab, holder.transform);
            StripScripts(go);
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) if (!Required(c)) Object.DestroyImmediate(c);
            foreach (Rigidbody rb in go.GetComponentsInChildren<Rigidbody>(true)) if (!Required(rb)) Object.DestroyImmediate(rb);
            Animator anim = go.GetComponentInChildren<Animator>(true);
            if (anim != null) anim.gameObject.AddComponent<BC_GullEvents>();
            BC_Gull g = go.AddComponent<BC_Gull>();
            g.anim = anim;
            g.index = i;
            go.name = "BC_Gull";
            go.transform.SetParent(null, false);
            go.SetActive(true);
            Object.Destroy(holder);
            if (gulls.Count == 0) LogV("gaivotas a caminho do cardume");
            return g;
        }
        catch (System.Exception e)
        {
            LogOnce("gaivota falhou: " + e.Message);
            if (go != null) Object.Destroy(go);
            Object.Destroy(holder);
            return null;
        }
    }

    public static void ClearGulls()
    {
        foreach (BC_Gull g in Object.FindObjectsOfType<BC_Gull>()) if (g != null) Object.Destroy(g.gameObject);
        gulls.Clear();
    }

    [ConsoleCommand("drift.storm", "Storms bring more floating items (host). Syntax: drift.storm <1-4>, 1 = like the normal game")]
    public static string CmdStorm(string[] args)
    {
        float v;
        if (args.Length < 1 || !float.TryParse(args[0].Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
            return "Tempestade: itens x" + stormFactor.ToString("0.#") + (Storming() ? " (agora ha tempestade)" : "") + ". Usa: drift.storm <1-4>";
        stormFactor = Mathf.Clamp(v, 1f, 4f);
        PlayerPrefs.SetFloat(PrefStorm, stormFactor);
        PlayerPrefs.Save();
        Log("tempestade: itens x" + stormFactor.ToString("0.#"));
        return "Tempestade: itens x" + stormFactor.ToString("0.#");
    }

    static readonly HashSet<string> said = new HashSet<string>();
    public static void LogOnce(string msg) { if (said.Add(msg)) Log(msg); }

    public static string SchoolStatus()
    {
        return " cardumes=" + (schoolsOn ? "on" : "off") + (SchoolActive ? " cardume_agora=a " + DistToMe(schoolPos).ToString("0") + "m, " + Mathf.RoundToInt(schoolUntil - Time.time) + "s" : " cardume_agora=nao")
            + " cardumes_feitos=" + schoolsMade + " mordidas_rapidas=" + schoolFastBites + " peixe_extra=" + schoolExtraFish;
    }

    [ConsoleCommand("cardume", "Fish schools. cardume = one now near you (host, test) | cardume on | cardume off")]
    public static string CmdSchool(string[] args)
    {
        if (args.Length >= 1 && (args[0] == "on" || args[0] == "off"))
        {
            schoolsOn = args[0] == "on";
            PlayerPrefs.SetInt(PrefSchools, schoolsOn ? 1 : 0);
            PlayerPrefs.Save();
            if (!schoolsOn && Raft_Network.IsHost) { EndSchool(); SendSchool(); }
            Log("cardumes " + args[0]);
            return "Cardumes " + args[0] + ".";
        }
        if (!Raft_Network.IsHost) return "So o anfitriao cria cardumes.";
        Network_Player me = ComponentManager<Network_Player>.Value;
        Raft raft = ComponentManager<Raft>.Value;
        if (me == null) return "Fora do jogo.";
        Vector3 spot;
        if (!SchoolSpot(me.transform.position, raft, out spot)) return "Nao ha mar aberto perto de ti.";
        StartSchool(spot, 180f);
        SendSchool();
        nextSchoolTry = Time.time + 180f + Random.Range(240f, 480f);
        Log("cardume de teste a " + me.transform.position.DistanceXZ(spot).ToString("0") + " m");
        return "Cardume a " + me.transform.position.DistanceXZ(spot).ToString("0") + " m de ti, durante 3 min.";
    }

    [ConsoleCommand("waves", "Calms the waves (host). Syntax: waves <0-100>, 0 = normal, 100 = flat")]
    public static string SetWaves(string[] args)
    {
        if (args.Length < 1) return "Ondas acalmadas " + Mathf.RoundToInt(calm * 100f) + "%. Usa: waves <0-100>";
        float v;
        if (!float.TryParse(args[0].Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) || v < 0f || v > 100f)
            return "Valor invalido. Usa 0 a 100, ex.: waves 70";
        calm = v / 100f;
        PlayerPrefs.SetFloat(PrefCalm, calm);
        PlayerPrefs.Save();
        lastRawWind = -1f; // log the new wave level
        Log("ondas acalmadas mudado para " + Mathf.RoundToInt(v) + "%");
        return "Ondas acalmadas " + Mathf.RoundToInt(v) + "%.";
    }
}

// The game turns the item spawners off while anchored. Keep the floating-item ones on.
[HarmonyPatch(typeof(ObjectSpawnerManager), "Update")]
static class Patch_KeepSpawnersWhileAnchored
{
    static void Postfix(ObjectSpawnerManager __instance)
    {
        if (!CalmFishing.DriftActive(__instance.raft)) return;
        if (__instance.plankSpawner != null) __instance.plankSpawner.enabled = true;
        if (__instance.itemSpawner != null) __instance.itemSpawner.enabled = true;
    }
}

// While drifting, the game's own "far from the raft" removal would delete the items that
// went to a player away from the raft; the mod removes by distance to all players instead.
[HarmonyPatch(typeof(ObjectSpawner_RaftDirection), "RemoveItemsOutsideRange")]
static class Patch_NoRaftRemoval
{
    static bool Prefix() { return !CalmFishing.DriftActive(ComponentManager<Raft>.Value); }
}

// While drifting the mod does the spawning itself (see CalmFishing.Drive).
[HarmonyPatch(typeof(ObjectSpawner_RaftDirection), "Update")]
static class Patch_GameSpawnerOff
{
    static bool Prefix() { return !CalmFishing.DriftActive(ComponentManager<Raft>.Value); }
}

// Item nets: log what they catch.
[HarmonyPatch(typeof(ItemCollector), "CollectItem")]
static class Patch_LogNet
{
    static void Postfix(PickupItem_Networked item, bool __result)
    {
        if (!__result || item == null) return;
        CalmFishing.caughtByNet++;
        CalmFishing.LogV("rede apanhou: " + item.name.Replace("(Clone)", "").Trim());
    }
}

// Calmer waves. Only the host changes them; the game sends the host's water to the others.
[HarmonyPatch(typeof(WaterRoughnessManager), "LerpWater")]
static class Patch_CalmWaves
{
    static readonly List<float> blended = new List<float>();

    static void Prefix(WaterRoughnessManager __instance, ref List<float> weights)
    {
        if (!Raft_Network.IsHost || weights == null) return;
        if (__instance.antiBobProfile == null || __instance.waterProfileBlend == null) return;
        var profiles = __instance.waterProfileBlend._Profiles;
        float raw = 0f;
        for (int i = 0; i < weights.Count && i < profiles.Count; i++) raw += profiles[i].Data.WindSpeed * weights[i];
        if (CalmFishing.calm <= 0f) { CalmFishing.OnWaveTarget(raw, raw); return; }
        int calmIdx = profiles.IndexOf(__instance.antiBobProfile);
        if (calmIdx < 0 || calmIdx >= weights.Count) return;
        float c = CalmFishing.calm;
        blended.Clear();
        for (int i = 0; i < weights.Count; i++) blended.Add(weights[i] * (1f - c));
        blended[calmIdx] += c;
        float calmed = 0f;
        for (int i = 0; i < blended.Count && i < profiles.Count; i++) calmed += profiles[i].Data.WindSpeed * blended[i];
        weights = blended;
        CalmFishing.OnWaveTarget(raw, calmed);
    }
}

// Fish school: the fish bite faster when the float is inside it.
[HarmonyPatch(typeof(Bobber), "StartBobbing")]
static class Patch_SchoolFastBite
{
    static readonly AccessTools.FieldRef<Bobber, Interval_Float> waitRef = AccessTools.FieldRefAccess<Bobber, Interval_Float>("waitTime");

    static void Prefix(Bobber __instance, out Vector2 __state)
    {
        __state = new Vector2(-1f, -1f);
        if (!CalmFishing.InSchool(__instance.transform.position)) return;
        Interval_Float w = waitRef(__instance);
        if (w == null) return;
        __state = new Vector2(w.minValue, w.maxValue);
        w.minValue *= 0.35f;
        w.maxValue *= 0.35f;
        CalmFishing.schoolFastBites++;
        CalmFishing.LogV("boia no cardume: o peixe morde mais depressa");
    }

    static void Postfix(Bobber __instance, Vector2 __state)
    {
        if (__state.x < 0f) return;
        Interval_Float w = waitRef(__instance);
        if (w == null) return;
        w.minValue = __state.x;
        w.maxValue = __state.y;
    }
}

// Fish school: sometimes a second fish comes up with the first.
[HarmonyPatch(typeof(FishingRod), "PullItemsFromSea")]
static class Patch_SchoolExtraFish
{
    static readonly AccessTools.FieldRef<FishingRod, Throwable> throwRef = AccessTools.FieldRefAccess<FishingRod, Throwable>("throwable");
    static readonly AccessTools.FieldRef<FishingRod, FishingBaitHandler> baitRef = AccessTools.FieldRefAccess<FishingRod, FishingBaitHandler>("fishingBaitHandler");
    static readonly AccessTools.FieldRef<FishingRod, bool> metalRef = AccessTools.FieldRefAccess<FishingRod, bool>("isMetalRod");
    static readonly AccessTools.FieldRef<FishingRod, Network_Player> playerRef = AccessTools.FieldRefAccess<FishingRod, Network_Player>("playerNetwork");

    static void Prefix(FishingRod __instance, out bool __state)
    {
        __state = false;
        Throwable t = throwRef(__instance);
        if (t == null || t.throwableObject == null) return;
        __state = CalmFishing.InSchool(t.throwableObject.position);
    }

    static void Postfix(FishingRod __instance, bool __state)
    {
        if (!__state || Random.value > 0.5f) return;
        FishingBaitHandler bait = baitRef(__instance);
        Network_Player p = playerRef(__instance);
        if (bait == null || p == null || !p.IsLocalPlayer) return;
        Item_Base extra = bait.GetRandomItemFromCurrentBaitPool(metalRef(__instance));
        if (extra == null) return;
        p.Inventory.AddItem(extra.UniqueName, 1);
        CalmFishing.schoolExtraFish++;
        CalmFishing.Log("cardume: veio mais um peixe (" + extra.UniqueName + ")");
    }
}

// Sailing in a storm: the game's own spawner runs faster too (anchored, CalmFishing.Drive does it).
[HarmonyPatch(typeof(ObjectSpawner_RaftDirection), "Update")]
static class Patch_StormSpawner
{
    static void Prefix(ObjectSpawner_RaftDirection __instance)
    {
        if (!Raft_Network.IsHost) return;
        float mul = CalmFishing.StormMul();
        if (mul <= 1f || CalmFishing.DriftActive(ComponentManager<Raft>.Value)) return;
        __instance.spawnTimer += Time.deltaTime * (mul - 1f);
    }
}

// The copied seagull's animation calls these; without a receiver Unity logs an error per flap.
public class BC_GullEvents : MonoBehaviour
{
    public void Animation_Flap() { }
    public void Animation_Peck() { }
}

// One seagull circling over the fish school, high up, with a slow rise and fall.
public class BC_Gull : MonoBehaviour
{
    public Animator anim;
    public int index;
    public bool leaving;
    float angle, radius, height, speed, leaveAt;
    Vector3 vel;

    void Start()
    {
        angle = index * 2.1f + Random.Range(0f, 1f);
        radius = Random.Range(5f, 9f);
        height = Random.Range(7f, 11f);
        speed = Random.Range(0.3f, 0.5f) * (Random.value < 0.5f ? 1f : -1f);
        Vector3 c = CalmFishing.schoolPos;
        Vector2 r = Random.insideUnitCircle.normalized * Random.Range(40f, 60f);
        transform.position = new Vector3(c.x + r.x, height + 10f, c.z + r.y);   // they come in from afar
        vel = (new Vector3(c.x, height, c.z) - transform.position).normalized * 6f;
        if (anim != null)
        {
            anim.SetBool("FlyingAltitude", true);
            anim.SetFloat("FlySpeed", Random.Range(0.3f, 0.8f));
        }
    }

    void Update()
    {
        float dt = Time.deltaTime;
        Vector3 want;
        if (!CalmFishing.SchoolActive)
        {
            if (!leaving) { leaving = true; leaveAt = Time.time + 12f; }
            Vector3 flat = vel; flat.y = 0f;
            if (flat.sqrMagnitude < 0.1f) flat = transform.forward;
            want = flat.normalized * 8f + Vector3.up * 2f;
            if (Time.time > leaveAt) { Destroy(gameObject); return; }
        }
        else
        {
            angle += speed * dt;
            Vector3 c = CalmFishing.schoolPos;
            float h = height + Mathf.Sin(Time.time * 0.6f + index * 1.7f);
            Vector3 target = new Vector3(c.x + Mathf.Cos(angle) * radius, h, c.z + Mathf.Sin(angle) * radius);
            want = Vector3.ClampMagnitude((target - transform.position) * 1.2f, 9f);
        }
        vel = Vector3.Lerp(vel, want, dt * 1.5f);
        transform.position += vel * dt;
        if (vel.sqrMagnitude > 0.05f)
        {
            float bank = leaving ? 0f : (speed > 0f ? -18f : 18f);
            Quaternion look = Quaternion.LookRotation(vel.normalized) * Quaternion.Euler(0f, 0f, bank);
            transform.rotation = Quaternion.Slerp(transform.rotation, look, dt * 3f);
        }
    }
}
