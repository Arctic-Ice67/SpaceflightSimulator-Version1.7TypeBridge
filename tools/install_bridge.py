#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Install / uninstall the TypeBridge bootstrap into the SFS 1.7 install.

  * copies TypeBridge.dll into Spaceflight Simulator_Data/Managed
  * appends it to ScriptingAssemblies.json  (names + types)
  * appends a SubsystemRegistration entry to RuntimeInitializeOnLoads.json
  * keeps .orig backups so `uninstall` restores byte-identical files
"""
import json, os, shutil, sys
try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

GAME = r"D:\2SFS Project\Spaceflight Simulator1.7"
DATA = os.path.join(GAME, "Spaceflight Simulator_Data")
MANAGED = os.path.join(DATA, "Managed")
WORK = r"D:\2SFS Project\work\sfs17_compare"

SA = os.path.join(DATA, "ScriptingAssemblies.json")
RI = os.path.join(DATA, "RuntimeInitializeOnLoads.json")
ENTRY = {"assemblyName": "TypeBridge", "nameSpace": "", "className": "TypeBridge",
         "methodName": "Install", "loadTypes": 4, "isUnityClass": False}

def backup(p):
    o = p + ".orig"
    if not os.path.exists(o):
        shutil.copy2(p, o)
        print("  备份 ->", os.path.basename(o))
    else:
        print("  备份已存在:", os.path.basename(o))

def install():
    src = os.path.join(WORK, "TypeBridge.dll")
    assert os.path.exists(src), "先编译 TypeBridge.dll"
    shutil.copy2(src, os.path.join(MANAGED, "TypeBridge.dll"))
    print("  已复制 TypeBridge.dll -> Managed/")

    backup(SA); backup(RI)

    j = json.load(open(SA, encoding="utf-8"))
    if "TypeBridge.dll" not in j["names"]:
        j["names"].append("TypeBridge.dll")
        j["types"].append(2)
    json.dump(j, open(SA, "w", encoding="utf-8"), ensure_ascii=False)
    print("  ScriptingAssemblies.json 现在 %d 项, 含 TypeBridge=%s"
          % (len(j["names"]), "TypeBridge.dll" in j["names"]))

    r = json.load(open(RI, encoding="utf-8"))
    r["root"] = [e for e in r["root"]
                 if not (e.get("assemblyName") == "TypeBridge" and e.get("methodName") == "Install")]
    r["root"].append(ENTRY)
    json.dump(r, open(RI, "w", encoding="utf-8"), ensure_ascii=False)
    print("  RuntimeInitializeOnLoads.json 现在 %d 项, 末尾=%s"
          % (len(r["root"]), r["root"][-1]))

def uninstall():
    d = os.path.join(MANAGED, "TypeBridge.dll")
    if os.path.exists(d):
        os.remove(d); print("  已删除 Managed/TypeBridge.dll")
    for p in (SA, RI):
        o = p + ".orig"
        if os.path.exists(o):
            shutil.copy2(o, p); print("  已还原", os.path.basename(p))

if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "install"
    print("=== %s ===" % cmd)
    (install if cmd == "install" else uninstall)()
