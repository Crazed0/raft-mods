using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using RaftModLoader;
using HMLLibrary;

// Mod Sync 2: loader + updater for the pack published at https://bestcode.pt/raft/.
// - On start and every few minutes (also in game) it reads manifest.json, downloads every .rmod whose
//   SHA-256 differs, verifies it, replaces the file and HOT-RELOADS that mod in the running game
//   (unload old version, compile + load the new one). No restart needed.
// - Safety net (2.1): if the new version does not start, the previous file is put back and loaded again.
//   RML keeps compiled assemblies in cache\mods keyed by file hash + write time, so the old version comes
//   back from that cache without compiling. The failed version is not retried until the game restarts.
// - RML's compiler only creates %TEMP%\HCompiler once at start-up; if anything cleans Temp, every later
//   compile fails. Mod Sync recreates that folder every few seconds and before every reload.
// - Mods it installed that left the pack are unloaded and removed.
// - It updates itself the same way (a small helper reloads Mod Sync after it is replaced).
// - The PC that publishes the pack has mods\.modsync-source: it never downloads, but it watches the
//   mods folder, hot-reloads any mod the build replaces, and tells the other players to check now.
// - 2.1.2: its notices are toasts in Raft's own style (BestCode UI kit), at the top centre.
// Console (F10): modsync | modsync status | modsync reload <f> | modsync teste [reload <f> | falha <f>]
public class ModSync : Mod
{
    public const string Version = "2.1.3";
    const string Base = "https://bestcode.pt/raft/";
    const string PrefManaged = "ModSync.Managed";
    const string PrefSelf = "ModSync.SelfUpdated";
    const string PrefMsg = "ModSync.LastMessage";
    public const string SelfFile = "mod-sync.rmod";
    const string NetSlug = "mod-sync";
    const string TestMod = "quick-autosave.rmod";

    static ModSync instance;
    static string status = "a iniciar";
    static bool busy;
    static float checkEvery = 120f;
    public static bool IsSource;
    static int compilerDirFixes;
    static float nextDirCheck;

    float nextCheck;
    readonly List<float> quickChecks = new List<float>();
    // source PC: last seen stamp and sha of every .rmod in the folder
    static readonly Dictionary<string, string> stamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> shas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    // last version of each mod known to be loaded: what a failed update rolls back to
    public class Snap { public byte[] data; public DateTime mtime; public string sha; }
    static readonly Dictionary<string, Snap> good = new Dictionary<string, Snap>(StringComparer.OrdinalIgnoreCase);
    float nextScan;

    public static string ModsDir { get { return Path.GetFullPath(Path.Combine(Application.dataPath, "../mods")); } }
    static string CompilerDir { get { return Path.Combine(Path.GetTempPath(), "HCompiler"); } }

    public void Start()
    {
        instance = this;
        try { SubscribeToNetworkChannel(NetSlug); } catch (Exception e) { Log("canal de rede falhou: " + e.Message); }
        IsSource = File.Exists(Path.Combine(ModsDir, ".modsync-source"));
        Log("carregado v" + Version + (IsSource ? " (PC de origem)" : "") + ", pasta " + ModsDir);
        EnsureCompilerDir();

        string self = PlayerPrefs.GetString(PrefSelf, "");
        if (self.Length > 0)
        {
            PlayerPrefs.DeleteKey(PrefSelf);
            PlayerPrefs.Save();
            if (self == "1") Notify("MOD SYNC ATUALIZADO", "Já está na versão " + Version + ", sem reiniciar.", false, 6f);
            else Notify("MOD SYNC", "A versão nova não arrancou, continua a " + Version + ". Reinicia o Raft para a aplicar.", true, 14f);
        }

        if (IsSource)
        {
            ScanSource(true);
            status = "PC de origem: vigia a pasta mods e recarrega mods alterados";
        }
        else nextCheck = Time.realtimeSinceStartup + 5f; // let the mod loader finish loading first
    }

    public void OnModUnload()
    {
        if (instance == this) instance = null;
        try { BCUI_MS.Toast.ClearAll(); } catch { }
    }

    static void Log(string m) { Debug.Log("[ModSync] " + m); }

    // a toast at the top centre, also in the main menu; bad = something went wrong (red title)
    static void Notify(string title, string text, bool bad, float seconds)
    {
        Log("aviso: " + title + " | " + text);
        try { BCUI_MS.Toast.Show(title, text, null, bad, seconds); } catch (Exception e) { Log("aviso falhou: " + e.Message); }
    }

    class Entry { public string name; public long size; public string sha256; public string version; }
    class Manifest { public string updated; public Entry[] files; public int checkSeconds; public string message; }

    // Unity's JsonUtility leaves every field empty for classes from a mod (they live in an assembly
    // loaded at runtime that Unity's serializer does not know), so the manifest is read by hand.
    static Manifest ReadManifest(string json)
    {
        var root = new MiniJson(json.TrimStart((char)0xFEFF)).Parse() as Dictionary<string, object>;
        if (root == null) return null;
        var m = new Manifest { updated = JStr(root, "updated"), message = JStr(root, "message"), checkSeconds = (int)JNum(root, "checkSeconds") };
        var list = new List<Entry>();
        object fo;
        if (root.TryGetValue("files", out fo) && fo is List<object>)
            foreach (object o in (List<object>)fo)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                list.Add(new Entry { name = JStr(d, "name"), sha256 = JStr(d, "sha256"), version = JStr(d, "version"), size = (long)JNum(d, "size") });
            }
        m.files = list.ToArray();
        return m;
    }

    static string JStr(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null; }
    static double JNum(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is double ? (double)v : 0d; }

    void Update()
    {
        float now = Time.realtimeSinceStartup;
        if (now >= nextDirCheck) { nextDirCheck = now + 5f; EnsureCompilerDir(); }
        if (busy) return;
        if (IsSource)
        {
            if (now >= nextScan) { nextScan = now + 3f; ScanSource(false); }
            return;
        }
        bool due = now >= nextCheck;
        for (int i = quickChecks.Count - 1; i >= 0; i--)
            if (now >= quickChecks[i]) { quickChecks.RemoveAt(i); due = true; }
        if (due)
        {
            nextCheck = now + checkEvery;
            StartCoroutine(Check());
        }
    }

    // The source PC asks everyone to check now (after a build). Retry a few times: the
    // publish to the site finishes some seconds after the local file changes.
    public override bool OnNetworkMessage(object message, Network_UserId from, string modslug)
    {
        if (modslug != NetSlug) return false;
        if (message as string == "check" && !IsSource)
        {
            float now = Time.realtimeSinceStartup;
            quickChecks.Add(now + 8f);
            quickChecks.Add(now + 30f);
            quickChecks.Add(now + 75f);
            Log("o PC de origem publicou mods novos, a verificar");
        }
        return true;
    }

    // ---------------- remote pack (friend's PC) ----------------
    class Fetch { public Manifest man; public string error; public float ms; }

    static IEnumerator GetManifest(Fetch f)
    {
        float t0 = Time.realtimeSinceStartup;
        UnityWebRequest req = UnityWebRequest.Get(Base + "manifest.json?t=" + DateTime.UtcNow.Ticks);
        req.timeout = 20;
        yield return req.SendWebRequest();
        f.ms = (Time.realtimeSinceStartup - t0) * 1000f;
        if (!string.IsNullOrEmpty(req.error) || req.responseCode != 200) { f.error = "sem ligacao ao servidor (" + req.error + " " + req.responseCode + ")"; yield break; }
        try { f.man = ReadManifest(req.downloadHandler.text); } catch (Exception e) { f.error = "manifest invalido: " + e.Message; yield break; }
        if (f.man == null || f.man.files == null || f.man.files.Length == 0) { f.man = null; f.error = "manifest vazio ou invalido"; }
    }

    static bool ValidName(string n)
    {
        return !string.IsNullOrEmpty(n) && n.IndexOfAny(new[] { '/', '\\', ':' }) < 0 && n.EndsWith(".rmod", StringComparison.OrdinalIgnoreCase);
    }

    IEnumerator Check()
    {
        if (busy) yield break;
        busy = true;
        string dir = ModsDir;
        status = "a verificar " + Base;
        var f = new Fetch();
        yield return GetManifest(f);
        if (f.man == null)
        {
            status = f.error + ", mods ficam como estao";
            Log(status);
            busy = false;
            yield break;
        }
        Manifest man = f.man;
        if (man.checkSeconds >= 30) checkEvery = man.checkSeconds;
        if (!string.IsNullOrEmpty(man.message) && PlayerPrefs.GetString(PrefMsg, "") != man.message)
        {
            PlayerPrefs.SetString(PrefMsg, man.message);
            PlayerPrefs.Save();
            Notify("AVISO", man.message, false, 12f);
        }

        var inPack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = new List<string>();
        var failed = new List<string>();
        var waiting = new List<string>();
        foreach (Entry e in man.files)
        {
            if (!ValidName(e.name) || string.IsNullOrEmpty(e.sha256)) continue;
            inPack.Add(e.name);
            string local = Path.Combine(dir, e.name);
            string want = e.sha256.ToLowerInvariant();
            if (File.Exists(local) && Sha(File.ReadAllBytes(local)) == want) continue;
            // this exact version already failed to start in this session: wait for a restart
            if (IsRejected(e.name, want)) { waiting.Add(e.name); continue; }

            status = "a descarregar " + e.name;
            UnityWebRequest dl = UnityWebRequest.Get(Base + "mods/" + Uri.EscapeDataString(e.name) + "?h=" + e.sha256);
            dl.timeout = 120;
            yield return dl.SendWebRequest();
            if (!string.IsNullOrEmpty(dl.error) || dl.responseCode != 200) { failed.Add(e.name + " (" + dl.error + ")"); continue; }
            byte[] data = dl.downloadHandler.data;
            if (data == null || Sha(data) != want) { failed.Add(e.name + " (hash errado)"); continue; }
            try
            {
                // keep the version that is running now, to roll back to if the new one does not start
                if (File.Exists(local) && IsRunning(Find(e.name))) Remember(e.name);
                string tmp = local + ".download";
                File.WriteAllBytes(tmp, data);
                if (File.Exists(local)) File.Delete(local);
                File.Move(tmp, local);
                changed.Add(e.name);
                Log("descarregado: " + e.name);
            }
            catch (Exception ex) { failed.Add(e.name + " (" + ex.Message + ")"); }
        }

        var removed = new List<string>();
        foreach (string old in PlayerPrefs.GetString(PrefManaged, "").Split('|'))
            if (old.Length > 0 && !inPack.Contains(old) && File.Exists(Path.Combine(dir, old))) removed.Add(old);
        PlayerPrefs.SetString(PrefManaged, string.Join("|", new List<string>(inPack).ToArray()));
        PlayerPrefs.Save();

        string wait = waiting.Count > 0 ? " | a espera de reiniciar: " + string.Join(", ", waiting.ToArray()) : "";
        if (changed.Count + removed.Count > 0)
            yield return Apply(changed, removed, failed, true);
        else
        {
            status = "pack de " + man.updated + ": " + inPack.Count + " mods, tudo atualizado" + wait + (failed.Count > 0 ? " | FALHARAM: " + string.Join(", ", failed.ToArray()) : "");
            Log(status);
            if (failed.Count > 0) Notify("MOD SYNC", "Falhou atualizar " + failed.Count + (failed.Count == 1 ? " mod" : " mods") + ". Vê a consola (F10).", true, 14f);
        }
        busy = false;
    }

    // ---------------- local folder (source PC) ----------------
    void ScanSource(bool baseline)
    {
        string[] files;
        try { files = Directory.GetFiles(ModsDir, "*.rmod"); } catch { return; }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = new List<string>();
        foreach (string path in files)
        {
            string name = Path.GetFileName(path);
            seen.Add(name);
            FileInfo fi;
            try { fi = new FileInfo(path); } catch { continue; }
            string stamp = fi.Length + "@" + fi.LastWriteTimeUtc.Ticks;
            string prev;
            if (stamps.TryGetValue(name, out prev) && prev == stamp) continue;
            // wait until the file has stopped changing (the build may still be copying it)
            if (!baseline && (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalSeconds < 2) continue;
            byte[] data;
            try { data = File.ReadAllBytes(path); } catch { continue; }
            string sha = Sha(data);
            stamps[name] = stamp;
            string old;
            bool known = shas.TryGetValue(name, out old);
            shas[name] = sha;
            // at start the files on disk are the versions the loader just loaded
            if (baseline) good[name] = new Snap { data = data, mtime = fi.LastWriteTimeUtc, sha = sha };
            else if ((!known || old != sha) && !IsRejected(name, sha)) changed.Add(name);
        }
        var removed = new List<string>();
        foreach (string name in new List<string>(shas.Keys))
            if (!seen.Contains(name)) { shas.Remove(name); stamps.Remove(name); good.Remove(name); removed.Add(name); }
        if (changed.Count + removed.Count > 0) StartCoroutine(ApplySource(changed, removed));
    }

    // after Mod Sync itself writes a file on the source PC, so the scan does not see it as a new build
    static void NoteStamp(string name)
    {
        try
        {
            string path = Path.Combine(ModsDir, name);
            var fi = new FileInfo(path);
            stamps[name] = fi.Length + "@" + fi.LastWriteTimeUtc.Ticks;
            shas[name] = Sha(File.ReadAllBytes(path));
        }
        catch { }
    }

    IEnumerator ApplySource(List<string> changed, List<string> removed)
    {
        busy = true;
        yield return Apply(changed, removed, new List<string>(), false);
        busy = false;
        try { SendNetworkMessage("check", Target.Other, Steamworks.EP2PSend.k_EP2PSendReliable, NetSlug); }
        catch (Exception e) { Log("aviso de rede falhou: " + e.Message); }
    }

    // ---------------- hot reload ----------------
    public class Result { public bool ok; public bool rolledBack; public string label; public float seconds; }

    IEnumerator Apply(List<string> changed, List<string> removed, List<string> failed, bool deleteRemoved)
    {
        var ok = new List<string>();
        var bad = new List<string>(failed);
        var back = new List<string>();
        yield return WaitModsIdle(60f);

        foreach (string name in removed)
        {
            var r = new Result();
            yield return Unload(name, r);
            if (deleteRemoved)
            {
                try { File.Delete(Path.Combine(ModsDir, name)); Log("removido (saiu do pack): " + name); }
                catch (Exception ex) { r.ok = false; r.label = name + " (" + ex.Message + ")"; }
            }
            (r.ok ? ok : bad).Add(r.label + (r.ok ? " removido" : ""));
        }
        if (removed.Count > 0) yield return WaitTask(ModManagerPage.RefreshMods(), 60f);

        bool self = false;
        foreach (string name in changed)
        {
            if (string.Equals(name, SelfFile, StringComparison.OrdinalIgnoreCase)) { self = true; continue; }
            var r = new Result();
            yield return ReloadSafe(name, r, IsSource);
            if (r.ok) ok.Add(r.label);
            else if (r.rolledBack) back.Add(r.label);
            else bad.Add(r.label);
        }

        status = "aplicados: " + (ok.Count > 0 ? string.Join(", ", ok.ToArray()) : "nada")
            + (back.Count > 0 ? " | REPOSTOS: " + string.Join(", ", back.ToArray()) : "")
            + (bad.Count > 0 ? " | FALHARAM: " + string.Join(", ", bad.ToArray()) : "");
        Log(status);
        if (bad.Count > 0)
            Notify("REINICIA O RAFT", string.Join(", ", bad.ToArray()) + ": fecha e abre o Raft para estes ficarem atualizados.", true, 16f);
        else if (back.Count > 0)
            Notify("VERSÃO NOVA NÃO ARRANCOU", "Continua a anterior: " + string.Join(", ", back.ToArray()) + ". Reinicia o Raft para tentar outra vez.", true, 16f);
        else if (ok.Count > 0)
            Notify("MODS ATUALIZADOS", string.Join("\n", ok.ToArray()), false, 7f);

        if (self) StartSelfReload();
    }

    public static ModData Find(string fileName)
    {
        List<ModData> list = ModManagerPage.modList;
        if (list == null) return null;
        for (int i = 0; i < list.Count; i++)
        {
            ModData m = list[i];
            if (m != null && m.modinfo != null && m.modinfo.modFile != null && string.Equals(m.modinfo.modFile.Name, fileName, StringComparison.OrdinalIgnoreCase)) return m;
        }
        return null;
    }

    static bool IsRunning(ModData md) { return md != null && md.modinfo != null && md.modinfo.modState == ModInfo.ModStateEnum.running; }

    static BaseModHandler Handler(ModData md) { return md.modinfo.modHandler ?? new ZipModHandler(); }

    // RML's compiler writes each mod's code to %TEMP%\HCompiler\ but only creates that folder at start-up
    // ("Could not find a part of the path ...\Temp\HCompiler\..."), so keep recreating it
    public static void EnsureCompilerDir()
    {
        try
        {
            string d = CompilerDir;
            if (Directory.Exists(d)) return;
            Directory.CreateDirectory(d);
            compilerDirFixes++;
            Debug.Log("[ModSync] a pasta " + d + " tinha desaparecido (algo limpou o Temp), recriada (" + compilerDirFixes + "x nesta sessao)");
        }
        catch (Exception e) { Debug.Log("[ModSync] pasta HCompiler: " + e.Message); }
    }

    static string Label(ModData md, string fileName)
    {
        if (md == null || md.jsonmodinfo == null) return fileName;
        return md.jsonmodinfo.name + " " + md.jsonmodinfo.version;
    }

    static IEnumerator WaitModsIdle(float timeout)
    {
        float end = Time.realtimeSinceStartup + timeout;
        while (Time.realtimeSinceStartup < end)
        {
            bool compiling = false;
            List<ModData> list = ModManagerPage.modList;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null && list[i].modinfo != null && list[i].modinfo.modState == ModInfo.ModStateEnum.compiling) { compiling = true; break; }
            if (!compiling && ModManagerPage.canRefreshModlist) yield break;
            yield return null;
        }
    }

    static IEnumerator WaitTask(Task t, float timeout)
    {
        float end = Time.realtimeSinceStartup + timeout;
        while (t != null && !t.IsCompleted && Time.realtimeSinceStartup < end) yield return null;
    }

    // A client connected to a host cannot unload a mod the server requires. The host reloads the
    // same new version, so lift that lock for this mod while it is swapped, then put it back.
    static List<Mods> ReleaseServerLock(ModData md)
    {
        var held = new List<Mods>();
        try
        {
            List<Mods> list = NetworkModManager.currentServerMods;
            if (list == null || Raft_Network.IsHost) return held;
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i] != null && list[i].modName == md.jsonmodinfo.name) { held.Add(list[i]); list.RemoveAt(i); }
        }
        catch (Exception e) { Log("lock do servidor: " + e.Message); }
        return held;
    }

    static void RestoreServerLock(List<Mods> held, ModData now)
    {
        if (held == null || held.Count == 0) return;
        try
        {
            List<Mods> list = NetworkModManager.currentServerMods;
            if (list == null) return;
            if (now != null && now.jsonmodinfo != null)
                list.Add(new Mods { modName = now.jsonmodinfo.name, modVersion = now.jsonmodinfo.version, modSlug = now.jsonmodinfo.slug });
            else list.AddRange(held);
        }
        catch (Exception e) { Log("lock do servidor: " + e.Message); }
    }

    static IEnumerator UnloadRunning(ModData md, float timeout)
    {
        try { Handler(md).UnloadMod(md, true); } catch (Exception e) { Log("unload " + md.modinfo.modFile.Name + ": " + e.Message); }
        float end = Time.realtimeSinceStartup + timeout;
        while (IsRunning(md) && Time.realtimeSinceStartup < end) yield return null;
        yield return null; // let Destroy() of the old mod object happen
    }

    IEnumerator Unload(string fileName, Result r)
    {
        ModData md = Find(fileName);
        r.label = Label(md, fileName);
        r.ok = true;
        if (!IsRunning(md)) yield break;
        List<Mods> held = ReleaseServerLock(md);
        yield return UnloadRunning(md, 10f);
        if (IsRunning(md)) { RestoreServerLock(held, null); r.ok = false; r.label += " (nao descarregou)"; }
    }

    // Unloads the running version (if any) and loads whatever file is on disk now.
    public static IEnumerator Reload(string fileName, Result r)
    {
        string path = Path.Combine(ModsDir, fileName);
        float t0 = Time.realtimeSinceStartup;
        ModData md = Find(fileName);
        r.label = fileName;
        r.ok = false;
        List<Mods> held = null;

        if (md == null)
        {
            // new mod, or the loader dropped it after a bad file: register it in the loader's list first
            if (!File.Exists(path)) { r.label += " (ficheiro em falta)"; yield break; }
            yield return WaitTask(ModManagerPage.RefreshMod(new FileInfo(path)), 30f);
            md = Find(fileName);
            if (md == null) { r.label += " (o loader nao o reconheceu)"; yield break; }
        }
        else if (IsRunning(md))
        {
            held = ReleaseServerLock(md);
            yield return UnloadRunning(md, 10f);
            if (IsRunning(md)) { RestoreServerLock(held, null); r.label = Label(md, fileName) + " (nao descarregou)"; yield break; }
        }
        // a previous attempt left it as errored: clear that, so the wait below only sees the new outcome
        if (md.modinfo.modState == ModInfo.ModStateEnum.errored) md.modinfo.modState = ModInfo.ModStateEnum.idle;

        // LoadMod sees the new file hash, re-reads the mod, compiles it (or takes it from RML's cache) and starts it
        EnsureCompilerDir();
        try { Handler(md).LoadMod(md, true); }
        catch (Exception e) { Log("load " + fileName + ": " + e.Message); }

        float end = Time.realtimeSinceStartup + 120f;
        float missingSince = -1f;
        ModData cur = null;
        while (Time.realtimeSinceStartup < end)
        {
            yield return null;
            cur = Find(fileName);
            if (cur == null)
            {
                // briefly missing while the loader replaces the list entry; for long = it rejected the file
                if (missingSince < 0f) missingSince = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - missingSince > 15f) break;
                continue;
            }
            missingSince = -1f;
            if (cur.modinfo.modState == ModInfo.ModStateEnum.errored) break;
            if (cur.modinfo.modState == ModInfo.ModStateEnum.running && ModManagerPage.ModsGameObjectParent != null
                && ModManagerPage.ModsGameObjectParent.transform.Find(fileName.ToLowerInvariant()) != null) break;
        }
        RestoreServerLock(held, cur);
        r.seconds = Time.realtimeSinceStartup - t0;
        r.label = Label(cur ?? md, fileName);
        r.ok = IsRunning(cur);
        if (!r.ok)
            r.label += cur == null ? " (o loader recusou o ficheiro)"
                : cur.modinfo.modState == ModInfo.ModStateEnum.errored ? " (erro ao compilar)" : " (nao arrancou)";
        Log((r.ok ? "recarregado: " : "falhou recarregar: ") + r.label + " em " + r.seconds.ToString("0.0") + " s");
    }

    // Reload with a safety net: when the new file does not start, put the last good version back and load
    // it again (from RML's compiled cache, no compiler needed). The failed version is remembered for this
    // session so it is not retried in a loop. keepNewOnDisk (source PC): the build output stays on disk.
    public static IEnumerator ReloadSafe(string fileName, Result r, bool keepNewOnDisk)
    {
        yield return Reload(fileName, r);
        if (r.ok) { Remember(fileName); yield break; }

        string path = Path.Combine(ModsDir, fileName);
        Snap s;
        byte[] bad = null;
        DateTime badTime = DateTime.UtcNow;
        try { bad = File.ReadAllBytes(path); badTime = File.GetLastWriteTimeUtc(path); } catch { }
        if (!good.TryGetValue(fileName, out s) || s == null || bad == null || Sha(bad) == s.sha)
        {
            r.label += ", sem versao anterior para repor";
            yield break;
        }
        string failedLabel = r.label;
        Reject(fileName, Sha(bad));
        try
        {
            File.WriteAllBytes(path, s.data);
            File.SetLastWriteTimeUtc(path, s.mtime); // same hash + time = RML's cached assembly
        }
        catch (Exception e) { r.label += ", nao consegui repor a anterior (" + e.Message + ")"; yield break; }
        Log("a repor a versao anterior de " + fileName);
        // the restored Mod Sync starts during the reload below and reads this to tell the player
        if (string.Equals(fileName, SelfFile, StringComparison.OrdinalIgnoreCase)) MarkSelfRolledBack();

        var back = new Result();
        yield return Reload(fileName, back);
        if (keepNewOnDisk)
        {
            try { File.WriteAllBytes(path, bad); File.SetLastWriteTimeUtc(path, badTime); } catch (Exception e) { Log("repor o ficheiro novo: " + e.Message); }
        }
        if (IsSource) NoteStamp(fileName);
        r.rolledBack = back.ok;
        r.seconds += back.seconds;
        r.label = failedLabel + (back.ok ? " -> continua " + back.label : " -> e a anterior tambem nao arrancou");
        Log(back.ok ? "reposto: " + back.label + " (a versao nova so volta a ser tentada depois de reiniciar)" : "a versao anterior de " + fileName + " tambem falhou");
    }

    // snapshot of the file on disk, taken when it is known to be the loaded version
    public static void Remember(string fileName)
    {
        try
        {
            string path = Path.Combine(ModsDir, fileName);
            byte[] d = File.ReadAllBytes(path);
            good[fileName] = new Snap { data = d, mtime = File.GetLastWriteTimeUtc(path), sha = Sha(d) };
        }
        catch { }
    }

    // Failed versions are marked on a scene object that survives Mod Sync reloading itself (its statics
    // do not) and disappears when the game closes, so a restart tries them again.
    static Transform Session()
    {
        GameObject go = GameObject.Find("ModSyncSession");
        if (go == null) { go = new GameObject("ModSyncSession"); UnityEngine.Object.DontDestroyOnLoad(go); }
        return go.transform;
    }

    static string RejectKey(string fileName, string sha) { return "falhou:" + fileName.ToLowerInvariant() + ":" + sha; }

    public static bool IsRejected(string fileName, string sha) { return Session().Find(RejectKey(fileName, sha)) != null; }

    public static void Reject(string fileName, string sha)
    {
        if (IsRejected(fileName, sha)) return;
        var c = new GameObject(RejectKey(fileName, sha));
        c.transform.SetParent(Session(), false);
    }

    static List<string> RejectedList()
    {
        var l = new List<string>();
        Transform t = Session();
        for (int i = 0; i < t.childCount; i++)
        {
            string[] p = t.GetChild(i).name.Split(':');
            if (p.Length == 3) l.Add(p[1] + " (" + p[2].Substring(0, Math.Min(8, p[2].Length)) + ")");
        }
        return l;
    }

    // Mod Sync cannot reload itself from inside (its object is destroyed on unload), so a small
    // helper object from this same assembly does it and then removes itself.
    static void StartSelfReload()
    {
        PlayerPrefs.SetString(PrefSelf, "1");
        PlayerPrefs.Save();
        var go = new GameObject("ModSyncSelfReloader");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<ModSyncSelfReloader>();
        Log("a recarregar o proprio Mod Sync");
    }

    public static void MarkSelfRolledBack()
    {
        PlayerPrefs.SetString(PrefSelf, "reposto");
        PlayerPrefs.Save();
    }

    public static string Sha(byte[] b)
    {
        using (SHA256 h = SHA256.Create())
        {
            byte[] x = h.ComputeHash(b);
            var sb = new StringBuilder(64);
            foreach (byte v in x) sb.Append(v.ToString("x2"));
            return sb.ToString();
        }
    }

    // ---------------- self test ----------------
    // modsync teste              -> checks compiler folder, loader, rollback copies and the server
    // modsync teste reload [f]   -> plus a real hot reload of one mod (default Quick Autosave)
    // modsync teste falha [f]    -> plus a deliberately broken version of that mod, which must fail
    //                               and roll back to the current one (proves the safety net in game)
    IEnumerator SelfTest(string mode, string fileName)
    {
        busy = true;
        var problems = new List<string>();
        Action<string> line = s => Log("TESTE " + s);
        line("Mod Sync v" + Version + (IsSource ? ", PC de origem" : ", PC do amigo") + ", " + (Raft_Network.IsHost ? "anfitriao" : "cliente"));

        // 1. compiler folder
        string d = CompilerDir;
        bool existed = Directory.Exists(d);
        try
        {
            Directory.CreateDirectory(d);
            string probe = Path.Combine(d, "modsync-teste.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            line("1 pasta do compilador " + d + ": " + (existed ? "ok" : "NAO EXISTIA, recriada") + ", escrita ok, desaparecimentos nesta sessao: " + compilerDirFixes);
        }
        catch (Exception e) { problems.Add("pasta do compilador"); line("1 pasta do compilador " + d + ": ERRO " + e.Message); }

        // 2. loader state
        List<ModData> list = ModManagerPage.modList;
        if (list == null) { problems.Add("lista do loader"); line("2 loader: lista de mods indisponivel"); }
        else
        {
            int running = 0;
            var other = new List<string>();
            foreach (ModData m in list)
            {
                if (m == null || m.modinfo == null) continue;
                if (m.modinfo.modState == ModInfo.ModStateEnum.running) running++;
                else other.Add(Label(m, m.modinfo.modFile != null ? m.modinfo.modFile.Name : "?") + " = " + m.modinfo.modState);
            }
            line("2 loader: " + list.Count + " mods, " + running + " a correr" + (other.Count > 0 ? ", parados: " + string.Join(", ", other.ToArray()) : ""));
        }

        // 3. rollback copies and failed versions
        var rej = RejectedList();
        line("3 copias para repor: " + good.Count + " mods" + (rej.Count > 0 ? "; versoes falhadas nesta sessao: " + string.Join(", ", rej.ToArray()) : ""));

        // 4. server
        var f = new Fetch();
        yield return GetManifest(f);
        if (f.man == null) { problems.Add("servidor"); line("4 servidor: " + f.error); }
        else
        {
            var outdated = new List<string>();
            foreach (Entry e in f.man.files)
            {
                if (!ValidName(e.name) || string.IsNullOrEmpty(e.sha256)) continue;
                string local = Path.Combine(ModsDir, e.name);
                if (!File.Exists(local)) outdated.Add(e.name + " (falta)");
                else if (Sha(File.ReadAllBytes(local)) != e.sha256.ToLowerInvariant()) outdated.Add(e.name);
            }
            string what = IsSource ? "diferentes do site (publicar?)" : "por atualizar";
            line("4 servidor: manifest ok em " + f.ms.ToString("0") + " ms, pack de " + f.man.updated + ", " + f.man.files.Length + " mods" + (outdated.Count > 0 ? "; " + what + ": " + string.Join(", ", outdated.ToArray()) : ", tudo igual"));
        }

        // 5. optional live reload tests
        if (mode == "reload" || mode == "falha")
        {
            string path = Path.Combine(ModsDir, fileName);
            if (!File.Exists(path)) { problems.Add("teste " + mode); line("5 nao existe mods\\" + fileName); }
            else if (mode == "reload")
            {
                var r = new Result();
                yield return ReloadSafe(fileName, r, IsSource);
                if (!r.ok) problems.Add("recarga");
                line("5 recarga: " + r.label + (r.ok ? " recarregado em " + r.seconds.ToString("0.0") + " s" : ""));
            }
            else
            {
                if (!IsRunning(Find(fileName))) { problems.Add("teste falha"); line("5 teste de falha: " + fileName + " nao esta a correr, nada para proteger"); }
                else
                {
                    Remember(fileName);
                    byte[] real = File.ReadAllBytes(path);
                    DateTime realTime = File.GetLastWriteTimeUtc(path);
                    byte[] broken = null;
                    try { broken = MakeBroken(real); } catch (Exception e) { line("5 nao consegui criar a versao partida: " + e.Message); }
                    if (broken == null) problems.Add("teste falha");
                    else
                    {
                        File.WriteAllBytes(path, broken);
                        var r = new Result();
                        yield return ReloadSafe(fileName, r, false);
                        // whatever happened, the real file goes back on disk
                        try { File.WriteAllBytes(path, real); File.SetLastWriteTimeUtc(path, realTime); } catch { }
                        if (IsSource) NoteStamp(fileName);
                        bool pass = !r.ok && r.rolledBack && IsRunning(Find(fileName));
                        if (!pass) problems.Add("teste falha");
                        line("5 teste de falha: " + (pass ? "OK, a versao partida falhou e voltou a anterior em " + r.seconds.ToString("0.0") + " s: " : "FALHOU: ") + r.label);
                    }
                }
            }
        }

        string result = problems.Count == 0 ? "tudo OK" : "PROBLEMAS: " + string.Join(", ", problems.ToArray());
        line("resultado: " + result);
        status = "teste: " + result;
        Notify("MOD SYNC TESTE", (problems.Count == 0 ? "Tudo OK" : result) + ". Detalhes na consola (F10).", problems.Count > 0, 10f);
        busy = false;
    }

    // a copy of the mod with one extra .cs file that cannot compile
    static byte[] MakeBroken(byte[] rmod)
    {
        using (var ms = new MemoryStream())
        {
            ms.Write(rmod, 0, rmod.Length);
            using (var z = new ZipArchive(ms, ZipArchiveMode.Update, true))
            {
                ZipArchiveEntry e = z.CreateEntry("ModSyncTesteFalha.cs");
                using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false)))
                    w.Write("// Mod Sync: versao partida de proposito para testar a reposicao\npublic class ModSyncTesteFalha { void X() { isto nao compila } }\n");
            }
            return ms.ToArray();
        }
    }

    static string RmodArg(string[] args, int i, string fallback)
    {
        if (args.Length <= i) return fallback;
        return args[i].EndsWith(".rmod", StringComparison.OrdinalIgnoreCase) ? args[i] : args[i] + ".rmod";
    }

    [ConsoleCommand("modsync", "Mod Sync: modsync (verificar agora) | status | reload <ficheiro.rmod> | teste [reload|falha] [ficheiro.rmod]")]
    public static string Cmd(string[] args)
    {
        if (args.Length > 0 && args[0] == "status")
        {
            var rej = RejectedList();
            return "v" + Version + ", verifica a cada " + checkEvery + " s: " + status
                + (compilerDirFixes > 0 ? " | pasta HCompiler recriada " + compilerDirFixes + "x" : "")
                + (rej.Count > 0 ? " | falhadas nesta sessao: " + string.Join(", ", rej.ToArray()) : "");
        }
        if (instance == null) return "Mod Sync nao esta carregado";
        if (busy) return "ocupado: " + status;
        if (args.Length > 0 && (args[0] == "teste" || args[0] == "test"))
        {
            string mode = args.Length > 1 ? args[1] : "";
            if (mode != "" && mode != "reload" && mode != "falha") return "uso: modsync teste [reload|falha] [ficheiro.rmod]";
            string f = RmodArg(args, 2, TestMod);
            if (string.Equals(f, SelfFile, StringComparison.OrdinalIgnoreCase) && mode != "") return "o teste de recarga nao pode ser feito ao proprio Mod Sync";
            instance.StartCoroutine(instance.SelfTest(mode, f));
            return "a testar" + (mode != "" ? " (" + mode + " " + f + ")" : "") + "... resultado nas linhas TESTE";
        }
        if (args.Length > 1 && args[0] == "reload")
        {
            string f = RmodArg(args, 1, "");
            if (!File.Exists(Path.Combine(ModsDir, f))) return "nao existe mods\\" + f;
            instance.StartCoroutine(instance.ManualReload(f));
            return "a recarregar " + f + "...";
        }
        if (IsSource) return "PC de origem: os mods sao recarregados sozinhos quando o ficheiro muda";
        instance.StartCoroutine(instance.Check());
        return "a verificar atualizacoes... (escreve 'modsync status' daqui a uns segundos)";
    }

    IEnumerator ManualReload(string f)
    {
        busy = true;
        yield return Apply(new List<string> { f }, new List<string>(), new List<string>(), false);
        busy = false;
    }
}

public class ModSyncSelfReloader : MonoBehaviour
{
    IEnumerator Start()
    {
        yield return new WaitForSecondsRealtime(0.5f);
        var r = new ModSync.Result();
        yield return ModSync.ReloadSafe(ModSync.SelfFile, r, ModSync.IsSource);
        if (!r.ok)
        {
            Debug.Log("[ModSync] o proprio Mod Sync: " + r.label);
            if (r.rolledBack) ModSync.MarkSelfRolledBack();
        }
        yield return new WaitForSecondsRealtime(120f);
        Destroy(gameObject);
    }
}

// Small JSON reader: objects -> Dictionary<string, object>, arrays -> List<object>, numbers -> double.
public class MiniJson
{
    readonly string s;
    int i;
    public MiniJson(string text) { s = text ?? ""; }

    public object Parse()
    {
        object v = Value();
        Ws();
        if (i < s.Length) throw new FormatException("texto a mais na posicao " + i);
        return v;
    }

    void Ws() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

    char Peek() { if (i >= s.Length) throw new FormatException("o JSON acaba a meio"); return s[i]; }

    void Expect(char c) { if (Peek() != c) throw new FormatException("esperava '" + c + "' na posicao " + i); i++; }

    bool Word(string w)
    {
        if (i + w.Length > s.Length || string.CompareOrdinal(s, i, w, 0, w.Length) != 0) return false;
        i += w.Length;
        return true;
    }

    object Value()
    {
        Ws();
        char c = Peek();
        if (c == '{')
        {
            i++;
            var d = new Dictionary<string, object>();
            Ws();
            if (Peek() == '}') { i++; return d; }
            while (true)
            {
                Ws();
                string k = Str();
                Ws();
                Expect(':');
                d[k] = Value();
                Ws();
                if (Peek() == ',') { i++; continue; }
                Expect('}');
                return d;
            }
        }
        if (c == '[')
        {
            i++;
            var l = new List<object>();
            Ws();
            if (Peek() == ']') { i++; return l; }
            while (true)
            {
                l.Add(Value());
                Ws();
                if (Peek() == ',') { i++; continue; }
                Expect(']');
                return l;
            }
        }
        if (c == '"') return Str();
        if (Word("true")) return true;
        if (Word("false")) return false;
        if (Word("null")) return null;
        int st = i;
        while (i < s.Length && "+-.eE0123456789".IndexOf(s[i]) >= 0) i++;
        if (st == i) throw new FormatException("caractere inesperado '" + c + "' na posicao " + i);
        return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    string Str()
    {
        Expect('"');
        var sb = new StringBuilder();
        while (true)
        {
            char c = Peek();
            i++;
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            char e = Peek();
            i++;
            switch (e)
            {
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 > s.Length) throw new FormatException("escape u incompleto");
                    sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                    i += 4;
                    break;
                default: sb.Append(e); break;
            }
        }
    }
}

// ---------- BestCode UI kit ----------
// Drawn with Raft's own font (ChineseRocks) and sprites (parchment plate, item slot, key chip), on
// one shared overlay canvas. Every BestCode mod carries its own copy of this kit in its own
// namespace; mods only share the canvas object and the toast column (plain Unity components and
// RectTransforms), so one mod reloading never breaks another. Source: raft-mods/src/bcui_kit.cs.txt
namespace BCUI_MS
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
