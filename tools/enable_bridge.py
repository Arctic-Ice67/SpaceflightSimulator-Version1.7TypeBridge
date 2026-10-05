# -*- coding: utf-8 -*-
"""一键启用 / 禁用 / 体检 SFS 1.7 TypeBridge。

    python enable_bridge.py              # 体检 + 安装
    python enable_bridge.py --check-only
    python enable_bridge.py --uninstall
    python enable_bridge.py --game "D:\\游戏目录"
"""
import argparse
import json
import re
import shutil
import subprocess
import sys
import os
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

HERE = Path(__file__).resolve().parent
DEFAULT_DLL = HERE / "TypeBridge.fixed.dll"
PREFLIGHT = HERE / "preflight.py"

ENTRY = {
    "assemblyName": "TypeBridge",
    "nameSpace": "",
    "className": "TypeBridge",
    "methodName": "Install",
    "loadTypes": 4,               # RuntimeInitializeLoadType.SubsystemRegistration（最早）
    "isUnityClass": False,
}

# 常见游戏位置
CANDIDATES = [
    r"D:\SFS17beta",
    r"D:\zm\新建文件夹 (3)\1.7 内测 10.2",
    r"D:\2SFS Project\Spaceflight Simulator1.7",
    r"C:\Program Files (x86)\Steam\steamapps\common\Spaceflight Simulator",
    r"D:\SteamLibrary\steamapps\common\Spaceflight Simulator",
    r"E:\SteamLibrary\steamapps\common\Spaceflight Simulator",
    r"G:\SteamLibrary\steamapps\common\Spaceflight Simulator",
]

# 记住上次选的路径，下次直接回车即可
CONFIG = HERE / ".bridge_game_path.txt"

OK = "[OK]"
WARN = "[!]"
ERR = "[X]"


def is_ascii(s):
    try:
        s.encode("ascii")
        return True
    except UnicodeEncodeError:
        return False


def has_game(p: Path):
    return (p / "Spaceflight Simulator_Data" / "Managed" / "Assembly-CSharp.dll").exists()


def resolve_game_path(raw):
    """把用户给的任意路径宽容地解析成游戏根目录。

    接受：游戏根目录 / `Spaceflight Simulator_Data` / `.../Managed` /
          游戏 exe / exe 所在目录 / 甚至游戏内的子目录（如 Mods）——
    做法是**逐级向上找**含 `Spaceflight Simulator_Data\\Managed\\Assembly-CSharp.dll` 的目录。
    这样用户直接把文件夹拖进来、或粘贴任意一层路径都能用。
    """
    if not raw:
        return None
    p = Path(str(raw).strip().strip('"').strip("'").rstrip("\\/"))
    try:
        if p.is_file():
            p = p.parent
    except OSError:
        return None
    for _ in range(6):
        if has_game(p):
            return p
        if p.parent == p:
            break
        p = p.parent
    return None


def load_saved():
    try:
        if CONFIG.exists():
            t = CONFIG.read_text(encoding="utf-8").strip()
            return t or None
    except Exception:
        pass
    return None


def save_path(game: Path):
    try:
        CONFIG.write_text(str(game), encoding="utf-8")
    except Exception:
        pass


def scan_candidates():
    out, seen = [], set()
    for c in CANDIDATES + ([load_saved()] if load_saved() else []):
        if not c:
            continue
        g = resolve_game_path(c)
        if g and str(g).lower() not in seen:
            seen.add(str(g).lower())
            out.append(g)
    return out


def prompt_game():
    """交互式选游戏目录。空白回车 = 用第 1 个候选或记住的路径。"""
    found = scan_candidates()
    print()
    print("  未指定游戏目录。以下是自动找到的：")
    if found:
        for i, g in enumerate(found, 1):
            mark = "  <- 上次用的" if (load_saved() and str(g) == load_saved()) else ""
            flag = "" if is_ascii(str(g)) else "   ⚠ 含中文（工具会自动建英文联接）"
            print("    [%d] %s%s%s" % (i, g, mark, flag))
    else:
        print("    （没找到任何候选）")
    print()
    print("  可以：输入序号 / 粘贴完整路径 / 把文件夹直接拖进本窗口 / 回车用第 1 个")
    print("  （注意：路径含 \u4e2d\u6587 也能用，工具会自动建 ASCII 目录联接）")
    try:
        raw = input("\n  游戏目录 > ").strip()
    except (EOFError, KeyboardInterrupt):
        return None
    if not raw:
        return found[0] if found else None
    if raw.isdigit():
        i = int(raw)
        if 1 <= i <= len(found):
            return found[i - 1]
        print("  %s 序号超出范围" % ERR)
        return None
    g = resolve_game_path(raw)
    if g is None:
        print("  %s 这个路径里找不到游戏（需要存在 "
              "Spaceflight Simulator_Data\\Managed\\Assembly-CSharp.dll）" % ERR)
    return g


def find_game(explicit=None):
    """优先级：命令行 --game > 交互输入 > 自动扫描。"""
    if explicit:
        g = resolve_game_path(explicit)
        if g:
            return g
        print("%s 指定路径里找不到游戏: %s" % (ERR, explicit))
        print("    （需要存在 Spaceflight Simulator_Data\\Managed\\Assembly-CSharp.dll）")
        return None
    # 自动扫描：唯一命中就不打扰用户
    found = scan_candidates()
    if len(found) == 1:
        print("\n自动找到游戏目录: %s" % found[0])
        return found[0]
    return prompt_game()


def make_junction(game: Path):
    """给中文路径的游戏建 ASCII 联接，返回可用路径（失败则原样返回）。"""
    if is_ascii(str(game)):
        return game, None
    base = game.drive or "D:"
    for name in ("SFS17beta", "SFS17", "SFS_TypeBridge"):
        dst = Path(base + "\\" + name)
        if dst.exists():
            # 已存在且指向同一目录则直接用
            if (dst / "Spaceflight Simulator_Data" / "Managed" / "Assembly-CSharp.dll").exists():
                return dst, dst
            continue
        r = subprocess.run(
            ["powershell", "-NoProfile", "-Command",
             'New-Item -ItemType Junction -Path "%s" -Target "%s" | Out-Null' % (dst, game)],
            capture_output=True, text=True)
        if r.returncode == 0 and (dst / "Spaceflight Simulator_Data" / "Managed" / "Assembly-CSharp.dll").exists():
            return dst, dst
    return game, None


def cecil_flavour(p: Path):
    b = p.read_bytes()
    return "GenericParameterConstraint" if b"GenericParameterConstraint" in b else "TypeReference"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=None)
    ap.add_argument("--dll", default=str(DEFAULT_DLL))
    ap.add_argument("--uninstall", action="store_true")
    ap.add_argument("--check-only", action="store_true")
    a = ap.parse_args()

    print("=" * 66)
    print("  SFS 1.7 模组兼容桥 TypeBridge —— 一键工具")
    print("=" * 66)

    game = find_game(a.game)
    # 没找到就让用户重试几次，而不是直接退出（双击 .bat 时最友好）
    tries = 0
    while game is None and not a.game and tries < 3:
        tries += 1
        try:
            again = input("\n  要再试一次吗？(直接回车重输 / 输入 n 退出) > ").strip().lower()
        except (EOFError, KeyboardInterrupt):
            break
        if again == "n":
            break
        game = prompt_game()
    if game is None:
        print("\n%s 没拿到有效的游戏目录，已退出。" % ERR)
        print("   也可以命令行指定: python enable_bridge.py --game \"D:\\你的游戏目录\"")
        return 2
    save_path(game)
    print("\n游戏目录: %s" % game)

    data = game / "Spaceflight Simulator_Data"
    managed = data / "Managed"
    sa, ri = data / "ScriptingAssemblies.json", data / "RuntimeInitializeOnLoads.json"

    # ---- 卸载 ----
    if a.uninstall:
        print("\n--- 卸载 ---")
        d = managed / "TypeBridge.dll"
        if d.exists():
            d.unlink(); print("  已删除 Managed/TypeBridge.dll")
        for f in (sa, ri):
            o = Path(str(f) + ".orig")
            if o.exists():
                shutil.copy2(o, f); print("  已还原 %s" % f.name)
        print("\n卸载完成（游戏恢复原状）。")
        return 0

    fail = 0
    # ---- 1. 中文路径 ----
    print("\n--- 1. 路径编码检查 ---")
    if is_ascii(str(game)):
        print("  %s 路径纯 ASCII" % OK)
        run_dir = game
    else:
        print("  %s 路径含非 ASCII 字符！Mono 会在这里抛 Illegal byte sequence，" % WARN)
        print("      导致 Harmony 失效 → 模组能显示但功能全挂。")
        print("      正在创建 ASCII 目录联接 …")
        run_dir, made = make_junction(game)
        if made:
            print("  %s 已建联接: %s -> %s" % (OK, run_dir, game))
            print("      请从 %s\\Spaceflight Simulator.exe 启动游戏！" % run_dir)
        else:
            print("  %s 建联接失败（可能需管理员权限）。请手动把游戏移到纯英文路径。" % ERR)
            fail += 1

    # ---- 2. 目标是否混淆版 ----
    print("\n--- 2. 目标版本检查 ---")
    raw = (managed / "Assembly-CSharp.dll").read_bytes()
    if b"__BB_OBFUSCATOR" not in raw:
        print("  %s 这个版本没有混淆，**不需要**桥（装了也白装）。" % WARN)
        print("      桥只对「1.7 内测混淆版」有意义。")
        fail += 1
    else:
        print("  %s 混淆版，需要桥" % OK)

    # ---- 3. Cecil API 匹配 ----
    print("\n--- 3. 依赖版本匹配 ---")
    dll = Path(a.dll)
    if not dll.exists():
        print("  %s 找不到 %s" % (ERR, dll)); return 2
    cecil = managed / "Mono.Cecil.dll"
    if cecil.exists():
        ver = sorted({x.decode() for x in re.findall(rb"0\.1[01]\.\d+\.\d+", cecil.read_bytes())})
        tf = cecil_flavour(cecil)
        # 从 DLL 里查它绑定的形态
        try:
            il = subprocess.run([r"C:/Users/afs/.dotnet/tools/ilspycmd.exe", "-il", str(dll)],
                                capture_output=True, text=True, encoding="utf-8",
                                errors="replace").stdout
        except Exception:
            il = ""
        bf = "GenericParameterConstraint" if ("GenericParameterConstraint" in il and "get_ConstraintType" in il) else "TypeReference"
        print("  目标 Mono.Cecil: %s / %s" % (ver, tf))
        print("  待装 DLL 绑定:   %s" % bf)
        if bf != tf:
            print("  %s 不匹配，运行时会 MissingMethodException（改写全失败）" % ERR)
            fail += 1
        else:
            print("  %s 匹配" % OK)

    if a.check_only:
        print("\n--- 仅体检，未做任何改动 ---")
        return 1 if fail else 0

    # ---- 4. 安装 ----
    print("\n--- 4. 安装 ---")
    shutil.copy2(dll, managed / "TypeBridge.dll")
    print("  %s Managed/TypeBridge.dll (%d 字节)" % (OK, (managed / "TypeBridge.dll").stat().st_size))

    for f in (sa, ri):
        o = Path(str(f) + ".orig")
        if not o.exists() and f.exists():
            shutil.copy2(f, o); print("  已备份 %s" % o.name)

    j = json.loads(sa.read_text(encoding="utf-8"))
    if "TypeBridge.dll" not in j["names"]:
        j["names"].append("TypeBridge.dll")
        j["types"].append(16)
        sa.write_text(json.dumps(j, ensure_ascii=False), encoding="utf-8")
        print("  %s ScriptingAssemblies.json 追加（共 %d 项）" % (OK, len(j["names"])))
    else:
        print("  %s ScriptingAssemblies.json 已注册" % OK)

    r = json.loads(ri.read_text(encoding="utf-8"))
    r["root"] = [e for e in r["root"]
                 if not (e.get("assemblyName") == "TypeBridge" and e.get("methodName") == "Install")]
    r["root"].append(ENTRY)
    ri.write_text(json.dumps(r, ensure_ascii=False), encoding="utf-8")
    print("  %s RuntimeInitializeOnLoads.json 注册 Install（loadTypes=4 最早回调）" % OK)

    # 清掉会让加载器报错的空目录
    stray = game / "Mods" / "TypeBridge"
    if stray.exists() and not any(stray.iterdir()):
        stray.rmdir(); print("  已删除空目录 Mods/TypeBridge（会让加载器报 FileNotFound）")

    print("\n" + "=" * 66)
    print("完成！")
    if not is_ascii(str(game)):
        print("⚠️  请从 **%s** 启动游戏（不要用原中文路径）" % run_dir)
    else:
        print("请启动游戏。")
    print("启动后日志: %s\\typebridge.log" % run_dir)
    print("游戏日志:   C:\\Users\\<你>\\AppData\\LocalLow\\Stef Morojna\\Spaceflight Simulator\\Player.log")
    print("卸载:       python enable_bridge.py --uninstall")
    print("=" * 66)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
