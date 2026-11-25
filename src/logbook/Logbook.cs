using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using HarmonyLib;
using HMLLibrary;
using RaftModLoader;
using BCUI_LB;

// Diario de Bordo v1.0.0: replaces the notebook on T with a logbook of this world, saved per world.
// Records the islands you reach, every catch, every meal, deaths, distance sailed and the days
// survived, plus a log of events. The story notes are still one click away ("Notas da historia").
// Console (F10): diario
public class Logbook : Mod
{
    public const string Version = "1.2.0";
    const string HarmonyId = "com.bestcode.logbook";
    const BindingFlags BF = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    public static Logbook I;
    Harmony harmony;

    // ---------- data (per world) ----------
    public class Isle { public string Name; public int First, Visits, Last; }
    public static Dictionary<string, Isle> isles = new Dictionary<string, Isle>();
    public static Dictionary<string, int> fish = new Dictionary<string, int>(), food = new Dictionary<string, int>();
    public static List<string> events = new List<string>();
    public static float hours, distance;
    public static int nightFish, meals, deaths, fishTotal, junk;

    string world;
    float lastHour = -1f, saveAt, isleAt, raftAt;
    Vector3 lastRaft; bool raftKnown;
    bool wasDead;
    readonly HashSet<string> inside = new HashSet<string>();
    public static bool allowOriginal;

    public void Start()
    {
        I = this;
        harmony = new Harmony(HarmonyId);
        foreach (Type t in new Type[] { typeof(P_Book), typeof(P_Pull), typeof(P_Bait), typeof(P_Eat), typeof(P_Freeze_LB) })
        {
            try { harmony.CreateClassProcessor(t).Patch(); }
            catch (Exception e) { Debug.LogWarning("[DiarioDeBordo] patch " + t.Name + ": " + e.Message); }
        }
        Debug.Log("[DiarioDeBordo] carregado v" + Version);
    }

    public void OnModUnload()
    {
        Save();
        BookUI.Kill();
        if (harmony != null) harmony.UnpatchAll(HarmonyId);
        I = null;
    }

    // the game's own day counter is saved with the world, so it already holds the real history
    public static int Day()
    {
        try { int d = WorldManager.DayCounter; if (d > 0) return d; } catch { }
        return Mathf.FloorToInt(hours / 24f) + 1;
    }
    public static float TimePlayed() { try { return GameManager.TimePlayed; } catch { return 0f; } }

    // story islands, read from the notes the game already unlocked in this world
    public class Story { public string Name; public int Have, Total; }
    public static List<Story> StoryIsles()
    {
        List<Story> l = new List<Story>();
        try
        {
            Network_Player me = RAPI.GetLocalPlayer();
            NoteBookUI nb = me != null ? me.GetComponentInChildren<NoteBookUI>(true) : null;
            if (nb == null) return l;
            string[] f = { "radarTowerNotes", "vasagatanNotes", "balboaNotes", "caravanNotes", "tangaroaNotes", "varunaNotes", "temperanceNotes" };
            string[] n = { "Torre de rádio", "Vasagatan", "Ilha Balboa", "Cidade Caravana", "Tangaroa", "Varuna Point", "Temperance" };
            for (int i = 0; i < f.Length; i++)
            {
                FieldInfo fi = typeof(NoteBookUI).GetField(f[i], BF);
                List<NoteBookNote> notes = fi != null ? fi.GetValue(nb) as List<NoteBookNote> : null;
                if (notes == null) continue;
                Story st = new Story { Name = n[i], Total = notes.Count };
                foreach (NoteBookNote x in notes) if (x != null && x.isUnlocked) st.Have++;
                l.Add(st);
            }
        }
        catch { }
        return l;
    }

    // lifetime numbers the game keeps for the Steam account (all worlds together)
    public static int Stat(string name)
    {
        try { FieldInfo fi = typeof(AchievementHandler).GetField(name, BF); return fi != null ? (int)fi.GetValue(null) : 0; }
        catch { return 0; }
    }
    public static void Log(string s)
    {
        events.Add("Dia " + Day() + "  -  " + s);
        if (events.Count > 80) events.RemoveAt(0);
    }

    // ---------- save ----------
    static string Dir() { return Path.Combine(Application.persistentDataPath, "BestCodeLogbook"); }
    static string F(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
    static float P(string s) { float f; float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f); return f; }
    static int N(string s) { int i; int.TryParse(s, out i); return i; }

    void Load()
    {
        isles.Clear(); fish.Clear(); food.Clear(); events.Clear(); inside.Clear();
        hours = distance = 0f; nightFish = meals = deaths = fishTotal = junk = 0;
        lastHour = -1f; raftKnown = false;
        try
        {
            string p = Path.Combine(Dir(), world + ".txt");
            if (!File.Exists(p)) return;
            foreach (string line in File.ReadAllLines(p))
            {
                string[] a = line.Split('\t');
                if (a.Length < 2) continue;
                switch (a[0])
                {
                    case "hours": hours = P(a[1]); break;
                    case "distance": distance = P(a[1]); break;
                    case "nightFish": nightFish = N(a[1]); break;
                    case "meals": meals = N(a[1]); break;
                    case "deaths": deaths = N(a[1]); break;
                    case "fishTotal": fishTotal = N(a[1]); break;
                    case "junk": junk = N(a[1]); break;
                    case "fish": if (a.Length >= 3) fish[a[1]] = N(a[2]); break;
                    case "food": if (a.Length >= 3) food[a[1]] = N(a[2]); break;
                    case "ev": events.Add(a[1]); break;
                    case "isle": if (a.Length >= 6) isles[a[1]] = new Isle { Name = a[2], First = N(a[3]), Visits = N(a[4]), Last = N(a[5]) }; break;
                }
            }
        }
        catch (Exception e) { Debug.LogWarning("[DiarioDeBordo] load: " + e.Message); }
    }

    void Save()
    {
        if (string.IsNullOrEmpty(world)) return;
        try
        {
            Directory.CreateDirectory(Dir());
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("hours\t" + F(hours));
            sb.AppendLine("distance\t" + F(distance));
            sb.AppendLine("nightFish\t" + nightFish);
            sb.AppendLine("meals\t" + meals);
            sb.AppendLine("deaths\t" + deaths);
            sb.AppendLine("fishTotal\t" + fishTotal);
            sb.AppendLine("junk\t" + junk);
            foreach (var kv in fish) sb.AppendLine("fish\t" + kv.Key + "\t" + kv.Value);
            foreach (var kv in food) sb.AppendLine("food\t" + kv.Key + "\t" + kv.Value);
            foreach (var kv in isles) sb.AppendLine("isle\t" + kv.Key + "\t" + kv.Value.Name + "\t" + kv.Value.First + "\t" + kv.Value.Visits + "\t" + kv.Value.Last);
            foreach (string e in events) sb.AppendLine("ev\t" + e.Replace('\t', ' '));
            File.WriteAllText(Path.Combine(Dir(), world + ".txt"), sb.ToString());
        }
        catch (Exception e) { Debug.LogWarning("[DiarioDeBordo] save: " + e.Message); }
    }

    // ---------- helpers ----------
    public static float Hour()
    {
        try
        {
            object sky = typeof(BedManager).GetField("skyController", BF).GetValue(null);
            if (sky == null) return -1f;
            object tod = sky.GetType().GetField("timeOfDay").GetValue(sky);
            return (float)tod.GetType().GetField("hour").GetValue(tod);
        }
        catch { return -1f; }
    }
    public static bool IsNight() { float h = Hour(); return h >= 0f && (h >= 20f || h < 5f); }

    public static string IsleName(Landmark lm)
    {
        string n = lm.name.ToLowerInvariant();
        if (n.Contains("radio")) return "Torre de rádio";
        if (n.Contains("vasagatan")) return "Vasagatan";
        if (n.Contains("balboa")) return "Ilha Balboa";
        if (n.Contains("caravan")) return "Cidade Caravana";
        if (n.Contains("tangaroa")) return "Tangaroa";
        if (n.Contains("varuna")) return "Varuna Point";
        if (n.Contains("temperance")) return "Temperance";
        if (n.Contains("utopia")) return "Utopia";
        if (n.Contains("pilot")) return "Ilhota";
        if (n.Contains("floating")) return "Jangada à deriva";
        if (n.Contains("boat")) return "Barco abandonado";
        if (n.Contains("big")) return "Ilha grande";
        if (n.Contains("small")) return "Ilha pequena";
        return "Ilha";
    }

    public static string ItemName(Item_Base it)
    {
        try { string d = it.settings_Inventory.DisplayName; if (!string.IsNullOrEmpty(d)) return d.Replace('\t', ' '); } catch { }
        return it.UniqueName;
    }

    public static void OnCatch(Item_Base it)
    {
        if (it == null) return;
        bool isFish = it.settings_consumeable != null && it.settings_consumeable.HungerYield > 0f;
        string n = ItemName(it);
        if (isFish)
        {
            int c; fish.TryGetValue(n, out c);
            fish[n] = c + 1;
            fishTotal++;
            bool night = IsNight();
            if (night) nightFish++;
            if (c == 0) Log("Primeiro " + n + (night ? " (de noite)" : ""));
        }
        else junk++;
    }

    public static void OnEat(Item_Base it)
    {
        if (it == null) return;
        string n = ItemName(it);
        int c; food.TryGetValue(n, out c);
        food[n] = c + 1;
        meals++;
    }

    // ---------- tick ----------
    public void Update()
    {
        Network_Player me = null;
        try { me = RAPI.GetLocalPlayer(); } catch { }
        string w = null;
        try { w = SaveAndLoad.CurrentGameFileName; } catch { }
        if (me == null || string.IsNullOrEmpty(w))
        {
            if (world != null) { Save(); world = null; }
            BookUI.Close();
            return;
        }
        if (w != world) { if (world != null) Save(); world = w; Load(); }
        float now = Time.time;
        bool dead = false;
        try { dead = me.PlayerScript != null && me.PlayerScript.IsDead; } catch { }

        float h = Hour();
        if (h >= 0f)
        {
            if (lastHour >= 0f && !dead)
            {
                float d = h - lastHour;
                if (d < -12f) d += 24f;
                if (d > 0f && d < 6f)
                {
                    int before = Day();
                    hours += d;
                    if (Day() > before) Log("Começou o dia " + Day());
                }
            }
            lastHour = h;
        }

        if (dead && !wasDead) { deaths++; Log("Morreste"); }
        wasDead = dead;

        // islands: close to a spawned landmark counts as a visit
        if (now >= isleAt)
        {
            isleAt = now + 2f;
            Vector3 pos = me.transform.position;
            foreach (Landmark lm in UnityEngine.Object.FindObjectsOfType<Landmark>())
            {
                if (lm == null) continue;
                string key = lm.uniqueLandmarkIndex.ToString();
                Vector3 d = lm.transform.position - pos; d.y = 0f;
                float dist = d.magnitude;
                bool on = dist < 55f;
                if (on && !inside.Contains(key))
                {
                    inside.Add(key);
                    Isle isle;
                    if (!isles.TryGetValue(key, out isle))
                    {
                        isle = new Isle { Name = IsleName(lm), First = Day(), Visits = 0 };
                        isles[key] = isle;
                        Log("Descobriste: " + isle.Name);
                        try { Toast.Show("NOVA ENTRADA NO DIÁRIO", isle.Name + " registada.", null, false, 4f); } catch { }
                    }
                    isle.Visits++;
                    isle.Last = Day();
                }
                else if (!on && dist > 90f) inside.Remove(key);
            }
        }

        // distance the raft has sailed
        if (now >= raftAt)
        {
            raftAt = now + 1f;
            Raft r = null;
            try { r = ComponentManager<Raft>.Value; } catch { }
            if (r != null)
            {
                Vector3 p = r.transform.position; p.y = 0f;
                if (raftKnown)
                {
                    float d = (p - lastRaft).magnitude;
                    if (d < 40f) distance += d;
                }
                lastRaft = p; raftKnown = true;
            }
        }

        BookUI.Tick();
        if (now >= saveAt) { saveAt = now + 15f; Save(); }
    }

    public void LateUpdate() { BookUI.LateTick(); }

    // the game's own notebook (story notes), opened on purpose
    public static void OpenStoryNotes()
    {
        Network_Player me = RAPI.GetLocalPlayer();
        if (me == null) return;
        NoteBookUI nb = me.GetComponentInChildren<NoteBookUI>(true);
        if (nb == null) return;
        allowOriginal = true;
        try { nb.ToggleBookActivity(); } finally { allowOriginal = false; }
    }

    [ConsoleCommand("diario", "Diario de Bordo: diario | diario abrir")]
    public static string CmdDiario(string[] args)
    {
        if (I == null) return "Diário de Bordo não está ativo.";
        if (args.Length > 0 && args[0] == "abrir") { BookUI.Open(); return "Aberto."; }
        return "Dia " + Day() + " | ilhas " + isles.Count + " | peixes " + fishTotal + " | refeições " + meals + " | mortes " + deaths + " | " + (distance / 1000f).ToString("0.0") + " km";
    }
}

// T opens the logbook instead of the notebook (closing the notebook still works as before)
[HarmonyPatch(typeof(NoteBookUI), "ToggleBookActivity")]
static class P_Book
{
    static bool Prefix(NoteBookUI __instance)
    {
        try
        {
            if (Logbook.I == null || Logbook.allowOriginal || __instance.isDisplayed) return true;
            Network_Player p = __instance.GetComponentInParent<Network_Player>();
            if (p == null || !p.IsLocalPlayer) return true;
            BookUI.Toggle();
            return false;
        }
        catch { return true; }
    }
}

[HarmonyPatch(typeof(FishingRod), "PullItemsFromSea")]
static class P_Pull
{
    public static bool pulling;
    static void Prefix(FishingRod __instance)
    {
        try { pulling = __instance.playerNetwork != null && __instance.playerNetwork.IsLocalPlayer; } catch { pulling = false; }
    }
    static void Postfix() { pulling = false; }
}

[HarmonyPatch(typeof(FishingBaitHandler), "GetRandomItemFromCurrentBaitPool")]
static class P_Bait
{
    static void Postfix(Item_Base __result)
    {
        if (!P_Pull.pulling) return;
        try { Logbook.OnCatch(__result); } catch { }
    }
}

[HarmonyPatch(typeof(PlayerStats), "Consume")]
static class P_Eat
{
    static void Postfix(PlayerStats __instance, Item_Base edibleItem)
    {
        try
        {
            Network_Player np = __instance.GetComponent<Network_Player>();
            if (np != null && np.IsLocalPlayer) Logbook.OnEat(edibleItem);
        }
        catch { }
    }
}


// no walking, jumping or sprinting while the panel is open
[HarmonyPatch(typeof(MyInput))]
static class P_Freeze_LB
{
    static bool Block(string id) { return (BookUI.IsOpen) && (id == "Walk" || id == "Strafe" || id == "Jump" || id == "Sprint" || id == "Crouch"); }
    [HarmonyPatch("GetAxis")] [HarmonyPrefix] static bool Axis(string identifier, ref float __result) { if (!Block(identifier)) return true; __result = 0f; return false; }
    [HarmonyPatch("GetButton")] [HarmonyPrefix] static bool Btn(string identifier, ref bool __result) { if (!Block(identifier)) return true; __result = false; return false; }
    [HarmonyPatch("GetButtonDown")] [HarmonyPrefix] static bool Down(string identifier, ref bool __result) { if (!Block(identifier)) return true; __result = false; return false; }
}

// Procedural art for the panels (leather, paper, stitching, shadows). Generated once, kept for the session.
static class LbArt
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

// The book: a leather-bound journal open in the middle of the screen. Cloth bookmarks on the
// left pick the section, two aged pages hold the rows.
public class BookUI : MonoBehaviour
{
    static BookUI inst;
    static bool open;
    const float W = 1240f, H = 800f, M = 20f;
    const int Rows = 16;
    const float RowH = 35f, RowTop = 136f, PadOut = 58f, PadIn = 52f;
    const float RibbonX = -172f, RibbonSel = -198f;
    static readonly string[] Tabs = { "Resumo", "Ilhas", "Pesca", "Comida", "Registo" };
    static readonly Color[] TabCol = {
        new Color(0.56f, 0.19f, 0.13f), new Color(0.15f, 0.40f, 0.40f), new Color(0.18f, 0.31f, 0.50f),
        new Color(0.66f, 0.46f, 0.13f), new Color(0.34f, 0.39f, 0.16f) };
    int tab = -1, page, pages = 1;
    GameObject canvasGo;
    RectTransform book, content;
    CanvasGroup group, contentGroup;
    Text worldText, dayText, timeText;
    Text[] folio = new Text[2];
    RectTransform prevBtn, nextBtn;
    RectTransform[] ribbon = new RectTransform[Tabs.Length];
    Image[] ribbonImg = new Image[Tabs.Length];
    Text[] ribbonTx = new Text[Tabs.Length];
    float[] ribbonX = new float[Tabs.Length];
    float openedAt, refreshAt, flipAt = -10f;
    int flipDir;
    bool closeNext;

    class RowUI { public RectTransform rt, fillRt; public Image bg, dot, rule, barBg, barFill; public Text a, b; }
    readonly RowUI[] ui = new RowUI[Rows * 2];

    // one line of a page: K 0 = label + value, 1 = heading, 2 = note, 3 = blank
    class R { public int K; public string A, B; public float F = -1f; public bool HasC; public Color C; }
    readonly List<R> rows = new List<R>();

    public static bool IsOpen { get { return open; } }

    static BookUI Get()
    {
        if (inst != null) return inst;
        foreach (GameObject g in UnityEngine.Object.FindObjectsOfType<GameObject>())
            if (g != null && g.name == "BC_Logbook") UnityEngine.Object.Destroy(g);
        GameObject go = new GameObject("BC_Logbook");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.layer = 5;
        Canvas c = go.AddComponent<Canvas>();
        c.renderMode = RenderMode.ScreenSpaceOverlay;
        c.sortingOrder = 650;
        CanvasScaler cs = go.AddComponent<CanvasScaler>();
        cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        cs.referenceResolution = new Vector2(1920f, 1080f);
        cs.matchWidthOrHeight = 1f;
        go.AddComponent<GraphicRaycaster>();
        inst = go.AddComponent<BookUI>();
        inst.canvasGo = go;
        inst.Build();
        go.SetActive(false);
        return inst;
    }

    public static void Kill() { if (open) Close(); if (inst != null) UnityEngine.Object.Destroy(inst.gameObject); inst = null; }

    public static void Toggle() { if (open) Close(); else Open(); }

    public static void Open()
    {
        if (open) return;
        try { if (PlayerItemManager.IsBusy) return; } catch { }
        if (CanvasHelper.ActiveMenu != MenuType.None) return;
        BookUI b = Get();
        open = true;
        b.canvasGo.SetActive(true);
        b.openedAt = Time.unscaledTime;
        b.flipAt = Time.unscaledTime + 0.06f; b.flipDir = 0;
        b.page = 0;
        b.Refresh();
        CanvasHelper.ActiveMenu = MenuType.Journal;
        try { SimpleMonoBehaviourSingleton<CustomInputConfig>.Instance.SwitchCurrentActionMap("UI"); } catch { }
        try { Helper.SetCursorVisibleAndLockState(true, CursorLockMode.None); } catch { }
        Ui("PlayUI_OpenMenu");
    }

    public static void Close()
    {
        if (!open) return;
        open = false;
        if (inst != null) inst.canvasGo.SetActive(false);
        if (CanvasHelper.ActiveMenu == MenuType.Journal) CanvasHelper.ActiveMenu = MenuType.None;
        try { SimpleMonoBehaviourSingleton<CustomInputConfig>.Instance.SwitchCurrentActionMap("Player"); } catch { }
        try { Helper.SetCursorVisibleAndLockState(false, CursorLockMode.Locked); } catch { }
        Ui("PlayUI_CloseMenu");
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

    public static void Tick()
    {
        if (!open || inst == null) return;
        // something else took over the screen (death, pause...)
        if (CanvasHelper.ActiveMenu != MenuType.Journal) { Close(); return; }
        try { Helper.SetCursorVisibleAndLockState(true, CursorLockMode.None); } catch { }
        if (Input.GetKeyDown(KeyCode.Escape)) inst.closeNext = true;
        if (Input.GetKeyDown(KeyCode.Q)) inst.Select((inst.tab + Tabs.Length - 1) % Tabs.Length);
        if (Input.GetKeyDown(KeyCode.E)) inst.Select((inst.tab + 1) % Tabs.Length);
        if (Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.LeftArrow)) inst.Flip(-1);
        if (Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.RightArrow)) inst.Flip(1);
        if (Time.unscaledTime >= inst.refreshAt) inst.Refresh();
    }

    // Esc closes after the game has handled its own input this frame (so the pause menu does not open too)
    public static void LateTick()
    {
        if (inst == null) return;
        if (open && inst.closeNext) { inst.closeNext = false; Close(); }
        if (!open) return;
        float now = Time.unscaledTime;
        float k = Kit.EaseOut((now - inst.openedAt) / 0.28f);
        inst.group.alpha = k;
        float s = Mathf.LerpUnclamped(0.95f, 1f, Kit.Back((now - inst.openedAt) / 0.34f));
        inst.book.localScale = new Vector3(s, s, 1f);
        inst.book.anchoredPosition = new Vector2(84f, 14f - 26f * (1f - k));
        // bookmarks slide out when picked
        float follow = 1f - Mathf.Exp(-16f * Time.unscaledDeltaTime);
        for (int i = 0; i < Tabs.Length; i++)
        {
            inst.ribbonX[i] = Mathf.Lerp(inst.ribbonX[i], i == inst.tab ? RibbonSel : RibbonX, follow);
            Vector2 p = inst.ribbon[i].anchoredPosition;
            inst.ribbon[i].anchoredPosition = new Vector2(inst.ribbonX[i], p.y);
        }
        // the pages settle in after a flip or a new section
        float f = Kit.EaseOut((now - inst.flipAt) / 0.22f);
        inst.contentGroup.alpha = f;
        inst.content.anchoredPosition = new Vector2(inst.flipDir * 22f * (1f - f), 0f);
    }

    static void NoNav(Button b)
    {
        Navigation nv = new Navigation(); nv.mode = Navigation.Mode.None; b.navigation = nv;
        ColorBlock cb = b.colors;
        cb.normalColor = new Color(0.88f, 0.88f, 0.88f, 1f); cb.highlightedColor = Color.white;
        cb.pressedColor = new Color(0.72f, 0.72f, 0.72f, 1f); cb.selectedColor = cb.normalColor; cb.fadeDuration = 0.08f;
        b.colors = cb;
    }
    static void DropShadow(Text t, float a)
    {
        UnityEngine.UI.Shadow sh = t.gameObject.AddComponent<UnityEngine.UI.Shadow>();
        sh.effectColor = new Color(0f, 0f, 0f, a); sh.effectDistance = new Vector2(1.5f, -1.5f);
    }
    static Image Diamond(Transform parent, string name, Vector2 anchor, Vector2 pos, float size, Color c)
    {
        Image d = Kit.Img(parent, name, Kit.Get("BC_Round"), c, false);
        Kit.Place(d.rectTransform, anchor, Kit.C, pos, new Vector2(size, size));
        d.rectTransform.localEulerAngles = new Vector3(0f, 0f, 45f);
        return d;
    }

    RectTransform Ribbon(Transform parent, string name, string text, Color col, float y, int fs, UnityEngine.Events.UnityAction act, out Image im, out Text tx)
    {
        im = Kit.Img(parent, name, LbArt.Ribbon(), col, true);
        im.raycastTarget = true;
        Kit.Place(im.rectTransform, Kit.TL, new Vector2(0f, 0.5f), new Vector2(RibbonX, y), new Vector2(250f, 56f));
        Button b = im.gameObject.AddComponent<Button>();
        b.targetGraphic = im;
        NoNav(b);
        b.onClick.AddListener(act);
        tx = Kit.Txt(im.rectTransform, "Label", text, fs, Kit.Cream, TextAnchor.MiddleLeft, false);
        Kit.Place(tx.rectTransform, Kit.ML, new Vector2(0f, 0.5f), new Vector2(30f, 1f), new Vector2(150f, 50f));
        tx.lineSpacing = 0.82f;
        DropShadow(tx, 0.45f);
        return im.rectTransform;
    }

    RectTransform Arrow(string key, Vector2 pos, Vector2 pivot, UnityEngine.Events.UnityAction act)
    {
        RectTransform c = Kit.Chip(book, key, 38f);
        c.anchorMin = c.anchorMax = Kit.TL; c.pivot = pivot; c.anchoredPosition = pos;
        Image im = c.GetComponent<Image>();
        im.raycastTarget = true;
        Button b = c.gameObject.AddComponent<Button>();
        b.targetGraphic = im;
        NoNav(b);
        b.onClick.AddListener(act);
        return c;
    }

    void Build()
    {
        group = gameObject.AddComponent<CanvasGroup>();
        // the world dims behind the book, darker at the corners
        Image veil = Kit.Img(transform, "Veil", null, new Color(0f, 0f, 0f, 0.35f), false);
        veil.raycastTarget = true;
        Kit.Stretch(veil.rectTransform, 0f, 0f, 0f, 0f);
        Image vig = Kit.Img(transform, "Vignette", LbArt.Vignette(), new Color(0f, 0f, 0f, 0.6f), false);
        Kit.Stretch(vig.rectTransform, 0f, 0f, 0f, 0f);

        book = Kit.Node(transform, "Book");
        Kit.Place(book, Kit.C, Kit.C, new Vector2(84f, 14f), new Vector2(W, H));

        Image shadow = Kit.Img(book, "Shadow", LbArt.Soft(), new Color(0f, 0f, 0f, 0.75f), true);
        Kit.Place(shadow.rectTransform, Kit.C, Kit.C, new Vector2(0f, -20f), new Vector2(W + 2f * M + 64f, H + 2f * M + 64f));

        // bookmarks, tucked under the cover
        RectTransform rib = Kit.Node(book, "Ribbons");
        Kit.Stretch(rib, 0f, 0f, 0f, 0f);
        for (int i = 0; i < Tabs.Length; i++)
        {
            int k = i;
            ribbon[i] = Ribbon(rib, "Tab" + i, Tabs[i].ToUpperInvariant(), TabCol[i], -128f - i * 68f, 25, () => { Select(k); }, out ribbonImg[i], out ribbonTx[i]);
            ribbonX[i] = RibbonX;
        }
        Image sImg; Text sTx;
        Ribbon(rib, "Story", "NOTAS DA\nHISTÓRIA", new Color(0.40f, 0.27f, 0.15f), -128f - Tabs.Length * 68f - 34f, 19,
            () => { Close(); Logbook.OpenStoryNotes(); }, out sImg, out sTx);

        // leather cover with stitching
        Image cover = Kit.Img(book, "Cover", Kit.Get("BC_Round"), LbArt.Leather, true);
        Kit.Stretch(cover.rectTransform, -M, -M, -M, -M);
        Image grain = Kit.Img(book, "Grain", LbArt.Mottle(), new Color(0f, 0f, 0f, 0.38f), false);
        Kit.Stretch(grain.rectTransform, -M + 4f, -M + 4f, -M + 4f, -M + 4f);
        Image stitch = Kit.Img(book, "Stitch", LbArt.Stitch(), Kit.A(Kit.Cream, 0.4f), false);
        stitch.type = Image.Type.Tiled; stitch.fillCenter = false;
        Kit.Stretch(stitch.rectTransform, -M + 6f, -M + 6f, -M + 6f, -M + 6f);

        // page block: the edges of the pages underneath, then the two open pages
        Image stack1 = Kit.Img(book, "Stack1", Kit.Get("BC_Round"), new Color(0.74f, 0.65f, 0.49f), true);
        Kit.Stretch(stack1.rectTransform, -7f, -7f, -7f, -1f);
        Image stack2 = Kit.Img(book, "Stack2", Kit.Get("BC_Round"), new Color(0.86f, 0.79f, 0.64f), true);
        Kit.Stretch(stack2.rectTransform, -4f, -4f, -4f, -1f);
        for (int c = 0; c < 2; c++)
        {
            Image pg = Kit.Img(book, "Page" + c, LbArt.Paper(c), Color.white, false);
            Kit.Place(pg.rectTransform, Kit.TL, Kit.TL, new Vector2(c * W * 0.5f, 0f), new Vector2(W * 0.5f, H));
        }
        Image spine = Kit.Img(book, "Spine", LbArt.Grad(), new Color(0.16f, 0.08f, 0.03f, 0.5f), false);
        Kit.Place(spine.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(120f, H));
        Image fold = Kit.Img(book, "Fold", null, new Color(0.16f, 0.08f, 0.03f, 0.45f), false);
        Kit.Place(fold.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(2f, H));

        // headers: title on the left page, day and time on the right
        Text title = Kit.Txt(book, "Title", "DIÁRIO DE BORDO", 46, Kit.Dark, TextAnchor.MiddleLeft, false);
        Kit.Place(title.rectTransform, Kit.TL, new Vector2(0f, 0.5f), new Vector2(PadOut, -58f), new Vector2(520f, 54f));
        worldText = Kit.Txt(book, "World", "", 19, Kit.A(Kit.Brown, 0.75f), TextAnchor.MiddleLeft, false);
        Kit.Place(worldText.rectTransform, Kit.TL, new Vector2(0f, 0.5f), new Vector2(PadOut + 2f, -95f), new Vector2(480f, 24f));
        dayText = Kit.Txt(book, "Day", "", 46, Kit.Dark, TextAnchor.MiddleRight, false);
        Kit.Place(dayText.rectTransform, Kit.TR, new Vector2(1f, 0.5f), new Vector2(-PadOut, -58f), new Vector2(480f, 54f));
        timeText = Kit.Txt(book, "Time", "", 19, Kit.A(Kit.Brown, 0.75f), TextAnchor.MiddleRight, false);
        Kit.Place(timeText.rectTransform, Kit.TR, new Vector2(1f, 0.5f), new Vector2(-PadOut - 2f, -95f), new Vector2(480f, 24f));
        float pw = W * 0.5f - PadOut - PadIn;
        for (int c = 0; c < 2; c++)
        {
            float x = c == 0 ? PadOut : W * 0.5f + PadIn;
            Image r1 = Kit.Img(book, "Rule" + c, null, Kit.A(Kit.Brown, 0.5f), false);
            Kit.Place(r1.rectTransform, Kit.TL, new Vector2(0f, 0.5f), new Vector2(x, -116f), new Vector2(pw, 2f));
            Image r2 = Kit.Img(book, "RuleThin" + c, null, Kit.A(Kit.Brown, 0.25f), false);
            Kit.Place(r2.rectTransform, Kit.TL, new Vector2(0f, 0.5f), new Vector2(x, -121f), new Vector2(pw, 1f));
            Diamond(book, "Orn" + c, Kit.TL, new Vector2(x + pw * 0.5f, -117f), 10f, Kit.Brown);
        }

        // the rows of both pages, built once and refilled
        content = Kit.Node(book, "Content");
        Kit.Stretch(content, 0f, 0f, 0f, 0f);
        contentGroup = content.gameObject.AddComponent<CanvasGroup>();
        contentGroup.blocksRaycasts = false;
        for (int c = 0; c < 2; c++)
            for (int r = 0; r < Rows; r++)
            {
                float x = c == 0 ? PadOut : W * 0.5f + PadIn;
                RowUI u = new RowUI();
                u.rt = Kit.Node(content, "Row" + c + "_" + r);
                Kit.Place(u.rt, Kit.TL, Kit.TL, new Vector2(x, -RowTop - r * RowH), new Vector2(pw, RowH));
                u.bg = Kit.Img(u.rt, "Bg", Kit.Get("BC_Round"), Kit.A(Kit.Brown, 0.075f), true);
                Kit.Stretch(u.bg.rectTransform, -8f, 2f, -8f, 2f);
                u.dot = Diamond(u.rt, "Dot", Kit.ML, new Vector2(6f, -1f), 9f, Kit.A(Kit.Brown, 0.9f));
                u.rule = Kit.Img(u.rt, "Rule", null, Kit.A(Kit.Brown, 0.32f), false);
                u.rule.rectTransform.anchorMin = new Vector2(0f, 0.5f); u.rule.rectTransform.anchorMax = new Vector2(1f, 0.5f);
                u.a = Kit.Txt(u.rt, "A", "", 21, Kit.Brown, TextAnchor.MiddleLeft, false);
                Kit.Stretch(u.a.rectTransform, 4f, 0f, 0f, 0f);
                u.b = Kit.Txt(u.rt, "B", "", 21, Kit.Dark, TextAnchor.MiddleRight, false);
                Kit.Stretch(u.b.rectTransform, 0f, 0f, 2f, 0f);
                u.barBg = Kit.Img(u.rt, "Bar", Kit.Get("BC_Round"), Kit.A(Kit.Dark, 0.15f), true);
                Kit.Place(u.barBg.rectTransform, Kit.MR, Kit.MR, new Vector2(-148f, -1f), new Vector2(118f, 12f));
                u.barFill = Kit.Img(u.barBg.rectTransform, "Fill", null, Kit.Good, false);
                u.fillRt = u.barFill.rectTransform;
                u.fillRt.anchorMin = Vector2.zero; u.fillRt.pivot = new Vector2(0f, 0.5f);
                u.fillRt.offsetMin = new Vector2(2f, 2f); u.fillRt.offsetMax = new Vector2(-2f, -2f);
                Image shine = Kit.Img(u.fillRt, "Shine", null, Kit.A(Kit.Cream, 0.3f), false);
                shine.rectTransform.anchorMin = new Vector2(0f, 0.55f); shine.rectTransform.anchorMax = Vector2.one;
                shine.rectTransform.offsetMin = shine.rectTransform.offsetMax = Vector2.zero;
                ui[c * Rows + r] = u;
            }

        // page numbers and arrows at the foot of the pages
        for (int c = 0; c < 2; c++)
        {
            folio[c] = Kit.Txt(book, "Folio" + c, "", 18, Kit.A(Kit.Brown, 0.6f), TextAnchor.MiddleCenter, false);
            Kit.Place(folio[c].rectTransform, Kit.TL, Kit.C, new Vector2(c == 0 ? (PadOut + pw * 0.5f) : (W * 0.5f + PadIn + pw * 0.5f), -(H - 40f)), new Vector2(160f, 30f));
        }
        prevBtn = Arrow("<", new Vector2(PadOut, -(H - 40f)), new Vector2(0f, 0.5f), () => { Flip(-1); });
        nextBtn = Arrow(">", new Vector2(W - PadOut, -(H - 40f)), new Vector2(1f, 0.5f), () => { Flip(1); });

        // key hints under the book, like the game's own prompts
        RectTransform hints = Kit.Node(book, "Hints");
        Kit.Place(hints, new Vector2(0.5f, 0f), Kit.C, new Vector2(0f, -M - 34f), new Vector2(10f, 40f));
        string[][] hs = { new[] { "T", "Fechar" }, new[] { "Q E", "Separador" }, new[] { "A D", "Página" } };
        RectTransform[] chips = new RectTransform[hs.Length];
        Text[] labs = new Text[hs.Length];
        float total = 0f;
        for (int i = 0; i < hs.Length; i++)
        {
            chips[i] = Kit.Chip(hints, hs[i][0], 34f);
            labs[i] = Kit.Txt(hints, "H" + i, hs[i][1], 22, Kit.Cream, TextAnchor.MiddleLeft, true);
            total += chips[i].sizeDelta.x + 10f + labs[i].preferredWidth + (i > 0 ? 34f : 0f);
        }
        float hx = -total * 0.5f;
        for (int i = 0; i < hs.Length; i++)
        {
            if (i > 0) hx += 34f;
            chips[i].anchorMin = chips[i].anchorMax = Kit.C; chips[i].pivot = new Vector2(0f, 0.5f);
            chips[i].anchoredPosition = new Vector2(hx, 0f);
            hx += chips[i].sizeDelta.x + 10f;
            Kit.Place(labs[i].rectTransform, Kit.C, new Vector2(0f, 0.5f), new Vector2(hx, 0f), new Vector2(labs[i].preferredWidth + 6f, 40f));
            hx += labs[i].preferredWidth;
        }
        Select(0);
    }

    void Select(int t)
    {
        if (t != tab && tab >= 0) Ui("PlayUI_Click");
        tab = t; page = 0;
        flipAt = Time.unscaledTime; flipDir = 0;
        for (int i = 0; i < Tabs.Length; i++)
        {
            bool on = i == tab;
            Color c = TabCol[i];
            ribbonImg[i].color = on ? c : new Color(c.r * 0.72f, c.g * 0.72f, c.b * 0.72f, 1f);
            ribbonTx[i].color = Kit.A(Kit.Cream, on ? 1f : 0.62f);
        }
        Refresh();
    }

    void Flip(int d)
    {
        int p = Mathf.Clamp(page + d, 0, pages - 1);
        if (p != page) { page = p; flipAt = Time.unscaledTime; flipDir = d; Ui("PlayUI_Click"); Refresh(); }
    }

    R Add(int k, string a, string b) { R x = new R { K = k, A = a, B = b }; rows.Add(x); return x; }
    void Blank() { Add(3, "", ""); }
    // a heading never sits on the last line of a page, and always has a gap above it
    void Head(string s)
    {
        if (rows.Count % Rows != 0) Blank();
        if (rows.Count % Rows == Rows - 1) Blank();
        Add(1, s.ToUpperInvariant(), null);
    }
    R Row(string a, string b) { return Add(0, a, b); }
    R Row(string a, string b, float f) { R x = Add(0, a, b); x.F = Mathf.Clamp01(f); return x; }
    static R Tint(R x, Color c) { x.HasC = true; x.C = c; return x; }
    void Empty(string s) { Add(2, s, ""); }

    static string Weather()
    {
        try
        {
            WeatherManager wm = UnityEngine.Object.FindObjectOfType<WeatherManager>();
            if (wm == null) return "";
            switch (wm.GetCurrentWeatherType())
            {
                case UniqueWeatherType.Fog: return "Nevoeiro";
                case UniqueWeatherType.BigWaves: return "Ondas grandes";
                case UniqueWeatherType.Rain: return "Chuva";
                case UniqueWeatherType.Snow: return "Neve";
                case UniqueWeatherType.SnowRough: return "Tempestade de neve";
                default: return "Mar calmo";
            }
        }
        catch { return ""; }
    }

    static List<KeyValuePair<string, int>> Sorted(Dictionary<string, int> d)
    {
        List<KeyValuePair<string, int>> l = new List<KeyValuePair<string, int>>(d);
        l.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.Compare(a.Key, b.Key, StringComparison.Ordinal));
        return l;
    }
    static int Max(List<KeyValuePair<string, int>> l) { int m = 1; foreach (var kv in l) if (kv.Value > m) m = kv.Value; return m; }

    static readonly Color Faded = new Color(0.373f, 0.224f, 0.125f, 0.5f);

    void Refresh()
    {
        refreshAt = Time.unscaledTime + 1f;
        rows.Clear();
        int day = Logbook.Day();
        float h = Logbook.Hour();
        dayText.text = "DIA " + day;
        string w = Weather();
        timeText.text = (h >= 0f ? Mathf.FloorToInt(h).ToString("00") + ":" + Mathf.FloorToInt((h % 1f) * 60f).ToString("00") : "")
            + (h >= 0f && w.Length > 0 ? "   |   " : "") + w.ToUpperInvariant();
        string world = "";
        try { world = SaveAndLoad.CurrentGameFileName; } catch { }
        worldText.text = string.IsNullOrEmpty(world) ? "" : ("JANGADA  " + world).ToUpperInvariant();
        Color accent = TabCol[tab];
        switch (tab)
        {
            case 0:
            {
                Head("A viagem");
                Row("Dias no mar", day.ToString());
                float tp = Logbook.TimePlayed();
                Row("Tempo de jogo", Mathf.FloorToInt(tp / 3600f) + " h " + Mathf.FloorToInt(tp % 3600f / 60f).ToString("00") + " min");
                Row("Distância navegada", (Logbook.distance / 1000f).ToString("0.00") + " km");
                int blocks = 0;
                try { blocks = UnityEngine.Object.FindObjectsOfType<Block>().Length; } catch { }
                Row("Peças da jangada", blocks.ToString());
                Row("Mortes", Logbook.deaths.ToString());
                Head("Descobertas");
                int visits = 0; foreach (var i in Logbook.isles.Values) visits += i.Visits;
                int storyHave = 0; foreach (var st in Logbook.StoryIsles()) if (st.Have > 0) storyHave++;
                R sr = Row("Ilhas da história", storyHave + " / 7", storyHave / 7f);
                if (storyHave >= 7) Tint(sr, Kit.Good);
                Row("Ilhas registadas", Logbook.isles.Count.ToString());
                Row("Visitas a ilhas", visits.ToString());
                Head("Pesca");
                Row("Peixes apanhados", Logbook.fishTotal.ToString());
                Row("De noite", Logbook.nightFish.ToString());
                Row("Espécies diferentes", Logbook.fish.Count.ToString());
                Row("Lixo pescado", Logbook.junk.ToString());
                string top = "-"; int tc = 0; foreach (var kv in Logbook.fish) if (kv.Value > tc) { tc = kv.Value; top = kv.Key; }
                Row("Mais apanhado", top);
                Head("Comida");
                Row("Refeições", Logbook.meals.ToString());
                Row("Pratos diferentes", Logbook.food.Count.ToString());
                string fav = "-"; int fc = 0; foreach (var kv in Logbook.food) if (kv.Value > fc) { fc = kv.Value; fav = kv.Key; }
                Row("Preferido", fav);
                if (AchievementHandler.UserStatsRecieved)
                {
                    Head("Conta (todos os mundos)");
                    Row("Lançamentos do gancho", Logbook.Stat("Stat_player_hookCount").ToString());
                    Row("Tubarões mortos", Logbook.Stat("Stat_player_sharkKills").ToString());
                    Row("Gaivotas mortas", Logbook.Stat("Stat_player_birdKills").ToString());
                    Row("Javalis mortos", Logbook.Stat("Stat_player_boarKills").ToString());
                    Row("Ursos mortos", Logbook.Stat("Stat_player_bearKills").ToString());
                    Row("Animais capturados", Logbook.Stat("Stat_player_capturedAnimals").ToString());
                    Row("Tesouros desenterrados", Logbook.Stat("Stat_player_excevations_treasure").ToString());
                    Row("Fundações construídas", Logbook.Stat("Stat_build_foundationCount").ToString());
                    Row("Mortes", Logbook.Stat("Stat_player_deaths").ToString());
                }
                Head("Antes do diário");
                Empty("Dias, tempo de jogo e ilhas da história vêm do save.");
                Empty("Peixes, comida e distância só contam desde a instalação:");
                Empty("o jogo não guarda esse histórico.");
                break;
            }
            case 1:
            {
                Head("Ilhas da história");
                foreach (var st in Logbook.StoryIsles())
                {
                    float f = st.Total > 0 ? (float)st.Have / st.Total : 0f;
                    if (st.Have == 0) Tint(Row(st.Name, "por descobrir", 0f), Faded);
                    else if (st.Have >= st.Total) Tint(Row(st.Name, "completa", 1f), Kit.Good);
                    else Row(st.Name, st.Have + " / " + st.Total + " notas", f);
                }
                Head("Ilhas e lugares (" + Logbook.isles.Count + ")");
                if (Logbook.isles.Count == 0) Empty("Ainda não pisaste nenhuma ilha.");
                List<Logbook.Isle> l = new List<Logbook.Isle>(Logbook.isles.Values);
                l.Sort((a, b) => a.First.CompareTo(b.First));
                int n = 0;
                foreach (Logbook.Isle i in l)
                {
                    n++;
                    Row(n + ".  " + i.Name, "dia " + i.First + (i.Visits > 1 ? "   |   " + i.Visits + " visitas" : ""));
                }
                if (l.Count > 0)
                {
                    Head("Por tipo");
                    Dictionary<string, int> kinds = new Dictionary<string, int>();
                    foreach (Logbook.Isle i in l) { int c; kinds.TryGetValue(i.Name, out c); kinds[i.Name] = c + 1; }
                    var ks = Sorted(kinds); int km = Max(ks);
                    foreach (var kv in ks) Row(kv.Key, kv.Value.ToString(), (float)kv.Value / km);
                }
                break;
            }
            case 2:
            {
                Head("Peixes (" + Logbook.fishTotal + ")");
                if (Logbook.fish.Count == 0) Empty("Ainda não pescaste nada.");
                var fs = Sorted(Logbook.fish); int fm = Max(fs);
                foreach (var kv in fs) Row(kv.Key, kv.Value.ToString(), (float)kv.Value / fm);
                Head("Notas");
                Row("Apanhados de noite", Logbook.nightFish.ToString());
                Row("Lixo e destroços", Logbook.junk.ToString());
                break;
            }
            case 3:
            {
                Head("Refeições (" + Logbook.meals + ")");
                if (Logbook.food.Count == 0) Empty("Ainda não comeste nada.");
                var ms = Sorted(Logbook.food); int mm = Max(ms);
                foreach (var kv in ms) Row(kv.Key, kv.Value.ToString(), (float)kv.Value / mm);
                break;
            }
            default:
            {
                Head("Registo");
                if (Logbook.events.Count == 0) Empty("Nada registado ainda.");
                for (int i = Logbook.events.Count - 1; i >= 0; i--) Row(Logbook.events[i], "");
                break;
            }
        }
        int per = Rows * 2;
        pages = Mathf.Max(1, (rows.Count + per - 1) / per);
        page = Mathf.Clamp(page, 0, pages - 1);
        for (int c = 0; c < 2; c++)
            for (int r = 0; r < Rows; r++)
            {
                int idx = page * per + c * Rows + r;
                Fill(ui[c * Rows + r], idx < rows.Count ? rows[idx] : null, r, accent);
            }
        folio[0].text = "-  " + (page * 2 + 1) + "  -";
        folio[1].text = "-  " + (page * 2 + 2) + "  -";
        prevBtn.gameObject.SetActive(page > 0);
        nextBtn.gameObject.SetActive(page < pages - 1);
    }

    static void Fill(RowUI u, R x, int r, Color accent)
    {
        bool show = x != null && x.K != 3;
        if (u.rt.gameObject.activeSelf != show) u.rt.gameObject.SetActive(show);
        if (!show) return;
        bool head = x.K == 1, note = x.K == 2, plain = x.K == 0;
        u.bg.enabled = plain && (r % 2 == 1);
        u.dot.enabled = head;
        u.rule.enabled = head;
        u.a.text = x.A;
        u.a.fontSize = head ? 25 : (note ? 19 : 21);
        u.a.color = head ? Kit.Dark : (note ? Kit.A(Kit.Brown, 0.72f) : Kit.Brown);
        u.a.rectTransform.offsetMin = new Vector2(head ? 20f : 4f, 0f);
        if (head)
        {
            float tw = u.a.preferredWidth;
            u.rule.rectTransform.offsetMin = new Vector2(20f + tw + 14f, -1f);
            u.rule.rectTransform.offsetMax = new Vector2(0f, 1f);
        }
        u.b.text = plain ? (x.B ?? "") : "";
        u.b.color = x.HasC ? x.C : Kit.Dark;
        bool bar = plain && x.F >= 0f;
        u.barBg.enabled = bar; u.barFill.enabled = bar;
        u.barFill.transform.GetChild(0).gameObject.SetActive(bar);
        if (bar)
        {
            u.fillRt.anchorMax = new Vector2(Mathf.Max(0.001f, x.F), 1f);
            u.barFill.color = x.HasC && x.C.a > 0.9f ? x.C : accent;
        }
    }
}


// ---------- BestCode UI kit ----------
// Drawn with Raft's own font (ChineseRocks) and sprites (parchment plate, item slot, key chip), on
// one shared overlay canvas. Every BestCode mod carries its own copy of this kit in its own
// namespace; mods only share the canvas object and the toast column (plain Unity components and
// RectTransforms), so one mod reloading never breaks another. Source: raft-mods/src/bcui_kit.cs.txt
namespace BCUI_LB
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
