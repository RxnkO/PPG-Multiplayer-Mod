# PPG Multiplayer — Setup & Play (v0.3, DLL build)

Lightweight online co-op for People Playground:

- A **clickable in-game menu** (press `M`) — Host, Join, connection status, player list
- A shared **room code** to connect with a friend over the internet
- **Live cursors** — you see each other's mouse moving, with name tags
- **Shared spawning** — spawn anything (or press `1`–`4`) and it appears for both of you

> **New in this build:** the relay URL is editable **in-game** (in the `M` menu), so
> you no longer have to rebuild the DLL when your tunnel address changes. And the menu
> now shows a real connection status — Connecting / Connected / Failed — with the actual
> error, instead of failing silently. See `DEPLOY_RELAY.md` to host the relay
> permanently so the mod works for everyone who downloads it.

## Why this is a DLL (important)

People Playground's mod compiler **hard-rejects networking code in source scripts**
(`UnityWebRequest`, sockets, etc.) — it shows `"UnityWebRequest" is a suspicious
identifier` and won't compile, even with "Reject suspicious mods" turned off.

The only way to run networking is to ship the code as a **precompiled DLL**, which
isn't run through the source scanner. So the real code lives in `src/Mod.cs` and you
compile it to `PPGMultiplayer.dll` with `build.bat`. This is the same approach the
big community multiplayer mod uses.

The DLL is still flagged "suspicious" (it talks to the internet), so **"Reject
suspicious mods" must be OFF** in game settings — for you and your friend.

---

## Step 1 — Relay server online (one time)

The relay (`relay_server.py`, pure Python) is what lets two PCs talk. Pick one:

**Your PC + free tunnel (quickest):**
```powershell
# terminal 1 — the relay itself:
python relay_server.py            # -> "listening on 0.0.0.0:8080"
# terminal 2 — expose it (leave BOTH running while you play):
npx cloudflared tunnel --url http://localhost:8080
```
Copy the `https://...trycloudflare.com` URL it prints. (Restarting the tunnel gives a
NEW url each time.) Test it: open that url in a browser → you should see
`{"status": "ok", "rooms": 0}`.

**Always-on (Railway/Render/Replit):** deploy `relay_server.py`, start command
`python3 relay_server.py`. These set `$PORT` automatically (the script reads it).

---

## Step 2 — Set the relay URL

**Easiest (no rebuild):** launch the game, press `M` to open the menu, and paste your
URL into the **Relay server URL** field before you Host/Join. Do this whenever your
tunnel address changes — no rebuilding.

**Or bake in a default** (so players don't have to paste anything) — edit `src/Mod.cs`:
```csharp
private const string DEFAULT_RELAY_URL = "https://abc-def-ghi.trycloudflare.com";
```
Use a **permanent** URL here (see `DEPLOY_RELAY.md`) so it never goes stale, then build
and upload once.

---

## Step 3 — Build the DLL

Double-click **`build.bat`** (or run it in a terminal). It:
- finds the game's `Managed` folder automatically (two levels up),
- finds the C# compiler that ships with Windows (.NET Framework `csc.exe`),
- compiles `src/Mod.cs` → `PPGMultiplayer.dll` in this folder.

On success it prints `[OK] Built PPGMultiplayer.dll`. If it prints compiler errors,
copy them back to Claude. (Re-run `build.bat` every time you change the URL or code.)

> No `csc.exe`? Install the ".NET Framework 4.x Developer Pack", or open `src/Mod.cs`
> in a Visual Studio "Class Library (.NET Framework)" project, add every DLL in the
> game's `Managed` folder as references, and build to `PPGMultiplayer.dll`.

---

## Step 4 — Turn off suspicious-mod rejection

In People Playground: **Settings → "Reject suspicious mods" → OFF**, then **fully quit
and relaunch** the game. (Required because any networking mod is flagged.) Your friend
must do this too.

---

## Step 5 — Play

**From the main menu:** a **`multiplayer`** text button now sits under `information`.
Click it → a popup asks for your friend's **room code** (and shows the relay URL). Enter
the code and hit **JOIN** (or **HOST NEW**), and you'll auto-connect the moment a
sandbox loads. The first time you play, the mod remembers your sandbox scene so it can
drop you straight in on later joins; until then it'll say "press PLAY."

> Note: joining puts you in your friend's live session — you'll see their cursor and
> everything spawned *after* you join. Replicating their **existing build** on join
> needs full level-sync (a planned next step), not just the current object streaming.

### Testing alone (no friend needed)

Open the menu (`M` in a sandbox, or the main-menu popup) and click **SOLO SELF-TEST**.
The relay mirrors *you* back beside yourself: you'll see a **`(echo)` cursor** following
your mouse ~3 m to the right, and anything you spawn gets **duplicated** next to it. If
that ghost appears and moves in sync, the whole pipeline works. (It still needs a live
relay URL — that's exactly what it's testing.)

### Loading the host's existing build

Once connected as host, click **"Share my whole scene →"**. It scans every physics
object already in your sandbox and starts streaming them, so a friend who joins sees
your existing creation — not just things spawned after they arrived. (Objects only
re-appear on their side if the item name matches a catalog item; heavily edited or
custom pieces may not.)

**In a sandbox map:** press **`M`** to open the multiplayer menu:

1. Make sure the **Relay server URL** is correct (paste if needed).
2. Click **HOST A NEW ROOM** → a 5-char code appears. Click **Copy code** and send it.
3. Your friend pastes the code into the **Join** field and clicks **JOIN**.
4. The status pill (top-left) goes **green — Connected** and shows who's online. The
   menu auto-collapses when connected; press `M` any time to reopen it.

Everything you spawn from the catalog is shared. Hotkeys still work as a fallback:

| Key       | Action                                                    |
|-----------|-----------------------------------------------------------|
| `M`       | Show / hide the multiplayer menu                          |
| `1`–`4`   | Quick-spawn a shared item at your mouse                   |
| `Esc`     | Leave the room                                            |

Quick-spawn items are set near the top of `src/Mod.cs` (`SPAWN_ITEMS`); rebuild after
changing them.

---

## Step 6 — Share it (folder OR Workshop)

- **Send the folder:** zip `PPG_Multiplayer` (including the built `PPGMultiplayer.dll`
  with your URL baked in) → friend drops it in their `...\People Playground\Mods\`.
- **Steam Workshop:** in-game uploader. Upload *after* building, so the DLL ships.

Either way your friend still needs "Reject suspicious mods" OFF.

---

## Troubleshooting

- **I rebuilt but nothing changed / I see nothing new after compiling:** two things
  must both happen. (1) Run **`build.bat`** — this is what turns your edited
  `src/Mod.cs` into a new `PPGMultiplayer.dll`. The in-game "Recompile" button does
  **not** do this; it only recompiles `loader.cs`. (2) Then in-game, open **mods →
  Recompile**. The loader now reloads the DLL from disk each time, so changes show up;
  if they still don't, **fully quit and relaunch** the game once. You're on the new
  build when the load message reads *"Multiplayer Mod Loaded! (v0.4 …)"*.
- **Mod doesn't appear / "Multiplayer Mod Loaded!" never shows:** confirm
  `PPGMultiplayer.dll` exists next to `mod.json`, that `mod.json` has
  `"AssemblyName": "PPGMultiplayer"` and `"Scripts": []`, then Recompile/reload.
- **Status pill says "Connection failed":** the menu now shows the actual error under
  the URL field. Open the relay url in a browser — if it's not `{"status":"ok"}` the
  server/tunnel is down. Check both players used the **same url** and **same room
  code** (letters + 2-9, case-insensitive).
- **Friend can't connect but you can:** almost always a **dead/mismatched URL** — their
  copy has an old tunnel address. Have them press `M` and paste your current URL, or
  host the relay permanently (`DEPLOY_RELAY.md`) so it never changes.
- **"Set a relay URL first":** the URL field is empty or not a valid `http(s)://` link.
- **Spawn says "Item not found":** that name isn't a valid catalog item — edit
  `SPAWN_ITEMS` and rebuild.

---

## How it works

`relay_server.py` is a dumb switchboard. ~12×/sec the mod POSTs `/sync`: its cursor +
any new spawn events + the highest event `seq` it has seen. The relay stores the
cursor, appends events to the room log, and returns everyone else's cursors + events
newer than your `seq` (excluding your own). No game logic on the server.

### Next steps
1. **Live physics:** host-authoritative transform streaming — host sends
   `{nid,x,y,rot}` for networked rigidbodies each tick; clients lerp. Keep it on a
   "latest snapshot" channel, not the event log.
2. **Despawn/ownership:** track `nid -> GameObject` so deletes replicate.
3. **Steam P2P:** for a no-server Workshop release, swap the HTTP relay for Steamworks
   lobbies (what the big community mod does).
