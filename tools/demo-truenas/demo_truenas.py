"""Demo-TrueNAS: simuliert die TrueNAS-25.04-API mit AD-Benutzern, Ordnern und Rechten – zum Ausprobieren ohne echtes NAS.

Start:  pip install websockets && python demo_truenas.py
Anmelden in NAS-Zentrale: Adresse http://127.0.0.1:8765, Benutzer FIRMA\\admin, Passwort geheim
"""
import asyncio, json, copy, itertools, sys
import websockets

PERMS = ["READ_DATA","WRITE_DATA","APPEND_DATA","READ_NAMED_ATTRS","WRITE_NAMED_ATTRS","EXECUTE","DELETE_CHILD",
         "READ_ATTRIBUTES","WRITE_ATTRIBUTES","DELETE","READ_ACL","WRITE_ACL","WRITE_OWNER","SYNCHRONIZE"]
FLAGS = ["FILE_INHERIT","DIRECTORY_INHERIT","NO_PROPAGATE_INHERIT","INHERIT_ONLY","INHERITED"]

USERS = {"FIRMA\\max": 1000001, "FIRMA\\anna": 1000002, "FIRMA\\admin": 1000003, "FIRMA\\tom": 1000004}
GROUPS = {"FIRMA\\buchhaltung": 2000001, "FIRMA\\vertrieb": 2000002, "FIRMA\\geschaeftsfuehrung": 2000003,
          "FIRMA\\nas-admins": 2000004, "FIRMA\\domain users": 2000005, "builtin_users": 545, "builtin_administrators": 544}
MEMBERS = {"FIRMA\\max": [2000001, 2000005], "FIRMA\\anna": [2000002, 2000005], "FIRMA\\admin": [2000004, 2000005, 544],
           "FIRMA\\tom": [2000003, 2000005]}
NAMES = {**{v: k for k, v in USERS.items()}, **{v: k for k, v in GROUPS.items()}}

def perms(*names):
    return {p: (p in names) for p in PERMS}
FULL = perms(*PERMS)
MODIFY = perms(*[p for p in PERMS if p not in ("WRITE_ACL", "WRITE_OWNER")])
READ = perms("READ_DATA","READ_NAMED_ATTRS","EXECUTE","READ_ATTRIBUTES","READ_ACL","SYNCHRONIZE")
def flags(*names):
    return {f: (f in names) for f in FLAGS}
INH = flags("FILE_INHERIT","DIRECTORY_INHERIT")

def ace(tag, id_, p, f=INH, type_="ALLOW"):
    return {"tag": tag, "id": id_, "type": type_, "perms": copy.deepcopy(p), "flags": copy.deepcopy(f)}

def inherited_for(parent_aces, is_dir):
    out = []
    for a in parent_aces:
        f = a["flags"]; np_ = f.get("NO_PROPAGATE_INHERIT")
        nf = None
        if is_dir:
            if f.get("DIRECTORY_INHERIT"):
                nf = flags("INHERITED") if np_ else flags(*(["INHERITED"] + [k for k in ("FILE_INHERIT","DIRECTORY_INHERIT") if f.get(k)]))
            elif f.get("FILE_INHERIT") and not np_:
                nf = flags("INHERITED","FILE_INHERIT","INHERIT_ONLY")
        elif f.get("FILE_INHERIT"):
            nf = flags("INHERITED")
        if nf is not None:
            c = copy.deepcopy(a); c["flags"] = nf; out.append(c)
    return out

FS = {}  # path -> {"dir":bool, "acl":[...], "protected":bool, "uid":..,"gid":.., "size":..}
def add(path, is_dir=True, acl=None, protected=False, size=0, mount=False):
    parent = path.rsplit("/", 1)[0]
    if acl is None:
        acl = inherited_for(FS[parent]["acl"], is_dir)
    FS[path] = {"dir": is_dir, "acl": acl, "protected": protected, "uid": 1000003, "gid": 545, "size": size, "mount": mount}

root_acl = [ace("owner@", -1, FULL), ace("group@", -1, MODIFY), ace("GROUP", 2000004, FULL), ace("GROUP", 2000005, READ)]
FS["/mnt/tank"] = {"dir": True, "acl": copy.deepcopy(root_acl), "protected": False, "uid": 0, "gid": 0, "size": 0, "mount": True}
for ds in ["daten", "buchhaltung", "vertrieb", "verwaltung"]:
    FS[f"/mnt/tank/{ds}"] = {"dir": True, "acl": copy.deepcopy(root_acl), "protected": False, "uid": 0, "gid": 545, "size": 0, "mount": True}
FS["/mnt/tank/buchhaltung"]["acl"] = [ace("owner@", -1, FULL), ace("GROUP", 2000004, FULL), ace("GROUP", 2000001, MODIFY), ace("GROUP", 2000003, READ)]
add("/mnt/tank/daten/Projekte"); add("/mnt/tank/daten/Projekte/2025"); add("/mnt/tank/daten/Projekte/2026")
add("/mnt/tank/daten/Vorlagen"); add("/mnt/tank/daten/Projekte/2026/Angebot Müller.pdf", False, size=482113)
add("/mnt/tank/daten/Projekte/Übersicht.xlsx", False, size=33211)
add("/mnt/tank/daten/Personal", acl=[ace("owner@", -1, FULL), ace("GROUP", 2000004, FULL), ace("GROUP", 2000003, MODIFY)], protected=True)
add("/mnt/tank/daten/Personal/Verträge")
pv = inherited_for(FS["/mnt/tank/daten"]["acl"], True) + [ace("GROUP", 2000002, MODIFY)]
add("/mnt/tank/daten/Vertrieb intern", acl=pv)
add("/mnt/tank/daten/Vertrieb intern/Kunden"); add("/mnt/tank/daten/Vertrieb intern/Kunden/Liste.csv", False, size=9123)
add("/mnt/tank/daten/Hinweise.txt", False, size=1200)
add("/mnt/tank/buchhaltung/2026"); add("/mnt/tank/buchhaltung/2026/Belege"); add("/mnt/tank/buchhaltung/Archiv")
add("/mnt/tank/vertrieb/Angebote")

SHARES = [
    {"id": 1, "name": "Daten", "path": "/mnt/tank/daten", "comment": "Allgemeine Ablage", "enabled": True, "abe": False, "ro": False},
    {"id": 2, "name": "Buchhaltung", "path": "/mnt/tank/buchhaltung", "comment": "", "enabled": True, "abe": True, "ro": False},
    {"id": 3, "name": "Vertrieb", "path": "/mnt/tank/vertrieb", "comment": "", "enabled": True, "abe": False, "ro": False},
]
SHARE_ACL = {s["name"]: [{"ae_who_sid": "S-1-1-0", "ae_who_str": "Everyone", "ae_who_id": None, "ae_perm": "FULL", "ae_type": "ALLOWED"}] for s in SHARES}
JOBS = {}
jobid = itertools.count(100)
CALLS = []

def who(a):
    if a["tag"] in ("USER", "GROUP"):
        return NAMES.get(a["id"], str(a["id"]))
    return None

def getacl(path, simplified=True, resolve_ids=False):
    n = FS[path]
    acl = copy.deepcopy(n["acl"])
    for a in acl:
        a["who"] = who(a)
        if a["tag"].endswith("@"): a["id"] = -1
    return {"path": path, "uid": n["uid"], "gid": n["gid"], "user": NAMES.get(n["uid"], "root"), "group": NAMES.get(n["gid"], "wheel"),
            "acltype": "NFS4", "trivial": False, "aclflags": {"autoinherit": True, "protected": n["protected"], "defaulted": False}, "acl": acl}

def children(path):
    return sorted(p for p in FS if p != path and p.rsplit("/", 1)[0] == path)

def setacl(data):
    path = data["path"]
    dacl = data["dacl"]
    for a in dacl:
        for k in PERMS: a["perms"].setdefault(k, False)
        for k in FLAGS: a["flags"].setdefault(k, False)
    FS[path]["acl"] = [{k: v for k, v in a.items() if k in ("tag", "id", "type", "perms", "flags")} for a in dacl]
    FS[path]["protected"] = data.get("nfs41_flags", {}).get("protected", False)
    if data.get("options", {}).get("recursive"):
        def rec(p):
            for c in children(p):
                if FS[c]["mount"]: continue
                FS[c]["acl"] = inherited_for(FS[p]["acl"], FS[c]["dir"]); FS[c]["protected"] = False
                if FS[c]["dir"]: rec(c)
        rec(path)
    return None

def job(result=None):
    j = next(jobid); JOBS[j] = {"id": j, "state": "SUCCESS", "result": result, "progress": {"percent": 100, "description": "done"}, "error": None}
    return j

def handle(method, params):
    CALLS.append(method)
    if method == "auth.login_ex":
        p = params[0]
        return {"response_type": "SUCCESS" if p["password"] == "geheim" else "AUTH_ERR"}
    if method == "auth.me":
        return {"pw_name": "FIRMA\\admin", "pw_uid": 1000003, "pw_gid": 2000005, "pw_groups": [2000004, 2000005, 544],
                "privilege": {"roles": ["FULL_ADMIN"], "web_shell": True}}
    if method == "core.ping": return "pong"
    if method == "system.info": return {"version": "25.04.2.1", "hostname": "nas"}
    if method == "smb.config": return {"workgroup": "FIRMA"}
    if method == "pool.dataset.query":
        out = []
        for name in ["tank", "tank/daten", "tank/buchhaltung", "tank/vertrieb", "tank/verwaltung", "tank/.system", "tank/ix-apps"]:
            mp = "/mnt/" + name if "ix-" not in name else "/mnt/.ix-apps"
            out.append({"id": name, "name": name, "type": "FILESYSTEM", "mountpoint": mp,
                        "acltype": {"value": "NFSV4" if name != "tank/.system" else "POSIX"},
                        "used": {"parsed": 1024**3 * 37, "value": "37G"}, "available": {"parsed": 1024**4 * 2, "value": "2T"}})
        return out
    if method == "sharing.smb.query": return SHARES
    if method == "user.query":
        f = params[0]
        local = [x for x in f if x[0] == "local"][0][2]
        if local: return [{"username": "truenas_admin", "uid": 950, "full_name": "Local Admin", "local": True, "builtin": False}]
        return [{"username": k, "uid": v, "full_name": k.split("\\")[1].title(), "local": False, "builtin": False, "sid": f"S-1-5-21-1-{v}"} for k, v in USERS.items()]
    if method == "group.query":
        local = [x for x in params[0] if x[0] == "local"][0][2]
        if local: return [{"group": "builtin_users", "gid": 545, "local": True, "builtin": True}, {"group": "wheel", "gid": 0, "local": True, "builtin": True}]
        return [{"group": k, "gid": v, "local": False, "builtin": False, "sid": f"S-1-5-21-2-{v}"} for k, v in GROUPS.items() if k.startswith("FIRMA")]
    if method == "user.get_user_obj":
        p = params[0]; n = p["username"]
        if n not in USERS: raise KeyError(n)
        r = {"pw_name": n, "pw_uid": USERS[n], "pw_gid": 2000005}
        if p.get("get_groups"): r["grouplist"] = MEMBERS[n]
        return r
    if method == "group.get_group_obj":
        n = params[0]["groupname"]
        if n not in GROUPS: raise KeyError(n)
        return {"gr_name": n, "gr_gid": GROUPS[n]}
    if method == "filesystem.listdir":
        path = params[0]
        return [{"name": c.rsplit("/", 1)[1], "path": c, "type": "DIRECTORY" if FS[c]["dir"] else "FILE", "size": FS[c]["size"],
                 "is_mountpoint": FS[c]["mount"], "is_ctldir": False, "uid": FS[c]["uid"], "gid": FS[c]["gid"]} for c in children(path)]
    if method == "filesystem.getacl": return getacl(*params)
    if method == "filesystem.setacl": setacl(params[0]); return job()
    if method == "filesystem.chown":
        p = params[0]
        if "uid" in p: FS[p["path"]]["uid"] = p["uid"]
        if "gid" in p: FS[p["path"]]["gid"] = p["gid"]
        return job()
    if method == "core.get_jobs":
        jid = params[0][0][2]; return [JOBS[jid]]
    if method == "sharing.smb.getacl":
        n = params[0]["share_name"]; return {"share_name": n, "share_acl": SHARE_ACL.get(n, [])}
    if method == "sharing.smb.setacl":
        SHARE_ACL[params[0]["share_name"]] = params[0]["share_acl"]; return params[0]
    if method == "sharing.smb.create":
        s = dict(params[0]); s["id"] = max(x["id"] for x in SHARES) + 1; SHARES.append(s); return s
    if method == "sharing.smb.update":
        s = [x for x in SHARES if x["id"] == params[0]][0]; s.update(params[1]); return s
    if method == "sharing.smb.delete":
        SHARES[:] = [x for x in SHARES if x["id"] != params[0]]; return True
    if method == "pool.dataset.create":
        return {"name": params[0]["name"]}
    raise NotImplementedError(method)

async def serve(ws):
    async for msg in ws:
        req = json.loads(msg)
        try:
            res = handle(req["method"], req.get("params", []))
            out = {"jsonrpc": "2.0", "id": req["id"], "result": res}
        except Exception as e:
            out = {"jsonrpc": "2.0", "id": req["id"], "error": {"code": -32001, "message": "Method call error",
                   "data": {"error": 22, "errname": "EINVAL", "reason": f"{type(e).__name__}: {e}"}}}
            print("ERROR", req["method"], e, flush=True)
        await ws.send(json.dumps(out))

async def main():
    async with websockets.serve(serve, "127.0.0.1", 8765):
        print("Demo-TrueNAS läuft: http://127.0.0.1:8765  (Benutzer FIRMA\\admin, Passwort geheim)", flush=True)
        await asyncio.Future()

asyncio.run(main())
