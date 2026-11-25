using System;
using System.Reflection;
using UnityEngine;
using RaftModLoader;
using HMLLibrary;

// Quick Autosave: changes Raft's autosave interval (default 10 min) to 1 min,
// and shows a small "Autosave em 0:42" countdown in the top-right corner.
// v1.3.0: the countdown and the "Jogo guardado" notice are drawn in Raft's own style (BestCode UI kit).
// Console (F10): autosave <minutes>   |   autosave.overlay  (show/hide the countdown)
public class QuickAutosave : Mod
{
    public const string Version = "1.3.1";
    const string PrefKey = "QuickAutosave.Minutes";
    const string PrefOverlay = "QuickAutosave.Overlay";
    static float minutes = 1f;
    static bool showOverlay = true;
    static FieldInfo delayField;
    static FieldInfo timerField;
    SaveAndLoad target;

    // Overlay state: text is rebuilt at most once per second.
    string overlayText = "";
    int lastShownSecond = -1;
    float lastTimer;
    bool overlayVisible;
    UnityEngine.UI.Text countdown;

    public void Start()
    {
        minutes = PlayerPrefs.GetFloat(PrefKey, 1f);
        if (minutes < 0.25f) minutes = 0.25f;
        showOverlay = PlayerPrefs.GetInt(PrefOverlay, 1) == 1;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        delayField = typeof(SaveAndLoad).GetField("autoSaveTimeDelay", flags);
        timerField = typeof(SaveAndLoad).GetField("autoSaveTimer", flags);
        try { SubscribeToNetworkChannel(NetSlug); } catch (Exception e) { Debug.Log("[QuickAutosave] canal de rede falhou: " + e.Message); }
        Debug.Log("[QuickAutosave] loaded v" + Version + ", interval " + minutes + " min" + (delayField == null ? " (ERROR: field not found)" : ""));
    }

    // "Jogo guardado" notice: a toast at the top centre (stacks with the other BestCode mods'
    // toasts), shown to every player: the host sends a network message when the game saves.
    const string NetSlug = "quick-autosave";

    static void SavedNotice()
    {
        if (Raft_Network.InMenuScene) return;
        try { BCUI_QA.Toast.Show("JOGO GUARDADO", null, null, false, 2.6f); } catch (Exception e) { Debug.Log("[QuickAutosave] aviso falhou: " + e.Message); }
    }

    public override bool OnNetworkMessage(object message, Network_UserId from, string modslug)
    {
        if (modslug != NetSlug || message as string != "saved") return false;
        SavedNotice();
        return true;
    }

    public void Update()
    {
        Tick();
        DrawCountdown();
    }

    void Tick()
    {
        overlayVisible = false;
        if (delayField == null || timerField == null) return;
        if (target == null) target = UnityEngine.Object.FindObjectOfType<SaveAndLoad>();
        if (target == null) return;

        float want = minutes * 60f;
        if (!Mathf.Approximately((float)delayField.GetValue(target), want)) delayField.SetValue(target, want);

        if (Raft_Network.InMenuScene || !Raft_Network.IsHost || !LoadSceneManager.IsGameSceneLoaded) return;

        float timer = (float)timerField.GetValue(target);
        // Timer dropped back to ~0 -> the game just saved.
        if (timer + 1f < lastTimer)
        {
            SavedNotice();
            try { SendNetworkMessage("saved", Target.Other, Steamworks.EP2PSend.k_EP2PSendReliable, NetSlug); } catch (Exception e) { Debug.Log("[QuickAutosave] aviso de rede falhou: " + e.Message); }
        }
        lastTimer = timer;
        if (!showOverlay) return;

        if (!GeneralSettingsBox.AutoSave)
        {
            if (lastShownSecond != -3) { overlayText = "Autosave desligado"; lastShownSecond = -3; }
        }
        else
        {
            int left = Mathf.Max(0, Mathf.CeilToInt(want - timer));
            // the game draws its own save icon in this corner: hide at 0 and for the first 2 s after a save
            if (left <= 0 || timer < 2f) return;
            if (left != lastShownSecond)
            {
                lastShownSecond = left;
                overlayText = "Autosave em " + (left / 60) + ":" + (left % 60).ToString("00");
            }
        }
        overlayVisible = true;
    }

    // the countdown: small, in the game's font, top-right under the game's own notifications
    void DrawCountdown()
    {
        bool menu = false;
        try { menu = CanvasHelper.ActiveMenu != MenuType.None; } catch { }
        bool on = overlayVisible && !menu;
        if (countdown == null)
        {
            if (!on) return;
            countdown = BCUI_QA.Kit.Txt(BCUI_QA.Kit.Root(), "BC_AutosaveCountdown", "", 24, BCUI_QA.Kit.A(BCUI_QA.Kit.Cream, 0.9f), TextAnchor.MiddleRight, true);
            BCUI_QA.Kit.Place(countdown.rectTransform, BCUI_QA.Kit.TR, BCUI_QA.Kit.TR, new Vector2(-26f, -14f), new Vector2(320f, 30f));
        }
        if (countdown.gameObject.activeSelf != on) countdown.gameObject.SetActive(on);
        if (on && countdown.text != overlayText) countdown.text = overlayText;
    }

    public void OnModUnload()
    {
        if (target != null && delayField != null) delayField.SetValue(target, 600f);
        if (countdown != null) UnityEngine.Object.Destroy(countdown.gameObject);
        BCUI_QA.Toast.ClearAll();
        Debug.Log("[QuickAutosave] unloaded, interval back to 10 min");
    }

    [ConsoleCommand("autosave", "Sets the autosave interval in minutes. Syntax: autosave <minutes>")]
    public static string SetInterval(string[] args)
    {
        if (args.Length < 1) return "Autosave every " + minutes + " min. Use: autosave <minutes>";
        float m;
        string s = args[0].Replace(',', '.');
        if (!float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out m) || m < 0.25f)
            return "Invalid value. Use a number of minutes >= 0.25, e.g. autosave 1";
        minutes = m;
        PlayerPrefs.SetFloat(PrefKey, m);
        PlayerPrefs.Save();
        var sl = UnityEngine.Object.FindObjectOfType<SaveAndLoad>();
        if (sl != null && timerField != null) timerField.SetValue(sl, 0f);
        return "Autosave every " + m + " min.";
    }

    [ConsoleCommand("autosave.overlay", "Shows or hides the autosave countdown.")]
    public static string ToggleOverlay(string[] args)
    {
        showOverlay = !showOverlay;
        PlayerPrefs.SetInt(PrefOverlay, showOverlay ? 1 : 0);
        PlayerPrefs.Save();
        return "Autosave countdown " + (showOverlay ? "on" : "off") + ".";
    }
}

// ---------- BestCode UI kit ----------
// Drawn with Raft's own font (ChineseRocks) and sprites (parchment plate, item slot, key chip), on
// one shared overlay canvas. Every BestCode mod carries its own copy of this kit in its own
// namespace; mods only share the canvas object and the toast column (plain Unity components and
// RectTransforms), so one mod reloading never breaks another. Source: raft-mods/src/bcui_kit.cs.txt
namespace BCUI_QA
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
