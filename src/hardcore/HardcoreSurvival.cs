using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.PostProcessing;
using HarmonyLib;
using HMLLibrary;
using RaftModLoader;

// Hardcore Survival v1.1.0: three survival systems for the local player, saved per world.
// - Sono: hours awake count up with the game clock; sleeping in a bed resets them. After 30 hours
//   awake you get slower, the colours drain, the view sways and your eyes start closing on their
//   own, worse until 54 hours. Heavy breathing now and then.
// - Comer cru: raw food has a 40% chance of food poisoning (90-150 s): health, hunger and thirst
//   drain, the view wobbles green and you throw up every so often. Cooked food cures it.
// - Temperatura: body heat drops at night, in rain/snow/big waves and in the water; a lit fire
//   (any cooking stand with fuel) within 6 m warms you up. Cold tints the view blue and makes you
//   shiver; freezing hurts.
// Screen effects use the game's own post-processing (vignette, chromatic aberration, colour
// grading) and the sounds are the game's own (player hurt/hungry/out-of-breath, UI).
// Panel above the Skills panel. Console (F10): hardcore | hardcore <sono|cru|temp> <on|off> |
// hardcore teste <acordado N|temp N|doente|vomitar>
public class HardcoreSurvival : Mod
{
    public const string Version = "2.8.1";
    const string HarmonyId = "com.bestcode.hardcore";
    const BindingFlags BF = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    public static HardcoreSurvival I;
    Harmony harmony;

    public static bool SleepOn = true, RawOn = true, TempOn = true;
    public static float awake, temp = 0.75f, sickLeft, sickTotal = 1f, wet, insul;
    float insulAt; bool wetShown;
    const float SickMax = 200f;

    // smoothed effect strengths (0..1), read by the HUD
    public static float FeverFx, heatT, feverLeft;
    static int hotStage;
    public static float FatFx, SickFx, ColdFx, FreezeFx, WarmFx, VomitFx, Blink, HurtFx;
    public static int BadEvent; // bumps when something bad happens (HUD shakes)

    string world;
    float lastHour = -1f, saveAt, fireAt, msgAt;
    bool nearFire;
    int tiredStage, coldStage;
    PersonController pc;
    float baseN, baseS, baseW;

    float flutterAt;
    float nextVomit, nextGrowl, nextBreath, nextShock, nextFreezeHurt, nextBlink, blinkStart, blinkLen, blinkDepth;
    Quaternion lastOff = Quaternion.identity, lastRes = Quaternion.identity; bool rollOn;
    bool micro, wokeUp, wasInWater;
    float sleepLid;
    float napRate = 1f; bool wasLying;
    // seconds spent past the limit of each need; the damage grows exponentially with it
    float exhaustT, freezeT, poisonT, dbgAt, nextExhaustHurt, exhaustRepeat;
    int exhaustWarn, freezeWarn, poisonWarn;

    // lethal drain: gentle at first, doubles every 20 s past the limit (death in ~1.5-2 min if ignored)
    static float Lethal(float t) { return 0.25f * Mathf.Pow(2f, Mathf.Min(t, 200f) / 20f); }

    int Overdue(ref float t, bool over, ref int warn, PlayerStats st, float dt, string[] titles, string[] bodies)
    {
        if (!over) { t = Mathf.Max(0f, t - dt * 3f); if (t <= 0f) warn = 0; return 0; }
        t += dt;
        if (st != null) Drain(st.stat_health, Lethal(t) * dt, 0f);
        int stage = t > 75f ? 3 : (t > 40f ? 2 : (t > 8f ? 1 : 0));
        if (stage > warn && stage > 0)
        {
            Debug.Log("[HardcoreSurvival] limite " + titles[0] + " fase " + stage + " (" + t.ToString("0") + " s)");
            Emit(st, "eventEmitter_Damaged"); HurtFx = 1f; BadEvent++;
        }
        if (stage > 0) Alert(titles[0], titles[stage - 1], bodies[stage - 1]);
        warn = Mathf.Max(warn, stage);
        return stage;
    }
    public static float Nod, Jolt;
    bool fxOwn;
    PostProcessingProfile fxProfile;

    const float TiredFrom = 57.6f, TiredMax = 72f; // sleepy only in the last 20% of the energy bar

    public void Start()
    {
        I = this;
        harmony = new Harmony(HarmonyId);
        harmony.CreateClassProcessor(typeof(P_Consume)).Patch();
        harmony.CreateClassProcessor(typeof(P_Llama)).Patch();
        Debug.Log("[HardcoreSurvival] carregado v" + Version);
    }

    public void OnModUnload()
    {
        Save();
        RestoreSpeed();
        ClearFx();
        if (harmony != null) harmony.UnpatchAll(HarmonyId);
        BCUI_HC.HcHud.Kill();
        BCUI_HC.FriendCard.Kill();
        BCUI_HC.Toast.ClearAll();
        I = null;
    }

    // ---------- save ----------
    static string Dir() { return Path.Combine(Application.persistentDataPath, "BestCodeHardcore"); }
    static string F(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

    void Load()
    {
        awake = 0f; temp = 0.75f; sickLeft = 0f; sickTotal = 1f; wet = 0f;
        SleepOn = RawOn = TempOn = true;
        tiredStage = coldStage = 0; lastHour = -1f;
        try
        {
            string p = Path.Combine(Dir(), world + ".txt");
            if (!File.Exists(p)) return;
            foreach (string line in File.ReadAllLines(p))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                float f;
                float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f);
                if (k == "awake") awake = f;
                else if (k == "temp") temp = Mathf.Clamp01(f);
                else if (k == "sick") sickLeft = f;
                else if (k == "sickTotal") sickTotal = Mathf.Max(1f, f);
                else if (k == "wet") wet = Mathf.Clamp01(f);
                else if (k == "sono") SleepOn = v == "1";
                else if (k == "cru") RawOn = v == "1";
                else if (k == "temperatura") TempOn = v == "1";
            }
        }
        catch (Exception e) { Debug.LogWarning("[HardcoreSurvival] load: " + e.Message); }
    }

    void Save()
    {
        if (string.IsNullOrEmpty(world)) return;
        try
        {
            Directory.CreateDirectory(Dir());
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("awake=" + F(awake));
            sb.AppendLine("temp=" + F(temp));
            sb.AppendLine("sick=" + F(sickLeft));
            sb.AppendLine("sickTotal=" + F(sickTotal));
            sb.AppendLine("wet=" + F(wet));
            sb.AppendLine("sono=" + (SleepOn ? "1" : "0"));
            sb.AppendLine("cru=" + (RawOn ? "1" : "0"));
            sb.AppendLine("temperatura=" + (TempOn ? "1" : "0"));
            File.WriteAllText(Path.Combine(Dir(), world + ".txt"), sb.ToString());
        }
        catch (Exception e) { Debug.LogWarning("[HardcoreSurvival] save: " + e.Message); }
    }

    // ---------- sounds (the game's own FMOD events, reached by reflection) ----------
    static void Emit(PlayerStats st, string field)
    {
        if (st == null) return;
        try
        {
            FieldInfo f = typeof(PlayerStats).GetField(field, BF);
            object e = f == null ? null : f.GetValue(st);
            if (e == null) return;
            MethodInfo m = e.GetType().GetMethod("Play", Type.EmptyTypes);
            if (m != null) m.Invoke(e, null);
        }
        catch { }
    }

    static Component soundMgr;
    static void Ui(string method)
    {
        try
        {
            if (soundMgr == null)
            {
                Type t = typeof(PlayerStats).Assembly.GetType("SoundManager");
                if (t != null) soundMgr = UnityEngine.Object.FindObjectOfType(t) as Component;
            }
            if (soundMgr == null) return;
            MethodInfo m = soundMgr.GetType().GetMethod(method, Type.EmptyTypes);
            if (m != null) m.Invoke(soundMgr, null);
        }
        catch { }
    }

    // ---------- helpers ----------
    static void Drain(Stat s, float amount, float floorFrac)
    {
        if (s == null || amount <= 0f) return;
        // in creative the game never refills hunger/thirst, so anything we take would stay gone
        if (Invulnerable()) return;
        float floor = s.Max * floorFrac;
        if (s.Value > floor) s.Value = Mathf.Max(floor, s.Value - amount);
    }

    static bool Invulnerable()
    {
        try { return GameModeValueManager.IsPlayerInvurnerable; } catch { return false; }
    }

    // repair bars that earlier versions drained in creative (they never refill there)
    static void RepairCreative(PlayerStats st)
    {
        if (st == null || !Invulnerable()) return;
        try
        {
            Stat[] ss = { st.stat_hunger.Normal, st.stat_thirst.Normal };
            foreach (Stat s in ss)
                if (s != null && s.Value < s.Max)
                {
                    s.Value = s.Max;
                    Stat_Consumable c = s as Stat_Consumable;
                    if (c != null) c.Consuming = false;
                }
            if (st.stat_health != null && st.stat_health.Value < st.stat_health.Max) st.stat_health.Value = st.stat_health.Max;
        }
        catch { }
    }

    public static float HourPub() { return Hour(); }
    static float Hour()
    {
        // AzureSkyController is read by reflection (its namespace is not referenced by every RML build)
        try
        {
            object sky = typeof(BedManager).GetField("skyController", BF).GetValue(null);
            if (sky == null) return -1f;
            object tod = sky.GetType().GetField("timeOfDay").GetValue(sky);
            return (float)tod.GetType().GetField("hour").GetValue(tod);
        }
        catch { return -1f; }
    }

    static bool IsNight(float h) { return h >= 0f && (h >= 20f || h < 5f); }

    // how much the sun warms you right now (0..1): none at night or under rain/snow, softer at dawn,
    // dusk and in fog
    static float Sun(float h, UniqueWeatherType wt)
    {
        if (h < 0f || IsNight(h)) return 0f;
        if (wt == UniqueWeatherType.Rain || wt == UniqueWeatherType.Snow || wt == UniqueWeatherType.SnowRough) return 0f;
        float s = Mathf.Min(Mathf.Clamp01((h - 5f) / 2f), Mathf.Clamp01((20f - h) / 2f));
        if (wt == UniqueWeatherType.Fog) s *= 0.5f;
        return s;
    }

    WeatherManager wm;
    UniqueWeatherType Weather()
    {
        if (wm == null) wm = UnityEngine.Object.FindObjectOfType<WeatherManager>();
        if (wm == null) return UniqueWeatherType.Default;
        try { return wm.GetCurrentWeatherType(); } catch { return UniqueWeatherType.Default; }
    }

    // heat sources: any grill, purifier or smelter that is burning / powered, the electric purifier
    // while it runs, a lantern, fire basket or fireplace while lit, and the wooden bathtub
    static readonly string[] HeatNames = { "lantern", "firebasket", "fire_basket", "fireplace", "bathtub", "bath_tub" };
    static string heatSeen = "";
    bool FireNear(Vector3 pos)
    {
        try
        {
            System.Collections.Generic.List<Block> blocks = BlockCreator.GetPlacedBlocks();
            if (blocks == null) return false;
            string seen = "";
            foreach (Block b in blocks)
            {
                if (b == null || (b.transform.position - pos).sqrMagnitude > 9f) continue;
                string n = b.buildableItem != null ? b.buildableItem.UniqueName ?? "" : b.name;
                if (Heats(b, n.ToLowerInvariant())) { seen = n; break; }
            }
            if (seen != heatSeen) { heatSeen = seen; Debug.Log("[HardcoreSurvival] calor: " + (seen == "" ? "nenhum" : seen)); }
            return seen != "";
        }
        catch { return false; }
    }

    static bool Heats(Block b, string n)
    {
        Block_CookingStand cs = b as Block_CookingStand;
        if (cs != null) return cs.IsUsingEnergySource();
        Block_ElectricPurifier ep = b as Block_ElectricPurifier;
        if (ep != null) return ep.purifier != null && ep.purifier.isOn && ep.purifier.battery != null && ep.purifier.battery.CanGiveElectricity;
        foreach (string k in HeatNames)
        {
            if (n.IndexOf(k, StringComparison.Ordinal) < 0) continue;
            // lanterns, fire baskets and fireplaces are always lit in Raft (their light lives in the
            // shared LightSingularity pool, not on the block, so there is nothing to check)
            return true;
        }
        return false;
    }

    public static bool AdvancedPurifier(Block b)
    {
        string n = b != null && b.buildableItem != null ? b.buildableItem.UniqueName ?? "" : "";
        return n.IndexOf("advanced", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    void ApplySpeed(PersonController c, float mul)
    {
        if (c != pc) { RestoreSpeed(); pc = c; baseN = c.normalSpeed; baseS = c.sprintSpeed; baseW = c.swimSpeed; }
        c.normalSpeed = baseN * mul; c.sprintSpeed = baseS * mul; c.swimSpeed = baseW * mul;
    }

    void RestoreSpeed()
    {
        if (pc != null) { pc.normalSpeed = baseN; pc.sprintSpeed = baseS; pc.swimSpeed = baseW; }
        pc = null;
    }

    // leather helmet / armour / greaves (made with wool): 0..0.8
    static float Insulation(Network_Player me)
    {
        float s = 0f;
        try
        {
            foreach (Equipment e in me.GetComponentsInChildren<Equipment>(true))
            {
                if (e == null || !e.Equipped || e.equipableItem == null) continue;
                string n = e.equipableItem.UniqueName ?? "";
                if (n.IndexOf("Leather", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string l = n.ToLowerInvariant();
                if (l.Contains("helm") || l.Contains("head") || l.Contains("hat")) s += 0.2f;
                else if (l.Contains("greave") || l.Contains("leg") || l.Contains("pant") || l.Contains("boot")) s += 0.25f;
                else s += 0.35f;
            }
        }
        catch { }
        return Mathf.Min(0.8f, s);
    }

    public static float Fatigue() { return Mathf.Clamp01((awake - TiredFrom) / (TiredMax - TiredFrom)); }
    public static float Cold() { return Mathf.Clamp01((0.35f - temp) / 0.35f); }

    // a warning pinned on screen for as long as it is called every frame (i.e. until the problem is solved)
    readonly System.Collections.Generic.Dictionary<string, BCUI_HC.Toast> alerts = new System.Collections.Generic.Dictionary<string, BCUI_HC.Toast>();
    void Alert(string key, string title, string sub)
    {
        BCUI_HC.Toast t;
        alerts.TryGetValue(key, out t);
        if (t == null || t.Title != title)
        {
            if (t != null) t.HoldUntil = 0f;
            BadEvent++; Ui("PlayUI_Click_Fail");
            try { t = BCUI_HC.Toast.Show(title, sub, null, true, 5f); } catch (System.Exception e) { Debug.LogWarning("[HardcoreSurvival] alerta: " + e.Message); t = null; }
            alerts[key] = t;
        }
        if (t != null) t.HoldUntil = Time.unscaledTime + 0.3f;
    }

    void Msg(string title, string sub, bool bad)
    {
        if (Time.time < msgAt) return;
        msgAt = Time.time + 3f;
        if (bad) { BadEvent++; Ui("PlayUI_Click_Fail"); } else Ui("PlayUI_OpenMenu");
        try { BCUI_HC.Toast.Show(title, sub, null, bad, 5f); } catch (System.Exception e) { Debug.LogWarning("[HardcoreSurvival] toast: " + e.Message); }
    }

    static float Approach(float cur, float target, float up, float down, float dt)
    {
        return Mathf.MoveTowards(cur, target, (target > cur ? up : down) * dt);
    }

    // ---------- tick ----------
    public void Update()
    {
        Network_Player me = null;
        try { me = RAPI.GetLocalPlayer(); } catch { }
        string w = null;
        try { w = SaveAndLoad.CurrentGameFileName; } catch { }
        // a client in a friend's world has no save name of its own
        if (me != null && string.IsNullOrEmpty(w)) w = "_cliente";
        if (me == null || string.IsNullOrEmpty(w))
        {
            if (world != null) { Save(); RestoreSpeed(); ClearFx(); world = null; }
            BCUI_HC.HcHud.Hide();
            return;
        }
        if (w != world) { if (world != null) Save(); world = w; Load(); }

        float dt = Time.deltaTime;
        float now = Time.time;
        PlayerStats st = me.Stats;
        bool dead = me.PlayerScript != null && me.PlayerScript.IsDead;

        // sleep
        float h = Hour();
        bool sleeping = false;
        try { sleeping = BedManager.Slumbering || (me.BedComponent != null && me.BedComponent.Sleeping); } catch { }
        if (h >= 0f)
        {
            if (lastHour >= 0f)
            {
                float dh = h - lastHour;
                if (dh < -12f) dh += 24f;
                // a full night (clock skips) restores everything; a nap only restores while you stay lying down:
                // about 30 s of real rest clears it all
                // a nap clears all the tiredness in about 30 s of lying down, whatever the amount
                if (sleeping && !wasLying) napRate = Mathf.Max(awake, 1f) / 30f;
                wasLying = sleeping;
                if (sleeping) awake = (BedManager.Slumbering || dh >= 1f || dh < 0f) ? 0f : Mathf.Max(0f, awake - napRate * Time.deltaTime);
                else if (dh > 0f && dh < 1f && SleepOn && !dead) awake += dh;
            }
            lastHour = h;
        }
        if (!SleepOn) awake = 0f;
        float fat = Fatigue();
        int ts = fat >= 1f ? 2 : (fat > 0f ? 1 : 0);
        if (ts > tiredStage)
        {
            if (ts == 1) Msg("ESTÁS CANSADO", "Há muito tempo que não dormes. Deita-te numa cama.", true);
            else Msg("ESTÁS EXAUSTO", "Mal te aguentas de pé. Dorme numa cama.", true);
            Emit(st, "eventEmitter_LowBreath");
            nextBreath = now + 12f;
        }
        tiredStage = ts;
        if (fat > 0.35f && !dead && !sleeping)
        {
            if (now >= nextBreath) { nextBreath = now + UnityEngine.Random.Range(16f, 26f) - 8f * fat; Emit(st, "eventEmitter_LowBreath"); }
            if (nextBlink <= 0f) nextBlink = now + UnityEngine.Random.Range(4f, 10f);
            if (now >= nextBlink)
            {
                blinkStart = now;
                micro = fat >= 0.55f && UnityEngine.Random.value < 0.15f + 0.35f * (fat - 0.55f) / 0.45f;
                wokeUp = false;
                if (micro)
                {
                    // microsleep: eyes drift shut, the head drops, then you jolt awake
                    blinkLen = UnityEngine.Random.Range(2.4f, 3.8f);
                    blinkDepth = 1f;
                    flutterAt = 0f;
                }
                else
                {
                    blinkLen = 0.45f + 1.1f * fat * UnityEngine.Random.value;
                    blinkDepth = Mathf.Clamp01(0.55f + 0.5f * fat + UnityEngine.Random.Range(-0.1f, 0.1f));
                    flutterAt = UnityEngine.Random.value < 0.25f + 0.3f * fat ? now + blinkLen + UnityEngine.Random.Range(0.12f, 0.35f) : 0f;
                }
                nextBlink = now + blinkLen + UnityEngine.Random.Range(5f, 14f) * (1.4f - fat);
            }
            if (flutterAt > 0f && now >= flutterAt)
            {
                // quick second blink, like fighting to keep the eyes open
                blinkStart = now; blinkLen = UnityEngine.Random.Range(0.25f, 0.45f);
                blinkDepth = Mathf.Clamp01(blinkDepth * UnityEngine.Random.Range(0.7f, 1f));
                flutterAt = 0f;
            }
        }
        else { nextBlink = 0f; flutterAt = 0f; }
        float bk = blinkLen > 0f ? (now - blinkStart) / blinkLen : 1f;
        float curve = 0f, nodT = 0f;
        if (bk < 1f && micro)
        {
            if (bk < 0.5f) curve = Mathf.SmoothStep(0f, 1f, bk / 0.5f);
            else if (bk < 0.86f) curve = 1f;
            else curve = 1f - Mathf.SmoothStep(0f, 1f, (bk - 0.86f) / 0.14f);
            nodT = bk < 0.86f ? Mathf.SmoothStep(0f, 1f, bk / 0.7f) : 0f;
            if (bk >= 0.86f && !wokeUp)
            {
                wokeUp = true; Jolt = 1f; BadEvent++;
                Emit(st, "eventEmitter_LowBreath");
            }
        }
        else if (bk < 1f)
        {
            // close fast-ish, hold, then open slowly (heavy lids)
            if (bk < 0.3f) curve = Mathf.SmoothStep(0f, 1f, bk / 0.3f);
            else if (bk < 0.45f) curve = 1f;
            else curve = 1f - Mathf.SmoothStep(0f, 1f, (bk - 0.45f) / 0.55f);
        }
        float droop = fat > 0.1f ? (0.3f + 0.12f * Mathf.Sin(now * 0.6f) + 0.06f * Mathf.Sin(now * 1.7f)) * fat : 0f;
        // lying in bed: eyes slowly close and stay shut, then open gently when you get up
        sleepLid = Mathf.MoveTowards(sleepLid, sleeping && !dead ? 1f : 0f, (sleeping ? 0.6f : 1.2f) * Time.deltaTime);
        Blink = dead ? 0f : Mathf.Max(sleeping ? 0f : Mathf.Max(blinkDepth * curve, droop), sleepLid);
        if (sleeping || dead) nodT = 0f;
        Nod = Mathf.MoveTowards(Nod, nodT, (nodT > Nod ? 1.2f : 6f) * dt);
        Jolt = Mathf.MoveTowards(Jolt, 0f, dt / 0.9f);

        RepairCreative(st);
        if (dead) { exhaustT = freezeT = poisonT = 0f; exhaustWarn = freezeWarn = poisonWarn = 0; }
        else
        {
            if (now >= dbgAt) { dbgAt = now + 10f; Debug.Log("[HardcoreSurvival] estado acordado=" + awake.ToString("0") + "h fat=" + fat.ToString("0.00") + " dormir=" + sleeping + " sono=" + SleepOn + " exaustT=" + exhaustT.ToString("0") + " frioT=" + freezeT.ToString("0")); }
            // real-world sleep deprivation, on the game clock: tired after 18 h awake, wrecked at 72 h
            // (energy 0, hallucination stage); past that the body starts failing and the damage doubles
            // every game day; the longest documented stretch is ~11 days (264 h), so that is the end
            if (SleepOn && !sleeping && awake > TiredMax && st != null)
            {
                float over = awake - TiredMax;
                Drain(st.stat_health, 0.02f * Mathf.Pow(2f, Mathf.Min(over, 240f) / 24f) * dt, 0f);
                if (awake >= 264f) Drain(st.stat_health, 5f * dt, 0f);
                int stage = awake >= 240f ? 3 : (awake >= 168f ? 2 : (awake >= 96f ? 1 : 0));
                // the current warning comes back regularly (more often the worse it is) until you sleep
                string[] tt = { "O CORPO ESTÁ A FALHAR", "VAIS DESMAIAR", "VAIS MORRER DE EXAUSTÃO" };
                string[] bb = { "4 dias sem dormir. Estás a perder vida. Dorme numa cama.", "7 dias sem dormir. O coração não aguenta muito mais.", "10 dias sem dormir. Dorme agora ou morres." };
                if (stage > exhaustWarn && stage > 0) { Emit(st, "eventEmitter_Damaged"); HurtFx = 1f; BadEvent++; }
                if (stage > 0) Alert("sono", tt[stage - 1], bb[stage - 1]);
                else Alert("sono", "ESTÁS EXAUSTO", "Mal te aguentas de pé. Dorme numa cama.");
                exhaustWarn = Mathf.Max(exhaustWarn, stage);
                if (now >= nextExhaustHurt && stage > 0) { nextExhaustHurt = now + UnityEngine.Random.Range(20f, 40f) / stage; Emit(st, "eventEmitter_Damaged"); HurtFx = Mathf.Max(HurtFx, 0.6f); }
            }
            else if (awake < TiredMax) exhaustWarn = 0;
            Overdue(ref poisonT, RawOn && sickLeft >= SickMax * 0.75f, ref poisonWarn, st, dt,
                new string[] { "ENVENENAMENTO GRAVE", "O VENENO ESTÁ A ESPALHAR-SE", "VAIS MORRER ENVENENADO" },
                new string[] { "Para de comer cru e come algo cozinhado.", "Precisas de comida cozinhada já.", "O corpo está a desligar-se." });
        }
        // raw food poisoning
        if (sickLeft > 0f)
        {
            if (!RawOn) sickLeft = 0f;
            else if (!dead && st != null)
            {
                sickLeft = Mathf.Max(0f, sickLeft - dt);
                // symptoms scale with how bad it is
                float sev = Mathf.Clamp01(sickLeft / SickMax);
                Drain(st.stat_health, (0.08f + 0.3f * sev) * dt, 0.15f);
                Drain(st.stat_hunger.Normal, (0.05f + 0.15f * sev) * dt, 0f);
                Drain(st.stat_thirst.Normal, (0.07f + 0.2f * sev) * dt, 0f);
                if (now >= nextGrowl) { nextGrowl = now + UnityEngine.Random.Range(9f, 15f); Emit(st, "eventEmitter_Hungry"); }
                if (nextVomit <= 0f) nextVomit = now + UnityEngine.Random.Range(10f, 18f) / (0.4f + sev);
                if (now >= nextVomit) Vomit(st);
                if (sickLeft <= 0f) { nextVomit = 0f; Msg("JÁ ESTÁS MELHOR", "A intoxicação passou.", false); }
            }
        }
        else nextVomit = 0f;

        // temperature
        bool freezing = false;
        if (TempOn && !dead)
        {
            if (now >= fireAt) { fireAt = now + 2f; nearFire = FireNear(me.transform.position); }
            // only really swimming counts: wading in the shallows of an island beach does not
            bool inWater = (me.PersonController != null && me.PersonController.controllerType == ControllerType.Water) || me.transform.position.y < -1.3f;
            if (inWater && !wasInWater && now >= nextShock)
            {
                nextShock = now + 20f;
                // cold shock when you jump in
                Emit(st, "eventEmitter_LowBreath");
                temp = Mathf.Max(0f, temp - 0.02f / 3f);
                if (temp < 0.6f) Msg("A ÁGUA ESTÁ GELADA", "Sai da água ou vais arrefecer depressa.", true);
            }
            bool leftWater = wasInWater && !inWater;
            wasInWater = inWater;
            if (now >= insulAt) { insulAt = now + 1f; insul = Insulation(me); }
            UniqueWeatherType wt = Weather();
            bool wetWeather = wt == UniqueWeatherType.Rain || wt == UniqueWeatherType.Snow || wt == UniqueWeatherType.SnowRough;
            // no wet clothes at all (removed on request): the sea only cools while you are in it
            wet = 0f; wetShown = false;
            float target = IsNight(h) ? 0.45f : 0.75f;
            switch (wt)
            {
                case UniqueWeatherType.Fog: target -= 0.05f; break;
                case UniqueWeatherType.BigWaves: target -= 0.1f; break;
                case UniqueWeatherType.Rain: target -= 0.15f; break;
                case UniqueWeatherType.Snow: target -= 0.3f; break;
                case UniqueWeatherType.SnowRough: target -= 0.45f; break;
            }
            if (inWater) target -= 0.65f;
            else
            {
                target -= 0.2f * wet;
                // dry on the raft or an island the body always gets out of the cold by itself, unless it snows
                bool snow = wt == UniqueWeatherType.Snow || wt == UniqueWeatherType.SnowRough;
                if (!snow) target = Mathf.Max(target, 0.4f);
            }
            float rate = inWater ? 1f / 120f : 1f / 270f;
            // a fire on the raft cannot warm you while you are in the sea
            if (inWater) nearFire = false;
            // next to a heat source: warms fast, a little slower while the clothes are still wet
            if (nearFire) { target = 1f; rate = (1f / 20f) * (1f - 0.6f * wet); }
            else if (!inWater && target > temp)
            {
                // without one the body warms itself slowly once out of the water (a fire is ~10x faster),
                // a bit quicker with leather clothes
                rate = wet > 0.05f ? 0f : (insul >= 0.15f ? Mathf.Lerp(1f / 200f, 1f / 110f, insul / 0.8f) : 1f / 200f);
                // out in the sun during the day the body warms up bit by bit, never as fast or as
                // high as next to a real heat source
                float sun = Sun(h, wt);
                if (sun > 0f) rate = Mathf.Max(rate, sun / 110f);
            }
            // in the water the temperature can only go down
            if (inWater) target = Mathf.Min(target, temp);
            if (!nearFire && target < temp)
            {
                // wet clothes cool faster; leather armour lined with wool keeps the heat in (less when soaked)
                float ins = Mathf.Min(0.92f, inWater ? insul * 0.9f : insul * 1.15f);
                if (!inWater) rate *= 1f + 0.8f * wet;
                rate *= 1f - ins * (1f - 0.35f * wet);
                rate /= 3f; // the body loses heat three times slower
            }
            temp = Mathf.MoveTowards(temp, Mathf.Clamp01(target), rate * dt);

            float cold = Cold();
            freezing = temp < 0.1f;
            if (st != null && cold > 0f) Drain(st.stat_hunger.Normal, 0.1f * cold * dt, 0f);
            Overdue(ref freezeT, freezing, ref freezeWarn, st, dt,
                new string[] { "HIPOTERMIA", "HIPOTERMIA GRAVE", "VAIS MORRER DE FRIO" },
                new string[] { "Sai da água e aquece-te junto a algo quente.", "Já não sentes as mãos. Fogo, agora.", "O corpo está a parar." });
            if (freezing && freezeT <= 8f) Alert("HIPOTERMIA", "ESTÁS GELADO", "Estás a perder vida. Procura algo quente e aceso.");
            if (st != null && freezing)
            {
                Drain(st.stat_health, 0.15f * dt, 0.1f);
                if (now >= nextFreezeHurt) { nextFreezeHurt = now + UnityEngine.Random.Range(6f, 10f); Emit(st, "eventEmitter_Damaged"); BadEvent++; }
            }
            heatT = 0f; feverLeft = 0f; hotStage = 0;
            // shivering is shown on screen only: the breath sound is the game's drowning cue
            int cs = freezing ? 2 : (cold > 0f ? 1 : 0);
            if (cs > coldStage)
            {
                if (cs == 1) Msg("ESTÁS COM FRIO", "Aquece-te perto de uma grelha, fornalha ou lareira acesa.", true);
                else Msg("ESTÁS GELADO", "Estás a perder vida. Procura algo quente e aceso.", true);
            }
            coldStage = cs;
        }
        else if (!TempOn) { temp = 0.75f; coldStage = 0; nearFire = false; heatT = 0f; feverLeft = 0f; hotStage = 0; }

        // smoothed strengths for the screen and the HUD
        bool alive = !dead;
        FatFx = Approach(FatFx, alive ? fat : 0f, 0.5f, 1.5f, dt);
        SickFx = Approach(SickFx, alive && sickLeft > 0f ? 1f : 0f, 0.6f, 0.4f, dt);
        // no screen filters from the cold: the chip by the hotbar says it (HcHud.ColdChip)
        ColdFx = 0f; FreezeFx = 0f;
        FeverFx = 0f;
        WarmFx = 0f; // no orange "warm" filter by the fire
        VomitFx = Mathf.MoveTowards(VomitFx, 0f, dt / 1.6f);
        HurtFx = Mathf.MoveTowards(HurtFx, 0f, dt / 0.7f);

        // speed
        if (me.PersonController != null)
        {
            float mul = (1f - 0.3f * fat) * (1f - 0.25f * (TempOn ? Cold() : 0f)) * (1f - 0.5f * VomitFx);
            ApplySpeed(me.PersonController, mul);
        }

        try { BCUI_HC.HcHud.Get().Set(fat, awake, temp, sickLeft, sickTotal, nearFire); } catch { }

        if (now >= saveAt) { saveAt = now + 10f; Save(); }
    }

    void Vomit(PlayerStats st)
    {
        nextVomit = Time.time + UnityEngine.Random.Range(18f, 32f);
        VomitFx = 1f;
        BadEvent++;
        Emit(st, "eventEmitter_Damaged");
        if (st != null)
        {
            Drain(st.stat_hunger.Normal, 6f, 0f);
            Drain(st.stat_thirst.Normal, 4f, 0f);
        }
    }

    // ---------- screen: post-processing + camera ----------
    public void LateUpdate()
    {
        Network_Player me = null;
        try { me = RAPI.GetLocalPlayer(); } catch { }
        if (me == null || world == null) return;
        try { Social(me); } catch (Exception e) { if (Time.time > socialErrAt) { socialErrAt = Time.time + 30f; Debug.LogWarning("[HardcoreSurvival] amigo: " + e.Message); } }
        float t = Time.time;

        // camera roll: nausea sway, tired drift, vomit lurch (no shake from cold or damage)
        Camera cam = Camera.main;
        if (cam != null)
        {
            Transform ct = cam.transform;
            if (rollOn && Quaternion.Angle(ct.localRotation, lastRes) < 0.05f)
                ct.localRotation = ct.localRotation * Quaternion.Inverse(lastOff);
            float roll = 0f, pitch = 0f, yaw = 0f;
            // microsleep: head sinks forward and tilts, then snaps back up when you wake
            pitch += Nod * 16f;
            roll += Nod * 6f * Mathf.Sin(t * 0.3f + 1f);
            pitch -= Jolt * Mathf.Sin(Jolt * 7f) * 7f * Jolt;
            roll += Jolt * (Mathf.PerlinNoise(t * 22f, 3f) - 0.5f) * 5f;
            roll += SickFx * (Mathf.Sin(t * 0.9f) * 3.2f + Mathf.Sin(t * 2.3f) * 0.8f);
            float tiredSway = Mathf.Clamp01((FatFx - 0.3f) / 0.7f);
            roll += tiredSway * Mathf.Sin(t * 0.45f) * 2.4f;
            roll += VomitFx * Mathf.Sin(VomitFx * 9f) * 6f * VomitFx;
            if (Mathf.Abs(roll) > 0.001f || Mathf.Abs(pitch) > 0.001f || Mathf.Abs(yaw) > 0.001f)
            {
                lastOff = Quaternion.Euler(pitch, yaw, roll);
                ct.localRotation = ct.localRotation * lastOff;
                lastRes = ct.localRotation; rollOn = true;
            }
            else rollOn = false;
        }

        // post-processing (the game drives these for low health; we only push them further)
        PostProcessingProfile pp = null;
        try { if (me.Stats != null) pp = me.Stats.postEffects; } catch { }
        if (pp == null) return;
        fxProfile = pp;
        float any = Mathf.Max(Mathf.Max(FatFx, SickFx), Mathf.Max(ColdFx, Mathf.Max(Mathf.Max(WarmFx, FeverFx), VomitFx)));
        if (any < 0.005f && HurtFx < 0.005f && Blink < 0.005f)
        {
            if (fxOwn) ClearFx();
            return;
        }
        fxOwn = true;
        float pulse = 0.5f + 0.5f * Mathf.Sin(t * 1.7f);

        VignetteModel.Settings v = pp.vignette.settings;
        float vi = Mathf.Max(FatFx * (0.32f + 0.12f * pulse), Mathf.Max(SickFx * (0.3f + 0.12f * pulse), ColdFx * 0.38f));
        vi = Mathf.Max(vi, Mathf.Max(VomitFx * 0.55f, HurtFx * 0.5f));
        vi = Mathf.Max(vi, Blink * 0.6f);
        Color vc = Color.black;
        float wCold = ColdFx + FreezeFx, wSick = SickFx + VomitFx, wWarm = WarmFx * (1f - ColdFx);
        float wSum = wCold + wSick + wWarm + FatFx + 0.0001f;
        vc = (new Color(0.12f, 0.32f, 0.55f) * wCold + new Color(0.18f, 0.3f, 0.02f) * wSick + new Color(0.5f, 0.2f, 0.02f) * wWarm + Color.black * FatFx) / wSum;
        if (HurtFx > 0f) vc = Color.Lerp(vc, new Color(0.45f, 0.02f, 0.02f), HurtFx * 0.8f);
        vc.a = 1f;
        if (vi > 0.01f) { pp.vignette.enabled = true; v.intensity = Mathf.Max(v.intensity, vi); }
        v.color = vc;
        pp.vignette.settings = v;

        ChromaticAberrationModel.Settings ca = pp.chromaticAberration.settings;
        float cai = Mathf.Max(SickFx * (0.5f + 0.5f * Mathf.Sin(t * 2.1f)), VomitFx * 1.2f);
        cai = Mathf.Max(cai, FatFx * 0.18f * pulse);
        if (cai > 0.01f) { pp.chromaticAberration.enabled = true; ca.intensity = Mathf.Max(ca.intensity, cai); }
        pp.chromaticAberration.settings = ca;

        ColorGradingModel.Settings cg = pp.colorGrading.settings;
        float tempTarget = -55f * Mathf.Max(ColdFx, FreezeFx) + 28f * WarmFx;
        float tintTarget = -38f * Mathf.Max(SickFx * (0.7f + 0.3f * pulse), VomitFx);
        cg.basic.temperature = Mathf.Lerp(cg.basic.temperature, tempTarget, Time.deltaTime * 3f);
        cg.basic.tint = Mathf.Lerp(cg.basic.tint, tintTarget, Time.deltaTime * 3f);
        // tiredness keeps the colours: it only dims the edges and narrows the eyes (see HcHud)
        float sat = 1f - 0.1f * FatFx - 0.25f * FreezeFx;
        if (sat < cg.basic.saturation) cg.basic.saturation = sat;
        pp.colorGrading.settings = cg;
    }

    void ClearFx()
    {
        Camera cam = Camera.main;
        if (cam != null && rollOn && Quaternion.Angle(cam.transform.localRotation, lastRes) < 0.05f)
            cam.transform.localRotation = cam.transform.localRotation * Quaternion.Inverse(lastOff);
        Nod = Jolt = 0f;
        rollOn = false;
        FatFx = SickFx = ColdFx = FreezeFx = WarmFx = VomitFx = Blink = HurtFx = 0f;
        if (fxProfile != null && fxOwn)
        {
            try
            {
                VignetteModel.Settings v = fxProfile.vignette.settings; v.color = Color.black; fxProfile.vignette.settings = v;
                ColorGradingModel.Settings cg = fxProfile.colorGrading.settings; cg.basic.temperature = 0f; cg.basic.tint = 0f; fxProfile.colorGrading.settings = cg;
            }
            catch { }
        }
        fxOwn = false;
    }

    public static void OnAte(Item_Base item, float hungerBefore, float hungerMax)
    {
        if (I == null || item == null || item.settings_consumeable == null) return;
        ItemInstance_Consumeable c = item.settings_consumeable;
        // eating when already full: the stomach turns (lowers SAUDE)
        if (hungerMax > 0f && c.HungerYield > 0f && hungerBefore >= hungerMax * 0.85f && hungerBefore + c.HungerYield > hungerMax && !Invulnerable())
        {
            float excess = hungerBefore + c.HungerYield - hungerMax;
            bool was = sickLeft > 0f;
            sickTotal = SickMax;
            sickLeft = Mathf.Min(SickMax, sickLeft + 10f + excess * 1.2f);
            I.msgAt = 0f;
            I.Msg("COMESTE DEMAIS", "Estás enfartado. A saúde vai baixar um pouco.", true);
            if (!was) I.nextVomit = Time.time + UnityEngine.Random.Range(12f, 20f);
            return;
        }
        if (!RawOn) return;
        if (c.IsRaw)
        {
            // each raw bite makes it a bit worse; it piles up instead of flipping you to fully sick
            if (UnityEngine.Random.value < 0.75f)
            {
                bool was = sickLeft > 0f;
                sickTotal = SickMax;
                sickLeft = Mathf.Min(SickMax, sickLeft + UnityEngine.Random.Range(30f, 50f));
                if (was) return;
                I.msgAt = 0f;
                I.Msg("INTOXICAÇÃO", "Comeste comida crua. Come algo cozinhado para curar.", true);
                Network_Player me = RAPI.GetLocalPlayer();
                if (me != null) Emit(me.Stats, "eventEmitter_Hungry");
                I.nextGrowl = Time.time + 8f;
                I.nextVomit = Time.time + UnityEngine.Random.Range(8f, 14f);
            }
        }
        else if (sickLeft > 0f && c.HungerYield > 0f)
        {
            // cooked food only settles the stomach a little
            sickLeft = Mathf.Max(0f, sickLeft - 30f);
            I.msgAt = 0f;
            if (sickLeft <= 0f) I.Msg("CURADO", "A comida cozinhada assentou-te bem.", false);
            else I.Msg("A MELHORAR", "A comida cozinhada ajudou um pouco.", false);
        }
    }

    // ---------- friends: share my stats, show theirs when I look at them ----------
    const string NetSlug = "hardcore-survival";
    static readonly System.Collections.Generic.Dictionary<ulong, float[]> friendStats = new System.Collections.Generic.Dictionary<ulong, float[]>();
    static readonly System.Collections.Generic.Dictionary<ulong, float> friendAt = new System.Collections.Generic.Dictionary<ulong, float>();
    float sendAt, scanAt, socialErrAt, lookUntil;
    Network_Player[] others = new Network_Player[0];
    Network_Player looked;
    static readonly System.Collections.Generic.HashSet<ulong> noData = new System.Collections.Generic.HashSet<ulong>();

    static float K(Stat s) { return s == null || s.Max <= 0f ? 0f : Mathf.Clamp01(s.Value / s.Max); }

    void Social(Network_Player me)
    {
        float now = Time.unscaledTime;
        if (now >= sendAt)
        {
            sendAt = now + 1f;
            PlayerStats st = me.Stats;
            if (st != null)
            {
                CultureInfo ci = CultureInfo.InvariantCulture;
                float[] v = { K(st.stat_health), K(st.stat_hunger.Normal), K(st.stat_thirst.Normal),
                    SleepOn ? 1f - Mathf.Clamp01(awake / TiredMax) : -1f, TempOn ? temp : -1f,
                    RawOn ? 1f - Mathf.Clamp01(sickLeft / SickMax) : -1f, wet, insul };
                StringBuilder sb = new StringBuilder("hc");
                foreach (float f in v) sb.Append('|').Append(f.ToString("0.###", ci));
                try { SendNetworkMessage(sb.ToString(), Target.Other, Steamworks.EP2PSend.k_EP2PSendReliable, NetSlug); } catch { }
            }
        }
        if (now >= scanAt) { scanAt = now + 1f; others = UnityEngine.Object.FindObjectsOfType<Network_Player>(); }

        // who am I looking at (within 15 m, close to the centre of the screen)
        Camera cam = Camera.main;
        Network_Player hit = null;
        if (cam != null && CanvasHelper.ActiveMenu == MenuType.None)
        {
            float best = 0f;
            foreach (Network_Player p in others)
            {
                if (p == null || p == me || p.IsLocalPlayer) continue;
                Vector3 d = HeadPos(p) - Vector3.up * 0.5f - cam.transform.position;
                float dist = d.magnitude;
                if (dist > 15f || dist < 0.3f) continue;
                float dot = Vector3.Dot(cam.transform.forward, d / dist);
                float need = Mathf.Lerp(0.95f, 0.993f, dist / 15f);
                if (dot > need && dot > best) { best = dot; hit = p; }
            }
        }
        if (hit != null) { looked = hit; lookUntil = now + 1.5f; }
        if (looked == null || now > lookUntil) { BCUI_HC.FriendCard.Hide(); looked = null; return; }

        ulong id = looked.steamID.Id;
        float[] fs; float at;
        friendStats.TryGetValue(id, out fs);
        friendAt.TryGetValue(id, out at);
        if (fs != null && now - at > 8f) fs = null;
        bool vanilla = false;
        if (fs == null)
        {
            // friend without this mod (or an old one): the base game still syncs health/hunger/thirst
            try
            {
                PlayerStats ps = looked.Stats;
                if (!noData.Contains(id)) { noData.Add(id); Debug.Log("[HardcoreSurvival] amigo " + id + " sem dados do mod (precisa do Hardcore Survival 2.4.5+); recebidos de: " + string.Join(",", System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(friendStats.Keys, k => k.ToString())))); }
                if (ps != null) { fs = new float[] { K(ps.stat_health), K(ps.stat_hunger.Normal), K(ps.stat_thirst.Normal), -1f, -1f, -1f, 0f, 0f }; vanilla = true; }
            }
            catch { }
        }
        string name = null;
        try { if (looked.characterSettings != null) name = looked.characterSettings.Name; } catch { }
        if (string.IsNullOrEmpty(name)) name = "Amigo";
        bool deadF = false;
        try { deadF = looked.PlayerScript != null && looked.PlayerScript.IsDead; } catch { }
        BCUI_HC.FriendCard.Get().Set(looked, name, fs, deadF, vanilla);
    }

    public static Vector3 HeadPos(Network_Player p)
    {
        try { if (p.currentModel != null && p.currentModel.headBone != null) return p.currentModel.headBone.position; } catch { }
        return p.transform.position + Vector3.up * 1.6f;
    }

    public override bool OnNetworkMessage(object message, Network_UserId from, string modslug)
    {
        if (modslug != NetSlug) return false;
        string m = message as string;
        if (m == null || !m.StartsWith("hc|")) return true;
        string[] parts = m.Split('|');
        float[] v = new float[8];
        for (int i = 0; i < v.Length && i + 1 < parts.Length; i++)
            float.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]);
        if (!friendStats.ContainsKey(from.Id)) Debug.Log("[HardcoreSurvival] dados recebidos do amigo " + from.Id);
        friendStats[from.Id] = v;
        friendAt[from.Id] = Time.unscaledTime;
        return true;
    }

    // ---------- console ----------
    [ConsoleCommand("hardcore", "Hardcore Survival. Syntax: hardcore | hardcore <sono|cru|temp> <on|off> | hardcore teste <acordado N|temp N|doente|vomitar>")]
    public static string CmdHardcore(string[] args)
    {
        if (I == null) return "Hardcore Survival não está ativo.";
        if (args.Length >= 2 && (args[1] == "on" || args[1] == "off"))
        {
            bool on = args[1] == "on";
            if (args[0] == "sono") SleepOn = on;
            else if (args[0] == "cru") RawOn = on;
            else if (args[0] == "temp") TempOn = on;
            else return "Usa: hardcore <sono|cru|temp> <on|off>";
            I.Save();
        }
        else if (args.Length >= 2 && args[0] == "teste")
        {
            float v = 0f;
            if (args.Length >= 3) float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            if (args[1] == "acordado") awake = Mathf.Max(0f, v);
            else if (args[1] == "temp") temp = Mathf.Clamp01(v / 100f);
            else if (args[1] == "doente") { sickLeft = 120f; sickTotal = 120f; }
            else if (args[1] == "vomitar") { Network_Player me = RAPI.GetLocalPlayer(); if (me != null) I.Vomit(me.Stats); }
            else return "Usa: hardcore teste <acordado N|temp N|doente|vomitar>";
        }
        else if (args.Length > 0) return "Usa: hardcore | hardcore <sono|cru|temp> <on|off> | hardcore teste <acordado N|temp N|doente|vomitar>";
        return "Sono " + (SleepOn ? "on" : "off") + " (acordado " + awake.ToString("0.0") + " h)"
            + " | Cru " + (RawOn ? "on" : "off") + (sickLeft > 0f ? " (intoxicado " + Mathf.CeilToInt(sickLeft) + " s)" : "")
            + " | Temperatura " + (TempOn ? "on" : "off") + " (" + Mathf.RoundToInt(temp * 100f) + "%)";
    }
}

// every island with farm animals gets 1 or 2 llamas (wool for clothes): the first farm-animal
// spawners of each island are forced to llama; how many (1 or 2) is fixed per island
[HarmonyPatch(typeof(LandmarkEntitySpawner), "SetRandomEntityType")]
static class P_Llama
{
    static bool Farm(AI_NetworkBehaviourType t) { return t == AI_NetworkBehaviourType.Llama || t == AI_NetworkBehaviourType.Goat || t == AI_NetworkBehaviourType.Chicken; }

    static void Postfix(LandmarkEntitySpawner __instance)
    {
        try
        {
            if (__instance.spawnabeEntityTypes == null || !System.Array.Exists(__instance.spawnabeEntityTypes, Farm)) return;
            Landmark lm = __instance.GetComponentInParent<Landmark>();
            if (lm == null) return;
            int rank = 0;
            foreach (LandmarkEntitySpawner s in lm.GetComponentsInChildren<LandmarkEntitySpawner>(true))
            {
                if (s == __instance) break;
                if (s.spawnabeEntityTypes != null && System.Array.Exists(s.spawnabeEntityTypes, Farm)) rank++;
            }
            int quota = 1 + (int)(lm.uniqueLandmarkIndex % 2u);
            if (rank < quota) __instance.entityType = AI_NetworkBehaviourType.Llama;
        }
        catch { }
    }
}

[HarmonyPatch(typeof(PlayerStats), "Consume")]
static class P_Consume
{
    static float before, max;
    static void Prefix(PlayerStats __instance)
    {
        before = 0f; max = 0f;
        try { before = __instance.stat_hunger.Normal.Value; max = __instance.stat_hunger.Normal.Max; } catch { }
    }
    static void Postfix(PlayerStats __instance, Item_Base edibleItem)
    {
        try
        {
            Network_Player np = __instance.GetComponent<Network_Player>();
            if (np == null || !np.IsLocalPlayer) return;
            HardcoreSurvival.OnAte(edibleItem, before, max);
        }
        catch (Exception e) { Debug.LogWarning("[HardcoreSurvival] consume: " + e.Message); }
    }
}

namespace BCUI_HC
{
    using UnityEngine;
    using UnityEngine.UI;

// Three extra rows cloned from the game's own stat bars (thirst/hunger/health), stacked above them.
    // Minecraft-style status right above the hotbar, split in the middle. Left half: health with the
    // equipped gear as icons above it. Right half: hunger, thirst, energy, heat and sickness stacked up.
    // The oxygen bar is never shown.
    public static class MineHud
    {
        class Orig { public Vector2 pos; public LayoutElement le; public bool added, ign; }
        static readonly System.Collections.Generic.Dictionary<RectTransform, Orig> touched = new System.Collections.Generic.Dictionary<RectTransform, Orig>();
        static readonly System.Collections.Generic.List<CanvasGroup> hiddenCg = new System.Collections.Generic.List<CanvasGroup>();
        static RectTransform health, hunger, thirst, oxygen;
        static float nextFind;
        static RectTransform gear;
        static Image[] gearBg = new Image[0], gearIc = new Image[0];
        static Sprite iconBg;
        static readonly Vector3[] wc = new Vector3[4];

        static Rect Bounds(RectTransform rt, RectTransform space)
        {
            rt.GetWorldCorners(wc);
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 q = space.InverseTransformPoint(wc[i]);
                x0 = Mathf.Min(x0, q.x); x1 = Mathf.Max(x1, q.x); y0 = Mathf.Min(y0, q.y); y1 = Mathf.Max(y1, q.y);
            }
            return Rect.MinMaxRect(x0, y0, x1, y1);
        }

        static Rect Union(Rect a, Rect b)
        {
            return Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));
        }

        // what a stat row draws: its bar and its icon (the glow image is far larger)
        static Rect Box(RectTransform row, RectTransform space)
        {
            bool any = false; Rect r = new Rect();
            foreach (Transform c in row)
            {
                if (c.name != "SliderBG" && c.name != "StatIconBG") continue;
                Rect b = Bounds((RectTransform)c, space);
                r = any ? Union(r, b) : b;
                any = true;
            }
            return any ? r : Bounds(row, space);
        }

        static string IconOf(RectTransform row)
        {
            foreach (Image im in row.GetComponentsInChildren<Image>(true))
                if (im.sprite != null && im.sprite.name.StartsWith("StatIcon_")) return im.sprite.name;
            return "";
        }

        static void Find(RectTransform par)
        {
            health = hunger = thirst = oxygen = null;
            foreach (Transform c in par)
            {
                if (c.name.StartsWith("BC_")) continue;
                RectTransform rt = c as RectTransform;
                if (rt == null) continue;
                string ic = IconOf(rt).ToLower();
                if (ic.Contains("oxygen") || ic.Contains("air")) oxygen = rt;
                else if (ic.Contains("health")) health = rt;
                else if (ic.Contains("hunger") || ic.Contains("food")) hunger = rt;
                else if (ic.Contains("thirst") || ic.Contains("water")) thirst = rt;
                else if (ic != "") Debug.Log("[HardcoreSurvival] hud: barra desconhecida " + c.name + " " + ic);
                if (iconBg == null)
                    foreach (Image im in rt.GetComponentsInChildren<Image>(true))
                        if (im.sprite != null && im.sprite.name == "StatIconBG") { iconBg = im.sprite; break; }
            }
        }

        static void Free(RectTransform rt)
        {
            if (touched.ContainsKey(rt)) return;
            Orig o = new Orig();
            o.pos = rt.anchoredPosition;
            o.le = rt.GetComponent<LayoutElement>();
            if (o.le == null) { o.le = rt.gameObject.AddComponent<LayoutElement>(); o.added = true; }
            o.ign = o.le.ignoreLayout;
            o.le.ignoreLayout = true;
            touched[rt] = o;
        }

        static Vector2 Delta(RectTransform row, RectTransform par, bool fromLeft, float x, float y)
        {
            Rect b = Box(row, par);
            return new Vector2(Mathf.Round(fromLeft ? x - b.xMin : x - b.xMax), Mathf.Round(y - b.yMin));
        }

        static float Place(RectTransform row, RectTransform par, bool fromLeft, float x, float y)
        {
            if (row == null || !row.gameObject.activeInHierarchy) return y;
            Free(row);
            row.anchoredPosition += Delta(row, par, fromLeft, x, y);
            return y + Box(row, par).height + 6f;
        }

        public static void Tick(NativeRows nat)
        {
            try
            {
                RectTransform par = nat.Row(0) != null ? nat.Row(0).parent as RectTransform : null;
                if (par == null) return;
                float now = Time.unscaledTime;
                if (now >= nextFind) { nextFind = now + 2f; Find(par); }
                Network_Player me = RAPI.GetLocalPlayer();
                PlayerInventory inv = me != null ? me.Inventory : null;
                if (inv == null) return;
                // the hotbar slots, in the stat rows' space
                bool any = false; Rect hb = new Rect();
                foreach (Slot sl in inv.allSlots)
                {
                    if (sl == null || sl.slotType != SlotType.Hotbar || !sl.gameObject.activeInHierarchy) continue;
                    Rect b = Bounds((RectTransform)sl.transform, par);
                    hb = any ? Union(hb, b) : b;
                    any = true;
                }
                if (!any) return;
                float y0 = hb.yMax + 12f;

                if (oxygen != null)
                {
                    CanvasGroup cg = oxygen.GetComponent<CanvasGroup>();
                    if (cg == null) cg = oxygen.gameObject.AddComponent<CanvasGroup>();
                    if (!hiddenCg.Contains(cg)) hiddenCg.Add(cg);
                    if (cg.alpha != 1f) cg.alpha = 1f;
                }

                // left half: health, the gear above it
                float yl = Place(health, par, true, hb.xMin, y0);
                Air(me, par, hb.xMin, yl);
                Tint(hunger, new Color32(0x9C, 0x5B, 0x34, 255));
                Tint(thirst, new Color32(0x3B, 0x8E, 0xD4, 255));
                // sleep, heat and sickness sit in the screen's bottom-left corner, stacked up from the very bottom
                Canvas cv = par.GetComponentInParent<Canvas>();
                RectTransform root = cv != null ? cv.rootCanvas.transform as RectTransform : null;
                if (root != null)
                {
                    Rect scr = Bounds(root, par);
                    float yc = scr.yMin + 16f;
                    for (int i = 0; i <= 2; i++)
                    {
                        RectTransform r = nat.Row(i);
                        if (r == null || !r.gameObject.activeInHierarchy) continue;
                        nat.MoveBy(i, Delta(r, par, true, scr.xMin + 16f, yc));
                        yc += Box(r, par).height + 6f;
                    }
                }
                // the gear sits to the right of the sleep bar
                RectTransform sleep = nat.Row(0);
                if (sleep != null && sleep.gameObject.activeInHierarchy)
                {
                    Rect sb = Box(sleep, par);
                    Gear(inv, par, sb.xMax + 14f, Mathf.Round(sb.center.y - 19f));
                }
                else Gear(inv, par, hb.xMin, yl);

                // right half: hunger, thirst, then the mod rows, all ending at the hotbar right edge
                float yr = Place(hunger, par, false, hb.xMax, y0);
                yr = Place(thirst, par, false, hb.xMax, yr);
            }
            catch (System.Exception e) { Debug.LogWarning("[HardcoreSurvival] hud minecraft: " + e.Message); nextFind = 0f; }
        }

        // the bar fill colour of a native row (both sliders: value and target)
        static void Tint(RectTransform row, Color c)
        {
            if (row == null) return;
            foreach (Slider s in row.GetComponentsInChildren<Slider>(true))
            {
                if (s.fillRect == null) continue;
                Image im = s.fillRect.GetComponent<Image>();
                if (im == null) continue;
                Color k = c; k.a = im.color.a;
                if (im.color != k) im.color = k;
            }
        }

        // a white air bar right above health (a stripped copy of the health row, bubbles for its icon); replaces the game's radial
        static GameObject air;
        static Slider[] airSl = new Slider[0];
        static CanvasGroup radialCg;
        static void Air(Network_Player me, RectTransform par, float x, float y)
        {
            PlayerStats st = me.Stats;
            if (st == null || st.stat_oxygen == null) return;
            if (radialCg == null && st.canvas != null && st.canvas.fatigueParent != null)
            {
                radialCg = st.canvas.fatigueParent.GetComponent<CanvasGroup>();
                if (radialCg == null) radialCg = st.canvas.fatigueParent.AddComponent<CanvasGroup>();
            }
            if (radialCg != null && radialCg.alpha != 0f) radialCg.alpha = 0f;
            if ((air == null || air.transform.parent != par) && health != null)
            {
                if (air != null) Object.Destroy(air);
                air = Object.Instantiate(health.gameObject, par, false);
                air.name = "BC_HC_Air";
                foreach (MonoBehaviour mb in air.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb != null && !(mb is Graphic) && !(mb is Slider) && !(mb is LayoutElement) && !(mb is UnityEngine.UI.Mask) && !(mb is UnityEngine.UI.RectMask2D) && !(mb is Shadow)) Object.DestroyImmediate(mb);
                LayoutElement le = air.GetComponent<LayoutElement>();
                if (le == null) le = air.AddComponent<LayoutElement>();
                le.ignoreLayout = true;
                // the heart becomes a cluster of bubbles, in the same dark brown as the other stat icons
                foreach (Image im in air.GetComponentsInChildren<Image>(true))
                    if (im.sprite != null && im.sprite.name.StartsWith("StatIcon_")) { im.sprite = Bubbles(); im.color = new Color32(0x43, 0x22, 0x0D, 255); im.preserveAspect = true; im.enabled = true; }
                airSl = air.GetComponentsInChildren<Slider>(true);
                foreach (Slider s in airSl)
                {
                    s.interactable = false; s.minValue = 0f; s.maxValue = 1f;
                    if (s.fillRect != null) { Image f = s.fillRect.GetComponent<Image>(); if (f != null) f.color = new Color(0.95f, 0.95f, 0.93f, f.color.a); }
                }
            }
            if (air == null) return;
            float v = st.stat_oxygen.NormalValue;
            bool show = v < 0.999f;
            if (air.activeSelf != show) air.SetActive(show);
            if (!show) return;
            foreach (Slider s in airSl) if (s != null) s.value = v;
            RectTransform rt = (RectTransform)air.transform;
            rt.anchoredPosition += Delta(rt, par, true, x, y);
        }

        // three bubbles (big, medium, small): a rim, a faint fill and a glint top-left left clear
        static Sprite bubbles;
        static Sprite Bubbles()
        {
            if (bubbles != null) return bubbles;
            const int n = 64;
            Texture2D t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear;
            Vector3[] b = { new Vector3(26f, 24f, 18f), new Vector3(47f, 46f, 11f), new Vector3(20f, 52f, 7f) };
            Color32[] px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float a = 0f;
                    for (int i = 0; i < b.Length; i++)
                    {
                        float r = b[i].z, w = Mathf.Max(2.4f, r * 0.2f);
                        float dx = x + 0.5f - b[i].x, dy = y + 0.5f - b[i].y;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        float rim = Mathf.Clamp01(w * 0.5f + 0.5f - Mathf.Abs(d - (r - w * 0.5f)));
                        float fill = Mathf.Clamp01(r - d + 0.5f) * 0.22f;
                        float gx = x + 0.5f - (b[i].x - r * 0.4f), gy = y + 0.5f - (b[i].y + r * 0.4f), gr = r * 0.24f;
                        // the glint is cut out of the fill (the icon is a dark stencil, like the game's)
                        float glint = Mathf.Clamp01(gr - Mathf.Sqrt(gx * gx + gy * gy) + 0.5f);
                        a = Mathf.Max(a, Mathf.Max(rim, fill * 1.6f * (1f - glint)));
                    }
                    px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            t.SetPixels32(px); t.Apply(false, true);
            bubbles = Sprite.Create(t, new Rect(0f, 0f, n, n), new Vector2(0.5f, 0.5f), 100f);
            bubbles.name = "BC_HC_Bubbles";
            return bubbles;
        }

        static void Gear(PlayerInventory inv, RectTransform par, float x, float y)
        {
            int n = inv.equipSlots.Count;
            if (gear == null || gear.parent != par || gearIc.Length != n)
            {
                if (gear != null) Object.Destroy(gear.gameObject);
                gear = Kit.Node(par, "BC_HC_Gear");
                gear.anchorMin = gear.anchorMax = new Vector2(0.5f, 0.5f); gear.pivot = Vector2.zero;
                gear.sizeDelta = new Vector2(Mathf.Max(1, n) * 42f, 38f);
                gearBg = new Image[n]; gearIc = new Image[n];
                for (int i = 0; i < n; i++)
                {
                    gearBg[i] = Kit.Img(gear, "Bg" + i, iconBg, iconBg != null ? Color.white : new Color(0f, 0f, 0f, 0.35f), false);
                    gearBg[i].raycastTarget = false;
                    gearIc[i] = Kit.Img(gearBg[i].rectTransform, "Ic", null, Color.white, false);
                    gearIc[i].raycastTarget = false; gearIc[i].preserveAspect = true;
                    RectTransform ir = gearIc[i].rectTransform;
                    ir.anchorMin = Vector2.zero; ir.anchorMax = Vector2.one; ir.offsetMin = new Vector2(5f, 5f); ir.offsetMax = new Vector2(-5f, -5f);
                }
            }
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                Slot sl = inv.equipSlots[i];
                Item_Base it = sl != null ? sl.GetItemBase() : null;
                Sprite sp = it != null && it.settings_Inventory != null ? it.settings_Inventory.Sprite : null;
                gearBg[i].gameObject.SetActive(sp != null);
                if (sp == null) continue;
                gearIc[i].sprite = sp;
                RectTransform br = gearBg[i].rectTransform;
                br.anchorMin = br.anchorMax = Vector2.zero; br.pivot = Vector2.zero;
                br.sizeDelta = new Vector2(38f, 38f);
                br.anchoredPosition = new Vector2(k * 42f, 0f);
                k++;
            }
            gear.SetAsLastSibling();
            // the gear holder is n*42 wide from its pivot: its bottom-left goes to (x, y)
            Rect b = Bounds(gear, par);
            gear.anchoredPosition += new Vector2(Mathf.Round(x - b.xMin), Mathf.Round(y - b.yMin));
        }

        public static void Restore()
        {
            foreach (System.Collections.Generic.KeyValuePair<RectTransform, Orig> kv in touched)
            {
                if (kv.Key == null) continue;
                kv.Key.anchoredPosition = kv.Value.pos;
                if (kv.Value.le != null) { if (kv.Value.added) Object.Destroy(kv.Value.le); else kv.Value.le.ignoreLayout = kv.Value.ign; }
            }
            touched.Clear();
            foreach (CanvasGroup cg in hiddenCg) if (cg != null) cg.alpha = 1f;
            hiddenCg.Clear();
            if (gear != null) Object.Destroy(gear.gameObject);
            if (air != null) Object.Destroy(air);
            if (radialCg != null) radialCg.alpha = 1f;
            air = null; radialCg = null;
            gear = null; health = hunger = thirst = oxygen = null;
        }
    }

    public class NativeRows
    {
        GameObject[] rows = new GameObject[3];
        Slider[][] sl = new Slider[3][];
        Image[][] fills = new Image[3][];
        Image[] icon = new Image[3];
        Sprite[] iconSp = new Sprite[3];
        Text[] labels = new Text[3];
        Vector2[] basePos = new Vector2[3];
        static readonly string[] Names = { "ENERGIA", "CALOR", "SAÚDE" };
        static readonly string[][] Icons = {
            new string[] { "Placeable_Bed_Hammock", "Placeable_Bed_Basic", "Placeable_Bed_Wood", "Bed" },
            new string[] { "Torch", "Placeable_Lantern_FireBasket", "Placeable_Lantern_Basic", "Lantern" },
            new string[] { "HealingSalve", "HealingSalve_Good", "Salve", "Bandage" } };

        public static NativeRows Build()
        {
            try
            {
                UISlider_Stat[] all = Object.FindObjectsOfType<UISlider_Stat>();
                if (all == null || all.Length < 2) return null;
                // the visible stat bars on the left side of the screen
                System.Collections.Generic.List<RectTransform> rs = new System.Collections.Generic.List<RectTransform>();
                foreach (UISlider_Stat s in all)
                {
                    if (s == null || !s.gameObject.activeInHierarchy) continue;
                    RectTransform row = RowOf(s.transform as RectTransform);
                    if (row != null && !rs.Contains(row) && row.position.x < Screen.width * 0.35f) rs.Add(row);
                }
                if (rs.Count < 2) return null;
                rs.Sort(delegate (RectTransform a, RectTransform b) { return a.position.y.CompareTo(b.position.y); });
                RectTransform top = rs[rs.Count - 1], below = rs[rs.Count - 2];
                if (top.parent != below.parent) return null;
                Vector2 step = top.anchoredPosition - below.anchoredPosition;
                if (step.sqrMagnitude < 1f) return null;
                NativeRows n = new NativeRows();
                for (int i = 0; i < 3; i++)
                {
                    // clone under an inactive holder so the copied game scripts never wake up
                    // (an awake copy re-registers with the player stats and freezes the real hunger/thirst bars)
                    GameObject hold = new GameObject("BC_HC_Hold"); hold.SetActive(false);
                    GameObject g = Object.Instantiate(top.gameObject, hold.transform);
                    g.name = "BC_HC_Row" + i;
                    foreach (UISlider u in g.GetComponentsInChildren<UISlider>(true)) Object.DestroyImmediate(u);
                    foreach (MonoBehaviour mb in g.GetComponentsInChildren<MonoBehaviour>(true))
                        if (mb != null && !(mb is Graphic) && !(mb is Slider) && !(mb is LayoutElement) && !(mb is UnityEngine.UI.Mask) && !(mb is UnityEngine.UI.RectMask2D) && !(mb is Shadow)) Object.DestroyImmediate(mb);
                    g.transform.SetParent(top.parent, false);
                    Object.Destroy(hold);
                    foreach (LayoutElement le in g.GetComponentsInChildren<LayoutElement>(true)) le.ignoreLayout = true;
                    RectTransform rt = g.transform as RectTransform;
                    n.basePos[i] = top.anchoredPosition + step * (3 - i);
                    rt.anchoredPosition = n.basePos[i];
                    // a stat row has two sliders (value + target): drive both with our value
                    Slider[] ss = g.GetComponentsInChildren<Slider>(true);
                    System.Collections.Generic.List<Image> fl = new System.Collections.Generic.List<Image>();
                    foreach (Slider s in ss)
                    {
                        s.interactable = false;
                        s.minValue = 0f; s.maxValue = 1f;
                        if (s.fillRect != null) { Image fi = s.fillRect.GetComponent<Image>(); if (fi != null) fl.Add(fi); }
                    }
                    // blink overlays, bonus bars and thirst threshold markers are not ours
                    RectTransform notch = null;
                    System.Collections.Generic.List<RectTransform> notches = new System.Collections.Generic.List<RectTransform>();
                    foreach (Transform tr in g.GetComponentsInChildren<Transform>(true))
                    {
                        string tn = tr.name.ToLower();
                        if (tn.Contains("divider") && notch == null && tr.GetComponent<Image>() != null) notch = tr as RectTransform;
                        if (tn.Contains("divider") && tr.GetComponent<Image>() != null) notches.Add(tr as RectTransform);
                        if (tn.Contains("blink") || tn.Contains("bonus") || tn.Contains("divider")) tr.gameObject.SetActive(false);
                    }
                    // heat row: the game's own notch marks where the cold (35%) begins
                    if (i == 1 && notch != null && ss.Length > 0 && ss[0].fillRect != null)
                    {
                        // same parent as the game's notches (the row itself), on top of everything, spread over the visible fill
                        Canvas.ForceUpdateCanvases();
                        RectTransform area = ss[ss.Length - 1].fillRect.parent as RectTransform;
                        Vector3[] wc = new Vector3[4]; area.GetWorldCorners(wc);
                        RectTransform par = notch.parent as RectTransform;
                        Vector3 l = par.InverseTransformPoint(wc[0]), r = par.InverseTransformPoint(wc[2]);
                        Debug.Log("[HardcoreSurvival] marcadores calor: " + notch.name + " " + notch.rect.size + " x " + l.x + ".." + r.x);
                        // the game uses a pair of notches (top and bottom edge) per marker
                        foreach (RectTransform nt in notches)
                        foreach (float f in new float[] { 0.35f })
                        {
                            GameObject m = Object.Instantiate(nt.gameObject, par, false);
                            m.name = "BC_HC_Mark";
                            m.SetActive(true);
                            RectTransform mr = m.transform as RectTransform;
                            Vector3 lp = mr.localPosition;
                            mr.localPosition = new Vector3(Mathf.Lerp(l.x, r.x, f) + (mr.pivot.x - 0.5f) * mr.rect.width, lp.y, lp.z);
                            mr.SetAsLastSibling();
                            Image mi = m.GetComponent<Image>(); mi.raycastTarget = false; mi.enabled = true;
                        }
                    }
                    n.sl[i] = ss;
                    n.fills[i] = fl.ToArray();
                    if (i == 0) Dump(g.transform, "");
                    SetIcon(n, i, g, ss, Icons[i]);
                    Text lb = Kit.Txt(rt, "Label", Names[i], 15, Kit.Cream, TextAnchor.MiddleLeft, true);
                    lb.gameObject.SetActive(false);
                    RectTransform lr = lb.rectTransform;
                    lr.anchorMin = new Vector2(1f, 0.5f); lr.anchorMax = new Vector2(1f, 0.5f); lr.pivot = new Vector2(0f, 0.5f);
                    lr.anchoredPosition = new Vector2(10f, 0f); lr.sizeDelta = new Vector2(220f, 26f);
                    lr.localScale = Vector3.one;
                    n.labels[i] = lb;
                    n.rows[i] = g;
                }
                return n;
            }
            catch (System.Exception e) { Debug.LogWarning("[HardcoreSurvival] hud nativo: " + e.Message); return null; }
        }

        static RectTransform RowOf(RectTransform s)
        {
            // climb until the node also holds the icon (an Image outside the slider)
            RectTransform cur = s;
            for (int k = 0; k < 3 && cur != null; k++)
            {
                RectTransform p = cur.parent as RectTransform;
                if (p == null) break;
                if (p.GetComponentsInChildren<UISlider_Stat>(true).Length > 1) return cur;
                cur = p;
            }
            return cur;
        }

        static void Dump(Transform t, string ind)
        {
            Image im = t.GetComponent<Image>();
            Debug.Log("[HardcoreSurvival] ui " + ind + t.name + (im != null ? " img=" + (im.sprite != null ? im.sprite.name : "null") + " " + im.rectTransform.rect.size : "") + (t.GetComponent<Slider>() != null ? " SLIDER" : ""));
            foreach (Transform c in t) Dump(c, ind + "  ");
        }

        static Sprite FindSprite(string[] keys)
        {
            try
            {
                // exact names first, then partial matches in key order
                foreach (string k in keys)
                {
                    Item_Base it = ItemManager.GetItemByName(k);
                    if (it != null && it.settings_Inventory != null && it.settings_Inventory.Sprite != null) return it.settings_Inventory.Sprite;
                }
                foreach (string k in keys)
                    foreach (Item_Base it in ItemManager.GetAllItems())
                        if (it != null && it.settings_Inventory != null && it.settings_Inventory.Sprite != null && it.UniqueName.ToLower().Contains(k.ToLower()))
                            return it.settings_Inventory.Sprite;
            }
            catch { }
            return null;
        }

        static bool InSlider(Transform t, Slider[] ss)
        {
            foreach (Slider s in ss) if (t.IsChildOf(s.transform)) return true;
            return false;
        }

        static float Seg(float px, float py, float ax, float ay, float bx, float by)
        {
            float dx = bx - ax, dy = by - ay;
            float h = Mathf.Clamp01(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy));
            float ex = px - ax - dx * h, ey = py - ay - dy * h;
            return Mathf.Sqrt(ex * ex + ey * ey);
        }

        static float ZDist(float x, float y, float cx, float cy, float s, float th)
        {
            float l = cx - s, r = cx + s, b = cy - s, tp = cy + s;
            float d = Seg(x, y, l, tp, r, tp);
            d = Mathf.Min(d, Seg(x, y, r, tp, l, b));
            d = Mathf.Min(d, Seg(x, y, l, b, r, b));
            return d - th;
        }

        // signed distance (uv units, negative inside) of each stencil glyph
        static float Glyph(int kind, float x, float y)
        {
            if (kind == 0)
            {
                float d = ZDist(x, y, 0.32f, 0.30f, 0.17f, 0.055f);
                d = Mathf.Min(d, ZDist(x, y, 0.63f, 0.58f, 0.12f, 0.045f));
                return Mathf.Min(d, ZDist(x, y, 0.84f, 0.82f, 0.075f, 0.035f));
            }
            if (kind == 2)
            {
                float d = Mathf.Min(Seg(x, y, 0.5f, 0.14f, 0.5f, 0.86f), Seg(x, y, 0.14f, 0.5f, 0.86f, 0.5f));
                return d - 0.12f;
            }
            // flame like the emoji: tall central tongue, two side tongues, inner flame cut out
            float body = Tear(x, y, 0.5f, 0.35f, 0.31f, 0.56f, 0.97f, 0.85f);
            body = Mathf.Min(body, Tear(x, y, 0.40f, 0.44f, 0.17f, 0.17f, 0.76f, 1f));
            body = Mathf.Min(body, Tear(x, y, 0.62f, 0.42f, 0.16f, 0.83f, 0.66f, 1f));
            float hole = Tear(x, y, 0.5f, 0.28f, 0.12f, 0.45f, 0.60f, 1f);
            return Mathf.Max(body, -hole);
        }

        // teardrop: circle of radius r at (cx, by) tapering to a tip at (tx, ty)
        static float Tear(float x, float y, float cx, float by, float r, float tx, float ty, float pw)
        {
            if (y <= by)
                return Mathf.Sqrt((x - cx) * (x - cx) + (y - by) * (y - by)) - r;
            if (y >= ty) return Mathf.Sqrt((x - tx) * (x - tx) + (y - ty) * (y - ty));
            float k = (y - by) / (ty - by);
            float mx = Mathf.Lerp(cx, tx, k * k);
            float half = r * Mathf.Pow(1f - k, pw);
            return (Mathf.Abs(x - mx) - half) * 0.8f;
        }
        static Sprite[] glyphs = new Sprite[3];
        static Sprite GlyphSprite(int kind)
        {
            if (glyphs[kind] != null) return glyphs[kind];
            int n = 96;
            Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            Color32[] px = new Color32[n * n];
            for (int yy = 0; yy < n; yy++)
                for (int xx = 0; xx < n; xx++)
                {
                    float d = Glyph(kind, (xx + 0.5f) / n, (yy + 0.5f) / n) * n;
                    float a = Mathf.Clamp01(0.5f - d);
                    px[yy * n + xx] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px); tex.Apply();
            glyphs[kind] = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
            glyphs[kind].name = "BC_Glyph" + kind;
            return glyphs[kind];
        }

        static void SetIcon(NativeRows n, int i, GameObject g, Slider[] ss, string[] keys)
        {
            Sprite sp = GlyphSprite(i);
            Debug.Log("[HardcoreSurvival] icon " + i + " = " + (sp != null ? sp.name : "nenhum"));
            if (sp == null) return;
            // prefer an Image named like an icon; else the smallest sprite Image outside the bars
            Image best = null; float area = float.MaxValue;
            foreach (Image im in g.GetComponentsInChildren<Image>(true))
            {
                if (InSlider(im.transform, ss) || im.sprite == null) continue;
                string nm = im.name.ToLower();
                if (im.sprite.name.StartsWith("StatIcon_")) { best = im; break; }
                if (nm.Contains("icon") && !im.sprite.name.Contains("BG")) { best = im; break; }
                Rect r = im.rectTransform.rect;
                float a = r.width * r.height;
                if (a > 1f && a < area) { area = a; best = im; }
            }
            if (best == null) return;
            Debug.Log("[HardcoreSurvival] icon " + i + " no objeto " + best.name);
            // keep the original glyph tint so the item reads as the same dark-brown stencil
            best.sprite = sp; best.overrideSprite = null; best.preserveAspect = true; best.color = new Color32(0x43, 0x22, 0x0D, 255);
            n.icon[i] = best; n.iconSp[i] = sp;
        }

        public bool Alive() { return rows[0] != null; }
        public void Show(bool on) { for (int i = 0; i < 3; i++) if (rows[i] != null) rows[i].SetActive(on); }
        public void Kill() { for (int i = 0; i < 3; i++) if (rows[i] != null) Object.Destroy(rows[i]); }

        public void SetRow(int i, float k, Color c, string text, Color tc, float flash, float scale)
        {
            if (rows[i] == null) return;
            if (i == 1) flash = 0f; // the heat bar never flashes
            if (sl[i] != null) foreach (Slider s in sl[i]) if (s != null) s.value = k;
            Color fc = Color.Lerp(c, Color.white, flash * 0.5f); fc.a = 1f;
            if (fills[i] != null) foreach (Image f in fills[i]) if (f != null) f.color = fc;
            if (icon[i] != null) { if (icon[i].sprite != iconSp[i]) icon[i].sprite = iconSp[i]; icon[i].color = new Color32(0x43, 0x22, 0x0D, 255); }
            if (labels[i] != null)
            {
                labels[i].text = Names[i] + "  " + text.ToUpper();
                labels[i].color = Color.Lerp(Kit.Cream, tc, 0.5f);
                labels[i].rectTransform.localScale = new Vector3(scale, scale, 1f);
            }
        }

        public RectTransform Row(int i) { return rows[i] != null ? (RectTransform)rows[i].transform : null; }
        public void MoveBy(int i, Vector2 d) { if (rows[i] != null) basePos[i] = ((RectTransform)rows[i].transform).anchoredPosition + d; }

        public void Shake(float dx)
        {
            for (int i = 0; i < 3; i++)
                if (rows[i] != null) ((RectTransform)rows[i].transform).anchoredPosition = basePos[i] + new Vector2(dx, 0f);
        }
    }

    public class HcHud : MonoBehaviour
    {
        static HcHud inst;
        const float W = 262f, RowH = 27f, HeadH = 34f;
        // sits right above the Skills panel (22 + its 210 height + gap)
        const float Bottom = 22f + 34f + 6 * 27f + 14f + 10f;

        RectTransform box, lidTop, lidBot, eyesRt;
        Image eyes;
        static Sprite eyeMask;

        // black screen with two soft almond-shaped holes (the eyes), generated once
        static Sprite EyeMask()
        {
            if (eyeMask != null) return eyeMask;
            int w = 512, h = 256;
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            Color32[] px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = x / (float)(w - 1), v = y / (float)(h - 1) - 0.5f;
                    float best = 9f;
                    for (int e = 0; e < 2; e++)
                    {
                        float cx = e == 0 ? 0.35f : 0.65f;
                        float dx = (u - cx) / 0.34f;
                        float ax = Mathf.Abs(dx);
                        if (ax >= 1f) continue;
                        float half = 0.42f * Mathf.Pow(1f - ax * ax, 0.6f);
                        float d = Mathf.Abs(v) / Mathf.Max(0.001f, half);
                        d = Mathf.Max(d, ax);
                        if (d < best) best = d;
                    }
                    float a = Mathf.SmoothStep(0f, 1f, (best - 0.4f) / 0.6f);
                    px[y * w + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply();
            eyeMask = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f));
            return eyeMask;
        }
        Image overlay, flashImg;
        Text[] val = new Text[3];
        RectTransform[] fill = new RectTransform[3];
        Image[] fillImg = new Image[3], rowFlash = new Image[3];
        float[] shown = { 1f, 1f, 1f };
        int[] state = new int[3];
        float[] rowFlashAt = { -9f, -9f, -9f };
        float bornAt;
        Vector2 boxPos;

        public static HcHud Get()
        {
            if (inst == null)
            {
                // leftovers from an earlier version of the mod (hot reload) would show the old panel
                foreach (Transform tr in Object.FindObjectsOfType<Transform>())
                    if (tr != null && (tr.name == "BC_Hardcore" || tr.name == "BC_HardcoreFx" || tr.name.StartsWith("BC_HC_Row"))) Object.Destroy(tr.gameObject);
                RectTransform rt = Kit.Node(Kit.Root(), "BC_Hardcore");
                Kit.Stretch(rt, 0f, 0f, 0f, 0f);
                rt.SetAsFirstSibling();
                inst = rt.gameObject.AddComponent<HcHud>();
                inst.Build();
            }
            if (!inst.gameObject.activeSelf) { inst.gameObject.SetActive(true); inst.bornAt = Time.time; }
            return inst;
        }
        public static void Hide() { if (inst != null && inst.gameObject.activeSelf) inst.gameObject.SetActive(false); }
        public static void Kill() { MineHud.Restore(); if (inst != null) Object.Destroy(inst.gameObject); inst = null; }

        // screen effects live on their own canvas BELOW the game HUD (hotbar, stats stay visible)
        GameObject fxGo;
        void OnEnable() { if (fxGo != null) fxGo.SetActive(true); if (nat != null) nat.Show(true); }
        void OnDisable() { if (fxGo != null) fxGo.SetActive(false); if (nat != null) nat.Show(false); }
        void OnDestroy() { if (fxGo != null) Object.Destroy(fxGo); if (nat != null) nat.Kill(); }
        Transform FxRoot()
        {
            fxGo = new GameObject("BC_HardcoreFx");
            Object.DontDestroyOnLoad(fxGo);
            Canvas c = fxGo.AddComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = -100;
            CanvasScaler s = fxGo.AddComponent<CanvasScaler>();
            s.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            s.referenceResolution = new Vector2(1920f, 1080f);
            s.matchWidthOrHeight = 1f;
            return fxGo.transform;
        }

        static Image Full(Transform parent, string name)
        {
            Image i = Kit.Img(parent, name, null, new Color(0f, 0f, 0f, 0f), false);
            i.raycastTarget = false;
            Kit.Stretch(i.rectTransform, 0f, 0f, 0f, 0f);
            return i;
        }

        void Build()
        {
            bornAt = Time.time;
            Transform fx = FxRoot();
            overlay = Full(fx, "Overlay");
            flashImg = Full(fx, "Flash");
            Image top = Kit.Img(fx, "LidTop", null, Color.black, false); top.raycastTarget = false;
            Image bot = Kit.Img(fx, "LidBot", null, Color.black, false); bot.raycastTarget = false;
            lidTop = top.rectTransform; lidBot = bot.rectTransform;
            lidTop.anchorMin = new Vector2(0f, 1f); lidTop.anchorMax = new Vector2(1f, 1f); lidTop.pivot = new Vector2(0.5f, 1f);
            lidBot.anchorMin = new Vector2(0f, 0f); lidBot.anchorMax = new Vector2(1f, 0f); lidBot.pivot = new Vector2(0.5f, 0f);
            lidTop.sizeDelta = Vector2.zero; lidBot.sizeDelta = Vector2.zero;
            eyes = Kit.Img(fx, "Eyes", EyeMask(), new Color(1f, 1f, 1f, 0f), false);
            eyes.raycastTarget = false;
            eyesRt = eyes.rectTransform;
            eyesRt.anchorMin = new Vector2(0f, 0.5f); eyesRt.anchorMax = new Vector2(1f, 0.5f); eyesRt.pivot = new Vector2(0.5f, 0.5f);
            eyesRt.sizeDelta = new Vector2(0f, 1080f);

            box = Kit.Node(transform, "Panel");
            float h = HeadH + 3 * RowH + 14f;
            boxPos = new Vector2(-22f, Bottom);
            Kit.Place(box, new Vector2(1f, 0f), new Vector2(1f, 0f), boxPos, new Vector2(W, h));
            box.gameObject.SetActive(false);
            CanvasGroup g = box.gameObject.AddComponent<CanvasGroup>();
            g.interactable = false; g.blocksRaycasts = false;
            Kit.Plate(box);
            Text title = Kit.Txt(box, "Title", "SOBREVIVÊNCIA", 22, Kit.Brown, TextAnchor.MiddleLeft, false);
            Kit.Place(title.rectTransform, Kit.TL, new Vector2(0f, 0.5f), new Vector2(18f, -HeadH * 0.5f - 6f), new Vector2(W - 36f, 26f));
            string[] names = { "Energia", "Calor", "Saúde" };
            for (int i = 0; i < 3; i++)
            {
                float y = -HeadH - 6f - RowH * i - RowH * 0.5f;
                rowFlash[i] = Kit.Img(box, "RowFlash" + i, Kit.Get("BC_Round"), Kit.A(Kit.Bad, 0f), true);
                Kit.Place(rowFlash[i].rectTransform, new Vector2(0.5f, 1f), Kit.C, new Vector2(0f, y), new Vector2(W - 20f, RowH - 2f));
                Text n = Kit.Txt(box, "Name" + i, names[i], 16, Kit.Brown, TextAnchor.MiddleLeft, false);
                Kit.Place(n.rectTransform, Kit.TL, new Vector2(0f, 0.5f), new Vector2(18f, y), new Vector2(80f, RowH));
                Image bg = Kit.Img(box, "Bar" + i, Kit.Get("BC_Round"), Kit.A(Kit.Dark, 0.28f), true);
                Kit.Place(bg.rectTransform, Kit.TL, new Vector2(0f, 0.5f), new Vector2(92f, y), new Vector2(W - 92f - 96f, 10f));
                fillImg[i] = Kit.Img(bg.rectTransform, "Fill", null, Kit.Good, false);
                fill[i] = fillImg[i].rectTransform;
                fill[i].anchorMin = Vector2.zero; fill[i].anchorMax = new Vector2(1f, 1f); fill[i].pivot = new Vector2(0f, 0.5f);
                fill[i].offsetMin = new Vector2(2f, 2f); fill[i].offsetMax = new Vector2(-2f, -2f);
                val[i] = Kit.Txt(box, "Val" + i, "", 15, Kit.Brown, TextAnchor.MiddleRight, false);
                Kit.Place(val[i].rectTransform, Kit.TR, new Vector2(1f, 0.5f), new Vector2(-16f, y), new Vector2(86f, RowH));
            }
        }

        void Row(int i, float k, Color c, string text, int st, bool off)
        {
            float target = Mathf.Clamp01(off ? 0f : k);
            shown[i] = Mathf.Lerp(shown[i], target, Time.deltaTime * 4f);
            fill[i].anchorMax = new Vector2(shown[i], 1f);
            if (st > state[i]) rowFlashAt[i] = Time.time;
            state[i] = off ? 0 : st;
            float ft = (Time.time - rowFlashAt[i]) / 1.2f;
            float rf = ft < 1f ? (1f - ft) * (0.55f + 0.45f * Mathf.Sin(ft * 18f)) : 0f;
            float t = Time.time;
            if (!off && st >= 2) rf = Mathf.Max(rf, 0.18f + 0.12f * Mathf.Sin(t * 4f));
            rowFlash[i].color = Kit.A(Kit.Bad, rf * 0.45f);
            fillImg[i].color = st >= 2 ? Color.Lerp(c, Color.white, 0.25f * (0.5f + 0.5f * Mathf.Sin(t * 6f))) : c;
            val[i].text = off ? "desligado" : text;
            Color vc = off ? Kit.A(Kit.Brown, 0.45f) : (st >= 1 ? Color.Lerp(Kit.Brown, Kit.Bad, 0.6f + 0.4f * Mathf.Sin(t * (st >= 2 ? 6f : 3f))) : Kit.Brown);
            val[i].color = vc;
            float sc = st >= 2 && !off ? 1f + 0.06f * Mathf.Abs(Mathf.Sin(t * 3f)) : 1f;
            if (i == 1)
            {
                // heat row: no effects at all (no flash, no pulsing colour, no throbbing text)
                rf = 0f; sc = 1f;
                rowFlash[i].color = Kit.A(Kit.Bad, 0f);
                fillImg[i].color = c;
                vc = off ? Kit.A(Kit.Brown, 0.45f) : (st >= 1 ? Color.Lerp(Kit.Brown, Kit.Bad, 0.7f) : Kit.Brown);
                val[i].color = vc;
            }
            val[i].rectTransform.localScale = new Vector3(sc, sc, 1f);
            // energy bar: a sleepy night blue (pale when rested, deeper as you tire)
            Color fc = i == 0 ? Color.Lerp(new Color32(0x6E, 0x8F, 0xC9, 255), new Color32(0x3A, 0x4E, 0x8C, 255), 1f - shown[i]) : fillImg[i].color;
            if (nat != null) nat.SetRow(i, shown[i], fc, val[i].text, vc, rf, sc);
        }

        NativeRows nat; float natTry;

        // ---- cold status: a chip right of the hotbar (snowflake + "Com frio" / "A congelar") ----
        RectTransform chip; Image chipIcon; Text chipText; CanvasGroup chipCg; float chipK; int chipState = -1;
        static Sprite snow;
        static readonly Vector3[] hbc = new Vector3[4];
        Transform hotbar; float hotbarTry;

        static float Seg(float px, float py, float ax, float ay, float bx, float by)
        {
            float dx = bx - ax, dy = by - ay;
            float k = Mathf.Clamp01(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy));
            float ex = px - ax - dx * k, ey = py - ay - dy * k;
            return Mathf.Sqrt(ex * ex + ey * ey);
        }

        // six-armed snowflake with twigs on each arm, white (tinted by the Image)
        static Sprite Snow()
        {
            if (snow != null) return snow;
            int n = 96; Texture2D t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp;
            Color32[] px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n - 0.5f, v = (y + 0.5f) / n - 0.5f, d = 9f;
                    for (int i = 0; i < 6; i++)
                    {
                        float a = i * Mathf.PI / 3f + Mathf.PI / 2f, ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                        d = Mathf.Min(d, Seg(u, v, 0f, 0f, ca * 0.42f, sa * 0.42f));
                        float bx = ca * 0.25f, by = sa * 0.25f;
                        for (int s2 = -1; s2 <= 1; s2 += 2)
                        {
                            float b = a + s2 * 0.75f;
                            d = Mathf.Min(d, Seg(u, v, bx, by, bx + Mathf.Cos(b) * 0.13f, by + Mathf.Sin(b) * 0.13f));
                        }
                    }
                    float al = Mathf.Clamp01((0.045f - d) * n + 0.5f);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(al * 255f));
                }
            t.SetPixels32(px); t.Apply();
            snow = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
            snow.name = "BC_Snow";
            return snow;
        }

        void ColdChip(float temp, bool fire)
        {
            if (chip == null)
            {
                chip = Kit.Node(transform, "ColdChip");
                Kit.Place(chip, new Vector2(0.5f, 0f), Kit.ML, new Vector2(520f, 60f), new Vector2(180f, 52f));
                Kit.Plate(chip);
                chipIcon = Kit.Img(chip, "Icon", Snow(), Color.white, false);
                Kit.Place(chipIcon.rectTransform, Kit.ML, Kit.C, new Vector2(30f, 0f), new Vector2(34f, 34f));
                chipText = Kit.Txt(chip, "Text", "", 24, Kit.Dark, TextAnchor.MiddleLeft, false);
                Kit.Place(chipText.rectTransform, Kit.ML, Kit.ML, new Vector2(54f, 1f), new Vector2(200f, 40f));
                chipCg = chip.gameObject.AddComponent<CanvasGroup>();
                chipCg.blocksRaycasts = false; chipCg.interactable = false;
            }
            bool on = HardcoreSurvival.TempOn && temp < 0.35f;
            int state = !on ? 0 : (temp < 0.1f ? 2 : (fire ? 3 : 1));
            if (state != chipState)
            {
                chipState = state;
                if (state > 0)
                {
                    chipText.text = state == 2 ? "A congelar" : (state == 3 ? "A aquecer" : "Com frio");
                    chipText.color = state == 2 ? Kit.Bad : Kit.Dark;
                    chipIcon.color = state == 2 ? new Color(0.2f, 0.42f, 0.7f) : (state == 3 ? new Color(0.75f, 0.42f, 0.12f) : new Color(0.3f, 0.52f, 0.72f));
                    chip.sizeDelta = new Vector2(Mathf.Round(54f + chipText.preferredWidth + 18f), 52f);
                }
            }
            chipK = Mathf.MoveTowards(chipK, on ? 1f : 0f, Time.unscaledDeltaTime * 4f);
            chipCg.alpha = chipK;
            if (chip.gameObject.activeSelf != chipK > 0f) chip.gameObject.SetActive(chipK > 0f);
            if (chipK <= 0f) return;
            float pul = state == 2 ? 1f + 0.08f * Mathf.Sin(Time.unscaledTime * 3f) : 1f;
            chipIcon.rectTransform.localScale = new Vector3(pul, pul, 1f);
            // right of the hotbar, centred on its height
            if (hotbar == null && Time.unscaledTime >= hotbarTry)
            {
                hotbarTry = Time.unscaledTime + 2f;
                GameObject cv = GameObject.Find("_CanvasGame_New");
                hotbar = cv != null ? cv.transform.Find("InventoryParent/Hotbar") : null;
            }
            RectTransform hb = hotbar as RectTransform, me = (RectTransform)transform;
            if (hb != null && hb.gameObject.activeInHierarchy)
            {
                hb.GetWorldCorners(hbc);
                Vector3 r0 = me.InverseTransformPoint(hbc[0]), r2 = me.InverseTransformPoint(hbc[2]);
                // chip is anchored at the bottom centre of this root
                float x = Mathf.Max(r0.x, r2.x) + 16f - me.rect.center.x;
                float y = (r0.y + r2.y) * 0.5f - me.rect.yMin;
                chip.anchoredPosition = new Vector2(Mathf.Round(x), Mathf.Round(y));
            }
        }
        void OnDestroy2() { }
        public void Set(float fatigue, float awake, float temp, float sickLeft, float sickTotal, bool fire)
        {
            float t = Time.time;
            float energy = 1f - Mathf.Clamp01(awake / 72f);
            Row(0, energy, Color.Lerp(Kit.Bad, Kit.Good, Mathf.Clamp01(energy * 1.8f)),
                fatigue >= 1f ? "Exausto" : (fatigue > 0f ? "Cansado" : "Bem"), fatigue >= 1f ? 2 : (fatigue > 0f ? 1 : 0), !HardcoreSurvival.SleepOn);

            string tx = temp < 0.1f ? "Gelado" : (temp < 0.35f ? "Frio" : (temp > 0.85f ? "Quente" : "Normal"));
            if (HardcoreSurvival.wet > 0.3f && temp >= 0.1f) tx = "Molhado";
            if (fire && temp < 0.99f) tx = HardcoreSurvival.wet > 0.05f ? "A secar" : "A aquecer";
            // the heat bar keeps one steady colour: no pulsing, no shifting with the temperature
            Color tc = new Color(0.9f, 0.56f, 0.2f);
            Row(1, temp, tc, tx, temp < 0.1f ? 2 : (temp < 0.35f ? 1 : 0), !HardcoreSurvival.TempOn);

            bool sick = sickLeft > 0f;
            Row(2, sick ? 1f - sickLeft / 200f : 1f, sick ? Kit.Bad : Kit.Good,
                sick ? (sickLeft > 100f ? "Intoxicado" : "Enjoado") : "Bem", sick ? (sickLeft > 100f ? 2 : 1) : 0, !HardcoreSurvival.RawOn);

            ColdChip(temp, fire);

            // panel: slides in when it appears (the bars no longer shake on a bad event)
            float shake = 0f;
            float inK = Kit.EaseOut((t - bornAt) / 0.5f);
            box.anchoredPosition = boxPos + new Vector2(shake + (1f - inK) * 300f, 0f);
            if (nat == null && t >= natTry) { natTry = t + 1f; nat = NativeRows.Build(); }
            if (nat != null && !nat.Alive()) nat = null;
            if (nat != null) MineHud.Tick(nat);
            box.gameObject.SetActive(false);
            if (nat != null) nat.Shake(shake);

            // overlays: tint, eyelids, flashes
            float pulse = 0.5f + 0.5f * Mathf.Sin(t * 1.6f);
            float fat = HardcoreSurvival.FatFx, sk2 = HardcoreSurvival.SickFx, cold = HardcoreSurvival.ColdFx;
            Color oc = new Color(0f, 0f, 0f, fat * (0.1f + 0.08f * pulse));
            if (sk2 > 0f) oc = Color.Lerp(oc, new Color(0.3f, 0.42f, 0.05f, 0.1f + 0.08f * Mathf.Sin(t * 1.1f)), sk2 * 0.6f);
            if (cold > 0f) oc = Color.Lerp(oc, new Color(0.65f, 0.85f, 1f, 0.06f + 0.1f * HardcoreSurvival.FreezeFx), cold * 0.6f);
            if (HardcoreSurvival.WarmFx > 0f) oc = Color.Lerp(oc, new Color(1f, 0.55f, 0.15f, 0.05f + 0.03f * Mathf.Sin(t * 7f)), HardcoreSurvival.WarmFx * 0.5f);
            overlay.color = oc;

            float v = HardcoreSurvival.VomitFx, hurt = HardcoreSurvival.HurtFx;
            Color fc = new Color(0f, 0f, 0f, 0f);
            if (v > 0f) fc = new Color(0.35f, 0.5f, 0.05f, v * v * 0.45f);
            if (hurt > 0f) fc = Color.Lerp(fc, new Color(0.7f, 0.9f, 1f, hurt * hurt * 0.35f), 0.7f);
            flashImg.color = fc;

            // two eyes: the eye mask squashes vertically as the lids close; black bars fill above/below
            float lid = Mathf.Clamp01(Mathf.Max(HardcoreSurvival.Blink, v * 0.25f));
            // while sleepy the eyes never open wide: the two-eyes shape stays drawn the whole time
            float tired = Mathf.SmoothStep(0f, 1f, (HardcoreSurvival.FatFx - 0.04f) / 0.1f);
            if (lid < 0.003f && tired <= 0f)
            {
                eyes.color = new Color(1f, 1f, 1f, 0f);
                lidTop.sizeDelta = Vector2.zero; lidBot.sizeDelta = Vector2.zero;
            }
            else
            {
                float open = 1f - lid;
                // at small lid values the mask is taller than the screen, so it slides in from the edges (no hard bars)
                float breathe = 1f + 0.04f * Mathf.Sin(t * 1.1f) * HardcoreSurvival.FatFx;
                float eh = Mathf.Lerp(0f, 1150f, Mathf.Pow(open, 1.3f));
                float cap = Mathf.Lerp(640f, 500f, HardcoreSurvival.FatFx) * Mathf.Pow(open, 1.1f);
                eh = Mathf.Lerp(eh, Mathf.Min(eh, cap), tired) * breathe;
                // light through closed eyelids: near black with a warm dark red when almost shut
                Color lc = Color.Lerp(Color.black, new Color(0.03f, 0.004f, 0.002f, 1f), Mathf.SmoothStep(0f, 1f, (lid - 0.7f) / 0.3f));
                // start straight at the two-eyes shape, fading in instead of sliding in from the screen edges
                lc.a = Mathf.Max(Mathf.SmoothStep(0f, 1f, lid / 0.12f), tired);
                eyes.color = lc;
                lidTop.GetComponent<Image>().color = lc;
                lidBot.GetComponent<Image>().color = lc;
                eyesRt.sizeDelta = new Vector2(0f, eh);
                eyesRt.anchoredPosition = new Vector2(0f, -40f * lid);
                // bars end exactly where the mask begins: any overlap of two semi-transparent layers shows as a dark line
                eh = Mathf.Round(eh * 0.5f) * 2f;
                eyesRt.sizeDelta = new Vector2(0f, eh);
                float off = Mathf.Round(40f * lid);
                eyesRt.anchoredPosition = new Vector2(0f, -off);
                float rest = Mathf.Max(0f, 540f - eh * 0.5f);
                lidTop.sizeDelta = new Vector2(0f, Mathf.Max(0f, rest + off));
                lidBot.sizeDelta = new Vector2(0f, Mathf.Max(0f, rest - off));
            }
        }
    }
}

namespace BCUI_HC
{
    using UnityEngine;
    using UnityEngine.UI;

    // Card at the top left with the stats of the friend you are looking at, and a live portrait
    // of their character (a small camera pointed at their face, rendered into a texture).
    public class FriendCard : MonoBehaviour
    {
        static FriendCard inst;
        const float W = 430f, H = 214f, Pic = 150f;
        static readonly string[] Names = { "Vida", "Fome", "Sede", "Energia", "Calor", "Saúde" };
        Text title, sub;
        RectTransform[] fill = new RectTransform[6];
        Image[] fillImg = new Image[6];
        Text[] val = new Text[6];
        float[] shown = new float[6];
        CanvasGroup group;
        Camera cam; RenderTexture rt; RawImage pic;
        Network_Player target;
        int frame;
        bool want;

        public static FriendCard Get()
        {
            if (inst == null)
            {
                foreach (Transform tr in Object.FindObjectsOfType<Transform>())
                    if (tr != null && (tr.name == "BC_FriendCard" || tr.name == "BC_FriendCam")) Object.Destroy(tr.gameObject);
                RectTransform r = Kit.Node(Kit.Root(), "BC_FriendCard");
                Kit.Place(r, Kit.TL, Kit.TL, new Vector2(24f, -24f), new Vector2(W, H));
                inst = r.gameObject.AddComponent<FriendCard>();
                inst.Build();
            }
            inst.want = true;
            return inst;
        }
        public static void Hide() { if (inst != null) inst.want = false; }
        public static void Kill() { if (inst != null) Object.Destroy(inst.gameObject); inst = null; }
        void OnDestroy() { if (cam != null) Object.Destroy(cam.gameObject); if (rt != null) { rt.Release(); Object.Destroy(rt); } }

        void Build()
        {
            group = gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0f; group.interactable = false; group.blocksRaycasts = false;
            Kit.Plate((RectTransform)transform);
            Image frameImg = Kit.Img(transform, "Frame", Kit.Get("BC_Round"), Kit.A(Kit.Dark, 0.85f), true);
            Kit.Place(frameImg.rectTransform, Kit.TL, Kit.TL, new Vector2(16f, -16f), new Vector2(Pic + 8f, Pic + 8f));
            rt = new RenderTexture(256, 256, 16);
            rt.Create();
            RectTransform pr = Kit.Node(frameImg.rectTransform, "Pic");
            Kit.Stretch(pr, 4f, 4f, 4f, 4f);
            pic = pr.gameObject.AddComponent<RawImage>();
            pic.texture = rt; pic.raycastTarget = false;
            GameObject cg = new GameObject("BC_FriendCam");
            Object.DontDestroyOnLoad(cg);
            cam = cg.AddComponent<Camera>();
            cam.enabled = false;
            cam.targetTexture = rt;
            cam.fieldOfView = 28f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 6f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.3f, 0.22f, 0.14f, 1f);
            cam.cullingMask = ~32;

            float x0 = 16f + Pic + 22f, bw = W - x0 - 18f;
            title = Kit.Txt(transform, "Name", "", 24, Kit.Brown, TextAnchor.MiddleLeft, false);
            Kit.Place(title.rectTransform, Kit.TL, new Vector2(0f, 0.5f), new Vector2(x0, -28f), new Vector2(bw, 28f));
            sub = Kit.Txt(transform, "Sub", "", 14, Kit.A(Kit.Brown, 0.75f), TextAnchor.MiddleLeft, false);
            Kit.Place(sub.rectTransform, Kit.TL, new Vector2(0f, 0.5f), new Vector2(x0, -50f), new Vector2(bw, 18f));
            for (int i = 0; i < 6; i++)
            {
                float y = -72f - i * 23f;
                Text n = Kit.Txt(transform, "N" + i, Names[i], 15, Kit.Brown, TextAnchor.MiddleLeft, false);
                Kit.Place(n.rectTransform, Kit.TL, new Vector2(0f, 0.5f), new Vector2(x0, y), new Vector2(70f, 20f));
                Image bg = Kit.Img(transform, "Bar" + i, Kit.Get("BC_Round"), Kit.A(Kit.Dark, 0.28f), true);
                Kit.Place(bg.rectTransform, Kit.TL, new Vector2(0f, 0.5f), new Vector2(x0 + 70f, y), new Vector2(bw - 70f - 44f, 10f));
                fillImg[i] = Kit.Img(bg.rectTransform, "Fill", null, Kit.Good, false);
                fill[i] = fillImg[i].rectTransform;
                fill[i].anchorMin = Vector2.zero; fill[i].anchorMax = new Vector2(0f, 1f); fill[i].pivot = new Vector2(0f, 0.5f);
                fill[i].offsetMin = new Vector2(2f, 2f); fill[i].offsetMax = new Vector2(-2f, -2f);
                val[i] = Kit.Txt(transform, "V" + i, "", 14, Kit.Brown, TextAnchor.MiddleRight, false);
                Kit.Place(val[i].rectTransform, Kit.TR, new Vector2(1f, 0.5f), new Vector2(-16f, y), new Vector2(44f, 20f));
            }
        }

        public void Set(Network_Player p, string name, float[] v, bool dead, bool vanilla)
        {
            target = p;
            title.text = name;
            if (dead) sub.text = "Inconsciente";
            else if (v == null) sub.text = "Sem dados";
            else if (vanilla) sub.text = "Sem o Hardcore Survival";
            else
            {
                string s = v[6] > 0.3f ? "Molhado" : "Seco";
                if (v.Length > 7 && v[7] > 0.05f) s += "  |  Couro " + Mathf.RoundToInt(v[7] / 0.8f * 100f) + "%";
                sub.text = s;
            }
            Color[] cs = { new Color(0.62f, 0.17f, 0.1f), new Color(0.75f, 0.5f, 0.15f), new Color(0.25f, 0.5f, 0.75f),
                new Color(0.43f, 0.56f, 0.79f), new Color(0.9f, 0.56f, 0.2f), Kit.Good };
            for (int i = 0; i < 6; i++)
            {
                float k = v == null ? -1f : v[i];
                bool off = k < 0f;
                shown[i] = Mathf.Lerp(shown[i], off ? 0f : k, Time.unscaledDeltaTime * 5f);
                fill[i].anchorMax = new Vector2(shown[i], 1f);
                Color c = cs[i];
                if (i == 4) c = Color.Lerp(new Color(0.36f, 0.62f, 0.86f), cs[4], Mathf.Clamp01(k));
                if (!off && k < 0.25f) c = Color.Lerp(c, Kit.Bad, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f));
                fillImg[i].color = c;
                val[i].text = off ? "-" : Mathf.RoundToInt(k * 100f) + "%";
                val[i].color = !off && k < 0.25f ? Kit.Bad : Kit.Brown;
            }
        }

        void LateUpdate()
        {
            float a = Mathf.MoveTowards(group.alpha, want && target != null ? 1f : 0f, Time.unscaledDeltaTime * 5f);
            group.alpha = a;
            ((RectTransform)transform).anchoredPosition = new Vector2(24f - (1f - Kit.EaseOut(a)) * 60f, -24f);
            if (a <= 0f || target == null) return;
            if (++frame % 3 != 0) return;
            try
            {
                Vector3 head = HardcoreSurvival.HeadPos(target);
                Vector3 fwd = target.transform.forward; fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                fwd.Normalize();
                cam.transform.position = head + fwd * 0.85f + Vector3.up * 0.02f;
                cam.transform.LookAt(head - Vector3.up * 0.06f);
                cam.Render();
            }
            catch { }
        }
    }
}

// ---------- BestCode UI kit ----------
// Drawn with Raft's own font (ChineseRocks) and sprites (parchment plate, item slot, key chip), on
// one shared overlay canvas. Every BestCode mod carries its own copy of this kit in its own
// namespace; mods only share the canvas object and the toast column (plain Unity components and
// RectTransforms), so one mod reloading never breaks another. Source: raft-mods/src/bcui_kit.cs.txt
namespace BCUI_HC
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
        public string Title;
        public float HoldUntil;

        public static Toast Show(string title, string sub, Sprite icon, bool bad, float secs)
        {
            RectTransform rt = Kit.Node(Kit.Toasts(), "BC_Toast");
            Kit.Place(rt, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(100f, 0f));
            Toast t = rt.gameObject.AddComponent<Toast>();
            t.Title = title;
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
            if (Time.unscaledTime < HoldUntil && t < life + 0.05f) life = Mathf.Max(life, t + 0.4f);   // pinned while its problem lasts
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

// the advanced purifier only works in daylight: at night it stops and its fuel is kept
[HarmonyPatch(typeof(Block_CookingStand_Purifier), "Update")]
static class P_PurifierDay
{
    static bool Prefix(Block_CookingStand_Purifier __instance)
    {
        try
        {
            if (!HardcoreSurvival.AdvancedPurifier(__instance)) return true;
            float h = HardcoreSurvival.HourPub();
            if (h < 0f || (h >= 6f && h < 19f)) return true;
            if (__instance.IsUsingEnergySource()) __instance.StopEnergySource();
            return false;
        }
        catch { return true; }
    }
}
