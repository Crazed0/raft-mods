using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using HMLLibrary;
using RaftModLoader;

// Inspecionar v1.1.0: look at a block and a small parchment window with its state:
// grills, purifiers and smelters (fuel or battery, each slot cooking and how far along), the electric
// purifier (on/off, battery, water), cooking pot and juicer (recipe, time, portions), crop plots
// (growth of each plant) and any tank or battery. It shows by itself in the top left while you look at one.
// 1.3.0: animals too (shark, boar, screecher, bears, llamas...): aim at one up to 40 m away to see its life.
public class Inspect : Mod
{
    public const string Version = "1.3.0";
    static bool open;
    static float next;
    static UnityEngine.Object shown;
    const float BlockRange = 4.5f, BeastRange = 40f;

    public void Start() { Debug.Log("[Inspecionar] carregado v" + Version); }
    public void OnModUnload() { BCUI_IN.InspectHud.Kill(); }

    public void Update()
    {
        bool menu = false;
        try { menu = CanvasHelper.ActiveMenu != MenuType.None; } catch { }
        Network_Player me = null;
        try { me = RAPI.GetLocalPlayer(); } catch { }
        if (menu || me == null) { BCUI_IN.InspectHud.Hide(); return; }
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + 0.25f;
        Network_Entity beast = null;
        Block b = Look(out beast);
        if (b == null && beast == null) { shown = null; BCUI_IN.InspectHud.Hide(); return; }
        List<Row> rows = new List<Row>();
        string title;
        if (beast != null)
        {
            title = BeastName(beast);
            try { DescribeBeast(beast, rows); } catch (Exception e) { rows.Add(new Row("erro: " + e.Message, -1f)); }
            if (beast != shown) { shown = beast; Debug.Log("[Inspecionar] a ver animal " + beast.name + " (" + title + ")"); }
        }
        else
        {
            title = NameOf(b.buildableItem);
            try { Describe(b, rows); } catch (Exception e) { rows.Add(new Row("erro: " + e.Message, -1f)); }
            if (b != shown) { shown = b; Debug.Log("[Inspecionar] a ver " + b.GetType().Name + " (" + b.buildableItem.UniqueName + ") linhas " + rows.Count); }
        }
        if (rows.Count == 0) rows.Add(new Row("Nada para mostrar", -1f));
        BCUI_IN.InspectHud.Show(title, rows);
    }

    public struct Row
    {
        public string text; public float bar; public bool good;
        public Row(string t, float b) { text = t; bar = b; good = false; }
        public Row(string t, float b, bool g) { text = t; bar = b; good = g; }
    }

    static Block Look(out Network_Entity beast)
    {
        beast = null;
        Camera cam = Camera.main;
        if (cam == null) return null;
        Vector3 o = cam.transform.position, d = cam.transform.forward;
        RaycastHit[] hits = Physics.RaycastAll(o, d, BlockRange, ~0, QueryTriggerInteraction.Collide);
        Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
        foreach (RaycastHit h in hits)
        {
            if (h.collider.GetComponentInParent<Network_Player>() != null) continue;
            Block b = h.collider.GetComponentInParent<Block>();
            if (b == null) continue;
            if (b is Block_CookingStand || b is Block_ElectricPurifier || b is Cropplot || b is Block_CookingTable_Pot || b is Block_CookingTable_Juicer
                || b.GetComponentInChildren<Tank>() != null || b.GetComponentInChildren<Battery>() != null) return b;
        }
        beast = LookBeast(o, d);
        return null;
    }

    // the nearest animal along the view (a slightly fat ray, they move), unless a wall or the floor is in front of it
    static Network_Entity LookBeast(Vector3 o, Vector3 d)
    {
        RaycastHit[] hits = Physics.SphereCastAll(o, 0.35f, d, BeastRange, ~0, QueryTriggerInteraction.Collide);
        Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
        float wall = BeastRange + 1f;
        foreach (RaycastHit h in Physics.RaycastAll(o, d, BeastRange, ~0, QueryTriggerInteraction.Ignore))
            if (h.distance < wall && h.collider.GetComponentInParent<Network_Entity>() == null && h.collider.GetComponentInParent<Network_Player>() == null) wall = h.distance;
        foreach (RaycastHit h in hits)
        {
            Collider c = h.collider;
            if (c == null || c.GetComponentInParent<Network_Player>() != null) continue;
            if (h.distance > wall) break;   // a solid thing in the way (the raft, an island, a wall) hides what is behind it
            Network_Entity e = c.GetComponentInParent<Network_Entity>();
            if (e != null && e.stat_health != null && e.entityType != EntityType.Player && e.GetComponentInParent<Network_Player>() == null
                && (c.GetComponentInParent<AI_NetworkBehaviour>() != null || e.GetComponentInChildren<AI_NetworkBehaviour>() != null)) return e;
        }
        return null;
    }

    static string BeastName(Network_Entity e)
    {
        AI_NetworkBehaviour ai = e.GetComponentInParent<AI_NetworkBehaviour>();
        if (ai == null) ai = e.GetComponentInChildren<AI_NetworkBehaviour>();
        if (ai != null)
        {
            switch (ai.behaviourType)
            {
                case AI_NetworkBehaviourType.Shark: return "Tubarão";
                case AI_NetworkBehaviourType.StoneBird: case AI_NetworkBehaviourType.StoneBird_Caravan: return "Screecher";
                case AI_NetworkBehaviourType.PufferFish: return "Baiacu";
                case AI_NetworkBehaviourType.Llama: return "Lama";
                case AI_NetworkBehaviourType.Goat: return "Cabra";
                case AI_NetworkBehaviourType.Chicken: return "Galinha";
                case AI_NetworkBehaviourType.Boar: return "Javali";
                case AI_NetworkBehaviourType.Rat: case AI_NetworkBehaviourType.Rat_Tangaroa: return "Rato";
                case AI_NetworkBehaviourType.Bear: return "Urso";
                case AI_NetworkBehaviourType.MamaBear: return "Ursa Mãe";
                case AI_NetworkBehaviourType.PolarBear: return "Urso Polar";
                case AI_NetworkBehaviourType.BugSwarm_Bee: return "Enxame de Abelhas";
                case AI_NetworkBehaviourType.Pig: return "Porco";
                case AI_NetworkBehaviourType.ButlerBot: return "Robô Mordomo";
                case AI_NetworkBehaviourType.Boss_Varuna: return "Varuna";
                case AI_NetworkBehaviourType.AnglerFish: return "Peixe-Pescador";
                case AI_NetworkBehaviourType.Roach: return "Barata";
                case AI_NetworkBehaviourType.Puffin: return "Papagaio-do-Mar";
                case AI_NetworkBehaviourType.Dolphin: return "Golfinho";
                case AI_NetworkBehaviourType.Whale: return "Baleia";
                case AI_NetworkBehaviourType.BirdPack: return "Bando de Aves";
                case AI_NetworkBehaviourType.Turtle: return "Tartaruga";
                case AI_NetworkBehaviourType.Stingray: return "Raia";
                case AI_NetworkBehaviourType.Hyena: return "Hiena";
                case AI_NetworkBehaviourType.HyenaBoss: return "Hiena Líder";
            }
            string t = ai.behaviourType.ToString();
            if (t.StartsWith("NPC_")) return t.Substring(4);
        }
        return e.name.Replace("(Clone)", "").Trim();
    }

    static void DescribeBeast(Network_Entity e, List<Row> rows)
    {
        Stat_Health hp = e.stat_health;
        float v = Mathf.Max(0f, hp.Value), mx = Mathf.Max(1f, hp.Max);
        if (e.IsDead || v <= 0f) { rows.Add(new Row("Morto", -1f)); return; }
        float n = Mathf.Clamp01(v / mx);
        rows.Add(new Row("Vida: " + Mathf.CeilToInt(v) + "/" + Mathf.RoundToInt(mx) + " (" + Mathf.RoundToInt(n * 100f) + "%)", n, n >= 0.999f));
        if (e.IsInvurnerable) rows.Add(new Row("Invulnerável", -1f));
        else if (hp.regenPerSecond > 0f && n < 0.999f) rows.Add(new Row("Recupera " + hp.regenPerSecond.ToString("0.#") + "/s", -1f));
    }

    static string NameOf(Item_Base it)
    {
        if (it == null) return "?";
        try { string d = it.settings_Inventory.DisplayName; if (!string.IsNullOrEmpty(d)) return d; } catch { }
        return it.UniqueName;
    }

    static string Time2(float s)
    {
        if (s < 0f) s = 0f;
        int t = Mathf.CeilToInt(s);
        return t >= 60 ? (t / 60) + "m " + (t % 60).ToString("00") + "s" : t + "s";
    }

    static void BatteryRows(Battery bat, List<Row> rows)
    {
        if (bat == null) return;
        if (bat.BatterySlotIsEmpty) { rows.Add(new Row("Bateria: sem bateria", -1f)); return; }
        float n = Mathf.Clamp01(bat.NormalizedBatteryLeft);
        rows.Add(new Row("Bateria: " + Mathf.RoundToInt(n * 100f) + "%" + (bat.On ? "" : " (desligada)"), n));
    }

    static void TankRow(Tank t, List<Row> rows)
    {
        if (t == null) return;
        float n = Mathf.Clamp01(t.NormalizedTankAmount);
        rows.Add(new Row("Depósito: " + Mathf.Round(t.CurrentTankAmount) + "/" + Mathf.Round(t.maxCapacity), n, t.IsFull));
    }

    static void Describe(Block b, List<Row> rows)
    {
        Block_CookingStand cs = b as Block_CookingStand;
        if (cs != null)
        {
            bool on = false;
            try { on = cs.IsUsingEnergySource(); } catch { }
            rows.Add(new Row(on ? "A trabalhar" : "Parado", -1f, on));
            // some stands (the advanced purifier) carry a dummy fuel object: MaxFuel -1, count int.MaxValue
            if (cs.fuel != null && cs.fuel.MaxFuel > 0 && cs.fuel.GetFuelCount() >= 0 && cs.fuel.GetFuelCount() <= cs.fuel.MaxFuel)
            {
                int f = cs.fuel.GetFuelCount(), mx = cs.fuel.MaxFuel;
                string burn = cs.fuel.IsBurning() ? " (arde mais " + Time2(cs.fuel.GetBurnSecondsLeft()) + ")" : "";
                rows.Add(new Row("Combustível: " + f + "/" + mx + burn, mx > 0 ? f / (float)mx : -1f));
            }
            foreach (Battery bat in cs.GetComponentsInChildren<Battery>()) BatteryRows(bat, rows);
            int idx = 0;
            if (cs.cookingSlots != null)
                foreach (CookingSlot s in cs.cookingSlots)
                {
                    if (s == null || s.FakeSlot) continue;
                    idx++;
                    Item_Base it = s.CurrentItem;
                    if (it == null) { rows.Add(new Row("Espaço " + idx + ": vazio", -1f)); continue; }
                    if (s.IsComplete)
                    {
                        Item_Base res = null;
                        try { res = it.settings_cookable.CookingResult.item; } catch { }
                        rows.Add(new Row(NameOf(res ?? it) + ": pronto", 1f, true));
                        continue;
                    }
                    float left = s.CookingTimeLeft, total = 0f;
                    try { total = it.settings_cookable.CookingTime * Mathf.Max(0.01f, s.cookTimeMultiplier); } catch { }
                    float p = total > 0f ? Mathf.Clamp01(1f - left / total) : -1f;
                    rows.Add(new Row(NameOf(it) + ": " + (on ? "falta " + Time2(left) : "parado"), p));
                }
            foreach (Tank t in cs.GetComponentsInChildren<Tank>()) TankRow(t, rows);
            return;
        }
        Block_ElectricPurifier ep = b as Block_ElectricPurifier;
        if (ep != null && ep.purifier != null)
        {
            Electric_Purifier p = ep.purifier;
            rows.Add(new Row(p.isOn ? "Ligado" : "Desligado", -1f, p.isOn));
            BatteryRows(p.battery, rows);
            TankRow(p.tank, rows);
            return;
        }
        CookingTable table = null;
        Block_CookingTable_Pot pot = b as Block_CookingTable_Pot;
        if (pot != null) table = pot.cookingPot;
        Block_CookingTable_Juicer ju = b as Block_CookingTable_Juicer;
        if (ju != null) table = ju.juicer;
        if (table != null)
        {
            SO_CookingTable_Recipe r = table.CurrentRecipe;
            if (r == null)
            {
                int filled = 0, all = 0;
                if (table.Slots != null) foreach (CookingTable_Slot sl in table.Slots) { if (sl == null) continue; all++; if (sl.HasItem) filled++; }
                rows.Add(new Row("Sem receita (" + filled + "/" + all + " ingredientes)", -1f));
            }
            else
            {
                float ct = r.CookTime, t = table.CookTimer;
                rows.Add(new Row(NameOf(r.Result), -1f));
                if (table.Portions > 0 && (t <= 0f || t >= ct)) rows.Add(new Row("Pronto: " + table.Portions + " porções", 1f, true));
                else rows.Add(new Row("A cozinhar: falta " + Time2(ct - t), ct > 0f ? Mathf.Clamp01(t / ct) : -1f));
            }
            foreach (Battery bat in b.GetComponentsInChildren<Battery>()) BatteryRows(bat, rows);
            return;
        }
        Cropplot cp = b as Cropplot;
        if (cp != null)
        {
            int n = 0;
            if (cp.plantationSlots != null)
                foreach (PlantationSlot s in cp.plantationSlots)
                {
                    n++;
                    Plant pl = s.plant;
                    string water = s.hasWater ? "" : " (sem água)";
                    if (pl == null) { rows.Add(new Row("Vaso " + n + ": vazio" + water, -1f)); continue; }
                    if (pl.fullyGrown) { rows.Add(new Row(NameOf(pl.item) + ": pronta", 1f, true)); continue; }
                    float g = pl.growTimeSec > 0f ? Mathf.Clamp01(pl.growTimer / pl.growTimeSec) : -1f;
                    rows.Add(new Row(NameOf(pl.item) + ": " + Mathf.RoundToInt(Mathf.Max(0f, g) * 100f) + "%" + water, g));
                }
            return;
        }
        foreach (Tank t in b.GetComponentsInChildren<Tank>()) TankRow(t, rows);
        foreach (Battery bat in b.GetComponentsInChildren<Battery>()) BatteryRows(bat, rows);
    }
}

namespace BCUI_IN
{
    using System.Collections.Generic;
    using UnityEngine;
    using UnityEngine.UI;

    public class InspectHud : MonoBehaviour
    {
        static InspectHud inst;
        RectTransform box;
        string sig;

        public static void Kill() { if (inst != null) Object.Destroy(inst.gameObject); inst = null; }
        public static void Hide() { if (inst != null && inst.gameObject.activeSelf) inst.gameObject.SetActive(false); }

        public static void Show(string title, List<Inspect.Row> rows)
        {
            if (inst == null)
            {
                RectTransform rt = Kit.Node(Kit.Root(), "BC_Inspect");
                Kit.Stretch(rt, 0f, 0f, 0f, 0f);
                inst = rt.gameObject.AddComponent<InspectHud>();
            }
            if (!inst.gameObject.activeSelf) inst.gameObject.SetActive(true);
            inst.Build(title, rows);
        }

        void Build(string title, List<Inspect.Row> rows)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(title);
            foreach (Inspect.Row r in rows) sb.Append('|').Append(r.text).Append(r.bar.ToString("0.00")).Append(r.good);
            string s = sb.ToString();
            if (s == sig && box != null) return;
            sig = s;
            if (box != null) Object.Destroy(box.gameObject);

            const float W = 340f, HeadH = 40f, RowH = 24f, BarH = 9f;
            float h = HeadH + 10f;
            foreach (Inspect.Row r in rows) h += RowH + (r.bar >= 0f ? BarH + 6f : 0f);
            h += 10f;
            box = Kit.Node(transform, "Panel");
            Kit.Place(box, Kit.TL, Kit.TL, new Vector2(30f, -30f), new Vector2(W, h));
            CanvasGroup g = box.gameObject.AddComponent<CanvasGroup>();
            g.interactable = false; g.blocksRaycasts = false;
            // the same small leather book as the Skills panel
            Image csh = Kit.Img(box, "Shadow", InArt.Soft(), new Color(0f, 0f, 0f, 0.6f), true);
            Kit.Place(csh.rectTransform, Kit.C, Kit.C, new Vector2(0f, -8f), new Vector2(W + 60f, h + 60f));
            Image ccover = Kit.Img(box, "Cover", Kit.Get("BC_Round"), InArt.Leather, true);
            Kit.Stretch(ccover.rectTransform, -10f, -10f, -10f, -10f);
            Image cgrain = Kit.Img(box, "Grain", InArt.Mottle(), new Color(0f, 0f, 0f, 0.38f), false);
            Kit.Stretch(cgrain.rectTransform, -8f, -8f, -8f, -8f);
            Image cstitch = Kit.Img(box, "Stitch", InArt.Stitch(), Kit.A(Kit.Cream, 0.4f), false);
            cstitch.type = Image.Type.Tiled; cstitch.fillCenter = false;
            Kit.Stretch(cstitch.rectTransform, -9f, -9f, -9f, -9f);
            Image cpaper = Kit.Img(box, "Paper", InArt.Paper(2), Color.white, false);
            Kit.Stretch(cpaper.rectTransform, 0f, 0f, 0f, 0f);
            Image crule = Kit.Img(box, "Rule", null, Kit.A(Kit.Brown, 0.4f), false);
            Kit.Place(crule.rectTransform, new Vector2(0.5f, 1f), Kit.C, new Vector2(0f, -HeadH - 3f), new Vector2(W - 32f, 2f));
            Text tt = Kit.Txt(box, "Title", title.ToUpper(), 20, Kit.Dark, TextAnchor.MiddleLeft, false);
            Kit.Place(tt.rectTransform, Kit.TL, new Vector2(0f, 0.5f), new Vector2(18f, -HeadH * 0.5f - 6f), new Vector2(W - 36f, 26f));
            float y = HeadH + 10f;
            foreach (Inspect.Row r in rows)
            {
                Text t = Kit.Txt(box, "Row", r.text, 16, r.good ? Kit.Good : Kit.Brown, TextAnchor.MiddleLeft, false);
                Kit.Place(t.rectTransform, Kit.TL, Kit.TL, new Vector2(18f, -y), new Vector2(W - 36f, RowH));
                y += RowH;
                if (r.bar >= 0f)
                {
                    Image bg = Kit.Img(box, "Bar", Kit.Get("BC_Round"), Kit.A(Kit.Dark, 0.28f), true);
                    Kit.Place(bg.rectTransform, Kit.TL, Kit.TL, new Vector2(18f, -y), new Vector2(W - 36f, BarH));
                    Image f = Kit.Img(bg.rectTransform, "Fill", null, r.good || r.bar >= 0.999f ? Kit.Good : new Color(0.72f, 0.45f, 0.16f), false);
                    RectTransform fr = f.rectTransform;
                    fr.anchorMin = Vector2.zero; fr.anchorMax = new Vector2(Mathf.Clamp01(r.bar), 1f);
                    fr.offsetMin = new Vector2(1f, 1f); fr.offsetMax = new Vector2(-1f, -1f);
                    Image shine = Kit.Img(fr, "Shine", null, Kit.A(Kit.Cream, 0.28f), false);
                    shine.rectTransform.anchorMin = new Vector2(0f, 0.55f); shine.rectTransform.anchorMax = Vector2.one;
                    shine.rectTransform.offsetMin = Vector2.zero; shine.rectTransform.offsetMax = Vector2.zero;
                    y += BarH + 6f;
                }
            }
        }
    }

    static class InArt
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
}

// ---------- BestCode UI kit ----------
// Drawn with Raft's own font (ChineseRocks) and sprites (parchment plate, item slot, key chip), on
// one shared overlay canvas. Every BestCode mod carries its own copy of this kit in its own
// namespace; mods only share the canvas object and the toast column (plain Unity components and
// RectTransforms), so one mod reloading never breaks another. Source: raft-mods/src/bcui_kit.cs.txt
namespace BCUI_IN
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
