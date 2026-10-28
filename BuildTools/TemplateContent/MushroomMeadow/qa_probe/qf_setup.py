"""Feature-probe test copy of the Mushroom Meadow template (QA only, never touches the template).

    python qf_setup.py            copy the template to TestResults\qf (keeps its own projectId), add the QF probe resources,
                                  put a QF Probe in the Mushroom Meadow, Hearthollow, Credits and Main Menu rooms and make Pip
                                  start campaign mode. Run again before every probe run: it re-copies the current template.
    python qf_setup.py --steps    also write the replay step files (steps_*.json) next to this script.

Run a probe with qfrun.ps1 (takes the machine-wide test mutex for that one run):
    powershell -File qfrun.ps1 -Room "Mushroom Meadow" -StepsFile steps_main.json -OutFile out_main.txt -Mode main
Modes (QF Mode.pgsl names it): main (logic, gore, fear, items, taint, options), hub (dialogue + shop, Enter taps one second
apart from replay second 3), loop (60 s of real keys with every item, then Save and Quit from the pause menu), credits,
menu (Main Menu with seeded and corrupt saves), aspect (window shapes). Results: TestCheck lines in the game.run answer,
pictures in TestResults\qf\Debug\Images. parse_result.py <out file> [name,filter] lists failures and chosen passes.
Replay notes: game.run cannot press Escape (use P / Backspace); InputReplayFrame() / 60 is the replay clock (the Probe uses it).
"""
import json, os, shutil, subprocess, sys, uuid

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", "..", "..", ".."))
TPL = os.path.join(REPO, "Source", "Genesis.Application.Core", "Projects", "Templates", "Assets", "TwoDShowcase")
QF = os.path.join(REPO, "TestResults", "qf")


def main():
    first = not os.path.exists(os.path.join(QF, "Mushroom Meadow.genesisproj"))
    r = subprocess.run(["robocopy", TPL, QF, "/E", "/NFL", "/NDL", "/NJH", "/NJS", "/NP", "/XF", "Mushroom Meadow.genesisproj"],
                       capture_output=True, text=True)
    if r.returncode >= 8:
        raise SystemExit("robocopy failed: " + r.stdout)
    if first:
        shutil.copy(os.path.join(TPL, "Mushroom Meadow.genesisproj"), QF)
    proj = os.path.join(QF, "Mushroom Meadow.genesisproj")
    d = json.load(open(proj, encoding="utf-8-sig"))
    d["projectId"] = "fe0000000000000000000000000000f1"
    json.dump(d, open(proj, "w", encoding="utf-8"), indent=2)
    r = subprocess.run(["robocopy", os.path.join(HERE, "assets"), os.path.join(QF, "Assets"), "/E", "/NFL", "/NDL", "/NJH", "/NJS", "/NP"],
                       capture_output=True, text=True)
    if r.returncode >= 8:
        raise SystemExit("robocopy failed: " + r.stdout)
    p = os.path.join(QF, "Assets", "Objects", "Explorer", "Create.pgsl")
    s = open(p, encoding="utf-8").read()
    if 'GlobalSet("campaign", 1);\nspawnX' not in s:
        s = s.replace("spawnX = x; spawnY = y;", 'GlobalSet("campaign", 1);\nspawnX = x; spawnY = y;', 1)
        open(p, "w", encoding="utf-8").write(s)
    pos = {"Mushroom Meadow": (700, 288), "Hearthollow": (300, 200), "Credits": (100, 100), "Main Menu": (100, 100)}
    for n, (x, y) in pos.items():
        rp = os.path.join(QF, "Assets", "Rooms", n + ".room.json")
        room = json.load(open(rp, encoding="utf-8-sig"))
        if any(nd.get("name") == "QF Probe" for nd in room["nodes"]):
            continue
        room["nodes"].append({"id": uuid.uuid4().hex, "name": "QF Probe", "kind": 0, "parentId": "", "layerId": "actors", "order": 200,
                              "enabled": True, "enabledIn2D": True, "enabledIn3D": True,
                              "transform": {"position": [float(x), float(y), 0.0], "rotation": [0.0, 0.0, 0.0], "scale": [1.0, 1.0, 1.0],
                                            "X": float(x), "Y": float(y), "Z": 0.0, "RotationX": 0.0, "RotationY": 0.0, "RotationZ": 0.0,
                                            "ScaleX": 1.0, "ScaleY": 1.0, "ScaleZ": 1.0},
                              "gameObject": {"prefab": "QF Probe", "componentOverrides": []}})
        open(rp, "w", encoding="utf-8").write(json.dumps(room, indent=2))
    print("qf ready:", QF)
    if "--steps" in sys.argv:
        write_steps()


def write_steps():
    def save(name, steps):
        json.dump(steps, open(os.path.join(HERE, name), "w"))
    save("steps_main.json", [{"seconds": 1.5}, {"seconds": 110}])
    save("steps_hub.json", [{"seconds": 3.0}] + [{"seconds": 1.0, "press": ["Enter"]} for _ in range(200)])
    save("steps_credits.json", [{"seconds": 1.5}, {"seconds": 60}])
    save("steps_aspect.json", [{"seconds": 1.5}, {"seconds": 50}])
    steps = [{"seconds": 2.0}]
    for i in range(24): steps.append({"seconds": 0.5, "hold": ["D"], "press": ["J"] + (["Space"] if i % 3 == 0 else [])})
    for i in range(24): steps.append({"seconds": 0.5, "hold": ["D", "J"], "press": ["Space"] if i % 4 == 0 else []})
    for i in range(12):
        steps.append({"seconds": 0.6, "hold": ["D", "J"] + (["Up"] if i % 2 else [])})
        steps.append({"seconds": 0.4, "hold": ["D"], "press": ["Space"] if i % 3 == 0 else []})
    for i in range(4):
        steps += [{"seconds": 0.35, "hold": ["D", "Space"]}, {"seconds": 1.4, "hold": ["D", "Space", "J"]},
                  {"seconds": 0.9, "hold": ["D", "J"]}, {"seconds": 0.6, "hold": ["D"]}]
    for i in range(14): steps.append({"seconds": 0.5, "hold": ["D"], "press": ["J"] + (["K"] if i in (4, 10) else []) + (["Space"] if i % 3 == 0 else [])})
    steps.append({"seconds": 2.0}); steps.append({"seconds": 0.5, "press": ["P"]})
    for i in range(4): steps.append({"seconds": 0.5, "press": ["Down"]})
    steps.append({"seconds": 0.5, "press": ["Enter"]}); steps.append({"seconds": 14})
    save("steps_loop.json", steps)
    events = [(1.0, "Z"), (2.0, "Enter"), (3.5, "Backspace"), (4.0, "Down"), (4.4, "Enter"), (5.6, "Backspace"), (6.0, "Down"), (6.4, "Enter"),
              (7.6, "Backspace"), (8.0, "Down"), (8.4, "Enter"), (9.6, "E"), (10.0, "E"), (11.2, "Backspace"), (12.4, "Up"), (12.7, "Up"),
              (13.0, "Up"), (13.4, "Enter"), (13.9, "Enter")]
    ms = []; cur = 0.0
    for t, k in events:
        if t > cur: ms.append({"seconds": round(t - cur, 3)}); cur = t
        ms.append({"seconds": 0.1, "press": [k]}); cur += 0.1
    ms.append({"seconds": 5.0})
    save("steps_menu.json", ms)


if __name__ == "__main__":
    main()
