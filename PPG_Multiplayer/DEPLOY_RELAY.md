# Make the relay permanent (so the mod works for everyone)

Right now the mod points at a `trycloudflare.com` tunnel that only lives while
your PC is running and **changes every restart**. Anyone who downloads the mod
after that URL dies can't connect — that's the "it doesn't work" problem.

Fix it once by hosting the relay somewhere always-on. It's free and takes ~5 min.
After this, the URL never changes and you never re-upload the DLL again.

You only need to do **one** of the options below.

---

## Option A — Render (free, no card)

1. Push this folder to a GitHub repo (or fork it there).
2. Go to https://render.com → **New → Web Service** → connect the repo.
3. Render reads `render.yaml` automatically. Confirm:
   - Runtime: **Python**
   - Start command: `python relay_server.py`
4. Click **Create Web Service**. Wait for the first deploy.
5. Copy the service URL, e.g. `https://ppg-multiplayer-relay.onrender.com`.
6. Open the URL in a browser — you should see `{"status": "ok", "rooms": 0}`.

Note: Render's free tier sleeps after ~15 min idle. The first request after a
nap takes ~30 s to wake — the mod shows "Connecting…" then connects. Fine for
casual play; upgrade if you want zero cold starts.

## Option B — Railway (free trial credit)

1. Push this folder to GitHub.
2. https://railway.app → **New Project → Deploy from GitHub repo**.
3. Railway detects Python + the `Procfile` and runs `python relay_server.py`.
4. In the service, open **Settings → Networking → Generate Domain**.
5. Copy that URL and open it in a browser to confirm `{"status": "ok", ...}`.

Railway doesn't sleep, so connections are instant — but the free credit is
limited each month.

---

## After deploying

You have two ways to use the permanent URL:

- **Best (no rebuild):** in-game, press **M**, paste the URL into the
  **Relay server URL** field, then Host/Join. Tell players to do the same.
  Because the URL is now editable in-game, you never have to rebuild for a URL
  change again.

- **Bake it in as the default** so players don't paste anything: edit
  `src/Mod.cs`, set

  ```csharp
  private const string DEFAULT_RELAY_URL = "https://YOUR-permanent-url";
  ```

  then run `build.bat` and re-upload to the Workshop once. From then on the mod
  "just works" on launch.

Health check any time: open the URL in a browser. `{"status":"ok"}` = up.
