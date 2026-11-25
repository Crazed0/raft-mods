using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using HarmonyLib;
using HMLLibrary;
using RaftModLoader;

// Fish Fight v1.3.0: slack line. Right after the bite you have 1-3 s (random) to start reeling or the
// fish slips off; once the fight is on, letting the tension drop to nothing (outside a pull) lets the
// fish pick up speed and run, exponentially faster the longer the line stays slack (1.3.1).
// Fish Fight v1.2.0: fights last longer (slower reel) and big fish are a bit harder; the Skills
// mod (Pescador) eases them.
// Fish Fight v1.1.0 (new HUD in the game's own style: parchment panel, tension bar, the fish
// swimming along the line, a result card with the fish you caught; pesca demo shows it)
// Fish Fight v1.0.0: when a fish bites, catching it is a fight on a tight line instead of one click.
// - Hold the fire button to reel the fish in. Now and then it pulls: the float twitches first,
//   then it dives and runs. Keep reeling while it pulls and the line stretches; too far and it
//   snaps (the fish is gone with the bait). Let go while it pulls, reel while it rests.
// - The game picks the fish when it bites; rare fish pull harder, longer and more often.
// - A perfect fight (the line never got close to snapping) sometimes gives one more of that fish
//   or turns out to be a rarer one.
// - Everything is local to the player fishing (like the game's own fishing); the fish goes into
//   the inventory the normal way, so other mods that touch the catch still work.
// Console (F10): pesca (status) | pesca on | pesca off (back to the game's one click) | pesca demo
public class FishFight : Mod
{
    public const string Version = "1.4.0";
    public const string HarmonyId = "com.bestcode.fishfight";
    const string PrefOn = "FishFight.On";

    public static bool fightOn = true;
    public static Fight fight;
    public static Item_Base overrideItem;                 // the fish picked at the bite, handed to the game's catch
    public static int fights, caught, snapped, escaped, perfect, bonusExtra, bonusRarer;
    Harmony harmony;
    static readonly HashSet<string> said = new HashSet<string>();

    public void Start()
    {
        fightOn = PlayerPrefs.GetInt(PrefOn, 1) == 1;
        harmony = new Harmony(HarmonyId);
        int patched = 0;
        var broken = new List<string>();
        foreach (Type t in typeof(FishFight).Assembly.GetTypes())
        {
            if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            try { harmony.CreateClassProcessor(t).Patch(); patched++; }
            catch (Exception e) { broken.Add(t.Name); Debug.LogError("[FishFight] patch " + t.Name + " falhou: " + (e.InnerException ?? e).Message); }
        }
        Log("carregado v" + Version + ", patches " + patched + "/" + (patched + broken.Count) + (broken.Count > 0 ? " (FALHARAM: " + string.Join(", ", broken.ToArray()) + ")" : "") + ", pesca com luta " + (fightOn ? "ligada" : "desligada"));
    }

    public void OnModUnload()
    {
        if (fight != null && fight.rod != null) { try { fight.rod.ResetRod(); } catch { } }
        fight = null;
        overrideItem = null;
        Patch_FightTime.RestoreAll();
        if (harmony != null) harmony.UnpatchAll(HarmonyId);
        BCUI_FF.FishHud.Kill();
        Log("descarregado");
    }

    public static void Log(string m) { Debug.Log("[FishFight] " + m); }
    public static void LogOnce(string m) { if (said.Add(m)) Log(m); }

    // the card that pops up when a fight ends
    public static void Result(Item_Base fish, bool good, string title, string line1, string line2, bool two, bool perf)
    {
        try { BCUI_FF.FishHud.Get().ShowResult(fish, good, title, line1, line2, two, perf); }
        catch (Exception e) { LogOnce("painel falhou: " + e.Message); }
    }

    public static string Name(Item_Base it)
    {
        if (it == null) return "nada";
        try { if (it.settings_Inventory != null && !string.IsNullOrEmpty(it.settings_Inventory.DisplayName)) return it.settings_Inventory.DisplayName; } catch { }
        return it.UniqueName;
    }

    public static Sprite Icon(Item_Base it)
    {
        try { return it != null && it.settings_Inventory != null ? it.settings_Inventory.Sprite : null; } catch { return null; }
    }

    // ---------- how rare each catch is ----------
    // The game picks from weighted pools (one per bait, plus the plain and metal rod pools). We
    // sample each pool once and keep the share each item got; that share is how rare it is.
    static readonly Dictionary<string, Dictionary<int, float>> pools = new Dictionary<string, Dictionary<int, float>>();
    const int Samples = 400;

    static string PoolKey(FishingRod rod)
    {
        Item_Base bait = rod.fishingBaitHandler != null ? rod.fishingBaitHandler.CurrentBait : null;
        return (bait != null ? bait.UniqueIndex.ToString() : "none") + (rod.isMetalRod ? "/metal" : "");
    }

    public static Item_Base Roll(FishingRod rod)
    {
        Item_Base keep = overrideItem;
        overrideItem = null;
        try { return rod.fishingBaitHandler.GetRandomItemFromCurrentBaitPool(rod.isMetalRod); }
        finally { overrideItem = keep; }
    }

    public static float Chance(FishingRod rod, Item_Base it)
    {
        if (it == null) return 1f;
        string key = PoolKey(rod);
        Dictionary<int, float> pool;
        if (!pools.TryGetValue(key, out pool))
        {
            pool = new Dictionary<int, float>();
            for (int i = 0; i < Samples; i++)
            {
                Item_Base x = Roll(rod);
                if (x == null) continue;
                float c;
                pool.TryGetValue(x.UniqueIndex, out c);
                pool[x.UniqueIndex] = c + 1f / Samples;
            }
            pools[key] = pool;
            var parts = new List<string>();
            foreach (KeyValuePair<int, float> kv in pool)
            {
                Item_Base x = ItemManager.GetItemByIndex(kv.Key);
                parts.Add((x != null ? x.UniqueName : kv.Key.ToString()) + " " + Mathf.RoundToInt(kv.Value * 100f) + "%");
            }
            Log("isco " + key + ": " + string.Join(", ", parts.ToArray()));
        }
        float ch;
        return pool.TryGetValue(it.UniqueIndex, out ch) ? ch : 0.5f / Samples;
    }

    // ---------- night fishing ----------
    // between 20:00 and 05:00 other fish come up: rarer ones bite more and every fish fights harder
    public static bool IsNight()
    {
        try
        {
            const System.Reflection.BindingFlags bf = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            object sky = typeof(BedManager).GetField("skyController", bf).GetValue(null);
            if (sky == null) return false;
            object tod = sky.GetType().GetField("timeOfDay").GetValue(sky);
            float h = (float)tod.GetType().GetField("hour").GetValue(tod);
            return h >= 20f || h < 5f;
        }
        catch { return false; }
    }

    // 0 = common, easy fish; 1 = rare, strong fish
    public static float Difficulty(float chance) { return Mathf.Clamp01(Mathf.InverseLerp(0.35f, 0.03f, chance)); }

    // ---------- the fight ----------
    public enum Phase { Rest, Warn, Pull }

    public class Fight
    {
        public FishingRod rod;
        public Item_Base fish;
        public float diff;
        public Phase phase = Phase.Rest;
        public float phaseUntil, nextSplash;
        public float dist, startDist, maxDist;
        public float tension, maxTension;
        public float waterY, lat, latTarget;
        public Vector3 dir;
        public bool reeling, engaged, night;
        public float graceUntil, slack;

        public string Size { get { return diff < 0.34f ? "pequeno" : diff < 0.67f ? "médio" : "grande"; } }
        float Gap() { return Mathf.Lerp(3.2f, 1.8f, diff) * UnityEngine.Random.Range(0.7f, 1.3f); }
        float PullTime() { return Mathf.Lerp(0.9f, 1.5f, diff) * UnityEngine.Random.Range(0.75f, 1.3f); }
        float Rise { get { return Mathf.Lerp(0.7f, 1.35f, diff); } }      // tension per second when reeling into a pull
        float Take { get { return Mathf.Lerp(1.0f, 1.6f, diff); } }       // metres per second the fish takes while pulling
        float Reel { get { return Mathf.Lerp(1.55f, 1.4f, diff); } }      // metres per second you reel while it rests (net per cycle: small ~4.8 m, big ~0.75 m)
        public const float WinDist = 2f;

        public static Fight Begin(FishingRod rod)
        {
            Fight f = new Fight();
            f.rod = rod;
            f.fish = Roll(rod);
            if (f.fish == null) return null;                          // nothing to fight: the game's click
            f.night = IsNight();
            if (f.night && UnityEngine.Random.value < 0.45f)
            {
                // at night the rare ones come up: a few more rolls, keep the rarest
                float best = Chance(rod, f.fish);
                for (int i = 0; i < 3; i++)
                {
                    Item_Base x = Roll(rod);
                    float c = Chance(rod, x);
                    if (x != null && c < best) { best = c; f.fish = x; }
                }
            }
            f.diff = Difficulty(Chance(rod, f.fish));
            if (f.night) f.diff = Mathf.Clamp01(f.diff + 0.2f);   // and everything pulls harder in the dark
            Vector3 me = rod.personController.transform.position;
            Vector3 b = rod.bobber.transform.position;
            Vector3 flat = b - me; flat.y = 0f;
            f.startDist = Mathf.Max(5f, flat.magnitude);
            f.dist = f.startDist;
            f.maxDist = Mathf.Min(f.startDist + 10f, 38f);
            f.dir = flat.sqrMagnitude > 0.01f ? flat.normalized : rod.personController.transform.forward;
            f.waterY = b.y;
            f.phase = Phase.Rest;
            f.phaseUntil = Time.time + UnityEngine.Random.Range(0.7f, 1.4f);   // it fights right after the bite
            f.graceUntil = Time.time + UnityEngine.Random.Range(1f, 3f);       // start reeling before this or it slips off
            try { rod.canvas.displayTextManager.HideDisplayTexts(); } catch { }
            try { BCUI_FF.FishHud.Get(); } catch (Exception e) { LogOnce("painel falhou: " + e.Message); }
            fights++;
            LogOnce("primeira luta: " + f.fish.UniqueName + " (" + f.Size + ")");
            return f;
        }

        // returns false when the fight is over
        public bool Tick(bool holding)
        {
            float dt = Time.deltaTime;
            if (Time.time >= phaseUntil)
            {
                if (phase == Phase.Rest) { phase = Phase.Warn; phaseUntil = Time.time + 0.45f; }
                else if (phase == Phase.Warn)
                {
                    phase = Phase.Pull;
                    phaseUntil = Time.time + PullTime();
                    latTarget = UnityEngine.Random.Range(1f, 2.5f) * (0.6f + diff) * (UnityEngine.Random.value < 0.5f ? -1f : 1f);
                    Splash();
                }
                else { phase = Phase.Rest; phaseUntil = Time.time + Gap(); latTarget *= 0.3f; }
            }
            reeling = holding;
            if (holding) engaged = true;
            if (!engaged && Time.time >= graceUntil) { End(false, "FUGIU", "Demoraste a puxar e soltou-se."); escaped++; return false; }
            if (phase == Phase.Pull)
            {
                if (holding) { tension += Rise * dt; dist -= 0.4f * dt; }
                else { tension = Mathf.MoveTowards(tension, 0f, 0.5f * dt); dist += Take * dt; }
                if (Time.time >= nextSplash) Splash();
            }
            else
            {
                if (holding) { dist -= Reel * dt; tension = Mathf.MoveTowards(tension, 0.3f, 0.8f * dt); }
                else { tension = Mathf.MoveTowards(tension, 0f, 0.6f * dt); dist += 0.15f * dt; }
            }
            tension = Mathf.Clamp(tension, 0f, 1f);
            // slack line: once the fight is on, no tension outside a pull lets the fish run, faster and faster
            if (engaged && phase != Phase.Pull && tension < 0.04f) { slack += dt; dist += 1.2f * Mathf.Exp(1.7f * slack) * dt; }
            else slack = Mathf.MoveTowards(slack, 0f, dt * 3f);
            if (tension > maxTension) maxTension = tension;
            lat = Mathf.Lerp(lat, latTarget, dt * 1.5f);
            PlaceBobber();

            if (tension >= 1f) { End(false, "A LINHA PARTIU", "O peixe levou o isco."); snapped++; return false; }
            if (dist >= maxDist) { End(false, "FUGIU", "Levou a linha toda e soltou-se."); escaped++; return false; }
            if (dist <= WinDist) { Catch(); return false; }
            return true;
        }

        void Splash()
        {
            nextSplash = Time.time + UnityEngine.Random.Range(0.35f, 0.6f);
            try { ParticleManager.PlaySystem("WaterSplash_Hook", rod.bobber.transform.position + Vector3.up * 0.1f); } catch { }
        }

        void PlaceBobber()
        {
            Vector3 me = rod.personController.transform.position;
            Vector3 right = Vector3.Cross(Vector3.up, dir);
            float t = Time.time;
            float dip = phase == Phase.Pull ? 0.3f + 0.08f * Mathf.Sin(t * 22f)
                      : phase == Phase.Warn ? 0.14f * Mathf.Abs(Mathf.Sin(t * 28f))
                      : 0.03f * Mathf.Sin(t * 3f);
            Vector3 p = me + dir * dist + right * lat * Mathf.Clamp01(dist / 4f);
            p.y = waterY - dip;
            rod.bobber.transform.position = p;
        }

        void Catch()
        {
            bool perf = maxTension < 0.75f;
            string extraMsg = "";
            Item_Base give = fish;
            bool extra = false;
            if (perf)
            {
                perfect++;
                float r = UnityEngine.Random.value;
                if (r < 0.3f)
                {
                    float best = Chance(rod, fish);
                    Item_Base rarer = null;
                    for (int i = 0; i < 4; i++)
                    {
                        Item_Base x = Roll(rod);
                        float c = Chance(rod, x);
                        if (x != null && c < best) { best = c; rarer = x; }
                    }
                    if (rarer != null) { give = rarer; bonusRarer++; extraMsg = "Afinal era mais raro do que parecia!"; }
                    else extra = true;
                }
                else if (r < 0.7f) extra = true;
                if (extra) { bonusExtra++; extraMsg = "Veio mais um!"; }
            }
            overrideItem = give;
            try { rod.PullItemsFromSea(); }
            finally { overrideItem = null; }
            if (extra && rod.playerNetwork != null) rod.playerNetwork.Inventory.AddItem(give.UniqueName, 1);
            caught++;
            if (night && extraMsg == "") extraMsg = "Pesca noturna.";
            Result(give, true, perf ? "PESCA PERFEITA!" : (night ? "APANHASTE DE NOITE!" : "APANHASTE!"), Name(give), extraMsg, extra, perf);
            Log("apanhado " + give.UniqueName + " (" + Size + ", tensao max " + Mathf.RoundToInt(maxTension * 100f) + "%)" + (extra ? " +1" : "") + (give != fish ? " (era " + fish.UniqueName + ")" : ""));
        }

        public void End(bool quiet, string title, string sub)
        {
            try
            {
                rod.playerAnimator.SetAnimation(PlayerAnimation.Trigger_FishingRetract);
                rod.rodAnimator.SetTrigger("FishingRetract");
            }
            catch { }
            try { rod.OnFishEscaped(); } catch (Exception e) { LogOnce("soltar o peixe falhou: " + e.Message); }
            if (!quiet) Result(fish, false, title, sub, "", false, false);
        }
    }

    // ---------- console ----------
    [ConsoleCommand("pesca", "Fish Fight: status, or turn it on/off, or show the HUD. Syntax: pesca [on|off|demo]")]
    public static string CmdPesca(string[] args)
    {
        if (args.Length > 0 && args[0] == "demo")
        {
            Item_Base it = null;
            foreach (string n in new string[] { "Raw_Pomfret", "Raw_Mackerel", "Raw_Herring", "Raw_Tilapia", "Raw_Salmon" })
            {
                it = ItemManager.GetItemByName(n);
                if (it != null) break;
            }
            if (it == null) return "nao encontrei um peixe para a demo";
            BCUI_FF.FishHud.Get().StartDemo(it);
            return "demo do painel da pesca: uma luta a fingir e depois os cartoes do resultado";
        }
        if (args.Length > 0 && (args[0] == "on" || args[0] == "off"))
        {
            fightOn = args[0] == "on";
            PlayerPrefs.SetInt(PrefOn, fightOn ? 1 : 0);
            PlayerPrefs.Save();
            if (!fightOn && fight != null) { fight.End(true, "", ""); fight = null; }
            Log("pesca com luta " + (fightOn ? "ligada" : "desligada (clique simples do jogo)"));
        }
        string s = "pesca_com_luta=" + (fightOn ? "on" : "off") + " lutas=" + fights + " apanhados=" + caught + " linha_partiu=" + snapped
            + " fugiram=" + escaped + " perfeitas=" + perfect + " peixe_a_mais=" + bonusExtra + " mais_raro=" + bonusRarer
            + (fight != null ? " agora=" + fight.fish.UniqueName + " (" + fight.Size + ") tensao=" + Mathf.RoundToInt(fight.tension * 100f) + "% falta=" + fight.dist.ToString("0.0") + "m" : "");
        Log("estado: " + s);
        return s;
    }
}

// The fight replaces the game's "click to pull" while a fish is on the hook.
[HarmonyPatch(typeof(FishingRod), "Update")]
static class Patch_RodFight
{
    static bool Prefix(FishingRod __instance)
    {
        try
        {
            FishFight.Fight f = FishFight.fight;
            if (f != null && f.rod != __instance) return true;
            if (!FishFight.fightOn || __instance.playerNetwork == null || !__instance.playerNetwork.IsLocalPlayer) return true;
            if (f == null)
            {
                if (!__instance.bobber.FishIsOnHook || !__instance.throwable.InWater) return true;
                f = FishFight.Fight.Begin(__instance);
                if (f == null) return true;
                FishFight.fight = f;
            }
            PersonController pc = __instance.personController;
            if (!__instance.bobber.FishIsOnHook || !__instance.throwable.InWater)
            {
                FishFight.fight = null;   // the game ended it on its own
                return true;
            }
            if (pc.SubmersionState != UltimateWater.SubmersionState.None || pc.controllerType == ControllerType.Water)
            {
                f.End(false, "CAÍSTE À ÁGUA", "O peixe soltou-se.");
                FishFight.escaped++;
                FishFight.fight = null;
                return false;
            }
            InputAction fire = PlayerInput.GetPlayerByIndex(0).actions["Fire"];
            bool holding = CanvasHelper.ActiveMenu == MenuType.None && SimpleMonoBehaviourSingleton<CustomInputConfig>.Instance.IsPressed(fire, "LMB");
            if (!f.Tick(holding)) FishFight.fight = null;
            __instance.SetRopePositionAndLayer();
            return false;
        }
        catch (Exception e)
        {
            FishFight.LogOnce("luta falhou, volta ao clique do jogo: " + e.Message);
            FishFight.fight = null;
            return true;
        }
    }
}

// Putting the rod away mid-fight lets the fish go quietly.
[HarmonyPatch(typeof(FishingRod), "OnDeSelect")]
static class Patch_RodPutAway
{
    static void Prefix(FishingRod __instance)
    {
        if (FishFight.fight == null || FishFight.fight.rod != __instance) return;
        FishFight.fight = null;
        try { __instance.bobber.StopBobbing(); } catch { }
    }
}

// The game lets a fish go after a few seconds; in a fight the fight decides when it ends.
[HarmonyPatch(typeof(Bobber), "StartBobbing")]
static class Patch_FightTime
{
    static readonly Dictionary<Bobber, Vector2> original = new Dictionary<Bobber, Vector2>();

    static void Prefix(Bobber __instance)
    {
        Interval_Float t = __instance.fishFightTime;
        if (t == null) return;
        if (!original.ContainsKey(__instance)) original[__instance] = new Vector2(t.minValue, t.maxValue);
        Vector2 o = original[__instance];
        if (FishFight.fightOn) { t.minValue = 600f; t.maxValue = 600f; }
        else { t.minValue = o.x; t.maxValue = o.y; }
    }

    public static void RestoreAll()
    {
        foreach (KeyValuePair<Bobber, Vector2> kv in original)
            if (kv.Key != null && kv.Key.fishFightTime != null) { kv.Key.fishFightTime.minValue = kv.Value.x; kv.Key.fishFightTime.maxValue = kv.Value.y; }
        original.Clear();
    }
}

// In a fight the fight moves the float; the game's random drift would fight it.
[HarmonyPatch(typeof(Bobber), "Flee")]
static class Patch_NoDrift
{
    static bool Prefix(ref IEnumerator __result)
    {
        if (!FishFight.fightOn) return true;
        __result = Nothing();
        return false;
    }
    static IEnumerator Nothing() { yield break; }
}

// The catch is the fish picked when it bit (or the rarer one after a perfect fight).
[HarmonyPatch(typeof(FishingBaitHandler), "GetRandomItemFromCurrentBaitPool")]
static class Patch_TheFish
{
    static bool Prefix(ref Item_Base __result)
    {
        if (FishFight.overrideItem == null) return true;
        __result = FishFight.overrideItem;
        FishFight.overrideItem = null;
        return false;
    }
}

// ---------- BestCode UI kit ----------
// Drawn with Raft's own font (ChineseRocks) and sprites (parchment plate, item slot, key chip), on
// one shared overlay canvas. Every BestCode mod carries its own copy of this kit in its own
// namespace; mods only share the canvas object and the toast column (plain Unity components and
// RectTransforms), so one mod reloading never breaks another. Source: raft-mods/src/bcui_kit.cs.txt
namespace BCUI_FF
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

// ---------- the fight HUD ----------
// Bottom centre, above the game's prompt line. A parchment panel with the fish size, the line
// tension (red end = it snaps, the light tick = the limit for a perfect fight) and the line with
// the fish swimming along it (left end = it gets away, right end = in your hands). Above it what
// to do now, below it the key. When the fight ends, a card with the fish takes its place.
namespace BCUI_FF
{
    using UnityEngine;
    using UnityEngine.UI;

    public class FishHud : MonoBehaviour
    {
        const float PanelY = 292f, W = 560f, H = 156f, BarX = 142f, BarW = W - BarX - 28f, RopeW = BarW - 78f, CardH = 138f;
        static readonly Color Silhouette = new Color(0.17f, 0.1f, 0.05f, 0.92f);
        static readonly Color GreenT = new Color(0.45f, 0.62f, 0.25f), AmberT = new Color(0.86f, 0.6f, 0.15f), RedT = new Color(0.78f, 0.2f, 0.12f);

        static FishHud inst;
        public static FishHud Get()
        {
            if (inst != null) return inst;
            RectTransform rt = Kit.Node(Kit.Root(), "BC_FishFight");
            Kit.Stretch(rt, 0f, 0f, 0f, 0f);
            inst = rt.gameObject.AddComponent<FishHud>();
            inst.Build();
            return inst;
        }
        public static void Kill()
        {
            foreach (FishHud h in Object.FindObjectsOfType<FishHud>()) if (h != null) Object.Destroy(h.gameObject);
            inst = null;
        }

        RectTransform panel, fillRt, rope, fishRt, card, iconRt, badge;
        CanvasGroup panelGroup, cardGroup;
        Text sizeTxt, status, distTxt, cTitle, cLine1, cLine2;
        Image[] dots;
        Image fill, danger, tick, fishImg, wake, endAway, endHere, icon, glow;
        float shown, punch, shownTension, fishX, cardBorn = -100f, cardLife;
        string lastStatus = "";
        FishFight.Fight lastFight;
        Item_Base lastFish;
        Color iconTo;
        bool glowOn;

        void Build()
        {
            RectTransform root = (RectTransform)transform;
            Sprite circle = Kit.Get("CircleTexture"), rsq = Kit.Get("RoundedSquare_5px");

            panel = Kit.Node(root, "Panel");
            Kit.Place(panel, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, PanelY), new Vector2(W, H));
            panelGroup = panel.gameObject.AddComponent<CanvasGroup>();
            panelGroup.alpha = 0f; panelGroup.interactable = false; panelGroup.blocksRaycasts = false;
            Kit.Plate(panel);

            // what to do now, big, above the panel
            status = Kit.Txt(panel, "Status", "", 54, Kit.Cream, TextAnchor.MiddleCenter, true);
            Kit.Place(status.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 0f), new Vector2(0f, 6f), new Vector2(760f, 66f));
            status.gameObject.SetActive(false);   // the user wants no word above the panel

            // size of the fish + three dots
            sizeTxt = Kit.Txt(panel, "Size", "PEIXE", 30, Kit.Brown, TextAnchor.MiddleLeft, false);
            Kit.Place(sizeTxt.rectTransform, Kit.TL, Kit.TL, new Vector2(28f, -12f), new Vector2(360f, 36f));
            dots = new Image[3];
            for (int i = 0; i < 3; i++)
            {
                dots[i] = Kit.Img(panel, "Dot" + i, circle, Kit.Brown, false);
                Kit.Place(dots[i].rectTransform, Kit.TR, Kit.C, new Vector2(-80f + i * 22f, -30f), new Vector2(14f, 14f));
            }

            // tension
            Text lt = Kit.Txt(panel, "TensionLabel", "TENSÃO", 22, Kit.A(Kit.Brown, 0.8f), TextAnchor.MiddleLeft, false);
            Kit.Place(lt.rectTransform, Kit.TL, Kit.ML, new Vector2(28f, -76f), new Vector2(110f, 28f));
            RectTransform track = Kit.Img(panel, "Track", rsq, new Color(0.22f, 0.13f, 0.06f, 0.88f), true).rectTransform;
            Kit.Place(track, Kit.TL, Kit.ML, new Vector2(BarX, -76f), new Vector2(BarW, 22f));
            danger = Kit.Img(track, "Danger", rsq, new Color(0.75f, 0.15f, 0.08f, 0.5f), true);
            danger.rectTransform.anchorMin = new Vector2(0.8f, 0f); danger.rectTransform.anchorMax = Vector2.one;
            danger.rectTransform.offsetMin = new Vector2(0f, 3f); danger.rectTransform.offsetMax = new Vector2(-3f, -3f);
            fill = Kit.Img(track, "Fill", rsq, GreenT, true);
            fillRt = fill.rectTransform;
            fillRt.anchorMin = Vector2.zero; fillRt.anchorMax = new Vector2(0f, 1f); fillRt.pivot = Kit.ML;
            fillRt.anchoredPosition = new Vector2(3f, 0f); fillRt.sizeDelta = new Vector2(0f, -6f);
            Image shine = Kit.Img(fillRt, "Shine", rsq, new Color(1f, 1f, 1f, 0.22f), true);
            shine.rectTransform.anchorMin = new Vector2(0f, 0.55f); shine.rectTransform.anchorMax = Vector2.one;
            shine.rectTransform.offsetMin = new Vector2(2f, 0f); shine.rectTransform.offsetMax = new Vector2(-2f, -2f);
            tick = Kit.Img(track, "PerfectLimit", null, Kit.Cream, false);
            Kit.Place(tick.rectTransform, new Vector2(0.75f, 0.5f), Kit.C, Vector2.zero, new Vector2(3f, 30f));
            Kit.AddShadow(tick.gameObject);

            // the line, the fish on it and how much is left
            Text lr = Kit.Txt(panel, "LineLabel", "LINHA", 22, Kit.A(Kit.Brown, 0.8f), TextAnchor.MiddleLeft, false);
            Kit.Place(lr.rectTransform, Kit.TL, Kit.ML, new Vector2(28f, -116f), new Vector2(110f, 28f));
            Sprite ropeS = Kit.Get("Notebook_Divider");
            rope = Kit.Img(panel, "Line", ropeS, Kit.A(Kit.Brown, Kit.IsReal(ropeS) ? 0.95f : 0.6f), false).rectTransform;
            Kit.Place(rope, Kit.TL, Kit.ML, new Vector2(BarX, -116f), new Vector2(RopeW, Kit.IsReal(ropeS) ? 8f : 3f));
            endAway = Kit.Img(rope, "Away", circle, Kit.Bad, false);
            Kit.Place(endAway.rectTransform, Kit.ML, Kit.C, Vector2.zero, new Vector2(12f, 12f));
            endHere = Kit.Img(rope, "Here", circle, Kit.Good, false);
            Kit.Place(endHere.rectTransform, Kit.MR, Kit.C, Vector2.zero, new Vector2(12f, 12f));
            wake = Kit.Img(rope, "Wake", Kit.Get("costblur"), Kit.A(Kit.Brown, 0.3f), false);
            Kit.Place(wake.rectTransform, new Vector2(0.5f, 0.5f), Kit.C, new Vector2(0f, -2f), new Vector2(76f, 24f));
            fishImg = Kit.Img(rope, "Fish", null, Silhouette, false);
            fishImg.preserveAspect = true;
            fishRt = fishImg.rectTransform;
            Kit.Place(fishRt, new Vector2(0.5f, 0.5f), Kit.C, new Vector2(0f, 4f), new Vector2(42f, 42f));
            distTxt = Kit.Txt(panel, "Left", "", 26, Kit.Brown, TextAnchor.MiddleRight, false);
            Kit.Place(distTxt.rectTransform, Kit.TR, Kit.MR, new Vector2(-28f, -116f), new Vector2(72f, 32f));

            // the key, below the panel, like the game's own prompts
            RectTransform hint = Kit.Node(panel, "Hint");
            RectTransform chip = Kit.Chip(hint, "CLIQUE", 34f);
            Text ht = Kit.Txt(hint, "Text", "segura para enrolar, larga quando ele puxar", 24, Kit.Cream, TextAnchor.MiddleLeft, true);
            float cw = chip.sizeDelta.x, tw = ht.preferredWidth + 6f;
            Kit.Place(hint, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0f, -8f), new Vector2(cw + 12f + tw, 36f));
            Kit.Place(chip, Kit.ML, Kit.ML, Vector2.zero, chip.sizeDelta);
            Kit.Place(ht.rectTransform, Kit.ML, Kit.ML, new Vector2(cw + 12f, 0f), new Vector2(tw, 36f));

            // the result card
            card = Kit.Node(root, "Result");
            Kit.Place(card, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, PanelY), new Vector2(460f, CardH));
            cardGroup = card.gameObject.AddComponent<CanvasGroup>();
            cardGroup.alpha = 0f; cardGroup.interactable = false; cardGroup.blocksRaycasts = false;
            Kit.Plate(card);
            glow = Kit.Img(card, "Glow", Kit.Get("costblur"), new Color(1f, 0.85f, 0.4f, 0f), false);
            Kit.Place(glow.rectTransform, Kit.ML, Kit.C, new Vector2(68f, 6f), new Vector2(170f, 150f));
            Sprite slotS = Kit.Get("ItemBG");
            Image slot = Kit.Img(card, "Slot", slotS, Kit.IsReal(slotS) ? Color.white : Kit.A(Kit.Brown, 0.35f), false);
            Kit.Place(slot.rectTransform, Kit.ML, Kit.ML, new Vector2(22f, 6f), new Vector2(92f, 94f));
            icon = Kit.Img(slot.rectTransform, "Icon", null, Color.white, false);
            icon.preserveAspect = true;
            iconRt = icon.rectTransform;
            Kit.Place(iconRt, Kit.C, Kit.C, new Vector2(0f, 3f), new Vector2(68f, 68f));
            Image b = Kit.Img(slot.rectTransform, "Badge", circle, Kit.Brown, false);
            badge = b.rectTransform;
            Kit.Place(badge, Kit.TR, Kit.C, new Vector2(-8f, -8f), new Vector2(36f, 36f));
            Text bt = Kit.Txt(badge, "Label", "x2", 20, Kit.Cream, TextAnchor.MiddleCenter, false);
            Kit.Stretch(bt.rectTransform, 0f, 1f, 0f, 0f);
            cTitle = Kit.Txt(card, "Title", "", 40, Kit.Brown, TextAnchor.MiddleLeft, false);
            cLine1 = Kit.Txt(card, "Name", "", 27, Kit.Brown, TextAnchor.MiddleLeft, false);
            cLine2 = Kit.Txt(card, "Extra", "", 22, Kit.Good, TextAnchor.MiddleLeft, false);
            card.gameObject.SetActive(false);
        }

        public void ShowResult(Item_Base fish, bool good, string title, string line1, string line2, bool two, bool perf)
        {
            bool third = !string.IsNullOrEmpty(line2);
            cTitle.text = title; cTitle.color = good ? Kit.Brown : Kit.Bad;
            cLine1.text = line1;
            cLine2.text = third ? line2 : "";
            cLine2.color = good ? Kit.Good : Kit.A(Kit.Brown, 0.8f);
            Sprite sp = FishFight.Icon(fish);
            icon.sprite = sp; icon.enabled = sp != null;
            iconTo = good ? Color.white : new Color(0.17f, 0.1f, 0.05f, 0.5f);
            badge.gameObject.SetActive(two);
            glowOn = perf;
            float tw = Mathf.Max(cTitle.preferredWidth, Mathf.Max(cLine1.preferredWidth, cLine2.preferredWidth)) + 8f;
            float x = 132f, y = third ? -14f : -27f;
            Kit.Place(cTitle.rectTransform, Kit.TL, Kit.TL, new Vector2(x, y), new Vector2(tw, 44f));
            Kit.Place(cLine1.rectTransform, Kit.TL, Kit.TL, new Vector2(x, y - 44f), new Vector2(tw, 32f));
            Kit.Place(cLine2.rectTransform, Kit.TL, Kit.TL, new Vector2(x, y - 76f), new Vector2(tw, 28f));
            card.sizeDelta = new Vector2(Mathf.Clamp(x + tw + 30f, 380f, 800f), CardH);
            cardBorn = Time.unscaledTime;
            cardLife = good ? 3.6f : 3f;
            card.gameObject.SetActive(true);
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime, now = Time.unscaledTime;
            FishFight.Fight f = FishFight.fight;
            if (demo != null) f = DemoTick(dt);
            if (demoFailAt > 0f && now >= demoFailAt)
            {
                demoFailAt = 0f;
                ShowResult(demoFish, false, "A LINHA PARTIU", "O peixe levou o isco.", "", false, false);
            }
            bool menu = false;
            try { menu = CanvasHelper.ActiveMenu != MenuType.None; } catch { }
            if (f != null && f != lastFight) NewFight(f);
            lastFight = f;
            bool cardUp = now < cardBorn + cardLife;
            if (f != null && cardUp && now - cardBorn > 0.5f) { cardLife = now - cardBorn; cardUp = false; }   // a new fish bit: the card goes
            float want = f != null && !menu && !cardUp ? 1f : 0f;
            shown = Mathf.MoveTowards(shown, want, dt * (want > shown ? 4f : 7f));
            float e = Kit.EaseOut(shown);
            panelGroup.alpha = e;
            Vector2 shake = Vector2.zero;
            if (f != null)
            {
                Draw(f, dt, now);
                if (f.tension > 0.8f) shake = Random.insideUnitCircle * (f.tension - 0.8f) * 16f;
            }
            panel.anchoredPosition = new Vector2(0f, PanelY - (1f - e) * 28f) + shake;
            DrawCard(now, menu);
        }

        void NewFight(FishFight.Fight f)
        {
            shownTension = 0f;
            fishX = Mathf.InverseLerp(f.maxDist, FishFight.Fight.WinDist, f.dist);
            lastStatus = "";
            if (f.fish != lastFish)
            {
                lastFish = f.fish;
                fishImg.sprite = FishFight.Icon(f.fish);
                fishImg.enabled = fishImg.sprite != null;
            }
        }

        void Draw(FishFight.Fight f, float dt, float now)
        {
            int lvl = f.diff < 0.34f ? 1 : f.diff < 0.67f ? 2 : 3;
            sizeTxt.text = lvl == 1 ? "PEIXE PEQUENO" : lvl == 2 ? "PEIXE MÉDIO" : "PEIXE GRANDE";
            for (int i = 0; i < 3; i++) dots[i].color = i < lvl ? (lvl == 3 ? Kit.Bad : Kit.Brown) : Kit.A(Kit.Brown, 0.2f);

            // tension
            shownTension = Mathf.Lerp(shownTension, f.tension, Mathf.Clamp01(dt * 14f));
            float tv = shownTension;
            fillRt.sizeDelta = new Vector2((BarW - 6f) * tv, -6f);
            fill.enabled = tv > 0.02f;
            Color c = tv < 0.55f ? Color.Lerp(GreenT, AmberT, tv / 0.55f) : Color.Lerp(AmberT, RedT, (tv - 0.55f) / 0.3f);
            float blink = 0.5f + 0.5f * Mathf.Sin(now * 18f);
            if (tv > 0.8f) c = Color.Lerp(c, new Color(1f, 0.5f, 0.4f), blink * 0.6f);
            fill.color = c;
            danger.color = new Color(0.75f, 0.15f, 0.08f, tv > 0.8f ? 0.45f + 0.4f * blink : 0.45f);
            tick.color = Kit.A(Kit.Cream, f.maxTension < 0.75f ? 1f : 0.25f);

            // the fish along the line: 0 = it got away, 1 = in your hands
            float p = Mathf.InverseLerp(f.maxDist, FishFight.Fight.WinDist, f.dist);
            fishX = Mathf.Lerp(fishX, p, Mathf.Clamp01(dt * 10f));
            Vector2 a = new Vector2(fishX, 0.5f);
            fishRt.anchorMin = a; fishRt.anchorMax = a;
            wake.rectTransform.anchorMin = a; wake.rectTransform.anchorMax = a;
            bool pull = f.phase == FishFight.Phase.Pull, warn = f.phase == FishFight.Phase.Warn;
            float wig = pull ? Mathf.Sin(now * 26f) * 14f : warn ? Mathf.Sin(now * 40f) * 7f : Mathf.Sin(now * 4f) * 4f;
            fishRt.localRotation = Quaternion.Euler(0f, 0f, wig);
            float bob = f.reeling && !pull ? Mathf.Abs(Mathf.Sin(now * 10f)) * 3f : 0f;
            fishRt.anchoredPosition = new Vector2(pull ? Random.Range(-1.5f, 1.5f) : 0f, 4f + bob);
            wake.color = Kit.A(Kit.Brown, pull ? 0.5f : 0.25f);
            wake.rectTransform.sizeDelta = new Vector2(pull ? 92f + 10f * Mathf.Sin(now * 20f) : 70f, pull ? 28f : 22f);
            float away = 1f - Mathf.Clamp01(p / 0.25f);   // near the far end: that end lights up
            endAway.rectTransform.sizeDelta = Vector2.one * (12f + 6f * away * blink);
            endAway.color = Kit.A(Kit.Bad, 0.55f + 0.45f * away);
            distTxt.text = Mathf.CeilToInt(Mathf.Max(0f, f.dist - FishFight.Fight.WinDist)) + " m";
            distTxt.color = away > 0.4f ? Kit.Bad : Kit.Brown;

            // what to do now
            string s; Color sc;
            if (pull) { s = f.reeling ? "SOLTA!" : "AGUENTA..."; sc = f.reeling ? new Color(1f, 0.5f, 0.33f) : Kit.Cream; }
            else if (warn) { s = "ATENÇÃO!"; sc = new Color(1f, 0.84f, 0.4f); }
            else { s = "ENROLA!"; sc = Kit.Cream; }
            if (s != lastStatus) { lastStatus = s; status.text = s; punch = 1f; }
            punch = Mathf.MoveTowards(punch, 0f, dt * 4f);
            status.rectTransform.localScale = Vector3.one * (1f + 0.3f * punch * punch);
            status.color = sc;
            status.rectTransform.anchoredPosition = new Vector2(pull && f.reeling ? Random.Range(-3f, 3f) : 0f, 6f);
        }

        void DrawCard(float now, bool menu)
        {
            float t = now - cardBorn;
            if (t > cardLife + 0.4f) { if (card.gameObject.activeSelf) card.gameObject.SetActive(false); return; }
            float a, s = 1f, y = 0f;
            if (t < cardLife)
            {
                a = Mathf.Clamp01(t / 0.15f);
                s = t < 0.16f ? Mathf.Lerp(0.8f, 1.06f, t / 0.16f) : t < 0.32f ? Mathf.Lerp(1.06f, 1f, (t - 0.16f) / 0.16f) : 1f;
            }
            else
            {
                float k = Mathf.Clamp01((t - cardLife) / 0.4f);
                a = 1f - k;
                y = -18f * Kit.EaseOut(k);
            }
            cardGroup.alpha = a * (menu ? 0.3f : 1f);
            card.localScale = new Vector3(s, s, 1f);
            card.anchoredPosition = new Vector2(0f, PanelY + y);
            // the fish comes out of the dark (a catch), with a little jump
            float r = Kit.EaseOut((t - 0.18f) / 0.35f);
            icon.color = Color.Lerp(Silhouette, iconTo, r);
            float jump = t > 0.18f && t < 0.53f ? Mathf.Sin((t - 0.18f) / 0.35f * Mathf.PI) : 0f;
            iconRt.localScale = Vector3.one * (1f + 0.25f * jump);
            iconRt.anchoredPosition = new Vector2(0f, 3f + 8f * jump);
            glow.color = new Color(1f, 0.85f, 0.4f, glowOn ? (0.5f + 0.2f * Mathf.Sin(now * 3f)) * Mathf.Clamp01(t / 0.4f) : 0f);
            if (badge.gameObject.activeSelf) badge.localScale = Vector3.one * (t < 0.55f ? 0f : Kit.Back((t - 0.55f) / 0.3f));
        }

        // ---------- pesca demo: a pretend fight, then the result cards ----------
        FishFight.Fight demo;
        Item_Base demoFish;
        float demoT, demoFailAt;

        public void StartDemo(Item_Base fish)
        {
            demoFish = fish;
            demo = new FishFight.Fight();
            demo.fish = fish; demo.diff = 0.8f; demo.startDist = 14f; demo.dist = 14f; demo.maxDist = 24f;
            demoT = 0f; demoFailAt = 0f; cardBorn = -100f;
        }

        FishFight.Fight DemoTick(float dt)
        {
            demoT += dt;
            float t = demoT;
            FishFight.Fight d = demo;
            if (t < 1.8f) { d.phase = FishFight.Phase.Rest; d.reeling = true; d.dist -= 2.1f * dt; d.tension = Mathf.MoveTowards(d.tension, 0.3f, 0.8f * dt); }
            else if (t < 2.25f) { d.phase = FishFight.Phase.Warn; d.reeling = true; }
            else if (t < 3.6f) { d.phase = FishFight.Phase.Pull; d.reeling = true; d.tension += 0.45f * dt; d.dist -= 0.4f * dt; }
            else if (t < 5.4f) { d.phase = FishFight.Phase.Pull; d.reeling = false; d.tension = Mathf.MoveTowards(d.tension, 0f, 0.5f * dt); d.dist += 1.6f * dt; }
            else if (d.dist > FishFight.Fight.WinDist) { d.phase = FishFight.Phase.Rest; d.reeling = true; d.dist -= 2.1f * dt; d.tension = Mathf.MoveTowards(d.tension, 0.3f, 0.8f * dt); }
            else
            {
                demo = null;
                ShowResult(demoFish, true, "PESCA PERFEITA!", FishFight.Name(demoFish), "Veio mais um!", true, true);
                demoFailAt = Time.unscaledTime + 4.3f;
                return null;
            }
            d.tension = Mathf.Clamp01(d.tension);
            if (d.tension > d.maxTension) d.maxTension = d.tension;
            return d;
        }
    }
}
