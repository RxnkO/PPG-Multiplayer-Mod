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
            // Create the manager FIRST. On the main menu at startup the item
            // catalog isn't loaded, so ModAPI.FindSpawnable/Register can fail —
            // if that ran first and threw, the manager (and all UI) would never
            // be created until you entered a map. Building it first means the
            // menu button works on a cold startup.
            GameObject old = GameObject.Find("MPManager");
            if (old != null) UnityEngine.Object.DestroyImmediate(old);

            GameObject manager = new GameObject("MPManager");
            manager.AddComponent<MPBehaviour>();
            UnityEngine.Object.DontDestroyOnLoad(manager);

            // Catalog entry is optional cosmetic; never let it block the mod.
            try
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
            }
            catch { }

            try { ModAPI.Notify("Multiplayer Mod Loaded! (v1.5 - stable)"); } catch { }
        }
    }

    // ---- wire formats (JsonUtility-friendly) -------------------------------
    [Serializable] public class ObjMsg { public string nid; public string item; public float x; public float y; public float rot; }
    [Serializable] public class SyncRequest
    {
        public string room; public string id; public string name;
        public string steam;      // my Steam ID (for avatar lookup)
        public bool grab;         // am I holding the mouse (grabbing)?
        public float x; public float y;
        public ObjMsg[] objs;
        public string[] claims;   // nids I'm taking control of this tick
        public ShotMsg[] shots;   // projectiles I fired this tick
        public string[] chats;    // chat lines I sent this tick
        public bool echo;         // solo self-test: ask relay to mirror me back
    }
    [Serializable] public class ClaimMsg { public string nid; public string owner; }
    [Serializable] public class ShotMsg { public long id; public float x; public float y; public float vx; public float vy; }
    [Serializable] public class ChatMsg { public long id; public string owner; public string name; public string text; }
    [Serializable] public class CursorMsg { public string id; public string name; public string steam; public bool grab; public float x; public float y; public float age; }
    [Serializable] public class SyncResponse { public double now; public CursorMsg[] cursors; public ObjMsg[] objs; public ClaimMsg[] claims; public ShotMsg[] shots; public ChatMsg[] chats; }

    // one displayed chat line (name + text, colored per player, fades over time)
    public class ChatLine { public string name; public string text; public Color color; public float time; }

    // ---- runtime tracking --------------------------------------------------
    public class RemoteCursor
    {
        public GameObject go; public TextMesh nameTag; public Vector3 target; public float lastSeen;
        public string steam; public bool avatarSet; public float nextAvatarTry;
        public int colorIndex; public bool grab;
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
        public Vector3 estVel;     // velocity estimated from position changes
        public float lastNetTime;  // when we last got a network update
        public bool flashed;       // muzzle flash already played for this projectile
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
        private const string DEFAULT_RELAY_URL = "https://ppg-multiplayer-mod.onrender.com";
        private string relayUrl = DEFAULT_RELAY_URL;

        // optional quick-spawn hotkeys (handy for testing; normal catalog
        // spawns are synced automatically too)
        private static readonly string[] SPAWN_ITEMS = { "Human", "Rod", "Watermelon", "Crate" };

        private const string CHARS = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        private const float SYNC_INTERVAL = 0.08f;   // ~12 Hz
        private const float OBJ_TIMEOUT = 1.2f;      // drop a mirror after this silence (short so spent bullets vanish)
        private const float CURSOR_TIMEOUT = 3f;

        // connection
        private bool connected = false, hosting = false;
        private bool soloTest = false;   // echo myself back for one-player testing
        private string room = "", myId = "", myName = "Player", mySteam = "";
        private bool typing = false; private string typed = "";

        // connection state machine + clickable panel
        private enum ConnState { Disconnected, Connecting, Connected, Failed }
        private ConnState state = ConnState.Disconnected;
        private string lastError = "";
        private bool panelOpen = true;
        private string joinField = "";
        private string urlField = DEFAULT_RELAY_URL;
        private float lastPing = 0f;   // round-trip time to relay, ms
        private float connectStart = 0f;   // when we began the current connect
        private bool diagShown = false;

        // chat
        private bool chatActive = false;
        private string chatInput = "";
        private int chatOpenedFrame = -1;
        private readonly List<string> pendingChats = new List<string>();
        private readonly HashSet<long> seenChats = new HashSet<long>();
        private readonly List<ChatLine> chatLog = new List<ChatLine>();
        private GUIStyle _titleStyle, _statusStyle, _codeStyle, _boxStyle, _btnStyle, _fieldStyle;
        private bool _stylesReady = false;

        // main-menu entry point ("multiplayer" button + join popup)
        private bool menuPopupOpen = false;
        private string pendingRoom = "";
        private bool pendingHost = false, pendingJoin = false, pendingSolo = false;
        private bool wasOnMenu = true;

        // cursors
        private readonly Dictionary<string, RemoteCursor> cursors = new Dictionary<string, RemoteCursor>();
        private readonly Dictionary<string, Sprite> avatarCache = new Dictionary<string, Sprite>();
        private readonly Sprite[] pointerSprites = new Sprite[4];   // custom cursor art, P1..P4
        private readonly Sprite[] grabSprites = new Sprite[4];      // closed-hand (grabbing) art
        private bool cursorSpritesLoaded = false;
        private int friendCount = 0;

        // connection feedback
        private readonly HashSet<string> knownFriends = new HashSet<string>();
        private readonly Dictionary<string, string> friendNames = new Dictionary<string, string>();
        private readonly Dictionary<string, string> friendSteams = new Dictionary<string, string>();
        private float nextSelfAvatarTry = 0f;
        private int netFail = 0;
        private bool everConnected = false;

        // objects
        private readonly Dictionary<string, NetObj> objects = new Dictionary<string, NetObj>(); // by nid
        private readonly HashSet<int> trackedInstances = new HashSet<int>(); // go.GetInstanceID() we've already handled
        private int objCounter = 0;
        private float lastCapture = 0f;   // periodic auto-capture of new spawns
        private readonly List<string> pendingClaims = new List<string>();   // objects I grabbed this tick
        private readonly List<ShotMsg> pendingShots = new List<ShotMsg>();   // projectiles fired this tick
        private readonly HashSet<int> shotSeen = new HashSet<int>();         // bullet instances already sent as shots
        private readonly HashSet<long> seenShots = new HashSet<long>();      // remote shot ids already played
        private bool suppressRegister = false; // true while WE instantiate a mirror
        private bool spawnDebugShown = false;

        // ui
        private GameObject codeDisplay; private TextMesh codeText;
        private GameObject statusDisplay; private TextMesh statusText;
        private Coroutine syncRoutine;

        // ===============================================================
        void Start()
        {
            // CRITICAL: a freshly loaded mod often starts Unity's RNG from the
            // same seed on every machine, so Random.Range gave every player the
            // SAME id (and the same room code). The relay then treated everyone
            // as one person and nobody could see anyone else. Use a real GUID for
            // the id and seed the RNG per session so room codes are unique too.
            try { UnityEngine.Random.InitState(System.Environment.TickCount ^ System.Guid.NewGuid().GetHashCode()); } catch { }
            mySteam = GetSteamId();
            // Prefer the Steam ID as our identity - it's globally unique, so two
            // players can NEVER collide. Falls back to a GUID if Steam isn't found.
            myId = !string.IsNullOrEmpty(mySteam) ? "p_" + mySteam : "p_" + System.Guid.NewGuid().ToString("N").Substring(0, 12);
            myName = GetPlayerName();
            LoadCursorSprites();
            CreateWorldUI();
            HookSpawnEvent();
        }

        // load the custom pointer / grab hand art shipped with the mod (P1..P4).
        // Retries (only fills the nulls) since ModAPI assets may not be ready the
        // instant the mod boots.
        void LoadCursorSprites()
        {
            int loaded = 0;
            for (int i = 0; i < 4; i++)
            {
                if (pointerSprites[i] == null) { try { pointerSprites[i] = ModAPI.LoadSprite("PointerP" + (i + 1) + "-256.png"); } catch { } }
                if (grabSprites[i] == null) { try { grabSprites[i] = ModAPI.LoadSprite("HandClosedP" + (i + 1) + "-256.png"); } catch { } }
                if (pointerSprites[i] != null) loaded++;
            }
            cursorSpritesLoaded = loaded > 0;
        }

        string RandomName()
        {
            string[] a = { "Red", "Blue", "Mad", "Calm", "Swift", "Lazy", "Bold", "Tiny" };
            string[] b = { "Fox", "Wolf", "Bear", "Hawk", "Cat", "Owl", "Crab", "Moth" };
            return a[UnityEngine.Random.Range(0, a.Length)] + b[UnityEngine.Random.Range(0, b.Length)];
        }

        // Use the player's real Steam name if the game's Steamworks library is
        // loaded. Done via reflection so we don't hard-depend on a specific
        // Steamworks wrapper; falls back to a random name if unavailable.
        string GetPlayerName()
        {
            // Facepunch.Steamworks:  Steamworks.SteamClient.Name  (static property)
            try
            {
                Type t = FindType("Steamworks.SteamClient");
                if (t != null)
                {
                    PropertyInfo p = t.GetProperty("Name", BindingFlags.Public | BindingFlags.Static);
                    if (p != null)
                    {
                        string n = p.GetValue(null, null) as string;
                        if (!string.IsNullOrEmpty(n)) return n;
                    }
                }
            }
            catch { }
            // Steamworks.NET:  Steamworks.SteamFriends.GetPersonaName()  (static method)
            try
            {
                Type t = FindType("Steamworks.SteamFriends");
                if (t != null)
                {
                    MethodInfo m = t.GetMethod("GetPersonaName", BindingFlags.Public | BindingFlags.Static);
                    if (m != null)
                    {
                        string n = m.Invoke(null, null) as string;
                        if (!string.IsNullOrEmpty(n)) return n;
                    }
                }
            }
            catch { }
            return RandomName();
        }

        Type FindType(string fullName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { Type t = a.GetType(fullName); if (t != null) return t; }
                catch { }
            }
            return null;
        }

        // Read the local player's 64-bit Steam ID via whichever Steam wrapper the
        // game bundles. Returns "" if not available.
        string GetSteamId()
        {
            // Steamworks.NET:  SteamUser.GetSteamID().m_SteamID
            try
            {
                Type su = FindType("Steamworks.SteamUser");
                if (su != null)
                {
                    MethodInfo m = su.GetMethod("GetSteamID", BindingFlags.Public | BindingFlags.Static);
                    if (m != null)
                    {
                        object cid = m.Invoke(null, null);
                        if (cid != null)
                        {
                            FieldInfo f = cid.GetType().GetField("m_SteamID");
                            if (f != null) { object v = f.GetValue(cid); if (v != null) return v.ToString(); }
                        }
                    }
                }
            }
            catch { }
            // Facepunch.Steamworks:  SteamClient.SteamId.Value
            try
            {
                Type sc = FindType("Steamworks.SteamClient");
                if (sc != null)
                {
                    PropertyInfo p = sc.GetProperty("SteamId", BindingFlags.Public | BindingFlags.Static);
                    if (p != null)
                    {
                        object sid = p.GetValue(null, null);
                        if (sid != null)
                        {
                            PropertyInfo vp = sid.GetType().GetProperty("Value");
                            if (vp != null) { object v = vp.GetValue(sid, null); if (v != null) return v.ToString(); }
                            FieldInfo vf = sid.GetType().GetField("Value");
                            if (vf != null) { object v = vf.GetValue(sid); if (v != null) return v.ToString(); }
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        // Build a Sprite from a player's Steam avatar (Steamworks.NET path).
        // Returns null if Steam isn't available or the avatar isn't ready yet.
        Sprite LoadSteamAvatar(string steamIdStr)
        {
            try
            {
                ulong sid;
                if (!ulong.TryParse(steamIdStr, out sid)) return null;
                Type csteamidT = FindType("Steamworks.CSteamID");
                Type friends = FindType("Steamworks.SteamFriends");
                Type utils = FindType("Steamworks.SteamUtils");
                if (csteamidT == null || friends == null || utils == null) return null;

                object cid = Activator.CreateInstance(csteamidT, sid);
                MethodInfo reqInfo = friends.GetMethod("RequestUserInformation", BindingFlags.Public | BindingFlags.Static);
                if (reqInfo != null) { try { reqInfo.Invoke(null, new object[] { cid, false }); } catch { } }

                MethodInfo getAv = friends.GetMethod("GetLargeFriendAvatar", BindingFlags.Public | BindingFlags.Static)
                                 ?? friends.GetMethod("GetMediumFriendAvatar", BindingFlags.Public | BindingFlags.Static);
                if (getAv == null) return null;
                object handleObj = getAv.Invoke(null, new object[] { cid });
                int handle = Convert.ToInt32(handleObj);
                if (handle <= 0) return null;   // not downloaded yet - try again later

                MethodInfo getSize = utils.GetMethod("GetImageSize", BindingFlags.Public | BindingFlags.Static);
                object[] sizeArgs = new object[] { handle, (uint)0, (uint)0 };
                getSize.Invoke(null, sizeArgs);
                uint w = Convert.ToUInt32(sizeArgs[1]), h = Convert.ToUInt32(sizeArgs[2]);
                if (w == 0 || h == 0) return null;

                byte[] data = new byte[w * h * 4];
                MethodInfo getRGBA = utils.GetMethod("GetImageRGBA", BindingFlags.Public | BindingFlags.Static);
                getRGBA.Invoke(null, new object[] { handle, data, data.Length });

                // Steam gives top-down rows; Unity textures are bottom-up, so flip.
                int rw = (int)w * 4;
                byte[] flip = new byte[data.Length];
                for (int row = 0; row < h; row++)
                    System.Array.Copy(data, row * rw, flip, (int)((h - 1 - row) * rw), rw);

                Texture2D tex = new Texture2D((int)w, (int)h, TextureFormat.RGBA32, false);
                tex.LoadRawTextureData(flip);
                tex.Apply();
                return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), w);
            }
            catch { return null; }
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
            HandleGrab();
            // load my own Steam avatar so it shows next to "(you)" in the list
            if (connected && !string.IsNullOrEmpty(mySteam) && !avatarCache.ContainsKey(mySteam) && Time.time > nextSelfAvatarTry)
            {
                Sprite s = LoadSteamAvatar(mySteam);
                if (s != null) avatarCache[mySteam] = s; else nextSelfAvatarTry = Time.time + 2f;
            }
            if (connected && Time.time - lastCapture > 0.1f) { lastCapture = Time.time; AutoCapture(); }
            if (soloTest && connected) LocalEchoTick();
            UpdateWorldUI();
            UpdateRemoteCursors();
            UpdateMirrors();
            HandleSceneFlow();
        }

        // SOLO SELF-TEST (local): mirror your own cursor + owned objects to the
        // right, entirely inside the mod, so you can verify the cursor/mirror
        // rendering works without needing a friend OR relay echo support. The
        // green "Connected" pill already proves the network round-trip; this
        // proves the visual sync half.
        void LocalEchoTick()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            Vector3 off = new Vector3(3f, 0f, 0f);

            Vector3 m = cam.ScreenToWorldPoint(Input.mousePosition); m.z = 0f;
            RemoteCursor rc = GetOrMakeCursor("self_echo", myName + " (echo)");
            rc.target = m + off;
            rc.lastSeen = Time.time;
            rc.grab = Input.GetMouseButton(0);

            List<NetObj> owned = new List<NetObj>();
            foreach (KeyValuePair<string, NetObj> kv in objects)
                if (kv.Value.owned && kv.Value.go != null) owned.Add(kv.Value);

            foreach (NetObj o in owned)
            {
                string gid = o.nid + "_echo";
                Vector3 p = o.go.transform.position + off;
                float rot = o.go.transform.eulerAngles.z;
                bool flip = o.go.transform.localScale.x < 0f;
                NetObj ghost;
                if (objects.TryGetValue(gid, out ghost))
                {
                    ghost.targetPos = p; ghost.targetRot = rot; ghost.lastSeen = Time.time;
                    ApplyFlip(ghost.go, flip);
                }
                else
                {
                    GameObject go = SpawnMirror(o.item, p, rot);
                    if (go == null) continue;
                    ApplyFlip(go, flip);
                    objects[gid] = new NetObj { nid = gid, item = o.item, go = go, owned = false, targetPos = p, targetRot = rot, lastSeen = Time.time };
                }
            }
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
            ModAPI.Notify("Now press PLAY and load a sandbox. You'll drop in automatically.");
        }

        void HandleInput()
        {
            if (chatActive) return;   // chat field owns the keyboard
            if (Input.GetKeyDown(KeyCode.M)) panelOpen = !panelOpen;
            // when the panel is open it owns the keyboard (so typing in the
            // room-code / URL fields never triggers game hotkeys)
            if (panelOpen) return;

            // open chat with Enter (only while connected)
            if (connected && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
            {
                chatActive = true; chatInput = ""; chatOpenedFrame = Time.frameCount; return;
            }

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
            everConnected = false; netFail = 0; connectStart = Time.time;
            LoadCursorSprites();
            // one-time, quiet note only if an optional extra didn't load
            if (!diagShown)
            {
                diagShown = true;
                int nptr = 0; for (int i = 0; i < 4; i++) if (pointerSprites[i] != null) nptr++;
                if (nptr < 4 || string.IsNullOrEmpty(mySteam))
                    try { ModAPI.Notify("Note: custom cursors " + nptr + "/4, Steam " + (string.IsNullOrEmpty(mySteam) ? "not detected (using colored dots)" : "ok")); } catch { }
            }
            if (syncRoutine != null) StopCoroutine(syncRoutine);
            syncRoutine = StartCoroutine(SyncLoop());
        }

        void Disconnect()
        {
            connected = false;
            if (syncRoutine != null) { StopCoroutine(syncRoutine); syncRoutine = null; }
            foreach (var c in cursors.Values) { if (c.go != null) UnityEngine.Object.Destroy(c.go); if (c.nameTag != null) UnityEngine.Object.Destroy(c.nameTag.gameObject); }
            cursors.Clear();
            // destroy mirrors (not our own objects)
            var rm = new List<string>();
            foreach (var kv in objects) if (!kv.Value.owned) { if (kv.Value.go != null) UnityEngine.Object.Destroy(kv.Value.go); rm.Add(kv.Key); }
            foreach (var k in rm) objects.Remove(k);
            friendCount = 0; room = "";
            knownFriends.Clear(); friendSteams.Clear(); netFail = 0; everConnected = false;
            state = ConnState.Disconnected; lastError = ""; panelOpen = true;
            soloTest = false;
            pendingClaims.Clear(); pendingShots.Clear(); shotSeen.Clear(); seenShots.Clear();
            pendingChats.Clear(); seenChats.Clear(); chatLog.Clear(); chatActive = false; chatInput = "";
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

                string[] claimsArr = pendingClaims.Count > 0 ? pendingClaims.ToArray() : null;
                pendingClaims.Clear();
                ShotMsg[] shotsArr = pendingShots.Count > 0 ? pendingShots.ToArray() : null;
                pendingShots.Clear();
                string[] chatsArr = pendingChats.Count > 0 ? pendingChats.ToArray() : null;
                pendingChats.Clear();
                SyncRequest reqObj = new SyncRequest
                {
                    room = room, id = myId, name = myName, steam = mySteam,
                    grab = Input.GetMouseButton(0),
                    x = m.x, y = m.y, objs = BuildOwnedSnapshot(),
                    claims = claimsArr,
                    shots = shotsArr,
                    chats = chatsArr,
                    echo = false   // solo test now echoes locally (see LocalEchoTick)
                };
                string json = JsonUtility.ToJson(reqObj);

                UnityWebRequest req = new UnityWebRequest(relayUrl.TrimEnd('/') + "/sync", "POST");
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 15;
                float t0 = Time.realtimeSinceStartup;
                yield return req.SendWebRequest();

                if (req.error == null && req.downloadHandler != null)
                {
                    lastPing = (Time.realtimeSinceStartup - t0) * 1000f;
                    if (netFail >= 3) ModAPI.Notify("Relay reconnected");
                    netFail = 0; lastError = "";
                    state = ConnState.Connected;
                    if (!everConnected) { everConnected = true; panelOpen = false; ModAPI.Notify("Connected! Room " + room + " - waiting for friend..."); }
                    HandleResponse(req.downloadHandler.text);
                }
                else
                {
                    netFail++;
                    if (!everConnected)
                    {
                        // First connection. The free server sleeps when idle and can
                        // take up to a minute to wake, so stay in "Connecting" and
                        // keep retrying rather than crying failure right away.
                        state = ConnState.Connecting;
                        if (Time.time - connectStart > 70f)
                        {
                            state = ConnState.Failed; panelOpen = true;
                            lastError = "Couldn't reach the server after a minute. Check your internet, and make sure you and your friend are on the latest version of the mod.";
                        }
                    }
                    else if (netFail == 3)
                    {
                        // We were connected and dropped - keep trying quietly.
                        state = ConnState.Failed;
                        lastError = "Lost connection to the server. Reconnecting...";
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
                float rot = o.go.transform.eulerAngles.z;
                // encode horizontal flip (facing left/right) into rot: guns etc.
                // face a direction via negative X scale, not rotation. +1000 marks flipped.
                if (o.go.transform.localScale.x < 0f) rot += 1000f;
                list.Add(new ObjMsg { nid = o.nid, item = o.item, x = p.x, y = p.y, rot = rot });
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
                    if (!string.IsNullOrEmpty(c.steam)) friendSteams[c.id] = c.steam;
                    RemoteCursor rcur = GetOrMakeCursor(c.id, c.name);
                    rcur.target = new Vector3(c.x, c.y, 0f);
                    rcur.steam = c.steam;
                    rcur.grab = c.grab;
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
                    Vector3 np = new Vector3(o.x, o.y, 0f);
                    // decode flip flag out of the rotation value
                    bool flip = o.rot >= 500f;
                    float rot = flip ? o.rot - 1000f : o.rot;
                    if (objects.TryGetValue(o.nid, out n))
                    {
                        if (n.owned) continue; // shouldn't happen (relay excludes own)
                        float dt = Time.time - n.lastNetTime;
                        if (n.lastNetTime > 0f && dt > 0.001f) n.estVel = (np - n.targetPos) / dt;
                        n.targetPos = np;
                        n.targetRot = rot;
                        n.lastNetTime = Time.time;
                        n.lastSeen = Time.time;
                        ApplyFlip(n.go, flip);
                        // first time it's moving fast -> it's a projectile: flash
                        if (!n.flashed && n.estVel.sqrMagnitude > 18f * 18f)
                        {
                            n.flashed = true;
                            SpawnFlash(n.go != null ? n.go.transform.position : np);
                        }
                    }
                    else
                    {
                        GameObject go = SpawnMirror(o.item, np, rot);
                        if (go == null) continue;
                        ApplyFlip(go, flip);
                        objects[o.nid] = new NetObj { nid = o.nid, item = o.item, go = go, owned = false,
                            targetPos = np, targetRot = rot, lastSeen = Time.time, lastNetTime = Time.time };
                    }
                }

            // shots: play a flash + tracer for each new projectile a friend fired
            if (resp.shots != null)
                foreach (var s in resp.shots)
                {
                    if (s == null || seenShots.Contains(s.id)) continue;
                    seenShots.Add(s.id);
                    SpawnTracer(new Vector3(s.x, s.y, 0f), new Vector2(s.vx, s.vy));
                }

            // chat: add any new lines to the log (colored by sender)
            if (resp.chats != null)
                foreach (var c in resp.chats)
                {
                    if (c == null || seenChats.Contains(c.id)) continue;
                    seenChats.Add(c.id);
                    AddChatLine(string.IsNullOrEmpty(c.name) ? "?" : c.name, c.text, ColorForId(c.owner));
                }

            // authority handoff: if another player grabbed an object I own, release it
            if (resp.claims != null)
                foreach (var cm in resp.claims)
                {
                    if (cm == null || string.IsNullOrEmpty(cm.nid) || cm.owner == myId) continue;
                    NetObj mine;
                    if (objects.TryGetValue(cm.nid, out mine) && mine.owned)
                    {
                        mine.owned = false;
                        if (mine.go != null)
                        {
                            FreezePhysics(mine.go);
                            mine.targetPos = mine.go.transform.position;
                            mine.targetRot = mine.go.transform.eulerAngles.z;
                        }
                        mine.lastSeen = Time.time;
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

        // Reliable spawn tracking: every ~0.5s, find any physics objects we
        // haven't networked yet and register them as owned. This replaces the
        // flaky catalog spawn-hook — whatever you spawn (catalog or hotkey) now
        // gets shared, and mirrors of other players are skipped (already tracked).
        void AutoCapture()
        {
            try
            {
                Rigidbody2D[] bodies = UnityEngine.Object.FindObjectsOfType<Rigidbody2D>();
                HashSet<int> seen = new HashSet<int>();
                foreach (Rigidbody2D rb in bodies)
                {
                    if (rb == null) continue;
                    GameObject go = rb.transform.root.gameObject;
                    int iid = go.GetInstanceID();
                    if (seen.Contains(iid)) continue;
                    seen.Add(iid);
                    if (trackedInstances.Contains(iid)) continue;

                    // A brand-new object moving very fast is a projectile (bullet).
                    // Send it as a one-shot "fire event" (flash + tracer on the
                    // other side) instead of trying to mirror the object itself.
                    if (!shotSeen.Contains(iid) && rb.velocity.sqrMagnitude > 40f * 40f)
                    {
                        shotSeen.Add(iid);
                        trackedInstances.Add(iid);   // don't also mirror it
                        Vector3 bp = go.transform.position;
                        pendingShots.Add(new ShotMsg { x = bp.x, y = bp.y, vx = rb.velocity.x, vy = rb.velocity.y });
                        // solo self-test: show the shot echoed beside you
                        if (soloTest) SpawnTracer(new Vector3(bp.x + 3f, bp.y, 0f), rb.velocity);
                        continue;
                    }

                    string item = CleanName(go.name);
                    if (string.IsNullOrEmpty(item)) continue;
                    if (ModAPI.FindSpawnable(item) == null) continue;
                    RegisterOwned(go, item);
                }
            }
            catch { }
        }

        // AUTHORITY HANDOFF: click another player's object to take control of
        // it. We unfreeze it, mark it ours, and queue a claim so the other
        // player releases their (authoritative) copy into a mirror.
        void HandleGrab()
        {
            if (!connected || panelOpen) return;
            if (!Input.GetMouseButtonDown(0)) return;
            Camera cam = Camera.main; if (cam == null) return;
            Vector3 w = cam.ScreenToWorldPoint(Input.mousePosition);
            Collider2D[] hits;
            try { hits = Physics2D.OverlapPointAll(new Vector2(w.x, w.y)); }
            catch { return; }
            if (hits == null) return;
            foreach (Collider2D col in hits)
            {
                if (col == null) continue;
                GameObject root = col.transform.root.gameObject;
                foreach (KeyValuePair<string, NetObj> kv in objects)
                {
                    NetObj o = kv.Value;
                    if (!o.owned && o.go == root && !kv.Key.EndsWith("_echo"))
                    {
                        TakeControl(kv.Key, o);
                        return;
                    }
                }
            }
        }

        void TakeControl(string nid, NetObj o)
        {
            o.owned = true;
            if (o.go != null) UnfreezePhysics(o.go);
            if (!pendingClaims.Contains(nid)) pendingClaims.Add(nid);
            ModAPI.Notify("You took control of " + o.item);
        }

        // ---- chat ----------------------------------------------------------
        void AddChatLine(string name, string text, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            chatLog.Add(new ChatLine { name = name, text = text, color = color, time = Time.time });
            while (chatLog.Count > 8) chatLog.RemoveAt(0);
        }

        void SendChat()
        {
            string t = (chatInput ?? "").Trim();
            if (t.Length > 200) t = t.Substring(0, 200);
            if (t.Length > 0)
            {
                pendingChats.Add(t);
                AddChatLine(myName, t, ColorForId(myId));   // show mine immediately
            }
            chatInput = "";
            chatActive = false;
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

        // Mirror of a remote object: kinematic (not simulated=false) so it
        // doesn't fall or fight the owner's stream, BUT keeps active colliders
        // so you can still click it to take control (authority handoff).
        void FreezePhysics(GameObject go)
        {
            try
            {
                Rigidbody2D[] bodies = go.GetComponentsInChildren<Rigidbody2D>(true);
                foreach (Rigidbody2D rb in bodies)
                {
                    rb.simulated = true;
                    rb.bodyType = RigidbodyType2D.Kinematic;
                    rb.velocity = Vector2.zero;
                    rb.angularVelocity = 0f;
                }
            }
            catch { }
        }

        // Match the owner's horizontal facing (left/right) on the mirror.
        void ApplyFlip(GameObject go, bool flip)
        {
            if (go == null) return;
            Vector3 s = go.transform.localScale;
            float ax = Mathf.Abs(s.x);
            float want = flip ? -ax : ax;
            if (s.x != want) { s.x = want; go.transform.localScale = s; }
        }

        // Give an object back its normal physics (when you take control of it).
        void UnfreezePhysics(GameObject go)
        {
            try
            {
                Rigidbody2D[] bodies = go.GetComponentsInChildren<Rigidbody2D>(true);
                foreach (Rigidbody2D rb in bodies)
                {
                    rb.bodyType = RigidbodyType2D.Dynamic;
                    rb.simulated = true;
                }
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
                // predict forward using estimated velocity so fast objects
                // (bullets) glide between the ~12 Hz updates instead of teleporting
                float ext = Mathf.Min(Time.time - o.lastNetTime, 0.25f);
                Vector3 predicted = o.targetPos + o.estVel * ext;
                o.go.transform.position = Vector3.Lerp(o.go.transform.position, predicted, Time.deltaTime * 20f);
                float z = Mathf.LerpAngle(o.go.transform.eulerAngles.z, o.targetRot, Time.deltaTime * 16f);
                o.go.transform.rotation = Quaternion.Euler(0f, 0f, z);
            }
            if (drop != null) foreach (var k in drop) { trackedInstances.Remove(0); objects.Remove(k); }
        }

        // ===============================================================
        //  remote cursors
        // ===============================================================
        // ---- muzzle / shoot flash shown when a projectile arrives ----------
        private Sprite _flashSprite;
        Sprite FlashSprite()
        {
            if (_flashSprite != null) return _flashSprite;
            int S = 32;
            Texture2D tex = new Texture2D(S, S);
            Color[] pix = new Color[S * S];
            for (int i = 0; i < pix.Length; i++)
            {
                float dx = (i % S) - S / 2f, dy = (i / S) - S / 2f;
                float d = Mathf.Sqrt(dx * dx + dy * dy) / (S / 2f);
                float a = Mathf.Clamp01(1f - d);
                pix[i] = new Color(1f, 0.9f, 0.5f, a * a);
            }
            tex.SetPixels(pix); tex.Apply();
            _flashSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S);
            return _flashSprite;
        }

        void SpawnFlash(Vector3 pos)
        {
            try
            {
                GameObject f = new GameObject("ShootFlash");
                f.transform.position = pos;
                SpriteRenderer sr = f.AddComponent<SpriteRenderer>();
                sr.sprite = FlashSprite();
                sr.color = new Color(1f, 0.85f, 0.4f, 0.9f);
                sr.sortingOrder = 9998;
                StartCoroutine(FlashAnim(f, sr));
            }
            catch { }
        }

        IEnumerator FlashAnim(GameObject f, SpriteRenderer sr)
        {
            float t = 0f, dur = 0.16f;
            while (t < dur && f != null)
            {
                t += Time.deltaTime;
                float k = t / dur;
                f.transform.localScale = Vector3.one * (0.6f + k * 1.6f);
                if (sr != null) { Color c = sr.color; c.a = 0.9f * (1f - k); sr.color = c; }
                yield return null;
            }
            if (f != null) UnityEngine.Object.Destroy(f);
        }

        // a friend's shot: muzzle flash at the origin + a tracer that flies
        void SpawnTracer(Vector3 pos, Vector2 vel)
        {
            SpawnFlash(pos);
            try
            {
                GameObject t = new GameObject("Tracer");
                t.transform.position = pos;
                t.transform.localScale = Vector3.one * 0.25f;
                SpriteRenderer sr = t.AddComponent<SpriteRenderer>();
                sr.sprite = FlashSprite();
                sr.color = new Color(1f, 0.95f, 0.6f, 1f);
                sr.sortingOrder = 9997;
                StartCoroutine(TracerAnim(t, sr, vel));
            }
            catch { }
        }

        IEnumerator TracerAnim(GameObject t, SpriteRenderer sr, Vector2 vel)
        {
            float life = 0.7f, e = 0f;
            while (e < life && t != null)
            {
                e += Time.deltaTime;
                t.transform.position += new Vector3(vel.x, vel.y, 0f) * Time.deltaTime;
                if (sr != null) { Color c = sr.color; c.a = 1f - (e / life); sr.color = c; }
                yield return null;
            }
            if (t != null) UnityEngine.Object.Destroy(t);
        }

        // 4 player colors that match the custom cursor art (P1..P4)
        private static readonly Color[] PLAYER_COLORS = {
            new Color(0.30f, 0.60f, 1.00f),   // P1 blue
            new Color(1.00f, 0.38f, 0.48f),   // P2 red / pink
            new Color(0.40f, 0.85f, 0.42f),   // P3 green
            new Color(1.00f, 0.62f, 0.22f),   // P4 orange
        };
        int ColorIndex(string id)
        {
            int h = 17;
            if (!string.IsNullOrEmpty(id)) foreach (char c in id) h = h * 31 + c;
            return ((h % 4) + 4) % 4;
        }
        Color ColorForId(string id) { return PLAYER_COLORS[ColorIndex(id)]; }

        RemoteCursor GetOrMakeCursor(string id, string name)
        {
            RemoteCursor rc;
            if (cursors.TryGetValue(id, out rc)) { if (rc.go != null) return rc; }
            if (!cursorSpritesLoaded) LoadCursorSprites();   // retry until the art loads
            int idx = ColorIndex(id);
            Color col = PLAYER_COLORS[idx];

            GameObject go = new GameObject("RemoteCursor_" + id);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = pointerSprites[idx] != null ? pointerSprites[idx] : BuildCursorSprite(col);
            sr.sortingOrder = 9999;
            UnityEngine.Object.DontDestroyOnLoad(go);

            // name tag is a SEPARATE object (not parented to the tiny cursor) so
            // it isn't shrunk with it — this is what makes the name readable.
            GameObject tagObj = new GameObject("NameTag_" + id);
            UnityEngine.Object.DontDestroyOnLoad(tagObj);
            TextMesh tm = tagObj.AddComponent<TextMesh>();
            tm.text = string.IsNullOrEmpty(name) ? "Friend" : name;
            tm.fontSize = 64; tm.characterSize = 0.02f; tm.anchor = TextAnchor.LowerCenter;
            tm.alignment = TextAlignment.Center; tm.color = col; tm.fontStyle = FontStyle.Bold;
            tagObj.GetComponent<MeshRenderer>().sortingOrder = 10000;

            rc = new RemoteCursor { go = go, nameTag = tm, target = Vector3.zero, lastSeen = Time.time, colorIndex = idx };
            cursors[id] = rc; return rc;
        }

        Sprite BuildCursorSprite(Color c)
        {
            Texture2D tex = new Texture2D(32, 32);
            tex.filterMode = FilterMode.Point;
            Color[] pix = new Color[1024];
            for (int i = 0; i < 1024; i++)
            {
                float dx = (i % 32) - 16f, dy = (i / 32) - 16f, dist = Mathf.Sqrt(dx * dx + dy * dy);
                if (dist < 11f) pix[i] = new Color(c.r, c.g, c.b, 0.95f);   // player-colored fill
                else if (dist < 15f) pix[i] = Color.white;                  // white outline ring
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
                    if (rc.nameTag != null) UnityEngine.Object.Destroy(rc.nameTag.gameObject);
                    (drop ?? (drop = new List<string>())).Add(kv.Key); continue;
                }
                rc.go.transform.position = Vector3.Lerp(rc.go.transform.position, rc.target, Time.deltaTime * 12f);

                // pointer normally, closed hand while that player is grabbing
                SpriteRenderer sr = rc.go.GetComponent<SpriteRenderer>();
                bool usingArt = pointerSprites[rc.colorIndex] != null;
                if (sr != null && usingArt)
                {
                    Sprite want = (rc.grab && grabSprites[rc.colorIndex] != null) ? grabSprites[rc.colorIndex] : pointerSprites[rc.colorIndex];
                    if (want != null && sr.sprite != want) sr.sprite = want;
                }
                float cs = usingArt ? 0.16f : 0.22f;   // custom art is larger, so scale it down
                rc.go.transform.localScale = new Vector3(scale * cs, scale * cs, 1f);

                if (rc.nameTag != null)
                {
                    rc.nameTag.transform.position = rc.go.transform.position + new Vector3(0f, 0.55f * scale, 0f);
                    rc.nameTag.transform.localScale = new Vector3(scale, scale, scale);
                }
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

            // scale text to the screen so it's readable at any resolution
            int bf = Mathf.Clamp(Mathf.RoundToInt(Screen.height * 0.020f), 16, 30);

            _titleStyle = new GUIStyle(GUI.skin.label); _titleStyle.fontSize = bf + 3; _titleStyle.fontStyle = FontStyle.Bold;
            _titleStyle.normal.textColor = new Color(0.55f, 0.85f, 1f);

            _statusStyle = new GUIStyle(GUI.skin.label); _statusStyle.fontSize = bf; _statusStyle.wordWrap = true;
            _statusStyle.normal.textColor = new Color(0.85f, 0.87f, 0.9f);

            _codeStyle = new GUIStyle(GUI.skin.label); _codeStyle.fontSize = bf * 2; _codeStyle.fontStyle = FontStyle.Bold;
            _codeStyle.alignment = TextAnchor.MiddleCenter; _codeStyle.normal.textColor = Color.white;

            _btnStyle = new GUIStyle(GUI.skin.button); _btnStyle.fontSize = bf + 1; _btnStyle.fontStyle = FontStyle.Bold;
            _btnStyle.padding = new RectOffset(8, 8, 8, 8);

            _fieldStyle = new GUIStyle(GUI.skin.textField); _fieldStyle.fontSize = bf + 2; _fieldStyle.fontStyle = FontStyle.Bold;
            _fieldStyle.padding = new RectOffset(6, 6, 6, 6);

            _stylesReady = true;
        }

        void OnGUI()
        {
            EnsureStyles();
            bool menu = OnMainMenu();

            if (menu)
            {
                // Main menu: the "multiplayer" text button toggles the SAME full
                // panel we use in-game (shown as a centered modal, off to the
                // right so it doesn't cover the game's menu list).
                DrawMenuEntry();
                if (menuPopupOpen)
                {
                    Color o = GUI.color;
                    GUI.color = new Color(0f, 0f, 0f, 0.55f);
                    GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
                    GUI.color = o;
                    float mpw = Mathf.Clamp(Screen.width * 0.26f, 400f, 580f);
                    float mph = Mathf.Clamp(Screen.height * 0.60f, 440f, 780f);
                    DrawPanel(new Rect(Screen.width * 0.18f, Mathf.Max(20f, (Screen.height - mph) / 2f), mpw, mph), true);
                }
                return;
            }

            // In-game: status pill + panel, anchored top-middle. Sizes scale
            // with the screen so text never clips.
            float panW = Mathf.Clamp(Screen.width * 0.26f, 400f, 580f);
            float panH = Mathf.Clamp(Screen.height * 0.60f, 440f, 780f);
            float cx = (Screen.width - panW) / 2f;
            DrawPill(cx, panW);
            if (connected) DrawChat();
            if (!panelOpen) return;
            DrawPanel(new Rect(cx, 42, panW, panH), false);
        }

        void DrawChat()
        {
            int fs = _statusStyle.fontSize + 3;
            float lineH = fs + 10f;
            // sit well above the game's bottom toolbar so it never overlaps it
            float baseY = Screen.height - Mathf.Max(170f, Screen.height * 0.16f);

            GUIStyle st = new GUIStyle(_statusStyle);
            st.fontSize = fs; st.fontStyle = FontStyle.Bold; st.padding = new RectOffset(8, 8, 3, 3);

            float y = chatActive ? baseY - lineH - 6f : baseY;
            for (int i = chatLog.Count - 1; i >= 0; i--)
            {
                ChatLine cl = chatLog[i];
                float age = Time.time - cl.time;
                float a = age < 10f ? 1f : Mathf.Clamp01(1f - (age - 10f) / 2f);
                if (a <= 0.02f) continue;
                string line = cl.name + ": " + cl.text;
                Vector2 sz = st.CalcSize(new GUIContent(line));
                float wbox = Mathf.Min(sz.x + 4f, Screen.width * 0.55f);
                // dark backing so it's readable over the scene
                Color oc = GUI.color; GUI.color = new Color(0f, 0f, 0f, 0.55f * a);
                GUI.DrawTexture(new Rect(12f, y, wbox, lineH), Texture2D.whiteTexture);
                GUI.color = oc;
                st.normal.textColor = new Color(cl.color.r, cl.color.g, cl.color.b, a);
                GUI.Label(new Rect(12f, y, wbox, lineH), line, st);
                y -= lineH;
                if (y < 60f) break;
            }

            if (chatActive)
            {
                Event e = Event.current;
                if (e != null && e.type == EventType.KeyDown && Time.frameCount != chatOpenedFrame)
                {
                    if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { SendChat(); e.Use(); }
                    else if (e.keyCode == KeyCode.Escape) { chatActive = false; chatInput = ""; e.Use(); }
                }
                Rect ir = new Rect(12f, baseY, Screen.width * 0.45f, lineH + 4f);
                Color oc = GUI.color; GUI.color = new Color(0f, 0f, 0f, 0.8f);
                GUI.DrawTexture(ir, Texture2D.whiteTexture); GUI.color = oc;
                GUIStyle fld = new GUIStyle(_fieldStyle); fld.fontSize = fs;
                GUI.SetNextControlName("ppgchat");
                chatInput = GUI.TextField(ir, chatInput ?? "", 200, fld);
                GUI.FocusControl("ppgchat");
            }
        }

        void DrawPill(float x, float w)
        {
            Color pc; string ptxt;
            if (state == ConnState.Connected) { pc = new Color(0.3f, 0.9f, 0.45f); ptxt = "● Connected  ·  Room " + room + "  ·  " + (friendCount + 1) + " online  ·  " + Mathf.RoundToInt(lastPing) + "ms"; }
            else if (state == ConnState.Connecting) { pc = new Color(1f, 0.8f, 0.2f); ptxt = "● Connecting…"; }
            else if (state == ConnState.Failed) { pc = new Color(1f, 0.4f, 0.4f); ptxt = "● Connection failed"; }
            else { pc = new Color(0.65f, 0.65f, 0.7f); ptxt = "● Multiplayer offline"; }

            // wide enough for the full line, and tall enough for the scaled font
            float pw = Mathf.Clamp(Screen.width * 0.44f, 480f, 900f);
            float ph = _statusStyle.fontSize + 18f;
            Rect r = new Rect((Screen.width - pw) / 2f, 8f, pw, ph);
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
            GUIStyle pill = new GUIStyle(_statusStyle);
            pill.fontSize = _statusStyle.fontSize; pill.fontStyle = FontStyle.Bold;
            pill.alignment = TextAnchor.MiddleCenter; pill.padding = new RectOffset(10, 10, 4, 4);
            pill.normal.textColor = pc; pill.hover.textColor = Color.white;
            if (GUI.Button(r, ptxt, pill)) panelOpen = !panelOpen;
        }

        // Unified multiplayer panel — used both in-game and on the main menu.
        // onMenu=true routes actions through "load a sandbox first, then connect".
        void DrawPanel(Rect area, bool onMenu)
        {
            GUILayout.BeginArea(area, _boxStyle);
            GUILayout.Label("PEOPLE PLAYGROUND MULTIPLAYER", _titleStyle);
            GUILayout.Space(6);

            if (state == ConnState.Connected)
            {
                GUILayout.Label("Room code (share it with a friend):", _statusStyle);
                GUILayout.Label(room, _codeStyle);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Copy code", _btnStyle)) { GUIUtility.systemCopyBuffer = room; ModAPI.Notify("Room code copied!"); }
                if (GUILayout.Button("Leave", _btnStyle)) Disconnect();
                GUILayout.EndHorizontal();
                GUILayout.Space(6);
                if (GUILayout.Button("Share my whole scene", _btnStyle)) ShareWholeScene();
                if (soloTest) GUILayout.Label("SOLO SELF-TEST active (echoing you back)", Colored(new Color(1f, 0.8f, 0.3f)));
                GUILayout.Space(10);

                // so two players can instantly confirm they're on the SAME server
                GUILayout.Label("Server: " + ShortUrl(relayUrl), Colored(new Color(0.6f, 0.6f, 0.65f)));
                GUILayout.Space(4);

                GUILayout.Label("Players (" + (friendCount + 1) + ")", _statusStyle);
                PlayerRow(myName + "   (you)", ColorForId(myId), mySteam);
                foreach (KeyValuePair<string, string> kv in friendNames)
                    if (knownFriends.Contains(kv.Key))
                    {
                        string fs; friendSteams.TryGetValue(kv.Key, out fs);
                        PlayerRow(kv.Value, ColorForId(kv.Key), fs);
                    }
                if (friendCount == 0) GUILayout.Label("Waiting for someone to join…", Colored(new Color(1f, 0.8f, 0.3f)));
            }
            else if (state == ConnState.Connecting)
            {
                GUILayout.Label("Connecting…", Colored(new Color(1f, 0.8f, 0.2f)));
                GUILayout.Label("Room " + room, _statusStyle);
                GUILayout.Space(4);
                GUILayout.Label("If the server was asleep this can take up to a minute the first time. Hang tight.", _statusStyle);
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

                if (GUILayout.Button("HOST A NEW ROOM", _btnStyle)) { if (onMenu) MenuHost(); else Host(); }
                GUILayout.Space(6);
                if (GUILayout.Button("SOLO SELF-TEST", _btnStyle)) { if (onMenu) MenuSolo(); else StartSoloTest(); }
                GUILayout.Space(10);

                GUILayout.Label("Or join a friend's room:", _statusStyle);
                GUILayout.BeginHorizontal();
                joinField = GUILayout.TextField((joinField ?? "").ToUpper(), 5, _fieldStyle);
                if (GUILayout.Button("JOIN", _btnStyle, GUILayout.Width(66)))
                {
                    string c = (joinField ?? "").Trim();
                    if (c.Length == 5) { if (onMenu) MenuJoin(c); else Join(c); }
                    else ModAPI.Notify("Room code must be 5 characters");
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label(onMenu ? "Click \"multiplayer\" again to close" : "M = show / hide this menu", Colored(new Color(0.5f, 0.5f, 0.55f)));
            GUILayout.EndArea();
        }

        string ShortUrl(string u)
        {
            if (string.IsNullOrEmpty(u)) return "(none)";
            string s = u.Replace("https://", "").Replace("http://", "").TrimEnd('/');
            return s;
        }

        void PlayerRow(string name, Color dot, string steam)
        {
            GUILayout.BeginHorizontal();
            int sz = _statusStyle.fontSize + 10;
            Rect r = GUILayoutUtility.GetRect(sz, sz, GUILayout.Width(sz), GUILayout.Height(sz));
            Sprite av = null;
            if (!string.IsNullOrEmpty(steam)) avatarCache.TryGetValue(steam, out av);
            if (av != null && av.texture != null) GUI.DrawTexture(r, av.texture, ScaleMode.ScaleToFit);
            else GUI.Label(r, "●", Colored(dot));
            GUILayout.Space(6);
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
        }

        void MenuSolo()
        {
            pendingHost = false; pendingJoin = false; pendingSolo = true;
            menuPopupOpen = false;
            LaunchIntoSandbox("Solo self-test: dropping into a sandbox...");
        }

        void OnDestroy()
        {
            if (codeDisplay != null) UnityEngine.Object.Destroy(codeDisplay);
            if (statusDisplay != null) UnityEngine.Object.Destroy(statusDisplay);
            foreach (var c in cursors.Values) { if (c.go != null) UnityEngine.Object.Destroy(c.go); if (c.nameTag != null) UnityEngine.Object.Destroy(c.nameTag.gameObject); }
        }
    }
}
