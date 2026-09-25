"""Injected by DevProfiler. Uses Python standard library only."""
import atexit as _atexit
import cProfile as _cprofile
import io as _io
import json as _json
import os as _os
import pstats as _pstats
import sys as _sys
import threading as _threading
import time as _time
import traceback as _traceback

_OUT = _os.environ.get("DEVPROFILER_SESSION_DIR", "")
_PID = _os.getpid()
_STARTED = _time.time()
_PROFILE = _cprofile.Profile()
_LOCK = _threading.Lock()
_FPS_BUFFER = []
_LAST_FRAME = None
_STOPPED = False


def _path(suffix):
    return _os.path.join(_OUT, f"proc_{_PID}.{suffix}")


def _write_json(path, value):
    temp = path + ".tmp"
    with open(temp, "w", encoding="utf-8") as stream:
        _json.dump(value, stream, ensure_ascii=False)
    _os.replace(temp, path)


def _meta(state):
    return {
        "pid": _PID,
        "ppid": _os.getppid(),
        "argv": _sys.argv[:],
        "script": _sys.argv[0] if _sys.argv else "",
        "cwd": _os.getcwd(),
        "python": _sys.executable,
        "started": _STARTED,
        "elapsed": _time.time() - _STARTED,
        "state": state,
    }


def _flush_fps():
    global _FPS_BUFFER
    with _LOCK:
        batch = _FPS_BUFFER
        _FPS_BUFFER = []
    if not batch:
        return
    try:
        with open(_path("fps.jsonl"), "a", encoding="utf-8") as stream:
            for item in batch:
                stream.write(_json.dumps(item) + "\n")
            stream.flush()
    except Exception:
        pass


def _frame_presented():
    global _LAST_FRAME
    now = _time.perf_counter()
    fps = 0.0 if _LAST_FRAME is None else (1.0 / max(0.000001, now - _LAST_FRAME))
    _LAST_FRAME = now
    with _LOCK:
        _FPS_BUFFER.append({"time": _time.time(), "fps": fps})
        should_flush = len(_FPS_BUFFER) >= 10
    if should_flush:
        _flush_fps()


try:
    if _OUT:
        _os.makedirs(_OUT, exist_ok=True)
        _write_json(_path("meta.json"), _meta("running"))
except Exception:
    pass

# Pygame present hooks. These are module-level functions and can normally be replaced.
try:
    import pygame as _pygame
    _old_flip = _pygame.display.flip
    _old_update = _pygame.display.update

    def _flip(*args, **kwargs):
        result = _old_flip(*args, **kwargs)
        _frame_presented()
        return result

    def _update(*args, **kwargs):
        result = _old_update(*args, **kwargs)
        _frame_presented()
        return result

    _pygame.display.flip = _flip
    _pygame.display.update = _update
except Exception:
    pass

# Multiprocessing spawn normally starts a fresh interpreter with a generated
# ``-c`` command. Inject the profiler import into that generated command so
# multiprocessing children are captured even on machines that already provide
# a different sitecustomize module earlier on sys.path.
try:
    import multiprocessing.spawn as _mp_spawn

    _old_get_command_line = _mp_spawn.get_command_line

    def _profiled_get_command_line(**kwargs):
        command = list(_old_get_command_line(**kwargs))
        try:
            code_index = command.index("-c") + 1
            command[code_index] = "import devprofiler_bootstrap;" + command[code_index]
        except (ValueError, IndexError):
            pass
        return command

    _mp_spawn.get_command_line = _profiled_get_command_line
except Exception:
    pass

_PROFILE.enable()


def _dump():
    global _STOPPED
    if _STOPPED:
        return
    _STOPPED = True
    try:
        _PROFILE.disable()
    except Exception:
        pass

    _flush_fps()
    rows = []
    raw = ""
    error = None

    try:
        stats = _pstats.Stats(_PROFILE)
        for (filename, line, function), values in stats.stats.items():
            primitive_calls, total_calls, total_time, cumulative_time, _callers = values
            rows.append({
                "file": filename or "",
                "line": int(line),
                "function": function or "?",
                "primitiveCalls": int(primitive_calls),
                "calls": int(total_calls),
                "selfSeconds": float(total_time),
                "inclusiveSeconds": float(cumulative_time),
            })
        rows.sort(key=lambda row: row["inclusiveSeconds"], reverse=True)

        raw_stream = _io.StringIO()
        _pstats.Stats(_PROFILE, stream=raw_stream).strip_dirs().sort_stats("cumulative").print_stats(500)
        raw = raw_stream.getvalue()
    except Exception:
        error = _traceback.format_exc()

    try:
        _write_json(_path("profile.json"), {
            "meta": _meta("complete"),
            "functions": rows,
            "error": error,
        })
        with open(_path("pstats.txt"), "w", encoding="utf-8", errors="replace") as stream:
            stream.write(raw)
        _write_json(_path("meta.json"), _meta("complete"))
    except Exception:
        pass


_atexit.register(_dump)
