using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace Mod
{
    public class Mod
    {
        public static void Main()
        {
            ModAPI.Register(
                new Modification()
                {
                    OriginalItem = ModAPI.FindSpawnable("Rod"),
                    NameOverride = "Multiplayer [H=Host  J=Join]",
                    DescriptionOverride = "H=Host  J=Join (code+Enter)  Spawn anything = shared  Esc=Leave",
                    CategoryOverride = ModAPI.FindCategory("Misc"),
                    ThumbnailOverride = ModAPI.LoadSprite("icon.png"),
                    AfterSpawn = (Instance) => { UnityEngine.Object.Destroy(Instance); }
                }
            );

            // If a previous build's manager is still alive (hot reload / repeat
            // Recompile), remove it first so we don't stack duplicates.
            GameObject old = GameObject.Find("MPManager");
            if (old != null) UnityEngine.Object.DestroyImmediate(old);

            GameObject manager = new GameObject("MPManager");
            manager.AddComponent<MPBehaviour>();
            UnityEngine.Object.DontDestroyOnLoad(manager);

            ModAPI.Notify("Multiplayer Mod Loaded! (v0.4 - menu button, self-test, scene share)");
        }
    }

    // ---- wire formats (JsonUtility-friendly) -------------------------------
    [Serializable] public class ObjMsg { public string nid; public string item; public float x; public float y; public float rot; }
    [Serializable] public class SyncRequest
    {
        public string room; public string id; public string name;
        public float x; public float y;
        public ObjMsg[] objs;
        public bool echo;   // solo self-test: ask relay to mirror me back
    }
    [Serializable] public class CursorMsg { public string id; public string name; public float x; public float y; public float age; }
    [Serializable] public class SyncResponse { public double now; public CursorMsg[] cursors; public ObjMsg[] objs; }

    // ---- runtime tracking --------------------------------------------------
    public class RemoteCursor
    {
        public GameObject go; public TextMesh nameTag; public Vector3 target; public float lastSeen;
    }

    // a networked object: either one WE own (we spawned, we stream it) or a
    // mirror of someone else's (frozen, follows their streamed transform)
    public class NetObj
    {
        public string nid;
        public string item;
        public GameObject go;
        public bool owned;
        public Vector3 targetPos;
        public float targetRot;
        public float lastSeen;
    }

    public class MPBehaviour : MonoBehaviour
    {
        // ---------------------------------------------------------------
        //  Default relay URL (no trailing slash). This is now ALSO editable
        //  in-game: open the panel (M), paste a new URL, and Host/Join — no
        //  rebuild or re-upload needed when your tunnel address changes.
        //  For a viral mod, host the relay somewhere permanent (see
        //  DEPLOY_RELAY.md) so this default just works for everyone.
        //  Both players must be on the SAME url + SAME room code.
        // ---------------------------------------------------------------
        private const string DEFAULT_RELAY_URL = "https://song-jet-mold-bet.trycloudflare.com";
        private string relayUrl = DEFAULT_RELAY_URL;

        // optional quick-spawn hotkeys (handy for testing; normal catalog
        // spawns are synced automatically too)
        private static readonly string[] SPAWN_ITEMS = { "Human", "Rod", "Watermelon", "Crate" };

        private const string CHARS = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        private const float SYNC_INTERVAL = 0.08f;   // ~12 Hz
        private const float OBJ_TIMEOUT = 3f;        // drop a mirror after this silence
        private const float CURSOR_TIMEOUT = 3f;

        // connection
        private bool connected = false, hosting = false;
        private bool soloTest = false;   // echo myself back for one-player testing
        private string room = "", myId = "", myName = "Player";
        private bool typing = false; private string typed = "";

        // connection state machine + clickable panel
        private enum ConnState { Disconnected, Connecting, Connected, Failed }
        private ConnState state = ConnState.Disconnected;
        private string lastError = "";
        private bool panelOpen = true;
        private string joinField = "";
        private string urlField = DEFAULT_RELAY_URL;
        private GUIStyle _titleStyle, _statusStyle, _codeStyle, _boxStyle, _btnStyle, _fieldStyle;
        private bool _stylesReady = false;

        // main-menu entry point ("multiplayer" button + join popup)
        private bool menuPopupOpen = false;
        private string menuCodeField = "";
        private string pendingRoom = "";
        private bool pendingHost = false, pendingJoin = false, pendingSolo = false;
        private bool wasOnMenu = true;

        // cursors
        private readonly Dictionary<string, RemoteCursor> cursors = new Dictionary<string, RemoteCursor>();
        private int friendCount = 0;

        // connection feedback
        private readonly HashSet<string> knownFriends = new HashSet<string>();
        private readonly Dictionary<string, string> friendNames = new Dictionary<string, string>();
        private int netFail = 0;
        private bool everConnected = false;

        // objects
        private readonly Dictionary<string, NetObj> objects = new Dictionary<string, NetObj>(); // by nid
        private readonly HashSet<int> trackedInstances = new HashSet<int>(); // go.GetInstanceID() we've already handled
        private int objCounter = 0;
        private bool suppressRegister = false; // true while WE instantiate a mirror
        private bool spawnDebugShown = false;

        // ui
        private GameObject codeDisplay; private TextMesh codeText;
        private GameObject statusDisplay; private TextMesh statusText;
        private Coroutine syncRoutine;

        // ===============================================================
        void Start()
        {
            myId = "p_" + UnityEngine.Random.Range(100000, 999999).ToString();
            myName = RandomName();
            CreateWorldUI();
            HookSpawnEvent();
        }

        string RandomName()
        {
            string[] a = { "Red", "Blue", "Mad", "Calm", "Swift", "Lazy", "Bold", "Tiny" };
            string[] b = { "Fox", "Wolf", "Bear", "Hawk", "Cat", "Owl", "Crab", "Moth" };
            return a[UnityEngine.Random.Range(0, a.Length)] + b[UnityEngine.Random.Range(0, b.Length)];
        }

        // ----- spawn hook: catch every catalog spawn -------------------
        void HookSpawnEvent()
        {
            try
            {
                // ModAPI.OnItemSpawned fires when the user spawns something.
                // Signature unknown across versions, so we take args as objects
                // and reflect to find the instance + spawnable name.
                ModAPI.OnItemSpawned += (sender, e) => OnAnySpawn(sender, e);
            }
            catch (Exception ex)
            {
                ModAPI.Notify("Spawn hook unavailable: " + ex.Message + " (hotkeys still work)");
            }
        }

        void OnAnySpawn(object sender, object e)
        {
            if (!connected || suppressRegister) return;
            try
            {
                GameObject go = null; string item = null;
                ExtractSpawn(sender, ref go, ref item);
                ExtractSpawn(e, ref go, ref item);

                if (!spawnDebugShown)
                {
                    spawnDebugShown = true;
                    ModAPI.Notify("Spawn detected: " + (item ?? "?") + (go != null ? " ok" : " (no obj)"));
                }
                if (go == null) return;
                if (string.IsNullOrEmpty(item)) item = CleanName(go.name);
                RegisterOwned(go, item);
            }
            catch { }
        }

        // pull a GameObject and a spawnable name out of an arbitrary object
        void ExtractSpawn(object src, ref GameObject go, ref string item)
        {
            if (src == null) return;
            if (src is GameObject) { if (go == null) go = (GameObject)src; return; }
            if (src is Component)  { if (go == null) go = ((Component)src).gameObject; }

            Type t = src.GetType();
            foreach (MemberInfo mi in t.GetMembers(BindingFlags.Public | BindingFlags.Instance))
            {
                object val = null;
                try
                {
                    if (mi is PropertyInfo && ((PropertyInfo)mi).GetIndexParameters().Length == 0)
                        val = ((PropertyInfo)mi).GetValue(src, null);
                    else if (mi is FieldInfo)
                        val = ((FieldInfo)mi).GetValue(src);
                }
                catch { }
                if (val == null) continue;

                if (go == null && val is GameObject) go = (GameObject)val;
                else if (go == null && val is Component) go = ((Component)val).gameObject;
                else if (item == null)
                {
                    string tn = val.GetType().Name;
                    if (tn.IndexOf("Spawnable", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try
                        {
                            PropertyInfo np = val.GetType().GetProperty("name");
                            if (np != null) item = np.GetValue(val, null) as string;
                        }
                        catch { }
                    }
                }
            }
        }

        string CleanName(string n)
        {
            if (string.IsNullOrEmpty(n)) return n;
            int i = n.IndexOf("(Clone)");
            if (i >= 0) n = n.Substring(0, i);
            return n.Trim();
        }

        // ===============================================================
        void Update()
        {
            HandleInput();
            UpdateWorldUI();
            UpdateRemoteCursors();
            UpdateMirrors();
            HandleSceneFlow();
        }

        // detect main menu vs. gameplay so the menu button only shows on the
        // menu and the in-game panel only shows in a sandbox
        bool OnMainMenu()
        {
            try
            {
                Scene sc = SceneManager.GetActiveScene();
                string n = (sc.name ?? "").ToLower();
                if (n.Contains("menu")) return true;
                if (n.Contains("sandbox") || n.Contains("game") || n.Contains("level") || n.Contains("play")) return false;
                return sc.buildIndex == 0;
            }
            catch { return Camera.main == null; }
        }

        // remember the sandbox scene (for one-click launch) and auto-connect
        // once we drop into gameplay with a pending room
        void HandleSceneFlow()
        {
            bool menu = OnMainMenu();
            if (!menu)
            {
                try
                {
                    string s = SceneManager.GetActiveScene().name;
                    if (!string.IsNullOrEmpty(s) && PlayerPrefs.GetString("ppg_scene", "") != s)
                        PlayerPrefs.SetString("ppg_scene", s);
                }
                catch { }
            }
            if (wasOnMenu && !menu && (pendingJoin || pendingSolo))
            {
                StartCoroutine(AutoConnectWhenReady());
            }
            wasOnMenu = menu;
        }

        IEnumerator AutoConnectWhenReady()
        {
            float t = 0f;
            while (Camera.main == null && t < 8f) { t += Time.deltaTime; yield return null; }
            yield return new WaitForSeconds(0.4f);
            bool solo = pendingSolo;
            pendingJoin = false; pendingSolo = false;
            if (solo) StartSoloTest();
            else if (pendingHost) Host();
            else Join(pendingRoom);
        }

        // ----- from the main-menu popup: arm a connection, then enter a map -
        void MenuJoin(string code)
        {
            pendingRoom = code.ToUpper(); pendingHost = false; pendingJoin = true;
            menuPopupOpen = false;
            LaunchIntoSandbox("Joining room " + pendingRoom + "…");
        }
        void MenuHost()
        {
            pendingHost = true; pendingJoin = true;
            menuPopupOpen = false;
            LaunchIntoSandbox("Starting your multiplayer room…");
        }
        void LaunchIntoSandbox(string msg)
        {
            relayUrl = (urlField ?? "").Trim();
            ModAPI.Notify(msg);
            string scene = "";
            try { scene = PlayerPrefs.GetString("ppg_scene", ""); } catch { }
            if (!string.IsNullOrEmpty(scene))
            {
                try { SceneManager.LoadScene(scene); return; }   // one-click after first play
                catch { }
            }
            ModAPI.Notify("Now press PLAY and load a sandbox — you'll drop in automatically.");
        }

        void HandleInput()
        {
            if (Input.GetKeyDown(KeyCode.M)) panelOpen = !panelOpen;
            // when the panel is open it owns the keyboard (so typing in the
            // room-code / URL fields never triggers game hotkeys)
            if (panelOpen) return;

            if (Input.GetKeyDown(KeyCode.H) && !connected && !typing) Host();
            if (Input.GetKeyDown(KeyCode.J) && !connected && !typing) { typing = true; typed = ""; ModAPI.Notify("Type room code, then Enter..."); }
            if (typing) TypeCode();
            if (Input.GetKeyDown(KeyCode.Escape)) { if (typing) { typing = false; typed = ""; } else if (connected) Disconnect(); }
            if (Input.GetKeyDown(KeyCode.C) && connected) ModAPI.Notify("Room Code: " + room);
            if (Input.GetKeyDown(KeyCode.Tab)) ModAPI.Notify(connected ? ("Room " + room + " | online: " + friendCount + " | objs: " + objects.Count) : "H = Host   J = Join");

            if (connected && !typing)
                for (int i = 0; i < SPAWN_ITEMS.Length && i < 4; i++)
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i)) HotkeySpawn(SPAWN_ITEMS[i]);
        }

        void TypeCode()
        {
            for (int i = 0; i < 26; i++) if (Input.GetKeyDown((KeyCode)(97 + i)) && typed.Length < 5) typed += (char)(65 + i);
            for (int i = 2; i <= 9; i++) if (Input.GetKeyDown((KeyCode)(48 + i)) && typed.Length < 5) typed += i;
            if (Input.GetKeyDown(KeyCode.Backspace) && typed.Length > 0) typed = typed.Substring(0, typed.Length - 1);
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                if (typed.Length == 5) { typing = false; Join(typed); }
                else ModAPI.Notify("Code must be 5 characters (" + typed.Length + "/5)");
            }
        }

        // ===============================================================
        bool RelayConfigured()
        {
            string u = (relayUrl ?? "").Trim();
            if (string.IsNullOrEmpty(u) || u.Contains("PASTE_YOUR_RELAY_URL") || !u.StartsWith("http"))
            {
                lastError = "No valid relay URL. Paste one in the panel (press M), then Host/Join.";
                state = ConnState.Failed; panelOpen = true;
                ModAPI.Notify("Set a relay URL first (press M)");
                return false;
            }
            return true;
        }

        void Host()
        {
            relayUrl = (urlField ?? "").Trim();
            if (!RelayConfigured()) return;
            room = ""; for (int i = 0; i < 5; i++) room += CHARS[UnityEngine.Random.Range(0, CHARS.Length)];
            state = ConnState.Connecting; lastError = "";
            StartSession(true);
            ModAPI.Notify("Room created: " + room + "  (share this code!)");
        }
        void Join(string code)
        {
            relayUrl = (urlField ?? "").Trim();
            if (!RelayConfigured()) return;
            room = code.ToUpper();
            state = ConnState.Connecting; lastError = "";
            StartSession(false);
            ModAPI.Notify("Joining room: " + room);
        }

        // Solo self-test: host a private room with echo on, so the relay mirrors
        // your own cursor + spawns back beside you. If a "(echo)" cursor appears
        // and your spawns are duplicated ~3m to the right, the whole pipeline
        // (mod -> relay -> mod -> render) works.
        void StartSoloTest()
        {
            relayUrl = (urlField ?? "").Trim();
            if (!RelayConfigured()) return;
            soloTest = true;
            room = "SOLO" + UnityEngine.Random.Range(1, 9);
            state = ConnState.Connecting; lastError = "";
            StartSession(true);
            ModAPI.Notify("Solo self-test: watch for a '(echo)' cursor + duplicated spawns on the right.");
        }

        void StartSession(bool asHost)
        {
            hosting = asHost; connected = true;
            everConnected = false; netFail = 0;
            if (syncRoutine != null) StopCoroutine(syncRoutine);
            syncRoutine = StartCoroutine(SyncLoop());
        }

        void Disconnect()
        {
            connected = false;
            if (syncRoutine != null) { StopCoroutine(syncRoutine); syncRoutine = null; }
            foreach (var c in cursors.Values) if (c.go != null) UnityEngine.Object.Destroy(c.go);
            cursors.Clear();
            // destroy mirrors (not our own objects)
            var rm = new List<string>();
            foreach (var kv in objects) if (!kv.Value.owned) { if (kv.Value.go != null) UnityEngine.Object.Destroy(kv.Value.go); rm.Add(kv.Key); }
            foreach (var k in rm) objects.Remove(k);
            friendCount = 0; room = "";
            knownFriends.Clear(); netFail = 0; everConnected = false;
            state = ConnState.Disconnected; lastError = ""; panelOpen = true;
            soloTest = false;
            ModAPI.Notify("Disconnected");
        }

        // ===============================================================
        //  network loop: send my cursor + my owned objects, receive theirs
        // ===============================================================
        IEnumerator SyncLoop()
        {
            while (connected)
            {
                Vector3 m = Vector3.zero;
                Camera cam = Camera.main;
                if (cam != null) { m = cam.ScreenToWorldPoint(Input.mousePosition); m.z = 0f; }

                SyncRequest reqObj = new SyncRequest
                {
                    room = room, id = myId, name = myName,
                    x = m.x, y = m.y, objs = BuildOwnedSnapshot(),
                    echo = soloTest
                };
                string json = JsonUtility.ToJson(reqObj);

                UnityWebRequest req = new UnityWebRequest(relayUrl.TrimEnd('/') + "/sync", "POST");
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 8;
                yield return req.SendWebRequest();

                if (req.error == null && req.downloadHandler != null)
                {
                    if (netFail >= 3) ModAPI.Notify("Relay reconnected");
                    netFail = 0; lastError = "";
                    state = ConnState.Connected;
                    if (!everConnected) { everConnected = true; panelOpen = false; ModAPI.Notify("Connected! Room " + room + " - waiting for friend..."); }
                    HandleResponse(req.downloadHandler.text);
                }
                else
                {
                    netFail++;
                    if (netFail == 3)
                    {
                        lastError = "Can't reach the relay (" + (req.error == null ? "timeout" : req.error) + "). Check the URL is correct and the server is running.";
                        state = ConnState.Failed; panelOpen = true;
                        ModAPI.Notify("CANNOT REACH RELAY - " + (req.error == null ? "timeout" : req.error));
                    }
                }
                req.Dispose();

                yield return new WaitForSeconds(SYNC_INTERVAL);
            }
        }

        ObjMsg[] BuildOwnedSnapshot()
        {
            List<ObjMsg> list = new List<ObjMsg>();
            List<string> dead = null;
            foreach (var kv in objects)
            {
                NetObj o = kv.Value;
                if (!o.owned) continue;
                if (o.go == null) { (dead ?? (dead = new List<string>())).Add(kv.Key); continue; }
                Vector3 p = o.go.transform.position;
                list.Add(new ObjMsg { nid = o.nid, item = o.item, x = p.x, y = p.y, rot = o.go.transform.eulerAngles.z });
            }
            if (dead != null) foreach (var k in dead) objects.Remove(k);
            return list.ToArray();
        }

        void HandleResponse(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            SyncResponse resp;
            try { resp = JsonUtility.FromJson<SyncResponse>(text); } catch { return; }
            if (resp == null) return;

            // cursors + join/leave detection
            HashSet<string> current = new HashSet<string>();
            if (resp.cursors != null)
                foreach (var c in resp.cursors)
                {
                    if (c == null || string.IsNullOrEmpty(c.id)) continue;
                    current.Add(c.id);
                    friendNames[c.id] = string.IsNullOrEmpty(c.name) ? "Friend" : c.name;
                    GetOrMakeCursor(c.id, c.name).target = new Vector3(c.x, c.y, 0f);
                    cursors[c.id].lastSeen = Time.time;
                }
            friendCount = current.Count;

            // announce new arrivals
            foreach (string id in current)
                if (!knownFriends.Contains(id))
                    ModAPI.Notify(">>> " + friendNames[id] + " joined the room! <<<");
            // announce departures
            List<string> left = new List<string>();
            foreach (string id in knownFriends)
                if (!current.Contains(id)) left.Add(id);
            foreach (string id in left)
            {
                string nm = friendNames.ContainsKey(id) ? friendNames[id] : "Friend";
                ModAPI.Notify(nm + " left the room");
            }
            knownFriends.Clear();
            foreach (string id in current) knownFriends.Add(id);

            // remote objects -> spawn mirror if new, else update target
            if (resp.objs != null)
                foreach (var o in resp.objs)
                {
                    if (o == null || string.IsNullOrEmpty(o.nid)) continue;
                    NetObj n;
                    if (objects.TryGetValue(o.nid, out n))
                    {
                        if (n.owned) continue; // shouldn't happen (relay excludes own)
                        n.targetPos = new Vector3(o.x, o.y, 0f);
                        n.targetRot = o.rot;
                        n.lastSeen = Time.time;
                    }
                    else
                    {
                        GameObject go = SpawnMirror(o.item, new Vector3(o.x, o.y, 0f), o.rot);
                        if (go == null) continue;
                        objects[o.nid] = new NetObj { nid = o.nid, item = o.item, go = go, owned = false,
                            targetPos = new Vector3(o.x, o.y, 0f), targetRot = o.rot, lastSeen = Time.time };
                    }
                }
        }

        // ===============================================================
        //  spawning
        // ===============================================================
        void HotkeySpawn(string item)
        {
            Camera cam = Camera.main; if (cam == null) return;
            Vector3 m = cam.ScreenToWorldPoint(Input.mousePosition); m.z = 0f;
            GameObject go = RawSpawn(item, m, 0f);
            if (go != null) { RegisterOwned(go, item); ModAPI.Notify("Spawned " + item); }
        }

        void RegisterOwned(GameObject go, string item)
        {
            if (go == null) return;
            int iid = go.GetInstanceID();
            if (trackedInstances.Contains(iid)) return;
            trackedInstances.Add(iid);
            string nid = myId + "_" + (++objCounter);
            objects[nid] = new NetObj { nid = nid, item = item, go = go, owned = true, lastSeen = Time.time };
        }

        // "Load everything the host had": scan the current scene for physics
        // objects and register them all as owned, so they stream to anyone who
        // joins — even things you built BEFORE hosting. Best-effort: an object
        // only re-creates on the other side if its name matches a catalog item.
        void ShareWholeScene()
        {
            int shared = 0, skipped = 0;
            try
            {
                Rigidbody2D[] bodies = UnityEngine.Object.FindObjectsOfType<Rigidbody2D>();
                HashSet<int> seenRoots = new HashSet<int>();
                foreach (Rigidbody2D rb in bodies)
                {
                    if (rb == null) continue;
                    GameObject go = rb.transform.root.gameObject;
                    int iid = go.GetInstanceID();
                    if (seenRoots.Contains(iid)) continue;
                    seenRoots.Add(iid);
                    if (trackedInstances.Contains(iid)) continue;   // already networked

                    string item = CleanName(go.name);
                    if (string.IsNullOrEmpty(item)) { skipped++; continue; }
                    // only share things the catalog can recreate on the other side
                    if (ModAPI.FindSpawnable(item) == null) { skipped++; continue; }
                    RegisterOwned(go, item);
                    shared++;
                }
            }
            catch (Exception e) { ModAPI.Notify("Scene share error: " + e.Message); return; }
            ModAPI.Notify("Now sharing " + shared + " objects from your scene" + (skipped > 0 ? " (" + skipped + " couldn't be matched)" : ""));
        }

        // spawn a frozen mirror of someone else's object
        GameObject SpawnMirror(string item, Vector3 pos, float rot)
        {
            suppressRegister = true;
            GameObject go = RawSpawn(item, pos, rot);
            suppressRegister = false;
            if (go == null) return null;
            trackedInstances.Add(go.GetInstanceID());
            FreezePhysics(go);
            return go;
        }

        void FreezePhysics(GameObject go)
        {
            try
            {
                Rigidbody2D[] bodies = go.GetComponentsInChildren<Rigidbody2D>(true);
                foreach (Rigidbody2D rb in bodies) rb.simulated = false;
            }
            catch { }
        }

        // instantiate a catalog item's prefab at a world position
        GameObject RawSpawn(string name, Vector3 pos, float rot)
        {
            try
            {
                object asset = ModAPI.FindSpawnable(name);
                if (asset == null) { ModAPI.Notify("Item not found: " + name); return null; }
                GameObject prefab = ExtractPrefab(asset);
                if (prefab == null) { ModAPI.Notify("No prefab for: " + name); return null; }
                GameObject go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.Euler(0f, 0f, rot));
                go.SetActive(true);
                return go;
            }
            catch (Exception e) { ModAPI.Notify("Spawn failed: " + e.Message); return null; }
        }

        GameObject ExtractPrefab(object asset)
        {
            string[] names = { "Prefab", "prefab", "Entity", "entity", "GameObject", "gameObject" };
            Type t = asset.GetType();
            foreach (string n in names)
            {
                PropertyInfo p = t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && typeof(GameObject).IsAssignableFrom(p.PropertyType)) return p.GetValue(asset, null) as GameObject;
                FieldInfo f = t.GetField(n, BindingFlags.Public | BindingFlags.Instance);
                if (f != null && typeof(GameObject).IsAssignableFrom(f.FieldType)) return f.GetValue(asset) as GameObject;
            }
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (typeof(GameObject).IsAssignableFrom(f.FieldType)) return f.GetValue(asset) as GameObject;
            return null;
        }

        // move mirrors toward their streamed transform; GC stale ones
        void UpdateMirrors()
        {
            List<string> drop = null;
            foreach (var kv in objects)
            {
                NetObj o = kv.Value;
                if (o.owned) { if (o.go == null) (drop ?? (drop = new List<string>())).Add(kv.Key); continue; }
                if (o.go == null || Time.time - o.lastSeen > OBJ_TIMEOUT)
                {
                    if (o.go != null) UnityEngine.Object.Destroy(o.go);
                    (drop ?? (drop = new List<string>())).Add(kv.Key);
                    continue;
                }
                o.go.transform.position = Vector3.Lerp(o.go.transform.position, o.targetPos, Time.deltaTime * 14f);
                float z = Mathf.LerpAngle(o.go.transform.eulerAngles.z, o.targetRot, Time.deltaTime * 14f);
                o.go.transform.rotation = Quaternion.Euler(0f, 0f, z);
            }
            if (drop != null) foreach (var k in drop) { trackedInstances.Remove(0); objects.Remove(k); }
        }

        // ===============================================================
        //  remote cursors
        // ===============================================================
        RemoteCursor GetOrMakeCursor(string id, string name)
        {
            RemoteCursor rc;
            if (cursors.TryGetValue(id, out rc)) { if (rc.go != null) return rc; }
            GameObject go = new GameObject("RemoteCursor_" + id);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = BuildCursorSprite(); sr.sortingOrder = 9999;
            UnityEngine.Object.DontDestroyOnLoad(go);
            GameObject tagObj = new GameObject("NameTag");
            tagObj.transform.SetParent(go.transform); tagObj.transform.localPosition = new Vector3(0f, 0.5f, 0f);
            TextMesh tm = tagObj.AddComponent<TextMesh>();
            tm.text = string.IsNullOrEmpty(name) ? "Friend" : name;
            tm.fontSize = 100; tm.characterSize = 0.012f; tm.anchor = TextAnchor.LowerCenter;
            tm.alignment = TextAlignment.Center; tm.color = Color.cyan; tm.fontStyle = FontStyle.Bold;
            tagObj.GetComponent<MeshRenderer>().sortingOrder = 10000;
            rc = new RemoteCursor { go = go, nameTag = tm, target = Vector3.zero, lastSeen = Time.time };
            cursors[id] = rc; return rc;
        }

        Sprite BuildCursorSprite()
        {
            Texture2D tex = new Texture2D(32, 32);
            Color[] pix = new Color[1024];
            for (int i = 0; i < 1024; i++)
            {
                float dx = (i % 32) - 16f, dy = (i / 32) - 16f, dist = Mathf.Sqrt(dx * dx + dy * dy);
                if (dist < 14f) pix[i] = new Color(0f, 1f, 1f, 0.9f);
                else if (dist < 16f) pix[i] = Color.white;
                else pix[i] = new Color(0f, 0f, 0f, 0f);
            }
            tex.SetPixels(pix); tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 32, 32), new Vector2(0.5f, 0.5f), 32f);
        }

        void UpdateRemoteCursors()
        {
            Camera cam = Camera.main;
            float scale = cam != null ? cam.orthographicSize / 5f : 1f;
            List<string> drop = null;
            foreach (var kv in cursors)
            {
                RemoteCursor rc = kv.Value;
                if (rc.go == null) { (drop ?? (drop = new List<string>())).Add(kv.Key); continue; }
                if (Time.time - rc.lastSeen > CURSOR_TIMEOUT)
                {
                    UnityEngine.Object.Destroy(rc.go);
                    (drop ?? (drop = new List<string>())).Add(kv.Key); continue;
                }
                rc.go.transform.position = Vector3.Lerp(rc.go.transform.position, rc.target, Time.deltaTime * 12f);
                rc.go.transform.localScale = new Vector3(scale * 0.15f, scale * 0.15f, 1f);
            }
            if (drop != null) foreach (var k in drop) cursors.Remove(k);
        }

        // ===============================================================
        //  world UI
        // ===============================================================
        void CreateWorldUI()
        {
            codeDisplay = new GameObject("CodeDisplay");
            codeText = codeDisplay.AddComponent<TextMesh>();
            codeText.fontSize = 100; codeText.characterSize = 0.015f; codeText.anchor = TextAnchor.LowerLeft; codeText.color = Color.white;
            codeDisplay.GetComponent<MeshRenderer>().sortingOrder = 10001; codeDisplay.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(codeDisplay);

            statusDisplay = new GameObject("StatusDisplay");
            statusText = statusDisplay.AddComponent<TextMesh>();
            statusText.fontSize = 100; statusText.characterSize = 0.025f; statusText.anchor = TextAnchor.MiddleCenter;
            statusText.alignment = TextAlignment.Center; statusText.color = Color.white;
            statusDisplay.GetComponent<MeshRenderer>().sortingOrder = 10001; statusDisplay.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(statusDisplay);
        }

        void UpdateWorldUI()
        {
            Camera cam = Camera.main;
            if (cam == null) { if (codeDisplay != null) codeDisplay.SetActive(false); if (statusDisplay != null) statusDisplay.SetActive(false); return; }
            Vector3 camPos = cam.transform.position;
            float h = cam.orthographicSize, w = h * cam.aspect, scale = h / 5f;

            if (codeDisplay != null)
            {
                if (connected)
                {
                    codeDisplay.SetActive(true);
                    codeDisplay.transform.position = new Vector3(camPos.x - w + 0.2f * scale, camPos.y - h + 0.2f * scale, 0f);
                    codeDisplay.transform.localScale = new Vector3(scale, scale, 1f);
                    codeText.text = "Room: " + room + "\nOnline: " + friendCount + "   Objects: " + objects.Count;
                    codeText.color = friendCount > 0 ? Color.green : Color.yellow;
                }
                else codeDisplay.SetActive(false);
            }
            if (statusDisplay != null)
            {
                if (typing)
                {
                    statusDisplay.SetActive(true);
                    statusDisplay.transform.position = new Vector3(camPos.x, camPos.y, 0f);
                    statusDisplay.transform.localScale = new Vector3(scale, scale, 1f);
                    string disp = typed; for (int i = typed.Length; i < 5; i++) disp += "_";
                    statusText.text = "TYPE ROOM CODE\n\n[ " + disp + " ]\n\nENTER = Join   ESC = Cancel"; statusText.color = Color.white;
                }
                else if (!connected && !panelOpen)
                {
                    statusDisplay.SetActive(true);
                    statusDisplay.transform.position = new Vector3(camPos.x, camPos.y - h * 0.3f, 0f);
                    statusDisplay.transform.localScale = new Vector3(scale * 0.7f, scale * 0.7f, 1f);
                    statusText.text = "Press M for menu"; statusText.color = new Color(1f, 1f, 1f, 0.5f);
                }
                else statusDisplay.SetActive(false);
            }
        }

        // ===============================================================
        //  SCREEN UI  —  clickable IMGUI panel + always-on status pill
        // ===============================================================
        Texture2D SolidTex(Color c) { Texture2D t = new Texture2D(1, 1); t.SetPixel(0, 0, c); t.Apply(); return t; }

        GUIStyle Colored(Color c) { GUIStyle s = new GUIStyle(_statusStyle); s.normal.textColor = c; return s; }

        void EnsureStyles()
        {
            if (_stylesReady) return;

            _boxStyle = new GUIStyle(GUI.skin.box);
            _boxStyle.normal.background = SolidTex(new Color(0.07f, 0.08f, 0.10f, 0.95f));
            _boxStyle.border = new RectOffset(2, 2, 2, 2);
            _boxStyle.padding = new RectOffset(14, 14, 12, 12);

            _titleStyle = new GUIStyle(GUI.skin.label); _titleStyle.fontSize = 15; _titleStyle.fontStyle = FontStyle.Bold;
            _titleStyle.normal.textColor = new Color(0.55f, 0.85f, 1f);

            _statusStyle = new GUIStyle(GUI.skin.label); _statusStyle.fontSize = 12; _statusStyle.wordWrap = true;
            _statusStyle.normal.textColor = new Color(0.85f, 0.87f, 0.9f);

            _codeStyle = new GUIStyle(GUI.skin.label); _codeStyle.fontSize = 30; _codeStyle.fontStyle = FontStyle.Bold;
            _codeStyle.alignment = TextAnchor.MiddleCenter; _codeStyle.normal.textColor = Color.white;

            _btnStyle = new GUIStyle(GUI.skin.button); _btnStyle.fontSize = 13; _btnStyle.fontStyle = FontStyle.Bold;
            _btnStyle.padding = new RectOffset(8, 8, 7, 7);

            _fieldStyle = new GUIStyle(GUI.skin.textField); _fieldStyle.fontSize = 14; _fieldStyle.fontStyle = FontStyle.Bold;
            _fieldStyle.padding = new RectOffset(6, 6, 6, 6);

            _stylesReady = true;
        }

        void OnGUI()
        {
            EnsureStyles();

            // On the main menu, show ONLY our "multiplayer" entry + popup.
            if (OnMainMenu()) { DrawMenuEntry(); return; }

            // ----- status pill (always visible; click to toggle panel) -----
            Color pc; string ptxt;
            if (state == ConnState.Connected) { pc = new Color(0.3f, 0.9f, 0.45f); ptxt = "● Connected  ·  Room " + room + "  ·  " + (friendCount + 1) + " online"; }
            else if (state == ConnState.Connecting) { pc = new Color(1f, 0.8f, 0.2f); ptxt = "● Connecting…"; }
            else if (state == ConnState.Failed) { pc = new Color(1f, 0.4f, 0.4f); ptxt = "● Connection failed"; }
            else { pc = new Color(0.65f, 0.65f, 0.7f); ptxt = "● Multiplayer offline"; }

            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(10, 10, 300, 26), Texture2D.whiteTexture);
            GUI.color = old;
            GUIStyle pill = new GUIStyle(_statusStyle); pill.fontStyle = FontStyle.Bold; pill.padding = new RectOffset(9, 9, 5, 5);
            pill.normal.textColor = pc; pill.hover.textColor = Color.white;
            if (GUI.Button(new Rect(10, 10, 300, 26), ptxt, pill)) panelOpen = !panelOpen;

            if (!panelOpen) return;

            // ----- main panel -----
            GUILayout.BeginArea(new Rect(10, 44, 310, 430), _boxStyle);
            GUILayout.Label("PEOPLE PLAYGROUND MULTIPLAYER", _titleStyle);
            GUILayout.Space(6);

            if (state == ConnState.Connected)
            {
                GUILayout.Label("Room code — share it with a friend:", _statusStyle);
                GUILayout.Label(room, _codeStyle);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Copy code", _btnStyle)) { GUIUtility.systemCopyBuffer = room; ModAPI.Notify("Room code copied!"); }
                if (GUILayout.Button("Leave", _btnStyle)) Disconnect();
                GUILayout.EndHorizontal();
                GUILayout.Space(6);
                if (GUILayout.Button("Share my whole scene →", _btnStyle)) ShareWholeScene();
                if (soloTest) GUILayout.Label("SOLO SELF-TEST active (echoing you back)", Colored(new Color(1f, 0.8f, 0.3f)));
                GUILayout.Space(10);

                GUILayout.Label("Players (" + (friendCount + 1) + ")", _statusStyle);
                PlayerRow(myName + "   (you)", new Color(0.55f, 0.85f, 1f));
                foreach (KeyValuePair<string, string> kv in friendNames)
                    if (knownFriends.Contains(kv.Key)) PlayerRow(kv.Value, new Color(0.3f, 0.9f, 0.45f));
                if (friendCount == 0) GUILayout.Label("Waiting for someone to join…", Colored(new Color(1f, 0.8f, 0.3f)));
            }
            else if (state == ConnState.Connecting)
            {
                GUILayout.Label("Connecting to relay…", Colored(new Color(1f, 0.8f, 0.2f)));
                GUILayout.Label("Room " + room, _statusStyle);
                GUILayout.Space(6);
                if (GUILayout.Button("Cancel", _btnStyle)) Disconnect();
            }
            else // Disconnected or Failed
            {
                if (state == ConnState.Failed && !string.IsNullOrEmpty(lastError))
                {
                    GUILayout.Label(lastError, Colored(new Color(1f, 0.45f, 0.45f)));
                    GUILayout.Space(6);
                }

                GUILayout.Label("Relay server URL", _statusStyle);
                GUILayout.BeginHorizontal();
                urlField = GUILayout.TextField(urlField ?? "", _fieldStyle);
                if (GUILayout.Button("Paste", _btnStyle, GUILayout.Width(58))) urlField = GUIUtility.systemCopyBuffer;
                GUILayout.EndHorizontal();
                GUILayout.Space(10);

                if (GUILayout.Button("HOST A NEW ROOM", _btnStyle)) Host();
                GUILayout.Space(6);
                if (GUILayout.Button("SOLO SELF-TEST (no friend needed)", _btnStyle)) StartSoloTest();
                GUILayout.Space(10);

                GUILayout.Label("Or join a friend's room:", _statusStyle);
                GUILayout.BeginHorizontal();
                joinField = GUILayout.TextField((joinField ?? "").ToUpper(), 5, _fieldStyle);
                if (GUILayout.Button("JOIN", _btnStyle, GUILayout.Width(66)))
                {
                    string c = (joinField ?? "").Trim();
                    if (c.Length == 5) Join(c);
                    else ModAPI.Notify("Room code must be 5 characters");
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label("M = show / hide this menu", Colored(new Color(0.5f, 0.5f, 0.55f)));
            GUILayout.EndArea();
        }

        void PlayerRow(string name, Color dot)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("●", Colored(dot), GUILayout.Width(16));
            GUILayout.Label(name, _statusStyle);
            GUILayout.EndHorizontal();
        }

        // ----- MAIN MENU: a text button under the game's menu list ---------
        void DrawMenuEntry()
        {
            GUIStyle item = new GUIStyle(GUI.skin.label);
            item.fontSize = Mathf.RoundToInt(Screen.height * 0.030f);
            item.normal.textColor = new Color(0.75f, 0.75f, 0.78f);
            item.hover.textColor = Color.white;
            item.alignment = TextAnchor.MiddleLeft;

            // positioned to sit just under "information" in the left menu list.
            // (proportional to resolution; ping me to nudge it if it's off.)
            float x = Screen.width * 0.004f;
            float y = Screen.height * 0.50f;
            float w = Screen.width * 0.25f;
            float h = Screen.height * 0.055f;
            if (GUI.Button(new Rect(x, y, w, h), "multiplayer", item)) menuPopupOpen = !menuPopupOpen;

            if (menuPopupOpen) DrawMenuPopup();
        }

        void DrawMenuPopup()
        {
            float pw = Mathf.Min(440, Screen.width * 0.6f);
            float ph = 270;
            float px = (Screen.width - pw) / 2f, py = (Screen.height - ph) / 2f;

            Color o = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = o;

            GUILayout.BeginArea(new Rect(px, py, pw, ph), _boxStyle);
            GUILayout.Label("JOIN A FRIEND", _titleStyle);
            GUILayout.Space(8);
            GUILayout.Label("Enter your friend's room code:", _statusStyle);
            menuCodeField = GUILayout.TextField((menuCodeField ?? "").ToUpper(), 5, _fieldStyle);
            GUILayout.Space(8);
            GUILayout.Label("Relay server URL", _statusStyle);
            GUILayout.BeginHorizontal();
            urlField = GUILayout.TextField(urlField ?? "", _fieldStyle);
            if (GUILayout.Button("Paste", _btnStyle, GUILayout.Width(58))) urlField = GUIUtility.systemCopyBuffer;
            GUILayout.EndHorizontal();
            GUILayout.Space(12);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("JOIN", _btnStyle))
            {
                string c = (menuCodeField ?? "").Trim();
                if (c.Length == 5) MenuJoin(c);
                else ModAPI.Notify("Room code must be 5 characters");
            }
            if (GUILayout.Button("HOST NEW", _btnStyle)) MenuHost();
            if (GUILayout.Button("Cancel", _btnStyle)) menuPopupOpen = false;
            GUILayout.EndHorizontal();
            GUILayout.Space(6);
            if (GUILayout.Button("Solo self-test (try it alone)", _btnStyle))
            {
                pendingHost = false; pendingJoin = false; menuPopupOpen = false;
                pendingSolo = true;
                LaunchIntoSandbox("Solo self-test — dropping into a sandbox…");
            }
            GUILayout.EndArea();
        }

        void OnDestroy()
        {
            if (codeDisplay != null) UnityEngine.Object.Destroy(codeDisplay);
            if (statusDisplay != null) UnityEngine.Object.Destroy(statusDisplay);
            foreach (var c in cursors.Values) if (c.go != null) UnityEngine.Object.Destroy(c.go);
        }
    }
}
