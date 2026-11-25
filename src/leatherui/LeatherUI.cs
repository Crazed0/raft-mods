using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using HMLLibrary;
using BCUI_LU;

// Couro e Papel: gives the game's own menus (crafting, inventory, hotbar, research table, buttons)
// the same leather and paper look as the BestCode panels. Nothing is loaded from disk: every
// texture is painted here when the mod starts, and the game's sprites are swapped for them in
// memory. Unloading the mod puts every original sprite back.
public class LeatherUI : Mod
{
    public const string Version = "1.7.0";

    readonly Dictionary<Sprite, Sprite> map = new Dictionary<Sprite, Sprite>();   // game sprite -> ours
    readonly Dictionary<Sprite, Sprite> back = new Dictionary<Sprite, Sprite>();  // ours -> game sprite
    readonly Dictionary<string, Sprite> made = new Dictionary<string, Sprite>();  // ours, by game name
    readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
    readonly Dictionary<Type, FieldInfo[]> spriteFields = new Dictionary<Type, FieldInfo[]>();
    float nextImages, nextDeep;
    int deepPasses;

    public void Start()
    {
        foreach (SkinSpec s in SkinPaint.All())
        {
            try { made[s.name] = Build(s); }
            catch (Exception e) { Debug.LogWarning("[Couro e Papel] " + s.name + ": " + e.Message); }
        }
        BuildBookArt();
        SceneManager.sceneLoaded += OnScene;
        Collect();
        Swap(map, true);
        deepPasses = 2;
        nextDeep = Time.unscaledTime + 2f;
        Debug.Log("[Couro e Papel] " + Version + " loaded, " + made.Count + " sprites painted, " + map.Count + " game sprites matched");
    }

    public void OnModUnload()
    {
        SceneManager.sceneLoaded -= OnScene;
        try { HideBook(); } catch (Exception e) { Debug.LogWarning("[Couro e Papel] book restore: " + e.Message); }
        if (book != null) UnityEngine.Object.Destroy(book);
        bookArt.Clear();
        try { Swap(back, true); } catch (Exception e) { Debug.LogWarning("[Couro e Papel] restore: " + e.Message); }
        foreach (UnityEngine.Object o in owned) if (o != null) UnityEngine.Object.Destroy(o);
        owned.Clear(); map.Clear(); back.Clear(); made.Clear();
        Debug.Log("[Couro e Papel] unloaded");
    }

    void OnScene(Scene scene, LoadSceneMode mode)
    {
        // the menus of a new scene come up over a few frames, so look again a few times
        deepPasses = 3;
        nextDeep = Time.unscaledTime + 0.5f;
    }

    public void Update()
    {
        BookTick();
        float now = Time.unscaledTime;
        if (deepPasses > 0 && now >= nextDeep)
        {
            deepPasses--;
            nextDeep = now + 2f;
            nextImages = now + 2f;
            Collect();
            Swap(map, true);
        }
        else if (now >= nextImages)
        {
            nextImages = now + 2f;
            Swap(map, false);
        }
    }

    Sprite Build(SkinSpec s)
    {
        Texture2D tex = new Texture2D(s.w, s.h, TextureFormat.RGBA32, false);
        tex.name = "BC_Skin_" + s.name;
        tex.wrapMode = s.repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        tex.hideFlags = HideFlags.HideAndDontSave;
        tex.SetPixels(SkinPaint.Render(s));
        tex.Apply(false, true);
        Sprite sp = Sprite.Create(tex, new Rect(0f, 0f, s.w, s.h), new Vector2(0.5f, 0.5f), s.ppu, 0u,
            SpriteMeshType.FullRect, new Vector4(s.bl, s.bb, s.br, s.bt));
        sp.name = "BC_Skin_" + s.name;
        sp.hideFlags = HideFlags.HideAndDontSave;
        owned.Add(tex); owned.Add(sp);
        return sp;
    }

    // match every loaded game sprite to its replacement by name
    void Collect()
    {
        foreach (Sprite sp in Resources.FindObjectsOfTypeAll<Sprite>())
        {
            if (sp == null || map.ContainsKey(sp) || back.ContainsKey(sp)) continue;
            Sprite ours;
            if (!made.TryGetValue(sp.name, out ours)) continue;
            map[sp] = ours;
            if (!back.ContainsKey(ours)) back[ours] = sp;
        }
    }

    // the BestCode panels (BC_UICanvas) already have their own look
    static bool Ours(Component c)
    {
        Transform root = c.transform.root;
        return root != null && root.name.StartsWith("BC_", StringComparison.Ordinal);
    }

    static Sprite To(Sprite s, Dictionary<Sprite, Sprite> d)
    {
        Sprite r;
        return s != null && d.TryGetValue(s, out r) ? r : null;
    }

    // Images and button states everywhere, prefabs included, so menus made later are already skinned.
    // deep also rewrites the Sprite fields the game's scripts keep (hover and selected slot looks).
    void Swap(Dictionary<Sprite, Sprite> d, bool deep)
    {
        if (d.Count == 0) return;
        foreach (Image img in Resources.FindObjectsOfTypeAll<Image>())
        {
            if (img == null || Ours(img)) continue;
            // overrideSprite reads back the plain sprite when no override is set; only touch a real one
            Sprite ov = img.overrideSprite;
            bool hasOverride = ov != null && ov != img.sprite;
            Sprite r = To(img.sprite, d);
            if (r != null) img.sprite = r;
            if (hasOverride)
            {
                Sprite ro = To(ov, d);
                if (ro != null) img.overrideSprite = ro;
            }
        }
        foreach (Selectable sel in Resources.FindObjectsOfTypeAll<Selectable>())
        {
            if (sel == null || Ours(sel)) continue;
            SpriteState st = sel.spriteState;
            bool changed = false;
            Sprite r = To(st.highlightedSprite, d);
            if (r != null) { st.highlightedSprite = r; changed = true; }
            r = To(st.pressedSprite, d);
            if (r != null) { st.pressedSprite = r; changed = true; }
            r = To(st.disabledSprite, d);
            if (r != null) { st.disabledSprite = r; changed = true; }
            if (changed) sel.spriteState = st;
        }
        if (!deep) return;
        foreach (MonoBehaviour mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
        {
            if (mb == null) continue;
            FieldInfo[] fs = FieldsOf(mb.GetType());
            for (int k = 0; k < fs.Length; k++)
            {
                FieldInfo f = fs[k];
                try
                {
                    if (f.FieldType == typeof(Sprite))
                    {
                        Sprite r = To(f.GetValue(mb) as Sprite, d);
                        if (r != null) f.SetValue(mb, r);
                    }
                    else
                    {
                        Sprite[] arr = f.GetValue(mb) as Sprite[];
                        if (arr == null) continue;
                        for (int i = 0; i < arr.Length; i++)
                        {
                            Sprite r = To(arr[i], d);
                            if (r != null) arr[i] = r;
                        }
                    }
                }
                catch (Exception) { }
            }
        }
    }

    // Sprite and Sprite[] fields of the game's own scripts (never Unity's or other mods')
    FieldInfo[] FieldsOf(Type t)
    {
        FieldInfo[] fs;
        if (spriteFields.TryGetValue(t, out fs)) return fs;
        List<FieldInfo> l = new List<FieldInfo>();
        if (t.Assembly.GetName().Name == "Assembly-CSharp")
        {
            for (Type k = t; k != null && k != typeof(MonoBehaviour); k = k.BaseType)
                foreach (FieldInfo f in k.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!f.IsInitOnly && (f.FieldType == typeof(Sprite) || f.FieldType == typeof(Sprite[]))) l.Add(f);
        }
        fs = l.ToArray();
        spriteFields[t] = fs;
        return fs;
    }
    // ---------- the open book behind the inventory ----------
    // While the inventory (with crafting, a storage or the research table) is open, the same leather
    // journal as the logbook (T) lies open behind the game's own panels: the category column and the
    // recipe list on the left page, always at full size (a row too long for the page breaks onto a second
    // line instead of crossing the spine), the selected recipe card drawn large at the top of the right
    // page and the inventory at its bottom. The world dims around it, the panels lose their brown boxes and
    // their light text turns to ink. The book is fitted to the screen every frame and kept above the
    // hotbar, and the panels are centred on their pages. Everything is put back when the menu closes.
    const float M = 20f, PadOut = 58f, PadIn = 52f, Head = 118f, NomW = 600f, Foot = 30f, Gap = 30f,
        ListHead = 85f, MaxCard = 2.2f, MinCard = 0.7f;
    static readonly Color Leather = new Color(0.29f, 0.17f, 0.085f, 1f);
    static readonly Color Cream = new Color(0.956f, 0.893f, 0.759f, 1f);
    static readonly Color Brown = new Color(0.373f, 0.224f, 0.125f, 1f);
    static readonly Color Dark = new Color(0.2f, 0.12f, 0.06f, 1f);
    static readonly Color InkCol = new Color(0.27f, 0.16f, 0.08f, 1f);
    static readonly HashSet<string> PanelSprites = new HashSet<string>(StringComparer.Ordinal) {
        "MenuBackground_9Slice", "BC_Skin_MenuBackground_9Slice", "Box1", "BC_Skin_Box1",
        "NotebookCrafting_Background", "BC_Skin_NotebookCrafting_Background" };

    class Held<T> { public T orig, set; public bool has; }
    class Inked { public Color orig, set; public bool has, skip; public Shadow[] shadows; public readonly List<Shadow> off = new List<Shadow>(); }

    readonly Dictionary<string, Sprite> bookArt = new Dictionary<string, Sprite>();
    readonly Dictionary<RectTransform, Held<Vector2>> moved = new Dictionary<RectTransform, Held<Vector2>>();
    readonly Dictionary<Image, Held<Color>> hidden = new Dictionary<Image, Held<Color>>();
    readonly Dictionary<Text, Inked> inked = new Dictionary<Text, Inked>();
    readonly Vector3[] corners = new Vector3[4];
    GameObject book;
    RectTransform bookRoot, pages;
    Transform canvasT, craftingT, consoleT, invParentT, hotbarT;
    RectTransform craftRef, listRT, cardT;
    CraftingMenu menu;
    // the recipe card's place and size are ours while the book is open
    Vector3 cardScaleOrig;
    Vector2 cardPosOrig, cardPosSet, cardTarget;
    bool cardHas, cardOn;
    float cardScale = 1f;
    // the recipe list: its width (up to the spine), its height (down to the page's foot) and the rows
    // broken onto more lines
    class Wrap
    {
        public RectTransform sub; public HorizontalLayoutGroup hlg; public ContentSizeFitter csf; public bool csfOn;
        public Vector2 aMin, aMax, piv, pos, size; public float rowH, subH, left, top;
        public LayoutElement le; public bool leAdded; public float leMin, lePref;
    }
    readonly Dictionary<RectTransform, Wrap> wraps = new Dictionary<RectTransform, Wrap>();
    readonly List<RectTransform> rowKids = new List<RectTransform>();
    readonly Held<Vector2> listSize = new Held<Vector2>(), viewSize = new Held<Vector2>();
    readonly Held<bool> listVert = new Held<bool>();
    bool listOn;
    float listW, listRoom, colInset;
    // what of each inventory panel is really drawn (its rect is taller than its slots), in its own space
    readonly Dictionary<RectTransform, Rect> invVis = new Dictionary<RectTransform, Rect>();
    // the recipe card as it is drawn, in its own space
    Rect cardVis;
    bool cardVisHas;
    // what decided the inventory's measured edges, written to the log when the layout changes
    string invWhy = "", lastWhy = "", cardWhy = "";
    // panels with a canvas of their own (the equipment slots) are drawn with the rest while the book is
    // open, or they would sit under the pages
    readonly List<Canvas> flattened = new List<Canvas>();
    Font font;
    bool bookShown, bookFailed;
    float nextScan;
    Vector2 craftShift, invShift;   // in the book root's units

    // ---- art: the logbook's leather, paper, stitching and shadows, painted once ----
    void BuildBookArt()
    {
        try
        {
            ArtRound(); ArtSoft(); ArtStitch(); ArtGrad(); ArtVignette(); ArtMottle(); ArtPaper(0); ArtPaper(1);
        }
        catch (Exception e) { Debug.LogWarning("[Couro e Papel] book art: " + e.Message); }
    }

    static Texture2D ArtTex(int w, int h)
    {
        Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, false);
        t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear; t.hideFlags = HideFlags.HideAndDontSave;
        return t;
    }

    void ArtPut(string name, Texture2D t, Vector4 border)
    {
        t.name = "BC_Book" + name;
        t.Apply(false, true);
        Sprite s = Sprite.Create(t, new Rect(0f, 0f, t.width, t.height), new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect, border);
        s.name = "BC_Book" + name; s.hideFlags = HideFlags.HideAndDontSave;
        owned.Add(t); owned.Add(s);
        bookArt[name] = s;
    }

    // signed distance to a rounded rectangle spanning 0..w x 0..h (negative inside)
    static float SdBox(float x, float y, float w, float h, float r)
    {
        float qx = Mathf.Abs(x - w * 0.5f) - (w * 0.5f - r);
        float qy = Mathf.Abs(y - h * 0.5f) - (h * 0.5f - r);
        float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
        return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
    }

    static float Grain(int x, int y)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }

    // white rounded box, 9-sliced
    void ArtRound()
    {
        int n = 32; float r = 10f; Texture2D t = ArtTex(n, n);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = Mathf.Max(0f, Mathf.Max(r - x - 0.5f, x + 0.5f - (n - r)));
                float dy = Mathf.Max(0f, Mathf.Max(r - y - 0.5f, y + 0.5f - (n - r)));
                t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f)));
            }
        ArtPut("Round", t, new Vector4(12f, 12f, 12f, 12f));
    }

    // soft drop shadow, 9-sliced
    void ArtSoft()
    {
        int n = 96; Texture2D t = ArtTex(n, n);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = SdBox(x + 0.5f - 32f, y + 0.5f - 32f, n - 64f, n - 64f, 8f);
                float a = Mathf.Clamp01(1f - (d + 2f) / 32f);
                t.SetPixel(x, y, new Color(1f, 1f, 1f, a * a * (3f - 2f * a)));
            }
        ArtPut("Soft", t, new Vector4(44f, 44f, 44f, 44f));
    }

    // dashed stitching along a rounded edge (Image.Type.Tiled, fillCenter off)
    void ArtStitch()
    {
        int n = 48; Texture2D t = ArtTex(n, n);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = Mathf.Abs(SdBox(x + 0.5f - 4f, y + 0.5f - 4f, n - 8f, n - 8f, 10f));
                float a = Mathf.Clamp01(1.3f - d);
                if (x >= 16 && x < 32 && ((x - 16) % 8) >= 5) a = 0f;
                if (y >= 16 && y < 32 && ((y - 16) % 8) >= 5) a = 0f;
                t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        ArtPut("Stitch", t, new Vector4(16f, 16f, 16f, 16f));
    }

    // horizontal fall-off, strongest in the middle (the spine)
    void ArtGrad()
    {
        int w = 64, h = 4; Texture2D t = ArtTex(w, h);
        for (int x = 0; x < w; x++)
        {
            float u = (x + 0.5f) / w;
            float a = Mathf.Pow(1f - Mathf.Abs(u * 2f - 1f), 2.4f);
            for (int y = 0; y < h; y++) t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        ArtPut("Grad", t, Vector4.zero);
    }

    // dark corners for the world behind the book
    void ArtVignette()
    {
        int n = 64; Texture2D t = ArtTex(n, n);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float r = Mathf.Sqrt(dx * dx + dy * dy) / 1.4142f;
                t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.SmoothStep(0f, 1f, (r - 0.2f) / 0.8f)));
            }
        ArtPut("Vignette", t, Vector4.zero);
    }

    // leather grain: blotches laid over the cover in black
    void ArtMottle()
    {
        int n = 192; Texture2D t = ArtTex(n, n);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float a = 0.55f * Mathf.PerlinNoise(x * 0.045f, y * 0.045f) + 0.3f * Mathf.PerlinNoise(x * 0.21f + 40f, y * 0.21f + 40f) + 0.15f * Grain(x, y);
                t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        ArtPut("Mottle", t, Vector4.zero);
    }

    // aged paper: mode 0 = left page (spine on the right), 1 = right page
    void ArtPaper(int mode)
    {
        int w = 256, h = 320; Texture2D t = ArtTex(w, h);
        Color baseC = new Color(0.95f, 0.89f, 0.75f), burn = new Color(0.69f, 0.51f, 0.30f);
        float seed = mode * 37.1f, r = 5f;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float n1 = Mathf.PerlinNoise(x * 0.02f + seed, y * 0.02f + 11f);
                float n2 = Mathf.PerlinNoise(x * 0.11f + 70f + seed, y * 0.11f);
                float k = 0.94f + 0.05f * n1 + 0.025f * n2 - (Grain(x + mode * 1000, y) > 0.996f ? 0.07f : 0f);
                Color c = new Color(baseC.r * k, baseC.g * k, baseC.b * k, 1f);
                float dl = x, dr = w - 1 - x, db = y, dt = h - 1 - y;
                if (mode == 0) dr = 999f; else dl = 999f;
                float d = Mathf.Min(Mathf.Min(dl, dr), Mathf.Min(db, dt));
                float b = (1f - Mathf.SmoothStep(0f, 1f, d / 22f)) * (0.45f + 0.35f * n1 + 0.2f * n2);
                c = Color.Lerp(c, burn, b);
                float s = mode == 0 ? (float)x / (w - 1) : 1f - (float)x / (w - 1);
                float m = 1f - 0.22f * Mathf.Pow(s, 7f);
                c = new Color(c.r * m, c.g * m, c.b * m, 1f);
                // rounded outer corners
                bool left = mode == 0, right = mode == 1;
                float cx = -1f, cy = -1f;
                if (left && x < r) cx = r; else if (right && x > w - 1 - r) cx = w - 1 - r;
                if (y < r) cy = r; else if (y > h - 1 - r) cy = h - 1 - r;
                if (cx >= 0f && cy >= 0f)
                {
                    float ex = x - cx, ey = y - cy;
                    c.a = Mathf.Clamp01(r - Mathf.Sqrt(ex * ex + ey * ey) + 0.5f);
                }
                t.SetPixel(x, y, c);
            }
        ArtPut("Paper" + mode, t, Vector4.zero);
    }

    Sprite Art(string n) { Sprite s; return bookArt.TryGetValue(n, out s) ? s : null; }

    // ---- showing and hiding ----
    bool lastWant;
    void BookTick()
    {
        if (bookFailed) return;
        try
        {
            bool want = CanvasHelper.ActiveMenu == MenuType.Inventory;
            if (want != lastWant) { lastWant = want; Debug.Log("[Couro e Papel] menu " + CanvasHelper.ActiveMenu); }
            if (want)
            {
                if (!EnsureBook()) return;
                if (!bookShown) { book.SetActive(true); bookShown = true; nextScan = 0f; }
                Keep();
            }
            else if (bookShown) HideBook();
        }
        catch (Exception e)
        {
            bookFailed = true;
            Debug.LogWarning("[Couro e Papel] book: " + e);
            try { HideBook(); } catch (Exception) { }
        }
    }

    void HideBook()
    {
        Restore();
        if (book != null) book.SetActive(false);
        bookShown = false;
    }

    bool EnsureBook()
    {
        if (book != null && canvasT != null && craftingT != null && invParentT != null) return true;
        if (book != null) UnityEngine.Object.Destroy(book);
        book = null;
        GameObject cv = GameObject.Find("_CanvasGame_New");
        if (cv == null) return false;
        canvasT = cv.transform;
        craftingT = canvasT.Find("Crafting");
        consoleT = canvasT.Find("CraftingConsole");
        invParentT = canvasT.Find("InventoryParent");
        hotbarT = invParentT != null ? invParentT.Find("Hotbar") : null;
        if (craftingT == null || invParentT == null) return false;
        craftRef = craftingT.Find("CraftingBox_Category") as RectTransform;
        listRT = craftRef != null ? craftRef.Find("CraftingBox_Selected Category") as RectTransform : null;
        cardT = listRT != null ? listRT.Find("SelectedItemBox") as RectTransform : null;
        menu = craftingT.GetComponent<CraftingMenu>();
        cardHas = false;
        font = null;
        foreach (Font f in Resources.FindObjectsOfTypeAll<Font>())
            if (f != null && f.name.IndexOf("ChineseRocks", StringComparison.OrdinalIgnoreCase) >= 0) { font = f; break; }
        if (font == null)
            foreach (Text t in craftingT.GetComponentsInChildren<Text>(true)) if (t.font != null) { font = t.font; break; }
        if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        moved.Clear(); hidden.Clear(); inked.Clear();
        // a book left behind by an older copy of the mod would stay open forever
        for (int i = canvasT.childCount - 1; i >= 0; i--)
        {
            Transform old = canvasT.GetChild(i);
            if (old.name == "BC_Book") { old.gameObject.SetActive(false); UnityEngine.Object.Destroy(old.gameObject); }
        }
        book = MakeBook(canvasT);
        book.SetActive(false);
        bookShown = false;
        return true;
    }

    // ---- building ----
    static RectTransform Node(Transform parent, string name)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.layer = parent.gameObject.layer;
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    static Image Pic(Transform parent, string name, Sprite sp, Color col, Image.Type type)
    {
        RectTransform rt = Node(parent, name);
        Image img = rt.gameObject.AddComponent<Image>();
        img.sprite = sp; img.color = col; img.type = type; img.raycastTarget = false;
        return img;
    }

    // anchors min..max, then offsets inward from those anchors: left, bottom, right, top
    static void Span(RectTransform rt, Vector2 amin, Vector2 amax, float l, float b, float r, float t)
    {
        rt.anchorMin = amin; rt.anchorMax = amax; rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(l, b); rt.offsetMax = new Vector2(-r, -t);
    }

    // a full-width strip near the top of a page: inset l and r, top edge y (negative, from the top), h tall
    static void Band(RectTransform rt, float l, float r, float y, float h)
    {
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(l, y - h); rt.offsetMax = new Vector2(-r, y);
    }

    static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = anchor; rt.pivot = pivot;
        rt.anchoredPosition = pos; rt.sizeDelta = size;
    }

    static Color A(Color c, float a) { return new Color(c.r, c.g, c.b, a); }

    Text Label(Transform parent, string name, string s, int size, Color c, TextAnchor al)
    {
        Text t = Node(parent, name).gameObject.AddComponent<Text>();
        t.font = font; t.fontSize = size; t.color = c; t.alignment = al; t.text = s; t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
        return t;
    }

    GameObject MakeBook(Transform parent)
    {
        Vector2 Z = Vector2.zero, O = Vector2.one;
        bookRoot = Node(parent, "BC_Book");
        Span(bookRoot, Z, O, 0f, 0f, 0f, 0f);
        CanvasGroup cg = bookRoot.gameObject.AddComponent<CanvasGroup>();
        cg.blocksRaycasts = false; cg.interactable = false;

        // the world dims behind the book, darker at the corners
        Span(Pic(bookRoot, "Veil", null, new Color(0f, 0f, 0f, 0.42f), Image.Type.Simple).rectTransform, Z, O, 0f, 0f, 0f, 0f);
        Span(Pic(bookRoot, "Vignette", Art("Vignette"), new Color(0f, 0f, 0f, 0.6f), Image.Type.Simple).rectTransform, Z, O, 0f, 0f, 0f, 0f);

        // the open book; its size is set every frame by Layout()
        pages = Node(bookRoot, "Book");
        Place(pages, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Z, new Vector2(1740f, 880f));

        Span(Pic(pages, "Shadow", Art("Soft"), new Color(0f, 0f, 0f, 0.75f), Image.Type.Sliced).rectTransform, Z, O,
            -M - 32f, -M - 52f, -M - 32f, -M - 12f);

        // leather cover with stitching
        Span(Pic(pages, "Cover", Art("Round"), Leather, Image.Type.Sliced).rectTransform, Z, O, -M, -M, -M, -M);
        Span(Pic(pages, "Grain", Art("Mottle"), new Color(0f, 0f, 0f, 0.38f), Image.Type.Simple).rectTransform, Z, O, -M + 4f, -M + 4f, -M + 4f, -M + 4f);
        Image stitch = Pic(pages, "Stitch", Art("Stitch"), A(Cream, 0.4f), Image.Type.Tiled);
        stitch.fillCenter = false;
        Span(stitch.rectTransform, Z, O, -M + 6f, -M + 6f, -M + 6f, -M + 6f);

        // page block: the edges of the pages underneath, then the two open pages
        Span(Pic(pages, "Stack1", Art("Round"), new Color(0.74f, 0.65f, 0.49f, 1f), Image.Type.Sliced).rectTransform, Z, O, -7f, -7f, -7f, -1f);
        Span(Pic(pages, "Stack2", Art("Round"), new Color(0.86f, 0.79f, 0.64f, 1f), Image.Type.Sliced).rectTransform, Z, O, -4f, -4f, -4f, -1f);
        RectTransform left = Pic(pages, "Page0", Art("Paper0"), Color.white, Image.Type.Simple).rectTransform;
        Span(left, Z, new Vector2(0.5f, 1f), 0f, 0f, 0f, 0f);
        RectTransform right = Pic(pages, "Page1", Art("Paper1"), Color.white, Image.Type.Simple).rectTransform;
        Span(right, new Vector2(0.5f, 0f), O, 0f, 0f, 0f, 0f);
        Span(Pic(pages, "Spine", Art("Grad"), new Color(0.16f, 0.08f, 0.03f, 0.5f), Image.Type.Simple).rectTransform,
            new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), -60f, 0f, -60f, 0f);
        Span(Pic(pages, "Fold", null, new Color(0.16f, 0.08f, 0.03f, 0.45f), Image.Type.Simple).rectTransform,
            new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), -1f, 0f, -1f, 0f);

        Header(left, true, "FABRICO", "Receitas e materiais");
        leftTitle = left.Find("Title").GetComponent<Text>();
        leftSub = left.Find("Sub").GetComponent<Text>();
        Header(right, false, "INVENTÁRIO", "Mochila e arrumação");
        return bookRoot.gameObject;
    }

    // page title and subtitle, then a double ink rule with a small diamond in the middle
    void Header(RectTransform page, bool leftPage, string title, string sub)
    {
        float inL = leftPage ? PadOut : PadIn, inR = leftPage ? PadIn : PadOut;
        Vector2 anchor = new Vector2(leftPage ? 0f : 1f, 1f), pivot = new Vector2(leftPage ? 0f : 1f, 0.5f);
        float x = leftPage ? PadOut : -PadOut;
        TextAnchor al = leftPage ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
        Place(Label(page, "Title", title, 44, Dark, al).rectTransform, anchor, pivot, new Vector2(x, -52f), new Vector2(560f, 52f));
        Place(Label(page, "Sub", sub, 19, A(Brown, 0.75f), al).rectTransform, anchor, pivot, new Vector2(x + (leftPage ? 2f : -2f), -87f), new Vector2(540f, 24f));
        Band(Pic(page, "Rule", null, A(Brown, 0.5f), Image.Type.Simple).rectTransform, inL, inR, -103f, 2f);
        Band(Pic(page, "RuleThin", null, A(Brown, 0.25f), Image.Type.Simple).rectTransform, inL, inR, -108f, 1f);
        RectTransform d = Pic(page, "Orn", Art("Round"), Brown, Image.Type.Sliced).rectTransform;
        Place(d, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2((inL - inR) * 0.5f, -104f), new Vector2(10f, 10f));
        d.localEulerAngles = new Vector3(0f, 0f, 45f);
    }

    // ---- fitting the book to the screen and the panels to the pages ----
    Rect Bounds(RectTransform rt, RectTransform space)
    {
        rt.GetWorldCorners(corners);
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        for (int i = 0; i < 4; i++)
        {
            Vector3 p = space.InverseTransformPoint(corners[i]);
            x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x);
            y0 = Mathf.Min(y0, p.y); y1 = Mathf.Max(y1, p.y);
        }
        return Rect.MinMaxRect(x0, y0, x1, y1);
    }

    // where rt would be if we had not moved `mover` (rt itself or one of its parents)
    Rect Home(RectTransform rt, RectTransform mover)
    {
        Rect b = Bounds(rt, bookRoot);
        Held<Vector2> h;
        if (mover != null && moved.TryGetValue(mover, out h) && h.has && mover.anchoredPosition == h.set)
        {
            Vector3 a = bookRoot.InverseTransformVector(mover.parent.TransformVector(h.set - h.orig));
            b.x -= a.x; b.y -= a.y;
        }
        return b;
    }

    bool Movable(Transform c)
    {
        return c != hotbarT && c.gameObject.activeSelf && c is RectTransform
            && !c.name.StartsWith("Gamepad", StringComparison.Ordinal);
    }

    void Layout()
    {
        Rect r = bookRoot.rect;
        float top = r.yMax - 26f - M;
        float bottom = r.yMin + 36f + M;
        if (hotbarT != null && hotbarT.gameObject.activeInHierarchy)
            bottom = Mathf.Max(bottom, Bounds(hotbarT as RectTransform, bookRoot).yMax + 12f + M);
        float w = Mathf.Round(Mathf.Min(r.width - 2f * (56f + M), 1900f));
        float h = Mathf.Round(Mathf.Max(420f, top - bottom));
        float cy = Mathf.Round((top + bottom) * 0.5f);
        if (pages.sizeDelta != new Vector2(w, h)) pages.sizeDelta = new Vector2(w, h);
        if (pages.anchoredPosition != new Vector2(r.center.x, cy)) pages.anchoredPosition = new Vector2(r.center.x, cy);
        float cx = r.center.x, content = cy + h * 0.5f - Head, floor = cy - h * 0.5f + Foot;
        // canvas units to book units (1 unless the canvases scale differently)
        float unit = Mathf.Abs(bookRoot.InverseTransformVector(craftingT.parent.TransformVector(Vector3.up)).y);
        if (unit < 0.01f) unit = 1f;

        // crafting: the category column always stands at the same place on the left page, at full size.
        // The recipe list beside it reaches to the spine and down to the foot of the page; FitList breaks
        // any row too long for it onto more lines.
        listOn = false;
        if (craftRef != null)
        {
            float lx0 = cx - w * 0.5f + PadOut, lx1 = cx - PadIn;
            Rect col = Home(craftRef, craftingT as RectTransform);
            // the drawn left edge of the category buttons lines up with the page title
            float inset = craftRef.rect.width > 0.01f ? colInset * col.width / craftRef.rect.width : 0f;
            craftShift = new Vector2(Mathf.Round(lx0 - (col.xMin + inset)), Mathf.Round(content - col.yMax));
            if (listRT != null && listRT.gameObject.activeInHierarchy && menu != null)
            {
                Rect lr = Home(listRT, craftingT as RectTransform);
                float colX0 = lr.xMin + craftShift.x;
                listW = Mathf.Floor((lx1 - 6f - colX0) / unit);
                // the selected recipe sits on the foot of the left page under the list, as wide as the list
                // column (never smaller than the game draws it); the list scrolls in the room above it
                float cardRoom = 0f;
                listRoom = Mathf.Floor((lr.yMax + craftShift.y - floor - cardRoom) / unit);
                listOn = listW > 160f && listRoom > ListHead + 60f;
            }
        }

        // the right page is one column: the recipe card on top, as large as the page allows, and right
        // under it the inventory (with any storage or research table beside it), as it is really drawn
        float rx0 = cx + PadIn, rx1 = cx + w * 0.5f - PadOut, pw = rx1 - rx0;
        bool any = false;
        Rect inv = new Rect(), store = new Rect();
        storeOpen = false;
        string parts = "";
        foreach (Transform c in invParentT)
        {
            if (!Movable(c)) continue;
            RectTransform rt = (RectTransform)c;
            Track(rt);
            Rect hr = Home(rt, rt);
            if (hr.width < 1f || hr.height < 1f) continue;
            Rect v;
            if (rt.gameObject.activeInHierarchy && rt.name.Contains("Storage"))
            {
                // a chest goes on the left page by itself (crafting is put away while it is open)
                if (invVis.TryGetValue(rt, out v)) hr = Part(hr, rt.rect, v);
                parts += " " + rt.name + R(hr);
                store = storeOpen ? Union(store, hr) : hr;
                storeOpen = true;
                continue;
            }
            if (invVis.TryGetValue(rt, out v)) hr = Part(hr, rt.rect, v);
            // a panel that draws nothing (an empty holder) takes no room; its bare rect used to push the
            // inventory ~95 units off the foot of the page
            else if (!rt.gameObject.activeInHierarchy) continue;
            // the player's own inventory always counts, even when its drawing was not measured
            // (opening a chest left it out and the recipe card was drawn over it)
            else if (invVis.Count > 0 && rt.name != "Inventory_Player") continue;
            parts += " " + rt.name + R(hr);
            inv = any ? Union(inv, hr) : hr;
            any = true;
        }

        cardOn = cardT != null && cardT.gameObject.activeInHierarchy;
        bool crafting = cardT != null && craftingT.gameObject.activeInHierarchy && listOn;
        if (!any) return;
        // the inventory leans on the outer edge of the right page; the recipe card fills the room between
        // the spine and the inventory
        float sx = rx1 - inv.xMax, sy = content - inv.yMax;
        // the right page is the inventory's alone: right under the header, centred
        // never into the spine; past the outer margin only if it cannot fit otherwise
        float lo = cx + 16f - inv.xMin, hi = cx + w * 0.5f - 16f - inv.xMax;
        sx = lo <= hi ? Mathf.Clamp(sx, lo, hi) : lo;
        // too tall for the page: its top stays under the header
        if (inv.yMax + sy > content) sy = content - inv.yMax;
        if (storeOpen)
        {
            // chest open: the inventory centred on the right page, the chest centred on the left one
            // both lean on the spine, one each side of it
            sx = cx + 70f - inv.xMin;
            float lx0 = cx - w * 0.5f + PadOut, lx1 = cx - PadIn;
            float tx = cx - 70f - store.xMax;
            float tlo = cx - w * 0.5f + 16f - store.xMin, thi = cx - 16f - store.xMax;
            tx = tlo <= thi ? Mathf.Clamp(tx, tlo, thi) : thi;
            // the chest's top lines up with the inventory's top
            float top0 = Mathf.Min(content, inv.yMax + (inv.yMax + sy > content ? content - inv.yMax : sy));
            storeShift = new Vector2(Mathf.Round(tx), Mathf.Round(top0 - store.yMax));
        }
        invShift = new Vector2(Mathf.Round(sx), Mathf.Round(sy));
        if (cardOn && !storeOpen)
        {
            float cx0 = rx0, cx1 = inv.xMin + sx - 24f;
            Rect cv = cardVisHas ? cardVis : cardT.rect;
            float kNow = Mathf.Abs(bookRoot.InverseTransformVector(cardT.TransformVector(Vector3.up)).y);
            float k1 = cardHas && Mathf.Abs(cardT.localScale.y) > 0.001f ? kNow * cardScaleOrig.y / cardT.localScale.y : kNow;
            if (k1 < 0.01f) k1 = unit;
            float cw = Mathf.Max(1f, cv.width * k1), ch = Mathf.Max(1f, cv.height * k1);
            float sc = 0.75f * Mathf.Min(MaxCard, Mathf.Min((cx1 - cx0) / cw, (content - floor) / ch));
            if ((cx1 - cx0) < cw * 0.8f)
            {
                // no room beside the inventory (a chest or another panel is open too): the card goes to the
                // foot of the left page, under the recipe list, which then scrolls in the room above it
                float lx0 = cx - w * 0.5f + PadOut, lx1 = cx - PadIn;
                cardScale = Mathf.Round(Mathf.Clamp(Mathf.Min(1f, (lx1 - lx0) / cw), MinCard, 1f) * 20f) / 20f;
                float cardH = ch * cardScale;
                if (listOn) listRoom = Mathf.Max(ListHead + 60f, listRoom - Mathf.Floor((cardH + 16f) / unit));
                cardTarget = new Vector2(Mathf.Round(lx1 - 6f - cw * cardScale * 0.5f), Mathf.Round(floor + cardH));
            }
            else
            {
            cardScale = Mathf.Round(Mathf.Clamp(sc, 0.8f, MaxCard) * 20f) / 20f;
            // lined up with the page header on the left and with the inventory title on top
            cardTarget = new Vector2(Mathf.Round(cx0 + cardT.rect.width * k1 * cardScale * 0.5f - (cv.xMin - cardT.rect.xMin) * k1 * cardScale), Mathf.Round(content - 14f));
            }
        }
        string why = "inv " + R(inv) + " shift " + invShift + " content " + Mathf.Round(content) + " floor " + Mathf.Round(floor)
            + " card x" + cardScale + (cardVisHas ? " vis " + R(cardVis) : "") + " | " + invWhy + " | parts" + parts + " | card bottom=" + cardWhy + " cardRect " + (cardT != null ? R(cardT.rect) : "");
        if (why != lastWhy)
        {
            lastWhy = why; Debug.Log("[Couro e Papel] layout: " + why);
            // where the slots are right now, in book units, to compare with what is on screen
            Transform ip = invParentT.Find("Inventory_Player");
            Transform sp = ip != null ? ip.Find("SlotParent") : null;
            if (sp != null)
            {
                string d = "SlotParent " + R(Bounds((RectTransform)sp, bookRoot));
                int n = 0;
                foreach (Transform k in sp)
                {
                    if (!k.gameObject.activeInHierarchy) continue;
                    if (k.name.StartsWith("Slot_Inventory", StringComparison.Ordinal) && (n++ % 5 != 0)) continue;
                    d += " | " + k.name + " " + R(Bounds((RectTransform)k, bookRoot));
                    if (k.name == "Equipslot parent")
                        foreach (Transform e in k) { d += " > " + e.name + " " + R(Bounds((RectTransform)e, bookRoot)) + " s" + e.localScale; break; }
                }
                Debug.Log("[Couro e Papel] slots: " + d + " | screen " + Screen.width + "x" + Screen.height + " book " + R(bookRoot.rect));
            }
        }
    }

    static string R(Rect r)
    {
        return "(" + Mathf.Round(r.xMin) + "," + Mathf.Round(r.yMin) + ")-(" + Mathf.Round(r.xMax) + "," + Mathf.Round(r.yMax) + ")";
    }

    static Rect Union(Rect a, Rect b)
    {
        return Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));
    }

    // `part` of a panel whose whole rect `full` (in its own space) lies at `home` (in the book's)
    static Rect Part(Rect home, Rect full, Rect part)
    {
        float kx = full.width > 0.01f ? home.width / full.width : 1f, ky = full.height > 0.01f ? home.height / full.height : 1f;
        return Rect.MinMaxRect(home.xMin + (part.xMin - full.xMin) * kx, home.yMin + (part.yMin - full.yMin) * ky,
            home.xMin + (part.xMax - full.xMin) * kx, home.yMin + (part.yMax - full.yMin) * ky);
    }

    // what of each inventory panel is drawn: its slots, items and text, not its (hidden) brown box nor
    // the icons that follow the cursor
    void MeasureInv()
    {
        // how far in from the category column's rect its first drawn button starts
        if (craftRef != null)
        {
            float x0 = float.MaxValue;
            foreach (Graphic g in craftRef.GetComponentsInChildren<Graphic>(false))
            {
                if (listRT != null && g.transform.IsChildOf(listRT)) continue;
                float a0 = float.MaxValue, b0 = float.MaxValue, a1 = float.MinValue, b1 = float.MinValue;
                if (Drawn(g, craftRef, ref a0, ref b0, ref a1, ref b1)) x0 = Mathf.Min(x0, a0);
            }
            colInset = x0 < float.MaxValue ? Mathf.Max(0f, x0 - craftRef.rect.xMin) : 0f;
        }
        cardVisHas = false;
        if (cardT != null && cardT.gameObject.activeInHierarchy)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            cardWhy = "";
            foreach (Graphic g in cardT.GetComponentsInChildren<Graphic>(false))
            {
                float py0 = y0;
                if (Drawn(g, cardT, ref x0, ref y0, ref x1, ref y1) && y0 < py0)
                {
                    Vector3 wp = cardT.InverseTransformPoint(g.rectTransform.position);
                    cardWhy = Path(g.transform, cardT) + " " + g.GetType().Name + " " + g.rectTransform.rect.size + " at " + wp
                        + " a" + g.canvasRenderer.GetInheritedAlpha() + " c" + g.color.a + " cull" + g.canvasRenderer.cull;
                }
            }
            if (y1 > y0) { cardVis = Rect.MinMaxRect(x0, y0, x1, y1); cardVisHas = true; }
        }
        invVis.Clear();
        foreach (Transform c in invParentT)
        {
            if (!Movable(c)) continue;
            RectTransform rt = (RectTransform)c;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            string lo = "", hi = "";
            foreach (Graphic g in rt.GetComponentsInChildren<Graphic>(false))
            {
                if (Floating(g.transform, rt)) continue;
                float py0 = y0, py1 = y1;
                if (!Drawn(g, rt, ref x0, ref y0, ref x1, ref y1)) continue;
                if (y0 < py0)
                {
                    Image li = g as Image;
                    lo = Path(g.transform, rt) + " " + g.rectTransform.rect.size
                        + (li != null ? " " + (li.sprite != null ? li.sprite.name : "-") + " " + li.type + (li.preserveAspect ? " aspect" : "") : "");
                }
                if (y1 > py1) hi = Path(g.transform, rt);
            }
            invWhy = rt.name + " top=" + hi + " bottom=" + lo;
            if (x1 - x0 > 20f && y1 - y0 > 20f) invVis[rt] = Rect.MinMaxRect(x0 - 4f, y0 - 4f, x1 + 4f, y1 + 4f);
        }
    }

    // grows (x0,y0)-(x1,y1), in `space`, by what `g` really draws: nothing when it is off, see-through
    // (also through a CanvasGroup) or one of our hidden boxes; for a text only its letters, not its rect
    // (the equipment slots' numbers have rects far larger than the slots)
    bool Drawn(Graphic g, Transform space, ref float x0, ref float y0, ref float x1, ref float y1)
    {
        if (!g.enabled || g.color.a < 0.05f) return false;
        if (g.canvasRenderer != null && g.canvasRenderer.GetInheritedAlpha() < 0.05f) return false;
        // cut away by a mask (the card's leftover ingredient rows)
        if (g.canvasRenderer != null && g.canvasRenderer.cull) return false;
        RectMask2D rm = g.GetComponentInParent<RectMask2D>();
        if (rm != null && rm.isActiveAndEnabled)
        {
            Vector3 c = rm.rectTransform.InverseTransformPoint(g.rectTransform.TransformPoint(g.rectTransform.rect.center));
            if (!rm.rectTransform.rect.Contains(new Vector2(c.x, c.y))) return false;
        }
        Image im = g as Image;
        if (im != null && hidden.ContainsKey(im)) return false;
        Text tx = g as Text;
        if (tx != null)
        {
            if (string.IsNullOrEmpty(tx.text)) return false;
            IList<UIVertex> vs = tx.cachedTextGenerator.verts;
            if (vs == null || vs.Count == 0) return false;
            float ppu = tx.pixelsPerUnit > 0.01f ? tx.pixelsPerUnit : 1f;
            // only the letters inside the parent's box count (a slot's number cannot reach outside the slot)
            RectTransform par = tx.rectTransform.parent as RectTransform;
            float a0 = float.MaxValue, b0 = float.MaxValue, a1 = float.MinValue, b1 = float.MinValue;
            for (int i = 0; i < vs.Count; i++)
            {
                Vector3 q = tx.rectTransform.TransformPoint(vs[i].position / ppu);
                if (par != null)
                {
                    Vector3 l = par.InverseTransformPoint(q);
                    Rect pr = par.rect;
                    l.x = Mathf.Clamp(l.x, pr.xMin, pr.xMax); l.y = Mathf.Clamp(l.y, pr.yMin, pr.yMax);
                    q = par.TransformPoint(l);
                }
                q = space.InverseTransformPoint(q);
                a0 = Mathf.Min(a0, q.x); a1 = Mathf.Max(a1, q.x);
                b0 = Mathf.Min(b0, q.y); b1 = Mathf.Max(b1, q.y);
            }
            if (a1 - a0 < 0.5f || b1 - b0 < 0.5f) return false;
            x0 = Mathf.Min(x0, a0); x1 = Mathf.Max(x1, a1);
            y0 = Mathf.Min(y0, b0); y1 = Mathf.Max(y1, b1);
            return true;
        }
        // a plain image draws only its sprite's opaque box, shrunk to the sprite's shape when it keeps its
        // aspect (the equipment slots: a tall rect with a square drawn in its middle)
        Rect dr = g.rectTransform.rect;
        if (im != null && im.sprite != null && im.type == Image.Type.Simple) dr = SpriteBox(im, dr);
        for (int i = 0; i < 4; i++)
        {
            Vector3 c = new Vector3(i < 2 ? dr.xMin : dr.xMax, (i == 1 || i == 2) ? dr.yMax : dr.yMin, 0f);
            Vector3 q = space.InverseTransformPoint(g.rectTransform.TransformPoint(c));
            x0 = Mathf.Min(x0, q.x); x1 = Mathf.Max(x1, q.x);
            y0 = Mathf.Min(y0, q.y); y1 = Mathf.Max(y1, q.y);
        }
        return true;
    }

    // the same box Image.GetDrawingDimensions fills: sprite padding cut away, aspect kept if asked
    static Rect SpriteBox(Image im, Rect r)
    {
        Vector4 pad = UnityEngine.Sprites.DataUtility.GetPadding(im.sprite);
        Vector2 size = im.sprite.rect.size;
        if (size.x < 0.5f || size.y < 0.5f) return r;
        if (im.preserveAspect)
        {
            float sr = size.x / size.y, rr = r.width / Mathf.Max(0.01f, r.height);
            Vector2 pv = im.rectTransform.pivot;
            if (sr > rr) { float oh = r.height; r.height = r.width / sr; r.y += (oh - r.height) * pv.y; }
            else { float ow = r.width; r.width = r.height * sr; r.x += (ow - r.width) * pv.x; }
        }
        return Rect.MinMaxRect(r.x + r.width * pad.x / size.x, r.y + r.height * pad.y / size.y,
            r.x + r.width * (size.x - pad.z) / size.x, r.y + r.height * (size.y - pad.w) / size.y);
    }

    static string Path(Transform t, Transform stop)
    {
        string p = t.name;
        for (t = t.parent; t != null && t != stop; t = t.parent) p = t.name + "/" + p;
        return p;
    }

    static bool Floating(Transform t, Transform stop)
    {
        for (; t != null && t != stop; t = t.parent)
        {
            string n = t.name;
            if (n.StartsWith("Dragging", StringComparison.Ordinal) || n.StartsWith("Hover", StringComparison.Ordinal)
                || n.StartsWith("Darkened", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // ---- the recipe list ----
    static bool Own(RectTransform rt, Held<Vector2> h, Vector2 want)
    {
        Vector2 cur = rt.sizeDelta;
        if (!h.has || cur != h.set) { h.orig = cur; h.has = true; }
        h.set = want;
        if (cur == want) return false;
        rt.sizeDelta = want;
        return true;
    }

    void FitList()
    {
        RectTransform rows = menu != null ? menu.menuItemSortParent : null;
        RectTransform view = menu != null ? menu.scrollViewRect : null;
        if (!listOn || rows == null || view == null) { UnwrapAll(); return; }
        RectTransform port = rows.parent as RectTransform;
        float inset = port != null ? Mathf.Max(0f, view.rect.width - port.rect.width) : 0f;
        // room for a row: the list's width less the viewport's inset and the scrollbar
        float avail = listW - inset - 14f;
        bool dirty = false;
        foreach (Transform t in rows)
        {
            RectTransform row = t as RectTransform;
            if (row != null && FitRow(row, avail)) dirty = true;
        }
        if (dirty) LayoutRebuilder.MarkLayoutForRebuild(rows);

        // as tall as its rows, down to the foot of the page at most; scrolling only when they do not fit
        float contentH = rows.rect.height;
        float viewH = Mathf.Max(0f, Mathf.Min(contentH, listRoom - ListHead));
        Own(listRT, listSize, new Vector2(listW, ListHead + viewH));
        Own(view, viewSize, new Vector2(listW, viewH));
        ScrollRect sr = menu.scrollRect;
        if (sr != null)
        {
            bool need = contentH > viewH + 1f;
            if (!listVert.has || sr.vertical != listVert.set) { listVert.orig = sr.vertical; listVert.has = true; }
            listVert.set = need;
            if (sr.vertical != need)
            {
                sr.vertical = need;
                if (!need) sr.verticalNormalizedPosition = 1f;
            }
        }
    }

    // a row is the recipe's variants (52 px squares) and its name; when they do not fit beside each other
    // the row's own layout is paused and they are laid out on as many lines as needed
    bool FitRow(RectTransform row, float avail)
    {
        Wrap wr;
        wraps.TryGetValue(row, out wr);
        RectTransform sub = row.Find("SubItems") as RectTransform;
        HorizontalLayoutGroup hlg = sub != null ? sub.GetComponent<HorizontalLayoutGroup>() : null;
        if (!row.gameObject.activeInHierarchy || hlg == null) return wr != null && Unwrap(row, wr);

        rowKids.Clear();
        float sp = hlg.spacing, natural = hlg.padding.left + hlg.padding.right;
        foreach (Transform t in sub)
        {
            RectTransform c = t as RectTransform;
            if (c == null || !c.gameObject.activeSelf) continue;
            LayoutElement le = c.GetComponent<LayoutElement>();
            if (le != null && le.enabled && le.ignoreLayout) continue;
            if (rowKids.Count > 0) natural += sp;
            natural += KidW(hlg, c);
            rowKids.Add(c);
        }
        if (natural <= avail || rowKids.Count < 2) return wr != null && Unwrap(row, wr);

        bool dirty = false;
        if (wr == null)
        {
            wr = new Wrap();
            wr.sub = sub; wr.hlg = hlg;
            wr.csf = sub.GetComponent<ContentSizeFitter>();
            wr.csfOn = wr.csf != null && wr.csf.enabled;
            wr.aMin = sub.anchorMin; wr.aMax = sub.anchorMax; wr.piv = sub.pivot; wr.pos = sub.anchoredPosition; wr.size = sub.sizeDelta;
            wr.rowH = row.sizeDelta.y; wr.subH = sub.rect.height;
            wr.left = sub.localPosition.x + sub.rect.xMin - row.rect.xMin;
            wr.top = row.rect.yMax - (sub.localPosition.y + sub.rect.yMax);
            wr.le = row.GetComponent<LayoutElement>();
            if (wr.le == null) { wr.le = row.gameObject.AddComponent<LayoutElement>(); wr.leAdded = true; }
            wr.leMin = wr.le.minHeight; wr.lePref = wr.le.preferredHeight;
            wraps[row] = wr;
            dirty = true;
        }
        if (hlg.enabled) { hlg.enabled = false; dirty = true; }
        if (wr.csf != null && wr.csf.enabled) { wr.csf.enabled = false; dirty = true; }

        // lines of squares, the name after the last one (or under it when it does not fit)
        float lineH = 0f;
        foreach (RectTransform c in rowKids) lineH = Mathf.Max(lineH, KidH(hlg, c));
        float gapY = Mathf.Max(sp, 4f), x = hlg.padding.left, y = hlg.padding.top, wMax = 0f;
        foreach (RectTransform c in rowKids)
        {
            float cw = KidW(hlg, c), chh = KidH(hlg, c);
            if (x > hlg.padding.left && x + cw > avail - hlg.padding.right) { x = hlg.padding.left; y += lineH + gapY; }
            if (Put(c, hlg, x, y + (lineH - chh) * 0.5f)) dirty = true;
            x += cw;
            wMax = Mathf.Max(wMax, x);
            x += sp;
        }
        float tall = y + lineH + hlg.padding.bottom;
        if (SetRect(sub, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(wr.left, -wr.top), new Vector2(wMax + hlg.padding.right, tall))) dirty = true;
        float rh = Mathf.Max(wr.rowH, wr.rowH - wr.subH + tall);
        if (row.sizeDelta.y != rh) { row.sizeDelta = new Vector2(row.sizeDelta.x, rh); dirty = true; }
        if (wr.le.minHeight != rh || wr.le.preferredHeight != rh) { wr.le.minHeight = rh; wr.le.preferredHeight = rh; dirty = true; }
        return dirty;
    }

    static float KidW(HorizontalLayoutGroup g, RectTransform c)
    {
        float w = g.childControlWidth ? LayoutUtility.GetPreferredWidth(c) : c.sizeDelta.x;
        return g.childScaleWidth ? w * c.localScale.x : w;
    }

    static float KidH(HorizontalLayoutGroup g, RectTransform c)
    {
        float h = g.childControlHeight ? LayoutUtility.GetPreferredHeight(c) : c.sizeDelta.y;
        return g.childScaleHeight ? h * c.localScale.y : h;
    }

    // what the row's own layout would do to a child, at (x, y) from the row's top left
    static bool Put(RectTransform c, HorizontalLayoutGroup g, float x, float y)
    {
        float w = g.childControlWidth ? LayoutUtility.GetPreferredWidth(c) : c.sizeDelta.x;
        float h = g.childControlHeight ? LayoutUtility.GetPreferredHeight(c) : c.sizeDelta.y;
        float kx = g.childScaleWidth ? c.localScale.x : 1f, ky = g.childScaleHeight ? c.localScale.y : 1f;
        Vector2 corner = new Vector2(0f, 1f);
        return SetRect(c, corner, corner, c.pivot, new Vector2(x + w * c.pivot.x * kx, -y - h * (1f - c.pivot.y) * ky), new Vector2(w, h));
    }

    static bool SetRect(RectTransform rt, Vector2 aMin, Vector2 aMax, Vector2 piv, Vector2 pos, Vector2 size)
    {
        bool ch = false;
        if (rt.anchorMin != aMin) { rt.anchorMin = aMin; ch = true; }
        if (rt.anchorMax != aMax) { rt.anchorMax = aMax; ch = true; }
        if (rt.pivot != piv) { rt.pivot = piv; ch = true; }
        if (rt.sizeDelta != size) { rt.sizeDelta = size; ch = true; }
        if (rt.anchoredPosition != pos) { rt.anchoredPosition = pos; ch = true; }
        return ch;
    }

    bool Unwrap(RectTransform row, Wrap wr)
    {
        wraps.Remove(row);
        if (row == null) return false;
        if (wr.sub != null)
        {
            SetRect(wr.sub, wr.aMin, wr.aMax, wr.piv, wr.pos, wr.size);
            if (wr.csf != null) wr.csf.enabled = wr.csfOn;
        }
        if (wr.hlg != null) wr.hlg.enabled = true;
        row.sizeDelta = new Vector2(row.sizeDelta.x, wr.rowH);
        if (wr.le != null)
        {
            if (wr.leAdded) UnityEngine.Object.Destroy(wr.le);
            else { wr.le.minHeight = wr.leMin; wr.le.preferredHeight = wr.lePref; }
        }
        return true;
    }

    void UnwrapAll()
    {
        if (wraps.Count == 0) return;
        RectTransform rows = menu != null ? menu.menuItemSortParent : null;
        foreach (KeyValuePair<RectTransform, Wrap> kv in new List<KeyValuePair<RectTransform, Wrap>>(wraps)) Unwrap(kv.Key, kv.Value);
        wraps.Clear();
        if (rows != null) LayoutRebuilder.MarkLayoutForRebuild(rows);
    }

    // the selected recipe card sits under the list in the game's hierarchy; it is lifted out to the
    // right page by moving it after the panel itself has moved, and drawn larger
    void PlaceCard()
    {
        if (!cardOn || cardT == null) return;
        if (!cardHas) { cardPosOrig = cardT.anchoredPosition; cardScaleOrig = cardT.localScale; cardPosSet = cardPosOrig; cardHas = true; }
        else if (cardT.anchoredPosition != cardPosSet) cardPosOrig = cardT.anchoredPosition;
        Vector3 ws = new Vector3(cardScaleOrig.x * cardScale, cardScaleOrig.y * cardScale, cardScaleOrig.z);
        if (cardT.localScale != ws) cardT.localScale = ws;
        Rect b = Bounds(cardT, bookRoot);
        if (cardVisHas) b = Part(b, cardT.rect, cardVis);
        Vector3 d = cardT.parent.InverseTransformVector(bookRoot.TransformVector(new Vector3(cardTarget.x - b.center.x, cardTarget.y - b.yMax, 0f)));
        Vector2 pos = cardT.anchoredPosition + new Vector2(Mathf.Round(d.x), Mathf.Round(d.y));
        cardPosSet = pos;
        if (cardT.anchoredPosition != pos) cardT.anchoredPosition = pos;
    }

    bool UnderHotbar(Transform t)
    {
        if (hotbarT == null) return false;
        for (Transform p = t; p != null; p = p.parent) if (p == hotbarT) return true;
        return false;
    }

    // text drawn on a button (craft, delete) keeps its own light colour
    static bool OnButton(Transform t)
    {
        Transform p = t;
        for (int i = 0; i < 3 && p != null; i++, p = p.parent)
        {
            Image im = p.GetComponent<Image>();
            if (im == null || im.sprite == null) continue;
            string n = im.sprite.name;
            if (n.IndexOf("Button", StringComparison.Ordinal) >= 0 || n.IndexOf("Box3", StringComparison.Ordinal) >= 0) return true;
        }
        return false;
    }

    static Color Inkify(Color c)
    {
        float hi = Mathf.Max(c.r, Mathf.Max(c.g, c.b)), lo = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
        float lum = 0.3f * c.r + 0.59f * c.g + 0.11f * c.b;
        if (lum < 0.55f) return c;
        // coloured text (green, gold...) keeps its hue, only darker; white and cream become ink
        if (hi - lo > 0.3f) return new Color(c.r * 0.5f, c.g * 0.5f, c.b * 0.5f, c.a);
        return new Color(InkCol.r, InkCol.g, InkCol.b, c.a);
    }

    void Scan()
    {
        Track(craftingT as RectTransform);
        foreach (Transform c in invParentT) if (c != hotbarT && c is RectTransform) Track((RectTransform)c);
        Transform[] roots = { craftingT, consoleT, invParentT };
        foreach (Transform r in roots)
        {
            if (r == null) continue;
            foreach (Canvas cv in r.GetComponentsInChildren<Canvas>(true))
                if (cv.overrideSorting && cv.transform != r && !UnderHotbar(cv.transform)) { cv.overrideSorting = false; flattened.Add(cv); }
            foreach (Image img in r.GetComponentsInChildren<Image>(true))
                if (!hidden.ContainsKey(img) && !UnderHotbar(img.transform)
                    && ((img.sprite != null && PanelSprites.Contains(img.sprite.name)) || ChestBox(img)))
                    hidden[img] = new Held<Color>();
            foreach (Text t in r.GetComponentsInChildren<Text>(true))
            {
                if (inked.ContainsKey(t)) continue;
                Inked k = new Inked();
                k.skip = UnderHotbar(t.transform) || OnButton(t.transform);
                k.shadows = t.GetComponents<Shadow>();
                inked[t] = k;
            }
        }
    }

    // the brown box behind a chest's slots: an image on the storage panel itself, or one of its direct
    // children that covers most of it (the slots, icon and buttons are much smaller)
    bool InStorage(Transform t)
    {
        for (Transform u = t; u != null; u = u.parent)
            if (u.parent == invParentT) return u.name.Contains("Storage");
        return false;
    }

    bool ChestBox(Image img)
    {
        Transform t = img.transform;
        // the chest's brown box and its drop shadow, by name
        if (t.name == "BrownBackground" || t.name == "DarkShadow")
            for (Transform u = t.parent; u != null; u = u.parent)
                if (u.parent == invParentT) return u.name.Contains("Storage");
        RectTransform panel = null;
        if (t.parent == invParentT) panel = t as RectTransform;
        else if (t.parent != null && t.parent.parent == invParentT) panel = t.parent as RectTransform;
        if (panel == null || !panel.name.Contains("Storage")) return false;
        if (t == panel) return true;
        Rect a = ((RectTransform)t).rect, b = panel.rect;
        return a.width >= b.width * 0.8f && a.height >= b.height * 0.6f && t.GetComponentInChildren<Text>(true) == null
            && img.GetComponentsInChildren<Image>(true).Length <= 2;
    }

    void Track(RectTransform rt)
    {
        if (rt != null && !moved.ContainsKey(rt)) moved[rt] = new Held<Vector2>();
    }

    void Keep()
    {
        int ci = craftingT.GetSiblingIndex(), bi = book.transform.GetSiblingIndex();
        if (bi != ci - 1) book.transform.SetSiblingIndex(bi < ci ? ci - 1 : ci);
        float now = Time.unscaledTime;
        if (now >= nextScan) { nextScan = now + 0.5f; Scan(); MeasureInv(); }
        Layout();
        HideCrafting(storeOpen);
        HideItemInfo(true);

        // the game may move, recolour or retint any of these itself: whatever it sets becomes the new original
        foreach (KeyValuePair<RectTransform, Held<Vector2>> kv in moved)
        {
            RectTransform rt = kv.Key; Held<Vector2> h = kv.Value;
            if (rt == null || rt.parent == null) continue;
            Vector2 cur = rt.anchoredPosition;
            if (!h.has || cur != h.set) { h.orig = cur; h.has = true; }
            Vector3 d = rt.parent.InverseTransformVector(bookRoot.TransformVector(rt == craftingT ? craftShift : (storeOpen && rt.name.Contains("Storage") ? storeShift : invShift)));
            Vector2 want = h.orig + new Vector2(Mathf.Round(d.x), Mathf.Round(d.y));
            h.set = want;
            if (cur != want) rt.anchoredPosition = want;
        }
        FitList();
        PlaceCard();
        foreach (KeyValuePair<Image, Held<Color>> kv in hidden)
        {
            Image img = kv.Key; Held<Color> h = kv.Value;
            if (img == null) continue;
            Color cur = img.color;
            if (h.has && cur == h.set) continue;
            h.orig = cur; h.has = true;
            h.set = new Color(cur.r, cur.g, cur.b, 0f);
            img.color = h.set;
        }
        foreach (KeyValuePair<Text, Inked> kv in inked)
        {
            Text t = kv.Key; Inked k = kv.Value;
            if (t == null || k.skip) continue;
            Color cur = t.color;
            if (k.has && cur == k.set) continue;
            k.orig = cur; k.has = true;
            k.set = Inkify(cur);
            // the chest's name is drawn faint by the game (it is an editable field): full ink on the paper
            if (InStorage(t.transform)) k.set = new Color(InkCol.r, InkCol.g, InkCol.b, 1f);
            t.color = k.set;
            if (k.set != k.orig && k.shadows != null)
                foreach (Shadow s in k.shadows)
                    if (s != null && s.enabled) { s.enabled = false; k.off.Add(s); }
        }
    }

    // while a chest is open the crafting column, recipe list and card are put away: every item the
    // player owns already counts for crafting, so there is nothing to craft from the chest
    bool storeOpen;
    Vector2 storeShift;
    CanvasGroup craftCG;
    bool craftHid;
    void HideCrafting(bool hide)
    {
        if (craftingT == null) { craftHid = false; return; }
        if (hide == craftHid && (!hide || (craftCG != null && craftCG.alpha == 0f))) return;
        if (craftCG == null) { craftCG = craftingT.GetComponent<CanvasGroup>(); if (craftCG == null) craftCG = craftingT.gameObject.AddComponent<CanvasGroup>(); }
        craftCG.alpha = hide ? 0f : 1f;
        craftCG.interactable = !hide;
        craftCG.blocksRaycasts = !hide;
        craftHid = hide;
        if (leftTitle != null) leftTitle.text = hide ? "BAÚ" : "FABRICO";
        if (leftSub != null) leftSub.text = hide ? "Arrumação e mantimentos" : "Receitas e materiais";
    }
    Text leftTitle, leftSub;

    // the selected item's picture, name and description above the inventory are not shown in the book
    readonly List<CanvasGroup> infoCg = new List<CanvasGroup>();
    void HideItemInfo(bool hide)
    {
        if (!hide)
        {
            foreach (CanvasGroup cg in infoCg) if (cg != null) { cg.alpha = 1f; cg.blocksRaycasts = true; }
            infoCg.Clear();
            return;
        }
        if (infoCg.Count == 0)
        {
            PlayerInventory pi = null;
            try { Network_Player me = RAPI.GetLocalPlayer(); if (me != null) pi = me.Inventory; } catch (Exception) { }
            if (pi == null) return;
            List<Transform> ts = new List<Transform>();
            if (pi.itemNameText != null) ts.Add(pi.itemNameText.transform);
            if (pi.itemDescriptionText != null) ts.Add(pi.itemDescriptionText.transform);
            if (pi.itemImage != null)
            {
                Transform t = pi.itemImage.transform;
                // the picture's frame, unless that frame also holds the name
                Transform f = t.parent;
                bool frame = f != null && f.name != "Inventory_Player" && (pi.itemNameText == null || !pi.itemNameText.transform.IsChildOf(f));
                ts.Add(frame ? f : t);
            }
            foreach (Transform t in ts)
            {
                CanvasGroup cg = t.GetComponent<CanvasGroup>();
                if (cg == null) cg = t.gameObject.AddComponent<CanvasGroup>();
                infoCg.Add(cg);
            }
        }
        foreach (CanvasGroup cg in infoCg) if (cg != null && cg.alpha != 0f) { cg.alpha = 0f; cg.blocksRaycasts = false; }
    }

    void Restore()
    {
        HideCrafting(false);
        HideItemInfo(false);
        if (cardHas && cardT != null)
        {
            if (cardT.anchoredPosition == cardPosSet) cardT.anchoredPosition = cardPosOrig;
            cardT.localScale = cardScaleOrig;
        }
        cardHas = false;
        foreach (Canvas cv in flattened) if (cv != null) cv.overrideSorting = true;
        flattened.Clear();
        UnwrapAll();
        if (listSize.has && listRT != null && listRT.sizeDelta == listSize.set) listRT.sizeDelta = listSize.orig;
        if (menu != null && viewSize.has && menu.scrollViewRect != null && menu.scrollViewRect.sizeDelta == viewSize.set) menu.scrollViewRect.sizeDelta = viewSize.orig;
        if (menu != null && listVert.has && menu.scrollRect != null && menu.scrollRect.vertical == listVert.set) menu.scrollRect.vertical = listVert.orig;
        listSize.has = viewSize.has = listVert.has = false;
        invVis.Clear();
        foreach (KeyValuePair<RectTransform, Held<Vector2>> kv in moved)
            if (kv.Key != null && kv.Value.has && kv.Key.anchoredPosition == kv.Value.set) kv.Key.anchoredPosition = kv.Value.orig;
        foreach (KeyValuePair<Image, Held<Color>> kv in hidden)
            if (kv.Key != null && kv.Value.has && kv.Key.color == kv.Value.set) kv.Key.color = kv.Value.orig;
        foreach (KeyValuePair<Text, Inked> kv in inked)
        {
            Text t = kv.Key; Inked k = kv.Value;
            if (t != null && k.has && t.color == k.set) t.color = k.orig;
            foreach (Shadow s in k.off) if (s != null) s.enabled = true;
        }
        moved.Clear(); hidden.Clear(); inked.Clear();
    }
}

namespace BCUI_LU
{
    // How one replacement sprite is drawn. Sizes are in pixels; sprites are made at 100 pixels per unit.
    public class SkinSpec
    {
        public string name;
        public int w, h;
        public float bl, bb, br, bt;      // 9-slice border: left, bottom, right, top
        public int shape;                 // 0 rounded box, 1 octagon
        public float radius = 10f;        // corner radius, or the octagon's cut
        public float lip;                 // darker drop under the body, like the game's buttons and slots
        public int fill;                  // 0 leather, 1 paper, 2 frame only, 3 leather strip fading right, 4 ink rule
        public Color tone;
        public float rim; public Color rimCol;
        public float stitch; public Color stitchCol; public float dash = 5f, gap = 3f;
        public float glow; public Color glowCol;   // glowCol.a is the strength
        public float inset;               // recessed look for slots, 0..1
        public float seed;
        public float ppu = 100f;          // pixels per unit, higher draws the borders thinner
        public bool repeat;               // texture wraps (for tiled grain)
        public float mottle = 1f;         // how blotchy the paper is (big pages are stretched, keep them calm)
    }

    public static class SkinPaint
    {
        static Color C(float r, float g, float b) { return new Color(r, g, b, 1f); }
        static Color C(float r, float g, float b, float a) { return new Color(r, g, b, a); }

        // leathers
        public static readonly Color DarkLeather = C(0.28f, 0.165f, 0.085f);
        public static readonly Color DeepLeather = C(0.20f, 0.115f, 0.06f);
        public static readonly Color Caramel = C(0.55f, 0.355f, 0.185f);
        public static readonly Color CaramelHi = C(0.66f, 0.45f, 0.24f);
        public static readonly Color Olive = C(0.37f, 0.46f, 0.15f);
        public static readonly Color OliveHi = C(0.47f, 0.58f, 0.19f);
        public static readonly Color OliveDeep = C(0.28f, 0.36f, 0.11f);
        public static readonly Color Oxblood = C(0.53f, 0.18f, 0.11f);
        public static readonly Color OxbloodHi = C(0.66f, 0.25f, 0.15f);
        public static readonly Color Ash = C(0.37f, 0.32f, 0.27f);
        // paper and ink
        public static readonly Color Slot = C(0.82f, 0.72f, 0.54f);
        public static readonly Color SlotHi = C(0.91f, 0.82f, 0.63f);
        public static readonly Color SlotPack = C(0.75f, 0.64f, 0.46f);
        public static readonly Color Ink = C(0.27f, 0.16f, 0.08f);
        public static readonly Color Rim = C(0.11f, 0.06f, 0.03f);
        public static readonly Color Thread = C(0.93f, 0.84f, 0.64f, 0.55f);
        public static readonly Color ThreadGold = C(1f, 0.84f, 0.48f, 0.85f);
        public static readonly Color Gold = C(1f, 0.80f, 0.40f);
        public static readonly Color Amber = C(1f, 0.76f, 0.32f, 0.55f);
        public static readonly Color Warm = C(1f, 0.90f, 0.62f, 0.30f);

        static SkinSpec Leather(string name, int w, int h, float b, float lip, Color tone, float stitch, Color thread)
        {
            SkinSpec s = new SkinSpec();
            s.name = name; s.w = w; s.h = h; s.bl = s.br = b; s.bt = b - 4f; s.bb = b + lip - 2f;
            s.fill = 0; s.tone = tone; s.lip = lip; s.radius = 11f;
            s.rim = 2f; s.rimCol = Rim; s.stitch = stitch; s.stitchCol = thread;
            return s;
        }
        static SkinSpec Paper(string name, int w, int h, float lip, Color tone, int shape, float radius)
        {
            SkinSpec s = new SkinSpec();
            s.name = name; s.w = w; s.h = h; s.fill = 1; s.tone = tone; s.lip = lip; s.shape = shape; s.radius = radius;
            s.rim = 3.5f; s.rimCol = Ink; s.inset = 0.45f;
            return s;
        }

        // every game sprite this skin replaces, by its name in the game
        public static SkinSpec[] All()
        {
            List<SkinSpec> l = new List<SkinSpec>();
            SkinSpec s;
            // big brown panels behind the inventory, crafting menu, hotbar and research table
            s = Leather("MenuBackground_9Slice", 320, 320, 26f, 0f, DarkLeather, 9f, Thread);
            s.bl = s.br = s.bb = s.bt = 26f; s.radius = 12f; s.ppu = 140f; l.Add(s);
            // darker frame boxes around the crafting columns
            s = Leather("Box1", 96, 96, 16f, 0f, DeepLeather, 0f, Thread);
            s.bl = s.br = s.bb = s.bt = 16f; s.radius = 9f; l.Add(s);
            // hotkey badges next to the hotbar
            s = Leather("NotebookCrafting_Background", 56, 56, 0f, 0f, DarkLeather, 4.5f, Thread);
            s.radius = 9f; s.dash = 3f; s.gap = 2f; l.Add(s);

            // buttons
            l.Add(Leather("BigButtonNormal", 192, 80, 24f, 7f, Caramel, 7f, Thread));
            s = Leather("BigButtonHighlighted", 192, 80, 24f, 7f, CaramelHi, 7f, ThreadGold); s.glow = 10f; s.glowCol = Warm; l.Add(s);
            l.Add(Leather("CraftButtonNormal", 192, 80, 24f, 7f, Olive, 7f, Thread));
            s = Leather("CraftButtonHighLighted", 192, 80, 24f, 7f, OliveHi, 7f, ThreadGold); s.glow = 10f; s.glowCol = Warm; l.Add(s);
            s = Leather("CraftButtonDisabled", 120, 56, 16f, 5f, Ash, 5.5f, C(0.85f, 0.80f, 0.72f, 0.30f)); s.radius = 9f; l.Add(s);
            l.Add(Leather("DeleteButtonNorma", 192, 80, 24f, 7f, Oxblood, 7f, Thread));
            s = Leather("DeleteButtonHighlighted", 192, 80, 24f, 7f, OxbloodHi, 7f, ThreadGold); s.glow = 10f; s.glowCol = Warm; l.Add(s);
            // pressed craft button and disabled menu buttons (small 9-sliced boxes)
            s = Leather("Box3Green", 72, 72, 16f, 0f, OliveDeep, 6f, Thread); s.bl = s.br = s.bb = s.bt = 16f; s.radius = 9f; l.Add(s);
            s = Leather("Box3Disabled", 72, 72, 16f, 5f, Ash, 6f, C(0.85f, 0.80f, 0.72f, 0.30f)); s.radius = 9f; l.Add(s);

            // item slots: aged paper set into the leather
            l.Add(Paper("ItemBG", 156, 160, 8f, Slot, 0, 16f));
            s = Paper("ItemBGHover", 156, 160, 8f, SlotHi, 0, 16f); s.glow = 14f; s.glowCol = Warm; l.Add(s);
            s = Paper("ItemBG_Highlighted", 156, 160, 8f, SlotHi, 0, 16f); s.rim = 5f; s.rimCol = Gold; s.glow = 22f; s.glowCol = Amber; s.inset = 0.2f; l.Add(s);
            l.Add(Paper("BP_ItemBG", 156, 160, 8f, SlotPack, 1, 40f));
            s = Paper("ItemBG_wo_Frame", 64, 64, 0f, Slot, 0, 8f); s.rim = 1.5f; s.inset = 0.35f; l.Add(s);
            // hover and selection frames drawn over a slot
            s = new SkinSpec(); s.name = "ItemBG_Frame"; s.w = 156; s.h = 160; s.lip = 8f; s.fill = 2;
            s.radius = 16f; s.rim = 5f; s.rimCol = Gold; s.glow = 7f; s.glowCol = C(1f, 0.80f, 0.40f, 0.35f); l.Add(s);
            s = new SkinSpec(); s.name = "BP_ItemBG_Frame"; s.w = 156; s.h = 160; s.lip = 8f; s.fill = 2; s.shape = 1;
            s.radius = 40f; s.rim = 6f; s.rimCol = Gold; s.glow = 14f; s.glowCol = C(1f, 0.80f, 0.40f, 0.35f); l.Add(s);

            // recipe rows: a leather strip that fades out to the right
            s = new SkinSpec(); s.name = "BGGradient"; s.w = 168; s.h = 34; s.fill = 3; s.tone = Ink; s.radius = 4f; l.Add(s);
            // dividers: an ink rule with a diamond in the middle
            s = new SkinSpec(); s.name = "Divider"; s.w = 448; s.h = 14; s.fill = 4; s.tone = C(0.93f, 0.82f, 0.58f); l.Add(s);

            for (int i = 0; i < l.Count; i++) l[i].seed = i * 13.7f;
            return l.ToArray();
        }

        static float NoiseP(float x, float y, int p)
        {
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float fx = x - ix, fy = y - iy;
            fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
            int x0 = ((ix % p) + p) % p, y0 = ((iy % p) + p) % p, x1 = (x0 + 1) % p, y1 = (y0 + 1) % p;
            float a = Hash(x0, y0), b = Hash(x1, y0), c = Hash(x0, y1), d = Hash(x1, y1);
            return (a + (b - a) * fx) + ((c + (d - c) * fx) - (a + (b - a) * fx)) * fy;
        }

        static Color Special(SkinSpec s, float fx, float fy)
        {
            float w = s.w, h = s.h;
            if (s.fill == 7)
            {
                // shadow where the page bends into the spine: darkest at x = 0
                float u = 1f - Mathf.Clamp01(fx / w);
                return new Color(0f, 0f, 0f, u * u * u * 0.6f);
            }
            if (s.fill == 8)
            {
                // tileable grain, black, alpha carries the pattern
                float n = 0.6f * NoiseP(fx / 16f, fy / 16f, 16) + 0.4f * NoiseP(fx / 4f, fy / 4f, 64);
                float a = Mathf.Clamp01((0.62f - n) * 1.6f);
                if (Hash((int)fx + 5000, (int)fy) > 0.988f) a = Mathf.Max(a, 0.55f);
                return new Color(0f, 0f, 0f, a);
            }
            if (s.fill == 5)
            {
                // ribbon bookmark with a V cut at the bottom
                float half = w * 0.5f;
                float cut = 26f * (1f - Mathf.Abs(fx - half) / half);
                float edge = Mathf.Min(fx, w - fx);
                float a = Mathf.Clamp01(fy - cut + 0.5f) * Mathf.Clamp01(edge + 0.5f);
                float k = 0.78f + 0.3f * Mathf.Sin(Mathf.PI * fx / w);
                k *= 0.72f + 0.28f * Smooth(0f, 3f, edge);
                if (Mathf.Abs(edge - 5.5f) < 0.8f) k *= 1.18f;
                k *= 0.96f + 0.08f * Noise(fx * 0.5f, fy * 0.08f);
                Color c = K(s.tone, k); c.a = a;
                return c;
            }
            // corner cap for the bottom-left corner of the cover (rotated for the others)
            float qx = Mathf.Abs(fx - w), qy = Mathf.Abs(fy - h);
            float r = s.radius;
            float bx = qx - (w - r), by = qy - (h - r);
            float ox = Mathf.Max(bx, 0f), oy = Mathf.Max(by, 0f);
            float dRound = Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(bx, by), 0f) - r;
            float dHyp = (fx + fy - w * 0.92f) * 0.70710678f;
            float dd = Mathf.Max(dRound, dHyp);
            float al = Mathf.Clamp01(0.5f - dd);
            if (al <= 0f) return new Color(0f, 0f, 0f, 0f);
            float e = -dd;
            SkinSpec b = new SkinSpec(); b.tone = s.tone; b.rim = s.rim; b.rimCol = s.rimCol; b.seed = s.seed; b.radius = 4f;
            Color c2 = Body(b, fx, fy, w, h, e);
            float t = (fx - fy) * 0.70710678f;
            float p = s.dash + s.gap;
            float m = t - Mathf.Floor(t / p) * p;
            float dash = Smooth(-0.6f, 0.6f, m) * (1f - Smooth(s.dash - 0.6f, s.dash + 0.6f, m));
            float th = Mathf.Clamp01(1.1f - Mathf.Abs(-dHyp - 9f) / 0.85f) * dash * Smooth(10f, 16f, -dRound);
            c2 = Mix(c2, new Color(s.stitchCol.r, s.stitchCol.g, s.stitchCol.b, 1f), s.stitchCol.a * th);
            c2.a = al;
            return c2;
        }

        // ---------- painting ----------
        static float Hash(int x, int y)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }
        static float Noise(float x, float y)
        {
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float fx = x - ix, fy = y - iy;
            fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
            float a = Hash(ix, iy), b = Hash(ix + 1, iy), c = Hash(ix, iy + 1), d = Hash(ix + 1, iy + 1);
            return (a + (b - a) * fx) + ((c + (d - c) * fx) - (a + (b - a) * fx)) * fy;
        }
        static float Smooth(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3f - 2f * t);
        }
        static Color Mix(Color a, Color b, float t)
        {
            t = Mathf.Clamp01(t);
            return new Color(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, a.a + (b.a - a.a) * t);
        }
        static Color K(Color c, float k) { return new Color(c.r * k, c.g * k, c.b * k, c.a); }

        // signed distance (negative inside) to the body spanning 0..w x 0..h
        static float Shape(SkinSpec s, float x, float y, float w, float h)
        {
            float hw = w * 0.5f, hh = h * 0.5f;
            float px = Mathf.Abs(x - hw), py = Mathf.Abs(y - hh);
            if (s.shape == 1)
            {
                float box = Mathf.Max(px - hw, py - hh);
                float diag = (px + py - (hw + hh - s.radius)) * 0.70710678f;
                return Mathf.Max(box, diag);
            }
            float r = Mathf.Min(s.radius, Mathf.Min(hw, hh));
            float qx = px - (hw - r), qy = py - (hh - r);
            float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
            return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
        }
        // is this point of a line running at depth `inset` inside the outline on a dash (1) or a gap (0)
        static float Dash(SkinSpec s, float x, float y, float w, float h, float inset)
        {
            float qx = Mathf.Abs(x - w * 0.5f), qy = Mathf.Abs(y - h * 0.5f);
            float r = Mathf.Max(s.radius - inset, 1f);
            float hx = w * 0.5f - inset - r, hy = h * 0.5f - inset - r;
            float t;
            if (qx > hx && qy > hy) t = 1000f + Mathf.Atan2(qy - hy, qx - hx) * r;
            else if (qx <= hx) t = x - w * 0.5f + 0.5f * s.dash;
            else t = y - h * 0.5f + 0.5f * s.dash;
            float p = s.dash + s.gap;
            float m = t - Mathf.Floor(t / p) * p;
            return Smooth(-0.6f, 0.6f, m) * (1f - Smooth(s.dash - 0.6f, s.dash + 0.6f, m));
        }

        // the colour of the body at (x, y), e = distance inside its outline
        static Color Body(SkinSpec s, float x, float y, float w, float h, float e)
        {
            Color c;
            if (s.fill == 1)
            {
                float n1 = Noise(x * 0.035f + s.seed, y * 0.035f + 11f), n2 = Noise(x * 0.13f + 70f, y * 0.13f + s.seed);
                float k = 0.93f + (0.07f * n1 + 0.035f * n2) * s.mottle - (Hash(x > 0f ? (int)x + 977 : 0, (int)y) > 0.994f ? 0.06f : 0f);
                c = K(s.tone, k);
                float burnW = Mathf.Min(w, h) * 0.13f;
                float b = (1f - Smooth(0f, burnW, e)) * (0.45f + (0.35f * n1 + 0.2f * n2) * s.mottle);
                c = Mix(c, C(0.60f, 0.43f, 0.24f), b * 0.75f);
                // a slot is set into the panel: shadow along the top inner edge, a little light along the bottom one
                float iw = Mathf.Min(w, h) * 0.11f;
                float near = 1f - Smooth(0f, iw, e - s.rim);
                float up = Mathf.Clamp01((y / h - 0.35f) * 1.6f);
                c = K(c, 1f - s.inset * near * (0.25f + 0.6f * up));
                float cx = (x / w - 0.5f) * 2f, cy = (y / h - 0.5f) * 2f;
                c = K(c, 1.0f + 0.06f * Mathf.Clamp01(1f - (cx * cx + cy * cy) * 0.7f));
            }
            else
            {
                float n = 0.62f * Noise(x * 0.045f + s.seed, y * 0.045f) + 0.38f * Noise(x * 0.17f + 40f, y * 0.17f + 9f + s.seed);
                float k = 0.80f + 0.34f * n + 0.06f * (Hash((int)x, (int)y + 31) - 0.5f);
                k *= 0.90f + 0.16f * (y / h);
                k *= 0.72f + 0.28f * Smooth(0f, 9f, e);
                c = K(s.tone, k);
                float top = Mathf.Clamp01(1f - Mathf.Abs(e - s.rim - 1.4f) / 1.3f) * Mathf.Clamp01((y / h - 0.45f) * 2.5f);
                c = Mix(c, K(s.tone, 1.6f), 0.35f * top);
            }
            c.a = 1f;
            if (s.glow > 0f)
            {
                float g = 1f - Smooth(0f, s.glow, e - s.rim);
                c = Mix(c, new Color(s.glowCol.r, s.glowCol.g, s.glowCol.b, 1f), s.glowCol.a * g);
            }
            if (s.stitch > 0f)
            {
                // the stitch's hole shadow sits a pixel below each dash
                float e2 = -Shape(s, x, y + 1.2f, w, h);
                float sh = Mathf.Clamp01(1.1f - Mathf.Abs(e2 - s.stitch) / 0.9f) * Dash(s, x, y + 1.2f, w, h, s.stitch);
                c = K(c, 1f - 0.45f * sh); c.a = 1f;
                float th = Mathf.Clamp01(1.1f - Mathf.Abs(e - s.stitch) / 0.85f) * Dash(s, x, y, w, h, s.stitch);
                c = Mix(c, new Color(s.stitchCol.r, s.stitchCol.g, s.stitchCol.b, 1f), s.stitchCol.a * th);
            }
            if (s.rim > 0f) c = Mix(new Color(s.rimCol.r, s.rimCol.g, s.rimCol.b, 1f), c, Smooth(s.rim - 0.8f, s.rim + 0.6f, e));
            return c;
        }

        // RGBA pixels, bottom row first (Texture2D.SetPixels order)
        public static Color[] Render(SkinSpec s)
        {
            int w = s.w, h = s.h;
            Color[] px = new Color[w * h];
            float bodyH = h - s.lip;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    Color o;
                    if (s.fill >= 5) { px[y * w + x] = Special(s, fx, fy); continue; }
                    if (s.fill == 4)
                    {
                        float u = fx / w, cxp = Mathf.Abs(fx - w * 0.5f), cyp = Mathf.Abs(fy - h * 0.5f);
                        float line = Mathf.Clamp01(1.25f - cyp) * Smooth(0f, 0.2f, u) * Smooth(0f, 0.2f, 1f - u);
                        if (cxp < 12f) line = 0f;
                        float dia = Mathf.Clamp01(6.2f - (cxp + cyp));
                        float dot = Mathf.Clamp01(2.2f - Mathf.Sqrt((cxp - 17f) * (cxp - 17f) + cyp * cyp));
                        o = s.tone; o.a = Mathf.Max(line * 0.85f, Mathf.Max(dia, dot * 0.85f));
                        px[y * w + x] = o; continue;
                    }
                    float dB = Shape(s, fx, fy - s.lip, w, bodyH);
                    float aB = Mathf.Clamp01(0.5f - dB);
                    float aL = s.lip > 0f ? Mathf.Clamp01(0.5f - Shape(s, fx, fy, w, bodyH)) : 0f;
                    float e = -dB;
                    if (s.fill == 2)
                    {
                        float line = 1f - Smooth(s.rim - 0.8f, s.rim + 0.6f, e);
                        float g = s.glowCol.a * (1f - Smooth(0f, s.glow, e - s.rim));
                        o = Mix(s.glowCol, s.rimCol, line);
                        o.a = aB * Mathf.Max(line, g);
                        px[y * w + x] = o; continue;
                    }
                    Color body = Body(s, fx, fy - s.lip, w, bodyH, e);
                    if (s.fill == 3)
                    {
                        float u = fx / w;
                        float wash = (0.5f + 0.5f * Mathf.Cos(Mathf.PI * Mathf.Clamp01(u * 1.05f))) * 0.2f;
                        if (fx < 3f) wash = 0.75f;
                        o = s.tone; o.a = aB * wash;
                        px[y * w + x] = o; continue;
                    }
                    Color lipC = s.fill == 1 ? C(0.30f, 0.19f, 0.10f) : K(s.tone, 0.42f);
                    lipC = Mix(lipC, Rim, 1f - Smooth(0.5f, 2.5f, -Shape(s, fx, fy, w, bodyH)));
                    float a = aB + aL * (1f - aB);
                    o = a > 0f ? Mix(lipC, body, aB / Mathf.Max(a, 0.0001f)) : body;
                    o.a = a;
                    px[y * w + x] = o;
                }
            return px;
        }
    }
}
