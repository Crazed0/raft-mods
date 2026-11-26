using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using HarmonyLib;
using HMLLibrary;
using RaftModLoader;
using Steamworks;

// Chest Organizer v1.0.0
// 1) "Arrumar jangada": a button next to the chest's own Sort button. It closes the chest and the HOST
//    sorts every chest on the raft by crafting category (same stacking rule as the game's Sort:
//    full-durability stacks merge, damaged tools and items with an exclusive string stay as they are).
//    A category starts in a fresh chest when it would otherwise be split and everything still fits.
//    Chests someone has open, the trash can and storages with special slots are left alone.
//    Before/after totals (amount and uses per item) must match, otherwise every slot is put back.
// 2) Sync fix for Craft From All Storage: it takes items from CLOSED chests and sends the game's
//    StorageManager_Close message. The host applies it but the game only passes a message on to the
//    other players when Deserialize returns true, and CloseStorage returns false for a chest that is
//    not open, so with 3+ players the others never heard about it. The host now passes it on.
//    Clients also ask the host for fresh chest contents when the inventory opens or the hammer shows
//    costs (at most every 8 s), so a copy that went stale for any reason heals itself.
// Console (F10): arrumar | arrumar status
public class ChestOrganizer : Mod
{
    public const string Version = "1.0.0";
    public const string HarmonyId = "com.bestcode.chestorganizer";
    const string NetSlug = "chest-organizer";
    const string BtnName = "BC_ArrumarJangada";
    const string BtnText = "Arrumar jangada";
    const float SyncGap = 8f;

    public static ChestOrganizer I;
    Harmony harmony;
    public static int relayed, syncedChests, arranged;
    public static bool wantSync;
    static float nextSyncReq, nextBtnLook;
    bool lastInv, pendingOrg;
    float pendingUntil;
    static readonly List<GameObject> buttons = new List<GameObject>();
    static readonly HashSet<string> logged = new HashSet<string>();

    public void Start()
    {
        I = this;
        harmony = new Harmony(HarmonyId);
        int patched = 0;
        var broken = new List<string>();
        foreach (Type t in typeof(ChestOrganizer).Assembly.GetTypes())
        {
            if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            try { harmony.CreateClassProcessor(t).Patch(); patched++; }
            catch (Exception e) { broken.Add(t.Name); Debug.LogError("[ChestOrganizer] patch " + t.Name + " falhou: " + (e.InnerException ?? e).Message); }
        }
        Log("carregado v" + Version + ", patches " + patched + "/" + (patched + broken.Count) + (broken.Count > 0 ? " (FALHARAM: " + string.Join(", ", broken.ToArray()) + ")" : ""));
    }

    public void OnModUnload()
    {
        if (harmony != null) harmony.UnpatchAll(HarmonyId);
        foreach (GameObject g in buttons) if (g != null) UnityEngine.Object.Destroy(g);
        buttons.Clear();
        I = null;
    }

    public static void Log(string m) { Debug.Log("[ChestOrganizer] " + m); }
    public static void LogOnce(string m) { if (logged.Add(m)) Log(m); }

    static Network_Player Me()
    {
        try { return RAPI.GetLocalPlayer(); } catch { return null; }
    }

    public void Update()
    {
        Network_Player me = Me();
        if (me == null) return;
        try
        {
            bool inv = CanvasHelper.ActiveMenu == MenuType.Inventory;
            if (inv && !lastInv) wantSync = true;
            lastInv = inv;
            if (Raft_Network.IsHost) wantSync = false;
            else if (wantSync && Time.unscaledTime >= nextSyncReq)
            {
                wantSync = false;
                nextSyncReq = Time.unscaledTime + SyncGap;
                Send("req");
            }

            Storage_Small cur = me.StorageManager != null ? me.StorageManager.currentStorage : null;
            if (inv && cur != null && cur.IsOpen && Time.unscaledTime >= nextBtnLook)
            {
                nextBtnLook = Time.unscaledTime + 0.4f;
                EnsureButton();
            }

            if (pendingOrg && (cur == null || !cur.IsOpen || Time.unscaledTime > pendingUntil))
            {
                pendingOrg = false;
                Send("org");
            }
        }
        catch (Exception e) { LogOnce("update falhou: " + e.Message); }
    }

    static void Send(string msg)
    {
        try { if (I != null) I.SendNetworkMessage(msg, Target.Other, EP2PSend.k_EP2PSendReliable, NetSlug); }
        catch (Exception e) { LogOnce("envio falhou: " + e.Message); }
    }

    public override bool OnNetworkMessage(object message, Network_UserId from, string modslug)
    {
        if (modslug != NetSlug) return false;
        string s = message as string;
        if (s == null) return true;
        try
        {
            if (s == "req") { if (Raft_Network.IsHost) SendSnapshot(); }
            else if (s == "org")
            {
                if (Raft_Network.IsHost)
                {
                    string r = Arrange();
                    Notify(r);
                    Send("done|" + r);
                }
            }
            else if (s.StartsWith("snap", StringComparison.Ordinal)) { if (!Raft_Network.IsHost) ApplySnapshot(s); }
            else if (s.StartsWith("done|", StringComparison.Ordinal)) Notify(s.Substring(5));
        }
        catch (Exception e) { Log("mensagem '" + (s.Length > 8 ? s.Substring(0, 8) : s) + "' falhou: " + e); }
        return true;
    }

    // ---------- shared helpers ----------
    static bool Skip(Storage_Small st, out Inventory inv)
    {
        inv = null;
        if (st == null) return true;
        inv = st.GetInventoryReference();
        if (inv == null || inv.allSlots == null || inv.allSlots.Count == 0) return true;
        if (inv is Inventory_Trashcan) return true;
        if (st.buildableItem != null && st.buildableItem.UniqueName != null && st.buildableItem.UniqueName.Contains("Trash")) return true;
        return false;
    }

    static string Encode(Inventory inv)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < inv.allSlots.Count; i++)
        {
            if (i > 0) sb.Append(';');
            Slot sl = inv.allSlots[i];
            if (sl == null || !sl.HasValidItemInstance()) continue;
            ItemInstance it = sl.itemInstance;
            sb.Append(it.UniqueIndex).Append(':').Append(it.Amount).Append(':').Append(it.Uses);
            if (!string.IsNullOrEmpty(it.exclusiveString)) sb.Append(':').Append(Uri.EscapeDataString(it.exclusiveString));
        }
        return sb.ToString();
    }

    static bool Decode(Inventory inv, string enc)
    {
        string[] cells = enc.Split(';');
        if (cells.Length != inv.allSlots.Count) { LogOnce("bau com tamanho diferente do host, ignorado"); return false; }
        for (int i = 0; i < cells.Length; i++)
        {
            Slot sl = inv.allSlots[i];
            if (sl == null) continue;
            string c = cells[i];
            if (c.Length == 0) { if (sl.HasValidItemInstance()) sl.SetItem((ItemInstance)null); continue; }
            string[] p = c.Split(':');
            int idx, amt, uses;
            if (p.Length < 3 || !int.TryParse(p[0], out idx) || !int.TryParse(p[1], out amt) || !int.TryParse(p[2], out uses)) continue;
            Item_Base b = ItemManager.GetItemByIndex(idx);
            if (b == null) continue;
            string ex = p.Length > 3 ? Uri.UnescapeDataString(p[3]) : "";
            sl.SetItem(new ItemInstance(b, amt, uses, ex));
        }
        return true;
    }

    // ---------- sync: host sends what every closed chest holds, clients copy it ----------
    static void SendSnapshot()
    {
        var sb = new StringBuilder("snap");
        int n = 0;
        foreach (Storage_Small st in StorageManager.allStorages)
        {
            Inventory inv;
            if (Skip(st, out inv) || st.IsOpen) continue;
            sb.Append('\n').Append(st.ObjectIndex).Append('|').Append(Encode(inv));
            n++;
        }
        Send(sb.ToString());
        LogOnce("primeiro envio de baus aos amigos (" + n + " baus)");
    }

    static void ApplySnapshot(string s)
    {
        Network_Player me = Me();
        StorageManager sm = me != null ? me.StorageManager : null;
        if (sm == null) return;
        string[] lines = s.Split('\n');
        int changed = 0;
        for (int l = 1; l < lines.Length; l++)
        {
            string line = lines[l];
            int bar = line.IndexOf('|');
            uint obj;
            if (bar <= 0 || !uint.TryParse(line.Substring(0, bar), out obj)) continue;
            Storage_Small st = sm.GetStorageByObjectIndex(obj);
            Inventory inv;
            if (Skip(st, out inv) || st.IsOpen) continue;
            string enc = line.Substring(bar + 1);
            if (Encode(inv) == enc) continue;
            if (Decode(inv, enc)) { changed++; Fill(st); }
        }
        if (changed > 0)
        {
            syncedChests += changed;
            Log("corrigidos " + changed + " baus que estavam desatualizados (copia do host)");
        }
        else LogOnce("baus em dia com o host");
    }

    static void Fill(Storage_Small st)
    {
        try { st.UpdateStorageFillRenderers(); } catch { }
    }

    // ---------- arrange (host only) ----------
    class Box
    {
        public Storage_Small st;
        public List<Slot> free = new List<Slot>();
        public List<ItemInstance> plan = new List<ItemInstance>();
        public string before;
    }

    static int Cat(ItemInstance it)
    {
        try { var r = it.baseItem.settings_recipe; return r != null ? (int)r.CraftingCategory : 999; }
        catch { return 999; }
    }

    static Dictionary<int, long[]> Totals(List<Box> boxes)
    {
        var d = new Dictionary<int, long[]>();
        foreach (Box b in boxes)
            foreach (Slot sl in b.free)
            {
                if (!sl.HasValidItemInstance()) continue;
                ItemInstance it = sl.itemInstance;
                long[] v;
                if (!d.TryGetValue(it.UniqueIndex, out v)) { v = new long[2]; d[it.UniqueIndex] = v; }
                v[0] += it.Amount;
                v[1] += it.UsesInStack;
            }
        return d;
    }

    static bool SameTotals(Dictionary<int, long[]> a, Dictionary<int, long[]> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            long[] o;
            if (!b.TryGetValue(kv.Key, out o) || o[0] != kv.Value[0] || o[1] != kv.Value[1]) return false;
        }
        return true;
    }

    public static string Arrange()
    {
        if (!Raft_Network.IsHost) return "So o host pode arrumar a jangada.";
        var boxes = new List<Box>();
        int openSkipped = 0;
        foreach (Storage_Small st in StorageManager.allStorages)
        {
            Inventory inv;
            if (Skip(st, out inv)) continue;
            bool special = false;
            var use = new List<Slot>();
            foreach (Slot sl in inv.allSlots)
            {
                if (sl == null) continue;
                if (sl.slotType != SlotType.Normal) { special = true; break; }
                if (!sl.locked && sl.active) use.Add(sl);
            }
            if (special || use.Count == 0) continue;
            if (st.IsOpen) { openSkipped++; continue; }
            boxes.Add(new Box { st = st, free = use, before = Encode(inv) });
        }
        if (boxes.Count == 0) return "Nao ha baus fechados para arrumar.";

        // fill order: rows across the raft, then along, then bottom to top
        boxes.Sort((a, b) =>
        {
            Vector3 pa = a.st.transform.position, pb = b.st.transform.position;
            int c = Mathf.RoundToInt(pa.z / 1.5f).CompareTo(Mathf.RoundToInt(pb.z / 1.5f));
            if (c != 0) return c;
            c = Mathf.RoundToInt(pa.x / 1.5f).CompareTo(Mathf.RoundToInt(pb.x / 1.5f));
            if (c != 0) return c;
            return pa.y.CompareTo(pb.y);
        });

        Dictionary<int, long[]> before = Totals(boxes);

        // gather: full-durability stacks merge (the game's own Sort rule), the rest stays as it is
        var merged = new Dictionary<int, int>();
        var stacks = new List<ItemInstance>();
        foreach (Box b in boxes)
            foreach (Slot sl in b.free)
            {
                if (!sl.HasValidItemInstance()) continue;
                ItemInstance it = sl.itemInstance;
                int ss = it.baseItem.settings_Inventory.StackSize;
                if (it.HasMaxUses && it.Amount <= ss && string.IsNullOrEmpty(it.exclusiveString))
                {
                    int v;
                    merged.TryGetValue(it.UniqueIndex, out v);
                    merged[it.UniqueIndex] = v + it.Amount;
                }
                else stacks.Add(it.Clone());
            }
        foreach (var kv in merged)
        {
            Item_Base ib = ItemManager.GetItemByIndex(kv.Key);
            if (ib == null) return "Item desconhecido (" + kv.Key + "), nada foi mexido.";
            int ss = Mathf.Max(1, ib.settings_Inventory.StackSize);
            for (int left = kv.Value; left > 0; left -= ss)
                stacks.Add(new ItemInstance(ib, Mathf.Min(ss, left), ib.MaxUses));
        }
        stacks.Sort((a, b) =>
        {
            int c = Cat(a).CompareTo(Cat(b));
            if (c != 0) return c;
            c = a.UniqueIndex.CompareTo(b.UniqueIndex);
            if (c != 0) return c;
            c = b.Amount.CompareTo(a.Amount);
            if (c != 0) return c;
            return b.Uses.CompareTo(a.Uses);
        });

        // plan
        int[] suffix = new int[boxes.Count + 1];
        for (int i = boxes.Count - 1; i >= 0; i--) suffix[i] = suffix[i + 1] + boxes[i].free.Count;
        if (stacks.Count > suffix[0]) return "Nao cabe tudo nos baus, nada foi mexido.";
        int bi = 0, used = 0, k = 0;
        while (k < stacks.Count)
        {
            int cat = Cat(stacks[k]);
            int end = k;
            while (end < stacks.Count && Cat(stacks[end]) == cat) end++;
            int need = end - k;
            if (used > 0 && bi < boxes.Count && boxes[bi].free.Count - used < need && suffix[bi + 1] >= stacks.Count - k)
            {
                bi++;
                used = 0;
            }
            for (int j = k; j < end; j++)
            {
                while (bi < boxes.Count && used >= boxes[bi].free.Count) { bi++; used = 0; }
                if (bi >= boxes.Count) return "Nao cabe tudo nos baus, nada foi mexido.";
                boxes[bi].plan.Add(stacks[j]);
                used++;
            }
            k = end;
        }

        // apply, with everything put back if the totals do not match
        var backup = new List<KeyValuePair<Slot, ItemInstance>>();
        foreach (Box b in boxes)
            foreach (Slot sl in b.free)
                backup.Add(new KeyValuePair<Slot, ItemInstance>(sl, sl.HasValidItemInstance() ? sl.itemInstance.Clone() : null));
        try
        {
            foreach (Box b in boxes)
                for (int j = 0; j < b.free.Count; j++)
                    b.free[j].SetItem(j < b.plan.Count ? b.plan[j] : null);
            if (!SameTotals(before, Totals(boxes))) throw new Exception("totais diferentes depois de arrumar");
        }
        catch (Exception e)
        {
            foreach (var kv in backup) kv.Key.SetItem(kv.Value);
            Log("arrumar falhou, tudo reposto: " + e.Message);
            return "Nao consegui arrumar, ficou tudo como estava.";
        }

        // tell everyone what changed (same message the game sends when a chest closes)
        Network_Player me = Me();
        Raft_Network net = ComponentManager<Raft_Network>.Value;
        int changed = 0;
        foreach (Box b in boxes)
        {
            if (Encode(b.st.GetInventoryReference()) == b.before) continue;
            changed++;
            Fill(b.st);
            try
            {
                if (net != null && me != null && me.StorageManager != null)
                    net.RPC(new Message_Storage_Close(Messages.StorageManager_Close, me.StorageManager, b.st), Target.Other, EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
            }
            catch (Exception e) { LogOnce("envio do bau falhou: " + e.Message); }
        }
        arranged++;
        string res = changed == 0
            ? "A jangada ja estava arrumada (" + boxes.Count + " baus)."
            : "Jangada arrumada: " + changed + " de " + boxes.Count + " baus mudaram.";
        if (openSkipped > 0) res += " " + openSkipped + (openSkipped == 1 ? " bau aberto ficou" : " baus abertos ficaram") + " de fora.";
        Log(res + " (" + stacks.Count + " montes)");
        return res;
    }

    // ---------- button ----------
    public void RequestArrange()
    {
        Network_Player me = Me();
        if (me == null) return;
        CloseMine(me);
        if (Raft_Network.IsHost) Notify(Arrange());
        else
        {
            pendingOrg = true;
            pendingUntil = Time.unscaledTime + 2.5f;
            Notify("A pedir ao host para arrumar a jangada...");
        }
    }

    static void CloseMine(Network_Player me)
    {
        StorageManager sm = me.StorageManager;
        Storage_Small cur = sm != null ? sm.currentStorage : null;
        if (cur == null || !cur.IsOpen) return;
        var m = new Message_Storage_Close(Messages.StorageManager_Close, sm, cur);
        if (Raft_Network.IsHost)
        {
            me.Network.RPC(m, Target.Other, EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
            sm.CloseStorage(cur);
        }
        else me.SendP2P(m, EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
    }

    static void Notify(string text)
    {
        Log(text);
        try
        {
            HNotify hn = UnityEngine.Object.FindObjectOfType<HNotify>();
            if (hn != null) hn.AddNotification(HNotify.NotificationType.normal, text, 5);
        }
        catch (Exception e) { LogOnce("aviso falhou: " + e.Message); }
    }

    static bool IsSort(Button b, bool exact)
    {
        int n = b.onClick.GetPersistentEventCount();
        for (int i = 0; i < n; i++)
        {
            string m = b.onClick.GetPersistentMethodName(i);
            if (m == null) continue;
            if (exact ? m == "Sort" : m.IndexOf("Sort", StringComparison.Ordinal) >= 0) return true;
        }
        return false;
    }

    void EnsureButton()
    {
        GameObject cv = GameObject.Find("_CanvasGame_New");
        Transform ip = cv != null ? cv.transform.Find("InventoryParent") : null;
        if (ip == null) { LogOnce("InventoryParent nao encontrado"); return; }
        foreach (Transform panel in ip)
        {
            if (!panel.gameObject.activeInHierarchy || !panel.name.Contains("Storage")) continue;
            Button src = null;
            foreach (Button b in panel.GetComponentsInChildren<Button>(false))
            {
                if (b.name == BtnName) continue;
                if (IsSort(b, true)) { src = b; break; }
                if (src == null && IsSort(b, false)) src = b;
            }
            if (src == null)
            {
                var names = new List<string>();
                foreach (Button b in panel.GetComponentsInChildren<Button>(false))
                    names.Add(b.name + (b.onClick.GetPersistentEventCount() > 0 ? "=" + b.onClick.GetPersistentMethodName(0) : ""));
                LogOnce("sem botao Sort em " + panel.name + ": " + string.Join(", ", names.ToArray()));
                continue;
            }
            if (src.transform.parent.Find(BtnName) != null) continue;
            MakeButton(src, panel.name);
        }
    }

    void MakeButton(Button src, string panelName)
    {
        GameObject go = UnityEngine.Object.Instantiate(src.gameObject, src.transform.parent, false);
        go.name = BtnName;
        buttons.Add(go);
        foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null) continue;
            string tn = mb.GetType().Name;
            if (tn.IndexOf("Locali", StringComparison.OrdinalIgnoreCase) >= 0 || tn.IndexOf("Tooltip", StringComparison.OrdinalIgnoreCase) >= 0)
                UnityEngine.Object.DestroyImmediate(mb);
        }
        Button btn = go.GetComponent<Button>();
        btn.onClick = new Button.ButtonClickedEvent();
        btn.onClick.AddListener(RequestArrange);
        int texts = 0;
        foreach (Text t in go.GetComponentsInChildren<Text>(true))
        {
            t.text = BtnText;
            t.resizeTextForBestFit = true;
            t.resizeTextMinSize = 8;
            t.resizeTextMaxSize = Mathf.Max(t.fontSize, 9);
            texts++;
        }
        foreach (TMPro.TMP_Text t in go.GetComponentsInChildren<TMPro.TMP_Text>(true))
        {
            t.text = BtnText;
            t.enableAutoSizing = true;
            t.fontSizeMin = 8;
            t.fontSizeMax = Mathf.Max(t.fontSize, 9);
            texts++;
        }

        // put it beside the Sort button, on the first side with room that does not cover anything
        RectTransform rt = (RectTransform)go.transform, sr = (RectTransform)src.transform;
        RectTransform par = sr.parent as RectTransform;
        string side = "layout";
        if (par != null && par.GetComponent<LayoutGroup>() == null)
        {
            Rect pr = par.rect;
            Rect me = Local(par, sr);
            float gap = 6f;
            Vector2[] shifts = { new Vector2(-(me.width + gap), 0f), new Vector2(me.width + gap, 0f), new Vector2(0f, -(me.height + gap)), new Vector2(0f, me.height + gap) };
            string[] names = { "esquerda", "direita", "baixo", "cima" };
            int pick = -1;
            for (int i = 0; i < shifts.Length && pick < 0; i++)
            {
                Rect r = new Rect(me.position + shifts[i], me.size);
                if (r.xMin < pr.xMin - 1f || r.xMax > pr.xMax + 1f || r.yMin < pr.yMin - 1f || r.yMax > pr.yMax + 1f) continue;
                bool hit = false;
                foreach (Transform c in par)
                {
                    if (c == sr || c == rt || !c.gameObject.activeInHierarchy) continue;
                    RectTransform crt = c as RectTransform;
                    if (crt == null) continue;
                    Rect cr = Local(par, crt);
                    if (cr.width >= pr.width * 0.9f && cr.height >= pr.height * 0.9f) continue; // background
                    if (cr.Overlaps(new Rect(r.xMin + 1f, r.yMin + 1f, r.width - 2f, r.height - 2f))) { hit = true; break; }
                }
                if (!hit) pick = i;
            }
            if (pick < 0) pick = 2;
            rt.anchoredPosition = sr.anchoredPosition + shifts[pick];
            side = names[pick];
        }
        Log("botao '" + BtnText + "' criado em " + panelName + " ao lado de " + src.name + " (" + side + ", textos " + texts + ")");
    }

    static Rect Local(RectTransform par, RectTransform rt)
    {
        Vector3[] c = new Vector3[4];
        rt.GetWorldCorners(c);
        Vector2 a = par.InverseTransformPoint(c[0]), b = par.InverseTransformPoint(c[2]);
        return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
    }

    // ---------- console ----------
    [ConsoleCommand("arrumar", "Chest Organizer. Syntax: arrumar | arrumar status")]
    public static string CmdArrumar(string[] args)
    {
        if (I == null) return "Chest Organizer nao esta ativo.";
        foreach (string a in args)
            if (a == "status")
                return "Chest Organizer v" + Version + ": baus " + StorageManager.allStorages.Count + ", mensagens passadas pelo host " + relayed
                    + ", baus corrigidos com a copia do host " + syncedChests + ", arrumacoes " + arranged + (Raft_Network.IsHost ? " (host)" : " (cliente)");
        I.RequestArrange();
        return Raft_Network.IsHost ? "Feito." : "Pedido enviado ao host.";
    }
}

// Host: pass a chest's new contents on to every player even when the chest was closed (Craft From All Storage).
[HarmonyPatch(typeof(StorageManager), nameof(StorageManager.Deserialize))]
static class Patch_ChestOrganizer_Relay
{
    static void Postfix(StorageManager __instance, Message_NetworkBehaviour msg, ref bool __result)
    {
        try
        {
            if (__result || !Raft_Network.IsHost || msg == null || msg.Type != Messages.StorageManager_Close) return;
            Message_Storage_Close m = msg as Message_Storage_Close;
            if (m == null || __instance.GetStorageByObjectIndex(m.storageObjectIndex) == null) return;
            __result = true;
            if (ChestOrganizer.relayed++ == 0) ChestOrganizer.Log("host passou a primeira mudanca de bau fechado aos amigos");
        }
        catch (Exception e) { ChestOrganizer.LogOnce("relay falhou: " + e.Message); }
    }
}

// Client: holding the hammer shows costs, so make sure chest contents are fresh.
[HarmonyPatch(typeof(BuildingUI_CostBox), nameof(BuildingUI_CostBox.SetAmountInInventory), typeof(PlayerInventory), typeof(bool))]
static class Patch_ChestOrganizer_CostSync
{
    static void Postfix() { ChestOrganizer.wantSync = true; }
}
