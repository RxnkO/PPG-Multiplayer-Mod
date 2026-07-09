#!/usr/bin/env python3
"""
PPG Multiplayer Relay  (v2 - cursors + object snapshots)
========================================================
A tiny, dependency-free switchboard so People Playground players can share
cursors AND world objects over the internet. It understands nothing about the
game - it just stores each player's latest snapshot and hands everyone else's
back.

Run it anywhere with a public URL (Replit / Railway / Render / a PC + cloudflared
or ngrok), then paste that URL into src/Mod.cs -> RELAY_URL and rebuild.

Endpoints
---------
GET  /        -> health check  {"status":"ok", ...}
POST /sync    -> one call per tick does everything (see below)

POST /sync request JSON:
  {
    "room":"ABCDE", "id":"p_8f3a", "name":"Serge",
    "x":1.2, "y":3.4,                         # this player's cursor
    "objs":[ {"nid":"p_8f3a_12","item":"Crate","x":1,"y":2,"rot":30}, ... ]
                                              # FULL snapshot of objects this
                                              # player OWNS (spawned), each tick
  }

POST /sync response JSON:
  {
    "now":1719...,
    "cursors":[ {"id","name","x","y","age"}, ... ],   # everyone except you
    "objs":[ {"nid","item","x","y","rot"}, ... ]      # union of everyone
                                                      # else's owned objects
  }
"""

import json, time, threading, os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT        = 8080
CURSOR_TTL  = 5.0     # seconds a player's data is considered "online"
ROOM_TTL    = 120.0   # silence before a room is dropped
MAX_OBJS    = 120     # cap objects per player (bounds payload size)

_lock = threading.Lock()
rooms = {}   # code -> { players: {id: {name,x,y,objs,t}}, t }


def _now(): return time.time()


def _gc():
    now = _now()
    for c in [c for c, r in rooms.items() if now - r["t"] > ROOM_TTL]:
        del rooms[c]
    for r in rooms.values():
        for pid in [p for p, d in r["players"].items() if now - d["t"] > ROOM_TTL]:
            del r["players"][pid]


def handle_sync(body):
    now = _now()
    code = str(body.get("room", "")).strip().upper()
    pid  = str(body.get("id", "")).strip()
    if not code or not pid:
        return {"error": "room and id required"}, 400

    # sanitise incoming object list
    objs_in = []
    for o in (body.get("objs") or [])[:MAX_OBJS]:
        try:
            objs_in.append({
                "nid":  str(o.get("nid", ""))[:48],
                "item": str(o.get("item", ""))[:48],
                "x": float(o.get("x", 0.0)),
                "y": float(o.get("y", 0.0)),
                "rot": float(o.get("rot", 0.0)),
            })
        except Exception:
            pass

    with _lock:
        _gc()
        room = rooms.get(code)
        if room is None:
            room = {"players": {}, "t": now}
            rooms[code] = room
        room["t"] = now
        room["players"][pid] = {
            "name": str(body.get("name", "Player"))[:24],
            "x": float(body.get("x", 0.0)),
            "y": float(body.get("y", 0.0)),
            "objs": objs_in,
            "t": now,
        }

        # SOLO SELF-TEST: when a client sends "echo": true, we hand its OWN
        # cursor + objects back (shifted sideways) so ONE player can verify the
        # full round-trip alone — you'll see a "ghost" of yourself beside you.
        echo = bool(body.get("echo", False))
        ECHO_DX = 3.0

        cursors, objs = [], []
        for opid, p in room["players"].items():
            is_self = (opid == pid)
            if is_self and not echo:
                continue
            if now - p["t"] > CURSOR_TTL:
                continue
            dx = ECHO_DX if is_self else 0.0
            suffix = " (echo)" if is_self else ""
            cursors.append({
                "id": (opid + "_echo") if is_self else opid,
                "name": p["name"] + suffix,
                "x": round(p["x"] + dx, 3), "y": round(p["y"], 3),
                "age": round(now - p["t"], 2),
            })
            for o in p["objs"]:
                objs.append({
                    "nid": (o["nid"] + "_echo") if is_self else o["nid"],
                    "item": o["item"],
                    "x": round(o["x"] + dx, 3), "y": round(o["y"], 3),
                    "rot": round(o["rot"], 1),
                })
        return {"now": round(now, 2), "cursors": cursors, "objs": objs}, 200


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a): pass

    def _send(self, code, obj):
        payload = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):
        with _lock:
            self._send(200, {"status": "ok", "rooms": len(rooms)})

    def do_POST(self):
        if not self.path.startswith("/sync"):
            self._send(404, {"error": "not found"}); return
        try:
            n = int(self.headers.get("Content-Length", 0))
            body = json.loads((self.rfile.read(n) if n else b"{}").decode("utf-8") or "{}")
        except Exception as e:
            self._send(400, {"error": "bad json: %s" % e}); return
        try:
            obj, status = handle_sync(body)
            self._send(status, obj)
        except Exception as e:
            self._send(500, {"error": str(e)})


def main():
    port = int(os.environ.get("PORT", PORT))
    srv = ThreadingHTTPServer(("0.0.0.0", port), Handler)
    print("PPG Multiplayer Relay v2 listening on 0.0.0.0:%d" % port)
    print("Health: GET /   |   Game: POST /sync")
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        print("\nbye")


if __name__ == "__main__":
    main()
