"""Static PGSL check of a generated project (no game is started).

    python check_scripts.py [project folder]

Default folder: the template (Source/Genesis.Application.Core/Projects/Templates/Assets/TwoDShowcase) relative to this
file, so the same file works in the repo and in a TestResults sandbox that mirrors the repo paths.
Builds check_scripts_tool (needs the published engine in 'Genesis Application' and the .NET SDK) into
TestResults/rvtool next to the repo root, then runs the same strict validation F5 runs plus a VM compile of every .pgsl.
Exit code 0 = no errors. Warnings are listed but do not fail.
"""
import os, shutil, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))


def find_repo(start):
    d = start
    while True:
        if os.path.exists(os.path.join(d, "Build.bat")):
            return d
        p = os.path.dirname(d)
        if p == d:
            raise SystemExit("Build.bat not found above " + start)
        d = p


REPO = find_repo(HERE)
TOOL = os.path.join(REPO, "TestResults", "rvtool")
SRC = os.path.join(HERE, "check_scripts_tool")
APP = os.path.join(REPO, "Genesis Application")


def build():
    exe = os.path.join(TOOL, "out", "CheckScripts.dll")
    src_newest = max(os.path.getmtime(os.path.join(SRC, f)) for f in ("Program.cs", "CheckScripts.csproj"))
    if os.path.exists(exe) and os.path.getmtime(exe) > src_newest:
        return exe
    work = os.path.join(TOOL, "src")
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work)
    for f in ("Program.cs", "CheckScripts.csproj"):
        shutil.copy(os.path.join(SRC, f), work)
    out = os.path.join(TOOL, "out")
    r = subprocess.run(["dotnet", "build", os.path.join(work, "CheckScripts.csproj"), "-c", "Release", "-o", out,
                        "-p:GenesisApp=" + APP + "\\", "-nologo", "-v", "q"])
    if r.returncode:
        raise SystemExit("tool build failed")
    for f in os.listdir(APP):  # the engine's own dependencies
        if f.lower().endswith(".dll") and not os.path.exists(os.path.join(out, f)):
            shutil.copy(os.path.join(APP, f), out)
    return exe


if __name__ == "__main__":
    folder = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.path.dirname(os.path.dirname(os.path.dirname(HERE))), "Source", "Genesis.Application.Core", "Projects", "Templates",
        "Assets", "TwoDShowcase")
    dll = build()
    sys.exit(subprocess.run(["dotnet", dll, folder]).returncode)
