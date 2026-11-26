using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using HarmonyLib;
using HMLLibrary;
using RaftModLoader;
using Steamworks;

// Chest Organizer v1.1.0
// 1) Shared chests. Several players can have the same chest open. The HOST owns what every chest holds:
//    - opening asks the host, which sends the chest's current contents to everyone;
//    - every change a player makes in an open chest goes to the host as "these slots were X, now Y";
//      the host only applies it if the slots still hold X (somebody else may have taken the item first);
//    - a refused change is undone on that player: the chest goes back to the host's copy and the items
//      come back out of (or go back into) the player's inventory, so nothing is ever duplicated;
//    - the host sends every change to everyone as it happens, so all copies stay the same.
// 2) Craft From All Storage on a client takes items from CLOSED chests. That edit is merged on the host
//    slot by slot; when the host's copy changed in the meantime the item difference is taken from the
//    chests, and whatever is missing is taken back from that player (now or as soon as they have it).
// 3) The host sorts every chest on the raft by crafting category on its own, a couple of seconds after
//    the last change, while nobody has a chest open. The game's Sort and "put everything in" buttons are
//    hidden. Before/after totals must match, otherwise every slot is put back.
// Console (F10): arrumar | arrumar status
public class ChestOrganizer : Mod
{
    public const string Version = "1.1.0";
    public const string HarmonyId = "com.bestcode.chestorganizer";
    const string NetSlug = "chest-organizer";
    const int MaxPending = 24;
    const float QuietBeforeArrange = 2f, ArrangeGap = 5f, ScanGap = 0.5f;

    public static ChestOrganizer I;
    Harmony harmony;
    static readonly HashSet<string> logged = new HashSet<string>();
    public static int arranged, opsOk, opsNo, merges, conflicts;

    // ---------- host ----------
    static readonly Dictionary<uint, HashSet<ulong>> viewers = new Dictionary<uint, HashSet<ulong>>();
    static readonly Dictionary<uint, string[]> lastSent = new Dictionary<uint, string[]>();
    static readonly Dictionary<ulong, HashSet<uint>> rejecting = new Dictionary<ulong, HashSet<uint>>();
    static bool dirty = true;
    static float lastChange, lastArrange, nextScan, nextClean;

    // ---------- local player (host and clients) ----------
    public static bool hasSession;
    public static Storage_Small session;
    static uint sessionObj;
    static float sessionAt;
    static bool leaving;

    // ---------- clients ----------
    class Op { public int id; public int[] idx; public string[] before, after; }
    class Debt { public int idx, amt; }
    static readonly Dictionary<uint, string[]> known = new Dictionary<uint, string[]>();
    static readonly Dictionary<uint, List<Op>> pending = new Dictionary<uint, List<Op>>();
    static readonly Dictionary<uint, int> pendingCf = new Dictionary<uint, int>();
    static readonly Dictionary<uint, string[]> lastCfNew = new Dictionary<uint, string[]>();
    static readonly List<Debt> debts = new List<Debt>();
    static int opSeq;
    static uint opening;
    static float openingUntil, nextReq, nextDebt;
    static bool synced;

    public void Start()
    {
        I = this;
        ResetAll();
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
        try { if (hasSession) LocalLeave(true); } catch { }
        if (harmony != null) harmony.UnpatchAll(HarmonyId);
        I = null;
    }

    public override void WorldEvent_WorldUnloaded() { ResetAll(); }

    public override void WorldEvent_OnPlayerDisconnected(Network_UserId userid, DisconnectReason disconnectReason)
    {
        if (!Raft_Network.IsHost) return;
        try
        {
            foreach (uint o in new List<uint>(viewers.Keys)) HostLeave(userid.Id, o);
            rejecting.Remove(userid.Id);
        }
        catch (Exception e) { LogOnce("saida de jogador falhou: " + e.Message); }
    }

    static void ResetAll()
    {
        viewers.Clear(); lastSent.Clear(); rejecting.Clear();
        known.Clear(); pending.Clear(); pendingCf.Clear(); lastCfNew.Clear(); debts.Clear();
        hasSession = false; session = null; sessionObj = 0; opening = 0;
        synced = false; dirty = true; nextReq = 0f;
        lastArrange = Time.unscaledTime;
        lastChange = Time.unscaledTime;
    }

    public static void Log(string m) { Debug.Log("[ChestOrganizer] " + m); }
    public static void LogOnce(string m) { if (logged.Add(m)) Log(m); }

    static Network_Player Me()
    {
        try { return RAPI.GetLocalPlayer(); } catch { return null; }
    }

    static ulong MyId()
    {
        Raft_Network net = ComponentManager<Raft_Network>.Value;
        return net != null ? net.LocalSteamID.Id : 0UL;
    }

    public void Update()
    {
        Network_Player me = Me();
        if (me == null) return;
        try
        {
            if (hasSession && session == null) LocalLeave(true); // chest destroyed
            if (Raft_Network.IsHost) HostTick();
            else ClientTick();
            if (hasSession && Time.unscaledTime - sessionAt > 0.5f && CanvasHelper.ActiveMenu != MenuType.Inventory) LocalLeave(true);
        }
        catch (Exception e) { LogOnce("update falhou: " + e); }
    }

    // ---------- network ----------
    static void Send(string msg)
    {
        try { if (I != null) I.SendNetworkMessage(msg, Target.Other, EP2PSend.k_EP2PSendReliable, NetSlug); }
        catch (Exception e) { LogOnce("envio falhou: " + e.Message); }
    }

    static void SendTo(ulong id, string msg)
    {
        try { if (I != null) I.SendNetworkMessageToPlayer(msg, new Network_UserId(id), EP2PSend.k_EP2PSendReliable, NetSlug); }
        catch (Exception e) { LogOnce("envio falhou: " + e.Message); }
    }

    static void SendHost(string msg)
    {
        Raft_Network net = ComponentManager<Raft_Network>.Value;
        if (net == null) return;
        SendTo(net.HostID.Id, msg);
    }

    public override bool OnNetworkMessage(object message, Network_UserId from, string modslug)
    {
        if (modslug != NetSlug) return false;
        string s = message as string;
        if (s == null || Me() == null) return true;
        try
        {
            if (Raft_Network.IsHost) HostMessage(from.Id, s);
            else
            {
                Raft_Network net = ComponentManager<Raft_Network>.Value;
                if (net != null && from.Id != net.HostID.Id) return true;
                ClientMessage(s);
            }
        }
        catch (Exception e) { Log("mensagem '" + (s.Length > 12 ? s.Substring(0, 12) : s) + "' falhou: " + e); }
        return true;
    }

    // ---------- cells: one string per slot, "" = empty, else UniqueIndex:Amount:Uses[:exclusive] ----------
    public static bool Skip(Storage_Small st, out Inventory inv)
    {
        inv = null;
        if (st == null) return true;
        inv = st.GetInventoryReference();
        if (inv == null || inv.allSlots == null || inv.allSlots.Count == 0) return true;
        if (inv is Inventory_Trashcan) return true;
        if (st.buildableItem != null && st.buildableItem.UniqueName != null && st.buildableItem.UniqueName.Contains("Trash")) return true;
        return false;
    }

    static string Cell(Slot sl)
    {
        if (sl == null || !sl.HasValidItemInstance()) return "";
        ItemInstance it = sl.itemInstance;
        string c = it.UniqueIndex + ":" + it.Amount + ":" + it.Uses;
        if (!string.IsNullOrEmpty(it.exclusiveString)) c += ":" + Uri.EscapeDataString(it.exclusiveString);
        return c;
    }

    static string[] Cells(Inventory inv)
    {
        var r = new string[inv.allSlots.Count];
        for (int i = 0; i < r.Length; i++) r[i] = Cell(inv.allSlots[i]);
        return r;
    }

    static bool Parse(string c, out Item_Base b, out int amt, out int uses, out string ex)
    {
        b = null; amt = 0; uses = 0; ex = "";
        if (string.IsNullOrEmpty(c)) return false;
        string[] p = c.Split(':');
        int idx;
        if (p.Length < 3 || !int.TryParse(p[0], out idx) || !int.TryParse(p[1], out amt) || !int.TryParse(p[2], out uses)) return false;
        b = ItemManager.GetItemByIndex(idx);
        if (p.Length > 3) ex = Uri.UnescapeDataString(p[3]);
        return b != null && amt > 0;
    }

    static bool SetCell(Slot sl, string c)
    {
        if (sl == null) return false;
        if (c.Length == 0)
        {
            if (sl.HasValidItemInstance()) sl.SetItem((ItemInstance)null);
            return true;
        }
        Item_Base b; int amt, uses; string ex;
        if (!Parse(c, out b, out amt, out uses, out ex)) { LogOnce("item desconhecido no bau: " + c); return false; }
        sl.SetItem(new ItemInstance(b, amt, uses, ex));
        return true;
    }

    static bool WriteCells(Inventory inv, string[] cells)
    {
        if (cells.Length != inv.allSlots.Count) { LogOnce("bau com tamanho diferente do host, ignorado"); return false; }
        bool changed = false;
        for (int i = 0; i < cells.Length; i++)
        {
            Slot sl = inv.allSlots[i];
            if (sl == null || Cell(sl) == cells[i]) continue;
            SetCell(sl, cells[i]);
            changed = true;
        }
        return changed;
    }

    static string Key(string cell, out int amt)
    {
        amt = 0;
        string[] p = cell.Split(':');
        if (p.Length < 3 || !int.TryParse(p[1], out amt)) return null;
        return p[0] + ":" + p[2] + ":" + (p.Length > 3 ? p[3] : "");
    }

    static void AddDelta(Dictionary<string, long> d, string cell, int sign)
    {
        if (string.IsNullOrEmpty(cell)) return;
        int amt;
        string k = Key(cell, out amt);
        if (k == null || amt <= 0) return;
        long v;
        d.TryGetValue(k, out v);
        d[k] = v + sign * (long)amt;
    }

    static bool SplitKey(string key, out Item_Base b, out int idx, out int uses, out string ex)
    {
        b = null; idx = 0; uses = 0; ex = "";
        string[] p = key.Split(':');
        if (p.Length < 2 || !int.TryParse(p[0], out idx) || !int.TryParse(p[1], out uses)) return false;
        if (p.Length > 2) ex = Uri.UnescapeDataString(p[2]);
        b = ItemManager.GetItemByIndex(idx);
        return b != null;
    }

    static Storage_Small FindSt(uint o)
    {
        foreach (Storage_Small st in StorageManager.allStorages)
            if (st != null && st.ObjectIndex == o) return st;
        return null;
    }

    static void Fill(Storage_Small st)
    {
        try { st.UpdateStorageFillRenderers(); } catch { }
    }

    static void VisualOpen(Storage_Small st)
    {
        if (st != null && !st.IsOpen) st.Open(null);
    }

    static void VisualClose(Storage_Small st)
    {
        if (st != null && st.IsOpen) st.Close(null);
    }

    // ---------- opening / closing on this machine ----------
    public static bool Rayed(Storage_Small st)
    {
        Inventory inv;
        if (Skip(st, out inv)) return true;
        CanvasHelper cv = st.canvas != null ? st.canvas : ComponentManager<CanvasHelper>.Value;
        if (cv == null || cv.displayTextManager == null) return true;
        if (CanvasHelper.ActiveMenu != MenuType.None) { cv.displayTextManager.HideDisplayTexts(); return false; }
        bool mine = hasSession && session == st;
        if (!mine && opening == 0 && !PlayerItemManager.IsBusy && cv.CanOpenMenu && Helper.LocalPlayerIsWithinDistance(st.transform.position, Player.UseDistance + 0.5f))
        {
            InputAction action = PlayerInput.GetPlayerByIndex(0).actions["Interact"];
            cv.displayTextManager.ShowText(Helper.GetTerm("Game/Open"), action.GetBindingDisplayString(), MyInput.Keybinds["Interact"].MainKey);
            if (SimpleMonoBehaviourSingleton<CustomInputConfig>.Instance.WasPressedThisFrame(action, "Interact"))
            {
                cv.displayTextManager.HideDisplayTexts();
                RequestOpen(st);
            }
        }
        else cv.displayTextManager.HideDisplayTexts();
        return false;
    }

    static void RequestOpen(Storage_Small st)
    {
        if (Raft_Network.IsHost) { HostOpen(MyId(), st); return; }
        opening = st.ObjectIndex;
        openingUntil = Time.unscaledTime + 3f;
        SendHost("o?|" + opening);
    }

    static void LocalOpen(Storage_Small st)
    {
        Network_Player me = Me();
        if (me == null || st == null) return;
        if (hasSession && session != st) LocalLeave(true);
        StorageManager sm = me.StorageManager;
        PlayerInventory pinv = me.Inventory;
        Inventory inv = st.GetInventoryReference();
        if (sm == null || pinv == null || inv == null) return;
        if (sm.currentStorage != null && sm.currentStorage != st && sm.currentStorage.IsOpen) sm.CloseStorage(sm.currentStorage);
        sm.currentStorage = st;
        pinv.secondInventory = inv;
        inv.secondInventory = pinv;
        session = st;
        sessionObj = st.ObjectIndex;
        hasSession = true;
        sessionAt = Time.unscaledTime;
        opening = 0;
        inv.Show();
        CanvasHelper cv = ComponentManager<CanvasHelper>.Value;
        if (cv != null) cv.OpenMenuCloseOther(MenuType.Inventory);
        PlayerItemManager.IsBusy = true;
        st.canCloseWithUsebutton = false;
        st.CancelInvoke("AllowCloseWithUseButton");
        st.Invoke("AllowCloseWithUseButton", 0.15f);
    }

    public static void Leave() { LocalLeave(true); }

    static void LocalLeave(bool tell)
    {
        if (!hasSession || leaving) return;
        leaving = true;
        try
        {
            Storage_Small st = session;
            uint o = sessionObj;
            if (!Raft_Network.IsHost && st != null) Capture(st, o);
            hasSession = false;
            session = null;
            Network_Player me = Me();
            if (me != null)
            {
                if (me.StorageManager != null && (me.StorageManager.currentStorage == st || me.StorageManager.currentStorage == null)) me.StorageManager.currentStorage = null;
                if (me.Inventory != null) me.Inventory.secondInventory = null;
            }
            if (st != null)
            {
                Inventory inv = st.GetInventoryReference();
                if (inv != null) { inv.secondInventory = null; inv.Hide(); }
            }
            PlayerItemManager.IsBusy = false;
            CanvasHelper cv = ComponentManager<CanvasHelper>.Value;
            if (cv != null && CanvasHelper.ActiveMenu == MenuType.Inventory) cv.CloseMenu(MenuType.Inventory);
            if (tell)
            {
                if (Raft_Network.IsHost) HostLeave(MyId(), o);
                else SendHost("c|" + o);
            }
        }
        finally { leaving = false; }
    }

    public static void SessionKeys(StorageManager sm)
    {
        Storage_Small st = session;
        Network_Player p = sm.playerNetwork;
        if (st == null || p == null) return;
        var cic = SimpleMonoBehaviourSingleton<CustomInputConfig>.Instance;
        var acts = PlayerInput.GetPlayerByIndex(0).actions;
        bool close = !cic.Gamepad
            ? ((MyInput.GetButtonDown("Interact") && st.CanCloseWithUseButton) || MyInput.GetButtonDown("Inventory") || MyInput.GetButtonDown("Cancel"))
            : ((acts["Interact"].WasPressedThisFrame() && st.CanCloseWithUseButton) || acts["Cancel"].WasPressedThisFrame());
        if (close || Vector3.Distance(p.transform.position, st.transform.position) > sm.maxDistanceToStorage || p.Stats.IsDead) Leave();
    }

    // ---------- host ----------
    static void HostTick()
    {
        float now = Time.unscaledTime;
        if (now >= nextClean) { nextClean = now + 1f; CleanViewers(); }
        bool all = now >= nextScan;
        if (all) nextScan = now + ScanGap;
        foreach (Storage_Small st in StorageManager.allStorages)
        {
            Inventory inv;
            if (Skip(st, out inv)) continue;
            if (all || viewers.ContainsKey(st.ObjectIndex)) Flush(st, true);
        }
        if (dirty && viewers.Count == 0 && now - lastChange >= QuietBeforeArrange && now - lastArrange >= ArrangeGap)
        {
            lastArrange = now;
            int changed;
            string r = Arrange(out changed);
            FlushAll(false);
            dirty = false;
            if (changed > 0) Log(r);
        }
    }

    static void CleanViewers()
    {
        Raft_Network net = ComponentManager<Raft_Network>.Value;
        if (net == null) return;
        var present = new HashSet<ulong> { MyId() };
        foreach (Network_UserId id in net.remoteUsers.Keys) present.Add(id.Id);
        foreach (uint o in new List<uint>(viewers.Keys))
        {
            HashSet<ulong> set = viewers[o];
            if (FindSt(o) == null) { viewers.Remove(o); Send("x|" + o); continue; }
            foreach (ulong id in new List<ulong>(set))
                if (!present.Contains(id)) HostLeave(id, o);
        }
        foreach (Storage_Small st in StorageManager.allStorages)
        {
            Inventory inv;
            if (Skip(st, out inv) || !st.IsOpen || viewers.ContainsKey(st.ObjectIndex)) continue;
            VisualClose(st);
            Send("x|" + st.ObjectIndex);
        }
    }

    static void Flush(Storage_Small st, bool markDirty)
    {
        Inventory inv;
        if (Skip(st, out inv)) return;
        uint o = st.ObjectIndex;
        string[] cur = Cells(inv);
        string[] last;
        if (!lastSent.TryGetValue(o, out last) || last.Length != cur.Length) { lastSent[o] = cur; return; }
        StringBuilder sb = null;
        for (int i = 0; i < cur.Length; i++)
        {
            if (cur[i] == last[i]) continue;
            if (sb == null) sb = new StringBuilder("u|").Append(o).Append('|');
            else sb.Append(';');
            sb.Append(i).Append('=').Append(cur[i]);
        }
        if (sb == null) return;
        lastSent[o] = cur;
        Send(sb.ToString());
        Fill(st);
        if (markDirty) { dirty = true; lastChange = Time.unscaledTime; }
    }

    static void FlushAll(bool markDirty)
    {
        foreach (Storage_Small st in StorageManager.allStorages) Flush(st, markDirty);
    }

    static void HostOpen(ulong pid, Storage_Small st)
    {
        Inventory inv;
        if (Skip(st, out inv)) return;
        uint o = st.ObjectIndex;
        Flush(st, true);
        HashSet<ulong> set;
        if (!viewers.TryGetValue(o, out set)) { set = new HashSet<ulong>(); viewers[o] = set; }
        set.Add(pid);
        VisualOpen(st);
        Send("o|" + o + "|" + pid + "|" + string.Join(";", Cells(inv)));
        if (pid == MyId()) LocalOpen(st);
    }

    static void HostLeave(ulong pid, uint o)
    {
        HashSet<ulong> set;
        if (!viewers.TryGetValue(o, out set) || !set.Remove(pid)) return;
        if (set.Count > 0) return;
        viewers.Remove(o);
        Storage_Small st = FindSt(o);
        if (st != null) { Flush(st, true); VisualClose(st); }
        Send("x|" + o);
    }

    static void HostMessage(ulong from, string s)
    {
        string[] p = s.Split('|');
        uint o = 0;
        if (p.Length > 1) uint.TryParse(p[1], out o);
        switch (p[0])
        {
            case "req": SendFull(from); break;
            case "o?":
                {
                    Storage_Small st = FindSt(o);
                    Inventory inv;
                    if (Skip(st, out inv)) SendTo(from, "ox|" + o);
                    else HostOpen(from, st);
                    break;
                }
            case "c": HostLeave(from, o); break;
            case "op":
                if (p.Length >= 4) { int id; int.TryParse(p[2], out id); HostOp(from, o, id, p.Length > 3 ? p[3] : ""); }
                break;
            case "rs":
                {
                    HashSet<uint> rj;
                    if (rejecting.TryGetValue(from, out rj)) rj.Remove(o);
                    break;
                }
            case "cf":
                if (p.Length >= 5) HostMerge(from, o, p[3], p[4]);
                break;
            case "org":
                {
                    string r = viewers.Count > 0 ? "Ha baus abertos, arrumo quando fecharem." : ArrangeNow();
                    SendTo(from, "done|" + r);
                    break;
                }
        }
    }

    static void SendFull(ulong to)
    {
        var sb = new StringBuilder("f");
        int n = 0;
        foreach (Storage_Small st in StorageManager.allStorages)
        {
            Inventory inv;
            if (Skip(st, out inv)) continue;
            Flush(st, true);
            sb.Append('\n').Append(st.ObjectIndex).Append('|').Append(viewers.ContainsKey(st.ObjectIndex) ? '1' : '0').Append('|').Append(string.Join(";", Cells(inv)));
            n++;
        }
        SendTo(to, sb.ToString());
        Log("enviados " + n + " baus a um jogador que entrou");
    }

    static void HostOp(ulong from, uint o, int id, string changes)
    {
        HashSet<uint> rj;
        if (rejecting.TryGetValue(from, out rj) && rj.Contains(o)) return; // the client undoes these itself
        bool ok = false;
        Storage_Small st = FindSt(o);
        Inventory inv;
        HashSet<ulong> set;
        if (!Skip(st, out inv) && viewers.TryGetValue(o, out set) && set.Contains(from))
        {
            Flush(st, true);
            string[] cur = Cells(inv);
            var idx = new List<int>();
            var after = new List<string>();
            ok = true;
            foreach (string ch in changes.Split(';'))
            {
                int eq = ch.IndexOf('='), gt = ch.IndexOf('>');
                int i;
                if (eq <= 0 || gt < eq || !int.TryParse(ch.Substring(0, eq), out i) || i < 0 || i >= cur.Length) { ok = false; break; }
                if (cur[i] != ch.Substring(eq + 1, gt - eq - 1)) { ok = false; break; }
                idx.Add(i);
                after.Add(ch.Substring(gt + 1));
            }
            if (ok)
            {
                for (int k = 0; k < idx.Count; k++) SetCell(inv.allSlots[idx[k]], after[k]);
                for (int k = 0; k < idx.Count; k++)
                    if (Cell(inv.allSlots[idx[k]]) != after[k]) { ok = false; break; }
                if (!ok) WriteCells(inv, cur);
            }
            Flush(st, true);
        }
        if (ok) opsOk++;
        else
        {
            opsNo++;
            if (rj == null) { rj = new HashSet<uint>(); rejecting[from] = rj; }
            rj.Add(o);
            Log("mudanca recusada no bau " + o + " (outro jogador mexeu primeiro)");
        }
        SendTo(from, "a|" + o + "|" + id + "|" + (ok ? "1" : "0"));
    }

    static void HostMerge(ulong from, uint o, string baseStr, string newStr)
    {
        Storage_Small st = FindSt(o);
        Inventory inv;
        if (Skip(st, out inv)) { SendTo(from, "cfa|" + o); return; }
        Flush(st, true);
        string[] cur = Cells(inv);
        string[] nw = newStr.Split(';');
        var delta = new Dictionary<string, long>();
        if (nw.Length != cur.Length) { LogOnce("fusao com tamanho errado, ignorada"); SendTo(from, "cfa|" + o); return; }
        if (baseStr == "?")
        {
            // the client never got this chest from the host: only take away what it used
            var a = new Dictionary<string, long>();
            foreach (string c in cur) AddDelta(a, c, 1);
            var b = new Dictionary<string, long>();
            foreach (string c in nw) AddDelta(b, c, 1);
            foreach (var kv in a)
            {
                long v;
                b.TryGetValue(kv.Key, out v);
                if (v < kv.Value) delta[kv.Key] = v - kv.Value;
            }
        }
        else
        {
            string[] bs = baseStr.Split(';');
            if (bs.Length != cur.Length) { LogOnce("fusao com tamanho errado, ignorada"); SendTo(from, "cfa|" + o); return; }
            for (int i = 0; i < cur.Length; i++)
            {
                if (bs[i] == nw[i]) continue;
                if (cur[i] == bs[i]) { SetCell(inv.allSlots[i], nw[i]); continue; }
                AddDelta(delta, nw[i], 1);
                AddDelta(delta, bs[i], -1);
                conflicts++;
            }
        }
        foreach (var kv in delta)
        {
            if (kv.Value < 0)
            {
                long left = RemoveFromChests(kv.Key, -kv.Value, st);
                if (left > 0) { SendTo(from, "dbt|" + kv.Key + "|" + left); Log("faltavam " + left + " de " + kv.Key + ", tirados ao jogador"); }
            }
            else if (kv.Value > 0)
            {
                long left = AddToChest(inv, kv.Key, kv.Value);
                if (left > 0) SendTo(from, "giv|" + kv.Key + "|" + left);
            }
        }
        FlushAll(true);
        merges++;
        SendTo(from, "cfa|" + o);
    }

    static long RemoveFromChests(string key, long amt, Storage_Small first)
    {
        Item_Base b; int idx, uses; string ex;
        if (!SplitKey(key, out b, out idx, out uses, out ex)) return amt;
        var order = new List<Storage_Small> { first };
        foreach (Storage_Small st in StorageManager.allStorages)
            if (st != first && !viewers.ContainsKey(st.ObjectIndex)) order.Add(st);
        foreach (Storage_Small st in StorageManager.allStorages)
            if (st != first && viewers.ContainsKey(st.ObjectIndex)) order.Add(st);
        for (int pass = 0; pass < 2 && amt > 0; pass++)
            foreach (Storage_Small st in order)
            {
                Inventory inv;
                if (Skip(st, out inv)) continue;
                foreach (Slot sl in inv.allSlots)
                {
                    if (amt <= 0) break;
                    if (sl == null || !sl.HasValidItemInstance() || sl.itemInstance.UniqueIndex != idx) continue;
                    if (pass == 0 && sl.itemInstance.Uses != uses) continue;
                    int n = (int)Math.Min(amt, sl.itemInstance.Amount);
                    sl.RemoveItem(n);
                    amt -= n;
                }
            }
        return amt;
    }

    static long AddToChest(Inventory inv, string key, long amt)
    {
        Item_Base b; int idx, uses; string ex;
        if (!SplitKey(key, out b, out idx, out uses, out ex)) return amt;
        int ss = Mathf.Max(1, b.settings_Inventory.StackSize);
        foreach (Slot sl in inv.allSlots)
        {
            if (amt <= 0) break;
            if (sl == null || sl.HasValidItemInstance() || sl.locked || !sl.active || sl.slotType != SlotType.Normal) continue;
            int n = (int)Math.Min(ss, amt);
            sl.SetItem(new ItemInstance(b, n, uses, ex));
            amt -= n;
        }
        return amt;
    }

    // ---------- clients ----------
    static void ClientTick()
    {
        float now = Time.unscaledTime;
        if (!synced && now >= nextReq) { nextReq = now + 4f; SendHost("req"); }
        if (hasSession && session != null) Capture(session, sessionObj);
        if (opening != 0 && now > openingUntil) opening = 0;
        if (debts.Count > 0 && now >= nextDebt) { nextDebt = now + 1f; PayDebts(); }
    }

    static int Cf(uint o) { int c; return pendingCf.TryGetValue(o, out c) ? c : 0; }

    static List<Op> Pend(uint o)
    {
        List<Op> l;
        if (!pending.TryGetValue(o, out l)) { l = new List<Op>(); pending[o] = l; }
        return l;
    }

    static string[] Expected(uint o)
    {
        string[] k;
        if (!known.TryGetValue(o, out k)) return null;
        var exp = (string[])k.Clone();
        List<Op> l;
        if (pending.TryGetValue(o, out l))
            foreach (Op op in l)
                for (int j = 0; j < op.idx.Length; j++)
                    if (op.idx[j] < exp.Length) exp[op.idx[j]] = op.after[j];
        return exp;
    }

    static void Refresh(uint o)
    {
        Storage_Small st = FindSt(o);
        Inventory inv;
        if (Skip(st, out inv)) return;
        string[] exp = Expected(o);
        if (exp == null) return;
        if (WriteCells(inv, exp)) Fill(st);
    }

    // what changed in the chest this player has open since the last look becomes one request to the host
    static void Capture(Storage_Small st, uint o)
    {
        Inventory inv = st.GetInventoryReference();
        if (inv == null) return;
        string[] exp = Expected(o);
        if (exp == null) return;
        string[] cur = Cells(inv);
        if (cur.Length != exp.Length) return;
        List<int> ch = null;
        for (int i = 0; i < cur.Length; i++)
            if (cur[i] != exp[i]) { if (ch == null) ch = new List<int>(); ch.Add(i); }
        if (ch == null) return;
        List<Op> list = Pend(o);
        if (list.Count >= MaxPending) { LogOnce("demasiadas mudancas por confirmar no bau " + o); return; }
        var op = new Op { id = ++opSeq, idx = ch.ToArray(), before = new string[ch.Count], after = new string[ch.Count] };
        var sb = new StringBuilder("op|").Append(o).Append('|').Append(op.id).Append('|');
        for (int j = 0; j < ch.Count; j++)
        {
            op.before[j] = exp[ch[j]];
            op.after[j] = cur[ch[j]];
            if (j > 0) sb.Append(';');
            sb.Append(ch[j]).Append('=').Append(op.before[j]).Append('>').Append(op.after[j]);
        }
        list.Add(op);
        SendHost(sb.ToString());
    }

    static void ClientMessage(string s)
    {
        if (s.StartsWith("f", StringComparison.Ordinal) && (s.Length == 1 || s[1] == '\n')) { ApplyFull(s); return; }
        string[] p = s.Split('|');
        uint o = 0;
        if (p.Length > 1) uint.TryParse(p[1], out o);
        switch (p[0])
        {
            case "u":
                {
                    string[] k;
                    if (!known.TryGetValue(o, out k)) { synced = false; nextReq = 0f; return; }
                    if (hasSession && o == sessionObj && session != null) Capture(session, o);
                    if (p.Length > 2)
                        foreach (string ch in p[2].Split(';'))
                        {
                            int eq = ch.IndexOf('=');
                            int i;
                            if (eq > 0 && int.TryParse(ch.Substring(0, eq), out i) && i >= 0 && i < k.Length) k[i] = ch.Substring(eq + 1);
                        }
                    if (Cf(o) == 0) Refresh(o);
                    break;
                }
            case "o":
                {
                    if (p.Length < 4) return;
                    if (hasSession && o == sessionObj && session != null) Capture(session, o);
                    known[o] = p[3].Split(';');
                    Storage_Small st = FindSt(o);
                    if (st == null) return;
                    VisualOpen(st);
                    ulong pid;
                    ulong.TryParse(p[2], out pid);
                    if (pid == MyId())
                    {
                        Refresh(o);
                        LocalOpen(st);
                    }
                    else if (Cf(o) == 0) Refresh(o);
                    break;
                }
            case "x":
                {
                    if (hasSession && sessionObj == o) LocalLeave(false);
                    VisualClose(FindSt(o));
                    break;
                }
            case "ox": if (opening == o) opening = 0; break;
            case "a":
                {
                    if (p.Length < 4) return;
                    int id;
                    int.TryParse(p[2], out id);
                    Ack(o, id, p[3] == "1");
                    break;
                }
            case "cfa":
                {
                    int c = Cf(o) - 1;
                    if (c > 0) pendingCf[o] = c;
                    else { pendingCf.Remove(o); lastCfNew.Remove(o); Refresh(o); }
                    break;
                }
            case "dbt":
                {
                    if (p.Length < 3) return;
                    long amt;
                    long.TryParse(p[2], out amt);
                    TakeFromMe(p[1], amt);
                    break;
                }
            case "giv":
                {
                    if (p.Length < 3) return;
                    long amt;
                    long.TryParse(p[2], out amt);
                    GiveToMe(p[1], amt);
                    break;
                }
            case "done": if (p.Length > 1) Notify(p[1]); break;
        }
    }

    static void ApplyFull(string s)
    {
        string[] lines = s.Split('\n');
        int n = 0;
        for (int l = 1; l < lines.Length; l++)
        {
            string[] p = lines[l].Split('|');
            uint o;
            if (p.Length < 3 || !uint.TryParse(p[0], out o)) continue;
            if (hasSession && o == sessionObj && session != null) Capture(session, o);
            known[o] = p[2].Split(';');
            Storage_Small st = FindSt(o);
            if (st == null) continue;
            if (p[1] == "1") VisualOpen(st);
            else if (!(hasSession && sessionObj == o)) VisualClose(st);
            if (Cf(o) == 0) Refresh(o);
            n++;
        }
        if (!synced) Log("baus recebidos do host (" + n + ")");
        synced = true;
    }

    static void Ack(uint o, int id, bool ok)
    {
        if (hasSession && o == sessionObj && session != null) Capture(session, o);
        List<Op> list = Pend(o);
        int at = list.FindIndex(x => x.id == id);
        if (at < 0) return;
        if (ok) { list.RemoveRange(0, at + 1); opsOk++; }
        else
        {
            // undo this change and every later one: the chest goes back to the host's copy,
            // the items go back the other way in this player's inventory
            var delta = new Dictionary<string, long>();
            for (int j = list.Count - 1; j >= at; j--)
                for (int k = 0; k < list[j].idx.Length; k++)
                {
                    AddDelta(delta, list[j].after[k], 1);
                    AddDelta(delta, list[j].before[k], -1);
                }
            list.RemoveRange(at, list.Count - at);
            foreach (var kv in delta)
            {
                if (kv.Value < 0) TakeFromMe(kv.Key, -kv.Value);
                else if (kv.Value > 0) GiveToMe(kv.Key, kv.Value);
            }
            opsNo++;
            SendHost("rs|" + o);
            Notify("Outro jogador mexeu primeiro nesse item, a tua jogada foi desfeita.");
        }
        Refresh(o);
    }

    static List<Slot> MySlots()
    {
        var r = new List<Slot>();
        Network_Player me = Me();
        PlayerInventory pinv = me != null ? me.Inventory : null;
        if (pinv == null) return r;
        if (pinv.allSlots != null) r.AddRange(pinv.allSlots);
        if (pinv.equipSlots != null) foreach (Slot_Equip e in pinv.equipSlots) r.Add(e);
        return r;
    }

    static long TakeFromMe(int idx, int uses, long amt)
    {
        List<Slot> slots = MySlots();
        for (int pass = 0; pass < 2 && amt > 0; pass++)
            foreach (Slot sl in slots)
            {
                if (amt <= 0) break;
                if (sl == null || !sl.HasValidItemInstance() || sl.itemInstance.UniqueIndex != idx) continue;
                if (pass == 0 && uses >= 0 && sl.itemInstance.Uses != uses) continue;
                int n = (int)Math.Min(amt, sl.itemInstance.Amount);
                sl.RemoveItem(n);
                amt -= n;
            }
        return amt;
    }

    static void TakeFromMe(string key, long amt)
    {
        Item_Base b; int idx, uses; string ex;
        if (amt <= 0 || !SplitKey(key, out b, out idx, out uses, out ex)) return;
        long left = TakeFromMe(idx, uses, amt);
        if (left > 0)
        {
            debts.Add(new Debt { idx = idx, amt = (int)left });
            Log("fica a dever " + left + " de " + b.UniqueName + " (tirado assim que aparecer)");
        }
    }

    static void GiveToMe(string key, long amt)
    {
        Item_Base b; int idx, uses; string ex;
        if (amt <= 0 || !SplitKey(key, out b, out idx, out uses, out ex)) return;
        // pay old debts of this item first
        for (int i = debts.Count - 1; i >= 0 && amt > 0; i--)
        {
            if (debts[i].idx != idx) continue;
            int n = (int)Math.Min(amt, debts[i].amt);
            debts[i].amt -= n;
            amt -= n;
            if (debts[i].amt <= 0) debts.RemoveAt(i);
        }
        Network_Player me = Me();
        PlayerInventory pinv = me != null ? me.Inventory : null;
        if (pinv == null) return;
        int ss = Mathf.Max(1, b.settings_Inventory.StackSize);
        while (amt > 0)
        {
            int n = (int)Math.Min(ss, amt);
            pinv.AddItem(new ItemInstance(b, n, uses, ex), true);
            amt -= n;
        }
    }

    static void PayDebts()
    {
        for (int i = debts.Count - 1; i >= 0; i--)
        {
            long left = TakeFromMe(debts[i].idx, -1, debts[i].amt);
            debts[i].amt = (int)left;
            if (left <= 0) debts.RemoveAt(i);
        }
    }

    // A client's game wants to tell the host about a chest. Opens become our open request; a close for a
    // chest that is not open here comes from Craft From All Storage and is merged on the host instead.
    public static bool ClientIntercept(Message_Storage m)
    {
        Storage_Small st = FindSt(m.storageObjectIndex);
        Inventory inv;
        if (Skip(st, out inv)) return true;
        uint o = st.ObjectIndex;
        if (m.Type == Messages.StorageManager_Open)
        {
            if (!(hasSession && sessionObj == o) && opening == 0) RequestOpen(st);
            return false;
        }
        if (hasSession && sessionObj == o) { LocalLeave(true); return false; }
        string[] cur = Cells(inv);
        int c = Cf(o);
        string[] bs = null;
        if (c > 0) lastCfNew.TryGetValue(o, out bs);
        if (bs == null) known.TryGetValue(o, out bs);
        if (bs != null && bs.Length == cur.Length)
        {
            bool same = true;
            for (int i = 0; i < cur.Length && same; i++) same = bs[i] == cur[i];
            if (same) return false;
        }
        SendHost("cf|" + o + "|" + (++opSeq) + "|" + (bs == null ? "?" : string.Join(";", bs)) + "|" + string.Join(";", cur));
        pendingCf[o] = c + 1;
        lastCfNew[o] = cur;
        return false;
    }

    public static bool HostDeserialize(Message_Storage m, Network_UserId remote)
    {
        Storage_Small st = FindSt(m.storageObjectIndex);
        Inventory inv;
        if (Skip(st, out inv)) return true;
        if (Raft_Network.IsHost)
        {
            if (m.Type == Messages.StorageManager_Open) HostOpen(remote.Id, st);
            else
            {
                HashSet<ulong> set;
                if (viewers.TryGetValue(st.ObjectIndex, out set) && set.Contains(remote.Id)) HostLeave(remote.Id, st.ObjectIndex);
                else LogOnce("fecho de bau do jogo ignorado (o mod trata disto)");
            }
        }
        return false;
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

    static string ArrangeNow()
    {
        int changed;
        string r = Arrange(out changed);
        FlushAll(false);
        dirty = false;
        lastArrange = Time.unscaledTime;
        Log(r);
        return r;
    }

    public static string Arrange(out int changed)
    {
        changed = 0;
        if (!Raft_Network.IsHost) return "So o host pode arrumar a jangada.";
        var boxes = new List<Box>();
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
            if (st.IsOpen || viewers.ContainsKey(st.ObjectIndex)) continue;
            boxes.Add(new Box { st = st, free = use, before = string.Join(";", Cells(inv)) });
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

        // plan: a category starts in a fresh chest when it would otherwise be split and everything still fits
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

        foreach (Box b in boxes)
            if (string.Join(";", Cells(b.st.GetInventoryReference())) != b.before) changed++;
        arranged++;
        return changed == 0
            ? "A jangada ja estava arrumada (" + boxes.Count + " baus)."
            : "Jangada arrumada: " + changed + " de " + boxes.Count + " baus mudaram (" + stacks.Count + " montes).";
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

    // ---------- console ----------
    [ConsoleCommand("arrumar", "Chest Organizer. Syntax: arrumar | arrumar status")]
    public static string CmdArrumar(string[] args)
    {
        if (I == null) return "Chest Organizer nao esta ativo.";
        foreach (string a in args)
            if (a == "status")
                return "Chest Organizer v" + Version + (Raft_Network.IsHost ? " (host)" : " (cliente)") + ": baus " + StorageManager.allStorages.Count
                    + ", abertos " + viewers.Count + ", arrumacoes " + arranged + ", mudancas aceites " + opsOk + ", recusadas " + opsNo
                    + ", fusoes " + merges + " (conflitos " + conflicts + "), por confirmar " + pending.Count + ", dividas " + debts.Count;
        if (Raft_Network.IsHost) return viewers.Count > 0 ? "Ha baus abertos, arrumo quando fecharem." : ArrangeNow();
        SendHost("org");
        return "Pedido enviado ao host.";
    }
}

// Chests: our own open prompt (works while someone else has the chest open).
[HarmonyPatch(typeof(Storage_Small), nameof(Storage_Small.OnIsRayed))]
static class Patch_ChestOrganizer_Rayed
{
    static bool Prefix(Storage_Small __instance)
    {
        if (ChestOrganizer.I == null) return true;
        try { return ChestOrganizer.Rayed(__instance); }
        catch (Exception e) { ChestOrganizer.LogOnce("abrir falhou: " + e.Message); return true; }
    }
}

// Close keys, distance and death for a shared chest; the game's sort keys do nothing there.
[HarmonyPatch(typeof(StorageManager), "Update")]
static class Patch_ChestOrganizer_SmUpdate
{
    static bool Prefix(StorageManager __instance)
    {
        if (ChestOrganizer.I == null || !ChestOrganizer.hasSession) return true;
        if (__instance.playerNetwork == null || !__instance.playerNetwork.IsLocalPlayer) return true;
        if (__instance.currentStorage != ChestOrganizer.session) return true;
        try { ChestOrganizer.SessionKeys(__instance); }
        catch (Exception e) { ChestOrganizer.LogOnce("teclas do bau falharam: " + e.Message); }
        return false;
    }
}

[HarmonyPatch(typeof(StorageManager), nameof(StorageManager.CloseStorage))]
static class Patch_ChestOrganizer_CloseStorage
{
    static bool Prefix(StorageManager __instance, Storage_Small storage, ref bool __result)
    {
        if (ChestOrganizer.I == null || !ChestOrganizer.hasSession || storage == null || storage != ChestOrganizer.session) return true;
        if (__instance.playerNetwork != null && __instance.playerNetwork.IsLocalPlayer) ChestOrganizer.Leave();
        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(StorageManager), nameof(StorageManager.CloseCurrentStorage))]
static class Patch_ChestOrganizer_CloseCurrent
{
    static bool Prefix(StorageManager __instance, ref bool __result)
    {
        if (ChestOrganizer.I == null || !ChestOrganizer.hasSession || __instance.currentStorage != ChestOrganizer.session) return true;
        if (__instance.playerNetwork == null || !__instance.playerNetwork.IsLocalPlayer) return true;
        ChestOrganizer.Leave();
        __result = true;
        return false;
    }
}

// The game's own open/close messages for chests are replaced by the host-run sessions.
[HarmonyPatch(typeof(StorageManager), nameof(StorageManager.Deserialize))]
static class Patch_ChestOrganizer_Deserialize
{
    static bool Prefix(Message_NetworkBehaviour msg, Network_UserId remoteID, ref bool __result)
    {
        if (ChestOrganizer.I == null || msg == null) return true;
        if (msg.Type != Messages.StorageManager_Open && msg.Type != Messages.StorageManager_Close) return true;
        Message_Storage m = msg as Message_Storage;
        if (m == null) return true;
        try
        {
            if (ChestOrganizer.HostDeserialize(m, remoteID)) return true;
            __result = false;
            return false;
        }
        catch (Exception e) { ChestOrganizer.LogOnce("mensagem de bau do jogo falhou: " + e.Message); return true; }
    }
}

// Client: chest open/close messages from the game (and from Craft From All Storage) go through the mod.
[HarmonyPatch(typeof(Raft_Network), nameof(Raft_Network.SendP2P), typeof(Network_UserId), typeof(Message), typeof(EP2PSend), typeof(NetworkChannel))]
static class Patch_ChestOrganizer_SendP2P
{
    static bool Prefix(Message message)
    {
        if (ChestOrganizer.I == null || Raft_Network.IsHost || message == null) return true;
        if (message.Type != Messages.StorageManager_Open && message.Type != Messages.StorageManager_Close) return true;
        Message_Storage m = message as Message_Storage;
        if (m == null) return true;
        try { return ChestOrganizer.ClientIntercept(m); }
        catch (Exception e) { ChestOrganizer.LogOnce("envio de bau falhou: " + e.Message); return true; }
    }
}

// Hide the chest's Sort and "put everything in" buttons; the host keeps the chests sorted.
[HarmonyPatch(typeof(Inventory), "Update")]
static class Patch_ChestOrganizer_HideButtons
{
    static void Postfix(Inventory __instance)
    {
        if (ChestOrganizer.I == null || __instance is PlayerInventory) return;
        if (__instance.sortButton != null && __instance.sortButton.activeSelf) __instance.sortButton.SetActive(false);
        if (__instance.dumpButton != null && __instance.dumpButton.activeSelf) __instance.dumpButton.SetActive(false);
    }
}
