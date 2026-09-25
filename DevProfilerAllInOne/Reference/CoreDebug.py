"""
DevProfiler v2 — Advanced cProfile + System Monitor
Multi-process aware: profiles the root app AND every Python child it spawns.

Architecture:
  1.  A sitecustomize.py is written to a temp dir and injected via PYTHONPATH.
  2.  Every Python interpreter (root + all subprocesses) auto-loads it.
  3.  sitecustomize.py starts cProfile and writes a .prof + .meta file on exit.
  4.  After the run, all .prof files are merged and analysed per-process.
"""

import tkinter as tk
from tkinter import ttk, filedialog, messagebox, scrolledtext
import threading
import subprocess
import os, sys, time, re, json, pstats, io, tempfile, shutil, glob
from pathlib import Path
from datetime import datetime
import queue

try:
    import psutil
    HAS_PSUTIL = True
except ImportError:
    HAS_PSUTIL = False

try:
    import matplotlib
    matplotlib.use("TkAgg")
    from matplotlib.backends.backend_tkagg import FigureCanvasTkAgg
    from matplotlib.figure import Figure
    import matplotlib.gridspec as gridspec
    HAS_MATPLOTLIB = True
except ImportError:
    HAS_MATPLOTLIB = False

# ─── CONSTANTS ────────────────────────────────────────────────────────────────
SORT_OPTIONS  = ["cumulative","calls","time","tottime","pcalls","name","filename"]
RESULT_SIZES  = [10, 25, 50, 100, 200, 500]
POLL_MS       = 400

BG_DARK  = "#0D1117"; BG_CARD  = "#161B22"; BG_PANEL = "#1C2128"
C_CYAN   = "#58D9F2"; C_GREEN  = "#3FB950"; C_ORANGE = "#FF8B3D"
C_RED    = "#FF5555"; C_PURPLE = "#BC8CFF"; C_YELLOW = "#E3B341"
C_BLUE   = "#79C0FF"
TEXT     = "#E6EDF3"; MUTED    = "#8B949E"; BORDER   = "#30363D"

CAT_COLOR = {
    "EVENT":    C_CYAN,
    "LOGIC":    C_GREEN,
    "ENGINE":   C_ORANGE,
    "BLOCKING": C_RED,
    "STDLIB":   C_PURPLE,
    "IMPORT":   MUTED,
}

# ─── sitecustomize.py injected into every child Python ────────────────────────
# Uses ONLY stdlib.  Hooks pygame clock/flip for real FPS if present.
SITE_TEMPLATE = r'''
import cProfile as _cp, atexit as _ae, os as _os, sys as _sys
import json as _json, time as _time, threading as _th

_prof   = _cp.Profile()
_t0     = _time.time()
_outdir = {out_dir!r}
_pid    = _os.getpid()
_fps    = []
_flock  = _th.Lock()

def _rec_fps(v):
    with _flock:
        _fps.append((_time.time(), float(v)))

# ── pygame FPS hooks (no-op if pygame not present) ──
try:
    import pygame as _pg
    _of = _pg.display.flip
    _ou = _pg.display.update
    def _pf():
        _rec_fps(0); return _of()
    def _pu(*a,**k):
        _rec_fps(0); return _ou(*a,**k)
    _pg.display.flip   = _pf
    _pg.display.update = _pu
    _oct = _pg.time.Clock.tick
    def _pct(self, fps=0):
        r = _oct(self, fps)
        try: _rec_fps(self.get_fps())
        except: pass
        return r
    _pg.time.Clock.tick = _pct
except Exception:
    pass

_prof.enable()

def _dump():
    _prof.disable()
    elapsed = _time.time() - _t0
    _pp = _os.path.join(_outdir, f"proc_{_pid}.prof")
    _mp = _os.path.join(_outdir, f"proc_{_pid}.meta.json")
    _fp = _os.path.join(_outdir, f"proc_{_pid}.fps.json")
    try: _prof.dump_stats(_pp)
    except: pass
    try:
        with open(_mp,"w") as _f:
            _json.dump({"pid":_pid,"ppid":_os.getppid(),
                        "argv":_sys.argv[:],"script":_sys.argv[0] if _sys.argv else "",
                        "elapsed":elapsed,"cwd":_os.getcwd()},_f)
    except: pass
    try:
        with _flock: _s = list(_fps)
        with open(_fp,"w") as _f: _json.dump(_s,_f)
    except: pass

_ae.register(_dump)
'''


class ProfilerApp(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("DevProfiler v2  //  Multi-Process cProfile + System Monitor")
        self.geometry("1480x960")
        self.minsize(1100, 700)
        self.configure(bg=BG_DARK)

        self.folder_path  = tk.StringVar()
        self.entry_point  = tk.StringVar()
        self.sort_by      = tk.StringVar(value="cumulative")
        self.result_size  = tk.IntVar(value=50)

        self.is_running   = False
        self.root_proc    = None
        self.tmp_dir      = None
        self.all_rows     = []
        self.proc_rows    = {}
        self.proc_meta    = {}
        self.warnings     = []
        self.stats_text   = ""

        self.cpu_history  = []
        self.ram_history  = []
        self.fps_history  = []
        self.t_labels     = []
        self.mq           = queue.Queue()
        self.start_time   = None
        self.log_entries  = []

        self._setup_styles()
        self._build_ui()
        self._check_deps()

    # ── styles ────────────────────────────────────────────────────────────────
    def _setup_styles(self):
        s = ttk.Style(self)
        s.theme_use("clam")
        s.configure(".", background=BG_DARK, foreground=TEXT,
                    fieldbackground=BG_PANEL, bordercolor=BORDER,
                    darkcolor=BG_DARK, lightcolor=BG_PANEL,
                    troughcolor=BG_PANEL, selectbackground=C_CYAN,
                    selectforeground=BG_DARK, insertcolor=C_CYAN,
                    font=("Consolas", 10))
        s.configure("TNotebook", background=BG_DARK, borderwidth=0)
        s.configure("TNotebook.Tab", background=BG_PANEL, foreground=MUTED,
                    padding=[14, 6], font=("Consolas", 10, "bold"))
        s.map("TNotebook.Tab",
              background=[("selected", BG_CARD)],
              foreground=[("selected", C_CYAN)])
        s.configure("Treeview", background=BG_PANEL, foreground=TEXT,
                    fieldbackground=BG_PANEL, rowheight=26,
                    font=("Consolas", 9))
        s.configure("Treeview.Heading", background=BG_CARD,
                    foreground=C_CYAN, font=("Consolas", 9, "bold"),
                    relief="flat")
        s.map("Treeview",
              background=[("selected", C_CYAN)],
              foreground=[("selected", BG_DARK)])
        s.configure("TCombobox", background=BG_PANEL, foreground=TEXT,
                    fieldbackground=BG_PANEL, arrowcolor=C_CYAN)

    # ── UI build ──────────────────────────────────────────────────────────────
    def _build_ui(self):
        hdr = tk.Frame(self, bg=BG_CARD, height=58)
        hdr.pack(fill="x")
        hdr.pack_propagate(False)
        tk.Label(hdr, text="⬡ DevProfiler v2", bg=BG_CARD, fg=C_CYAN,
                 font=("Consolas", 17, "bold")).pack(side="left", padx=20, pady=10)
        tk.Label(hdr, text="Multi-Process · cProfile · CPU · RAM · FPS",
                 bg=BG_CARD, fg=MUTED, font=("Consolas", 9)).pack(side="left")
        self.status_lbl = tk.Label(hdr, text="● IDLE", bg=BG_CARD,
                                   fg=MUTED, font=("Consolas", 10, "bold"))
        self.status_lbl.pack(side="right", padx=20)

        tb = tk.Frame(self, bg=BG_PANEL, height=52)
        tb.pack(fill="x")
        tb.pack_propagate(False)

        tk.Label(tb, text="📁", bg=BG_PANEL, fg=MUTED,
                 font=("Consolas", 9)).pack(side="left", padx=(14, 2), pady=14)
        self._mk_entry(tb, self.folder_path, 36).pack(
            side="left", padx=2, pady=14, ipady=4)
        self._btn(tb, "Browse", self._browse_folder,
                  C_CYAN, BG_DARK).pack(side="left", padx=4, pady=14)
        self._sep(tb)
        tk.Label(tb, text="▶ Entry:", bg=BG_PANEL, fg=MUTED,
                 font=("Consolas", 9)).pack(side="left", padx=(4, 2))
        self.entry_combo = ttk.Combobox(tb, textvariable=self.entry_point,
                                        width=28, state="readonly")
        self.entry_combo.pack(side="left", padx=2, pady=14, ipady=4)
        self._sep(tb)
        tk.Label(tb, text="Sort:", bg=BG_PANEL, fg=MUTED,
                 font=("Consolas", 9)).pack(side="left", padx=4)
        ttk.Combobox(tb, textvariable=self.sort_by, values=SORT_OPTIONS,
                     width=13, state="readonly").pack(side="left", padx=2, pady=14)
        tk.Label(tb, text="Top:", bg=BG_PANEL, fg=MUTED,
                 font=("Consolas", 9)).pack(side="left", padx=4)
        ttk.Combobox(tb, textvariable=self.result_size, values=RESULT_SIZES,
                     width=6, state="readonly").pack(side="left", padx=2, pady=14)
        self._sep(tb)
        self.run_btn = self._btn(tb, "▶  RUN", self._toggle_run,
                                 C_GREEN, BG_DARK, bold=True)
        self.run_btn.pack(side="left", padx=6, pady=10)
        self._btn(tb, "⬇ Export MD", self._export_report,
                  C_ORANGE, BG_DARK).pack(side="left", padx=6, pady=10)

        # process badge strip
        self.proc_strip = tk.Frame(self, bg=BG_DARK)
        self.proc_strip.pack(fill="x", padx=10, pady=(4, 0))
        tk.Label(self.proc_strip, text="Processes:", bg=BG_DARK,
                 fg=MUTED, font=("Consolas", 8)).pack(side="left")
        self.proc_labels = {}

        # metrics
        met = tk.Frame(self, bg=BG_DARK)
        met.pack(fill="x", padx=10, pady=(4, 0))
        self.cpu_card   = self._metric_card(met, "CPU",   "0.0%",  C_CYAN)
        self.ram_card   = self._metric_card(met, "RAM",   "0 MB",  C_PURPLE)
        self.fps_card   = self._metric_card(met, "FPS",   "—",     C_GREEN)
        self.time_card  = self._metric_card(met, "Time",  "0.00s", C_ORANGE)
        self.calls_card = self._metric_card(met, "Calls", "—",     C_RED)
        self.procs_card = self._metric_card(met, "Procs", "0",     C_YELLOW)

        # critical warning banner (hidden until needed)
        self.warn_frame = tk.Frame(self, bg="#2a1515", pady=4)
        self.warn_lbl   = tk.Label(self.warn_frame, text="", bg="#2a1515",
                                   fg=C_RED, font=("Consolas", 8),
                                   justify="left", wraplength=1400)
        self.warn_lbl.pack(side="left", padx=12)

        # notebook
        main = tk.Frame(self, bg=BG_DARK)
        main.pack(fill="both", expand=True, padx=10, pady=6)
        nb = ttk.Notebook(main)
        nb.pack(fill="both", expand=True)

        t1 = tk.Frame(nb, bg=BG_CARD); nb.add(t1, text="  📊 Merged Table  ")
        t2 = tk.Frame(nb, bg=BG_CARD); nb.add(t2, text="  🔀 Per-Process  ")
        t3 = tk.Frame(nb, bg=BG_CARD); nb.add(t3, text="  📈 Graphs  ")
        t4 = tk.Frame(nb, bg=BG_CARD); nb.add(t4, text="  ⚠️ Warnings  ")
        t5 = tk.Frame(nb, bg=BG_CARD); nb.add(t5, text="  🗒 Event Log  ")
        t6 = tk.Frame(nb, bg=BG_CARD); nb.add(t6, text="  📄 Raw Output  ")

        self._build_merged_tab(t1)
        self._build_perproc_tab(t2)
        self._build_graphs_tab(t3)
        self._build_warnings_tab(t4)
        self._build_log_tab(t5)
        self._build_raw_tab(t6)

    # ── widget helpers ────────────────────────────────────────────────────────
    def _btn(self, parent, text, cmd, fg, bg, bold=False):
        f = ("Consolas", 9, "bold") if bold else ("Consolas", 9)
        return tk.Button(parent, text=text, command=cmd, bg=bg, fg=fg,
                         activebackground=fg, activeforeground=bg,
                         relief="flat", cursor="hand2", font=f,
                         padx=12, pady=4, highlightthickness=1,
                         highlightbackground=fg)

    def _mk_entry(self, parent, var, width):
        return tk.Entry(parent, textvariable=var, bg=BG_DARK, fg=TEXT,
                        width=width, insertbackground=C_CYAN, relief="flat",
                        font=("Consolas", 9), highlightthickness=1,
                        highlightbackground=BORDER, highlightcolor=C_CYAN)

    def _sep(self, parent):
        ttk.Separator(parent, orient="vertical").pack(
            side="left", fill="y", padx=8, pady=8)

    def _metric_card(self, parent, label, value, color):
        f = tk.Frame(parent, bg=BG_CARD, padx=16, pady=8,
                     highlightthickness=1, highlightbackground=color)
        f.pack(side="left", padx=6)
        tk.Label(f, text=label, bg=BG_CARD, fg=MUTED,
                 font=("Consolas", 8)).pack()
        lbl = tk.Label(f, text=value, bg=BG_CARD, fg=color,
                       font=("Consolas", 14, "bold"))
        lbl.pack()
        return lbl

    def _make_tree(self, parent, cols, hdrs, widths):
        frame = tk.Frame(parent, bg=BG_CARD)
        frame.pack(fill="both", expand=True, padx=8, pady=(0, 8))
        vsb = ttk.Scrollbar(frame, orient="vertical")
        hsb = ttk.Scrollbar(frame, orient="horizontal")
        tree = ttk.Treeview(frame, columns=cols, show="headings",
                            yscrollcommand=vsb.set, xscrollcommand=hsb.set)
        vsb.configure(command=tree.yview)
        hsb.configure(command=tree.xview)
        for c, h, w in zip(cols, hdrs, widths):
            tree.heading(c, text=h,
                         command=lambda col=c, t=tree: self._sort_tree(t, col))
            tree.column(c, width=w, anchor="center" if w <= 80 else "w")
        for cat, col in CAT_COLOR.items():
            tree.tag_configure(cat.lower(), foreground=col)
        tree.tag_configure("hot", background="#2a1515", foreground=C_RED)
        vsb.pack(side="right", fill="y")
        hsb.pack(side="bottom", fill="x")
        tree.pack(fill="both", expand=True)
        return tree

    # ── tab builders ──────────────────────────────────────────────────────────
    def _build_merged_tab(self, parent):
        fbar = tk.Frame(parent, bg=BG_CARD)
        fbar.pack(fill="x", padx=8, pady=6)
        tk.Label(fbar, text="Filter:", bg=BG_CARD, fg=MUTED,
                 font=("Consolas", 9)).pack(side="left")
        self.filter_var = tk.StringVar()
        self.filter_var.trace("w", lambda *_: self._apply_filter())
        self._mk_entry(fbar, self.filter_var, 30).pack(
            side="left", padx=6, ipady=3)

        cols   = ("rank","process","ncalls","tottime","percall_t",
                  "cumtime","percall_c","filename","function","category")
        hdrs   = ("#","Process","Calls","Tot Time","Per Call",
                  "Cum Time","Per Call(C)","File","Function","Category")
        widths = [36, 110, 80, 90, 90, 90, 90, 200, 200, 90]
        self.tree = self._make_tree(parent, cols, hdrs, widths)

    def _build_perproc_tab(self, parent):
        pane = tk.PanedWindow(parent, orient="horizontal",
                              bg=BG_DARK, sashwidth=6, sashrelief="flat")
        pane.pack(fill="both", expand=True, padx=8, pady=8)

        left = tk.Frame(pane, bg=BG_CARD)
        pane.add(left, minsize=260)
        tk.Label(left, text="Processes", bg=BG_CARD, fg=C_CYAN,
                 font=("Consolas", 10, "bold")).pack(fill="x", padx=8, pady=6)
        proc_vsb = ttk.Scrollbar(left, orient="vertical")
        self.proc_list = ttk.Treeview(
            left, columns=("pid","script","time","calls"),
            show="headings", yscrollcommand=proc_vsb.set, height=30)
        proc_vsb.configure(command=self.proc_list.yview)
        for c, h, w in zip(("pid","script","time","calls"),
                           ("PID","Script","Time","Calls"),
                           [55, 160, 75, 65]):
            self.proc_list.heading(c, text=h)
            self.proc_list.column(c, width=w, anchor="center" if w <= 75 else "w")
        proc_vsb.pack(side="right", fill="y")
        self.proc_list.pack(fill="both", expand=True)
        self.proc_list.bind("<<TreeviewSelect>>", self._on_proc_select)

        right = tk.Frame(pane, bg=BG_CARD)
        pane.add(right, minsize=700)
        self.proc_detail_lbl = tk.Label(
            right, text="← Select a process", bg=BG_CARD,
            fg=MUTED, font=("Consolas", 9))
        self.proc_detail_lbl.pack(fill="x", padx=8, pady=6)

        cols   = ("rank","ncalls","tottime","percall_t",
                  "cumtime","percall_c","filename","function","category")
        hdrs   = ("#","Calls","Tot Time","Per Call",
                  "Cum Time","Per Call(C)","File","Function","Category")
        widths = [36, 80, 90, 90, 90, 90, 200, 200, 90]
        self.proc_tree = self._make_tree(right, cols, hdrs, widths)

    def _build_graphs_tab(self, parent):
        if not HAS_MATPLOTLIB:
            tk.Label(parent, text="pip install matplotlib",
                     bg=BG_CARD, fg=C_RED, font=("Consolas", 12)).pack(expand=True)
            return
        self.fig = Figure(figsize=(13, 6.5), dpi=96, facecolor=BG_CARD)
        gs = gridspec.GridSpec(2, 3, figure=self.fig,
                               hspace=0.44, wspace=0.30,
                               left=0.06, right=0.97, top=0.93, bottom=0.10)
        self.ax_cpu = self.fig.add_subplot(gs[0, 0])
        self.ax_ram = self.fig.add_subplot(gs[0, 1])
        self.ax_fps = self.fig.add_subplot(gs[0, 2])
        self.ax_top = self.fig.add_subplot(gs[1, 0:2])
        self.ax_pie = self.fig.add_subplot(gs[1, 2])
        for ax in (self.ax_cpu, self.ax_ram, self.ax_fps,
                   self.ax_top, self.ax_pie):
            ax.set_facecolor(BG_PANEL)
            ax.tick_params(colors=MUTED, labelsize=7)
            for sp in ax.spines.values():
                sp.set_edgecolor(BORDER)
        self.ax_cpu.set_title("CPU %",     color=C_CYAN,   fontsize=8)
        self.ax_ram.set_title("RAM MB",    color=C_PURPLE, fontsize=8)
        self.ax_fps.set_title("FPS",       color=C_GREEN,  fontsize=8)
        self.ax_top.set_title("Top Functions — Cumulative Time",
                              color=C_ORANGE, fontsize=8)
        self.ax_pie.set_title("Time by Category", color=TEXT, fontsize=8)
        canvas = FigureCanvasTkAgg(self.fig, master=parent)
        canvas.get_tk_widget().configure(bg=BG_CARD)
        canvas.get_tk_widget().pack(fill="both", expand=True, padx=4, pady=4)
        self.canvas = canvas

    def _build_warnings_tab(self, parent):
        self.warn_text = scrolledtext.ScrolledText(
            parent, bg=BG_PANEL, fg=TEXT, font=("Consolas", 9),
            relief="flat", insertbackground=C_CYAN, selectbackground=C_CYAN)
        self.warn_text.pack(fill="both", expand=True, padx=8, pady=8)
        self.warn_text.insert("end", "Run a profile to see health warnings...\n")
        for tag, col in [("RED",C_RED),("ORANGE",C_ORANGE),("YELLOW",C_YELLOW),
                         ("GREEN",C_GREEN),("CYAN",C_CYAN),("BLUE",C_BLUE)]:
            self.warn_text.tag_configure(tag, foreground=col)
        self.warn_text.tag_configure("BOLD", font=("Consolas", 9, "bold"))

    def _build_log_tab(self, parent):
        fbar = tk.Frame(parent, bg=BG_CARD)
        fbar.pack(fill="x", padx=8, pady=6)
        tk.Label(fbar, text="Category:", bg=BG_CARD, fg=MUTED,
                 font=("Consolas", 9)).pack(side="left")
        self.log_filter = tk.StringVar(value="ALL")
        ttk.Combobox(fbar, textvariable=self.log_filter,
                     values=["ALL","EVENT","LOGIC","BLOCKING","ENGINE",
                             "SYSTEM","ERROR","IMPORT","STDLIB"],
                     width=10, state="readonly").pack(side="left", padx=6)
        self._btn(fbar, "Apply", self._refresh_log,
                  C_CYAN, BG_DARK).pack(side="left", padx=4)

        cols   = ("time","pid","category","function","file","details")
        hdrs   = ("Time","PID","Category","Function","File","Details")
        widths = [60, 55, 80, 200, 200, 340]
        frame  = tk.Frame(parent, bg=BG_CARD)
        frame.pack(fill="both", expand=True, padx=8, pady=(0, 8))
        vsb = ttk.Scrollbar(frame, orient="vertical")
        self.log_tree = ttk.Treeview(frame, columns=cols, show="headings",
                                     yscrollcommand=vsb.set)
        vsb.configure(command=self.log_tree.yview)
        vsb.pack(side="right", fill="y")
        self.log_tree.pack(fill="both", expand=True)
        for c, h, w in zip(cols, hdrs, widths):
            self.log_tree.heading(c, text=h)
            self.log_tree.column(c, width=w, anchor="w")
        for cat, col in CAT_COLOR.items():
            self.log_tree.tag_configure(cat, foreground=col)
        self.log_tree.tag_configure("SYSTEM", foreground=C_BLUE)
        self.log_tree.tag_configure("ERROR",  foreground=C_RED)

    def _build_raw_tab(self, parent):
        self.raw_text = scrolledtext.ScrolledText(
            parent, bg=BG_PANEL, fg=TEXT, font=("Consolas", 8),
            relief="flat", insertbackground=C_CYAN, selectbackground=C_CYAN)
        self.raw_text.pack(fill="both", expand=True, padx=8, pady=8)
        self.raw_text.insert("end",
            "Run a profile to see merged raw pstats output.\n")

    # ── folder scan ───────────────────────────────────────────────────────────
    def _browse_folder(self):
        p = filedialog.askdirectory(title="Select Project Folder")
        if p:
            self.folder_path.set(p)
            self._scan_entry_points(p)

    def _scan_entry_points(self, folder):
        pat  = re.compile(r'if\s+__name__\s*==\s*["\']__main__["\']', re.I)
        skip = {".git","__pycache__","venv",".venv","node_modules",".tox"}
        found = []
        for root, dirs, files in os.walk(folder):
            dirs[:] = [d for d in dirs if d not in skip and not d.startswith(".")]
            for f in files:
                if not f.endswith(".py"):
                    continue
                fp = os.path.join(root, f)
                try:
                    with open(fp, "r", encoding="utf-8", errors="ignore") as fh:
                        if pat.search(fh.read()):
                            found.append(os.path.relpath(fp, folder))
                except Exception:
                    pass
        if found:
            self.entry_combo["values"] = found
            self.entry_combo.set(found[0])
            self._log("SYSTEM","scan",folder,f"Found {len(found)} entry point(s)")
        else:
            self.entry_combo["values"] = []
            self.entry_combo.set("")
            self._log("ERROR","scan",folder,"No entry points found")

    # ── run / stop ────────────────────────────────────────────────────────────
    def _toggle_run(self):
        if self.is_running:
            self._stop_run()
        else:
            self._start_run()

    def _start_run(self):
        folder = self.folder_path.get().strip()
        entry  = self.entry_point.get().strip()
        if not folder or not entry:
            messagebox.showwarning("Missing",
                "Select a project folder and entry point first.")
            return
        script = os.path.join(folder, entry)
        if not os.path.isfile(script):
            messagebox.showerror("Not Found", f"Script not found:\n{script}")
            return

        # reset state
        self.is_running = True
        self.start_time = time.time()
        for attr in ("all_rows","cpu_history","ram_history",
                     "fps_history","t_labels","log_entries","warnings"):
            getattr(self, attr).clear()
        self.proc_rows.clear()
        self.proc_meta.clear()
        for item in self.log_tree.get_children():
            self.log_tree.delete(item)
        for item in self.proc_list.get_children():
            self.proc_list.delete(item)
        for lbl in self.proc_labels.values():
            lbl.destroy()
        self.proc_labels.clear()
        self.warn_text.delete("1.0","end")
        self.raw_text.delete("1.0","end")
        self.warn_frame.pack_forget()

        # fresh temp dir
        if self.tmp_dir and os.path.isdir(self.tmp_dir):
            shutil.rmtree(self.tmp_dir, ignore_errors=True)
        self.tmp_dir = tempfile.mkdtemp(prefix="devprofiler_")

        # write sitecustomize.py
        with open(os.path.join(self.tmp_dir,"sitecustomize.py"),
                  "w", encoding="utf-8") as f:
            f.write(SITE_TEMPLATE.replace("{out_dir!r}", repr(self.tmp_dir)))

        self.run_btn.configure(text="■  STOP", fg=C_RED,
                               highlightbackground=C_RED)
        self._set_status("RUNNING", C_GREEN)
        self._log("SYSTEM","_start_run",script,
                  f"tmp_dir={self.tmp_dir}")

        threading.Thread(target=self._run_thread,
                         args=(folder, script), daemon=True).start()
        if HAS_PSUTIL:
            threading.Thread(target=self._monitor_thread,
                             daemon=True).start()
        self._poll_ui()

    def _run_thread(self, folder, script):
        env = os.environ.copy()
        old_pp = env.get("PYTHONPATH","")
        env["PYTHONPATH"] = (self.tmp_dir + os.pathsep + old_pp
                             if old_pp else self.tmp_dir)
        env["PYTHONDONTWRITEBYTECODE"] = "1"

        python_exe = sys.executable
        for venv_name in (".venv", "venv", "env"):
            venv_dir = os.path.join(folder, venv_name)
            if os.name == "nt":
                venv_bin = os.path.join(venv_dir, "Scripts")
                venv_exe = os.path.join(venv_bin, "python.exe")
            else:
                venv_bin = os.path.join(venv_dir, "bin")
                venv_exe = os.path.join(venv_bin, "python")
                
            if os.path.isfile(venv_exe):
                python_exe = venv_exe
                env["VIRTUAL_ENV"] = venv_dir
                env["PATH"] = venv_bin + os.pathsep + env.get("PATH", "")
                if "PYTHONHOME" in env:
                    del env["PYTHONHOME"]
                break

        try:
            self.root_proc = subprocess.Popen(
                [python_exe, script],
                cwd=folder, env=env,
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            stdout, stderr = self.root_proc.communicate(timeout=600)
            if stdout:
                for line in stdout.splitlines()[-200:]:
                    self._log("LOGIC","stdout",script,line[:200])
            if stderr:
                for line in stderr.splitlines():
                    if line.strip():
                        self._log("ERROR","stderr",script,line[:200])
        except subprocess.TimeoutExpired:
            self.root_proc.kill()
            self._log("ERROR","_run_thread",script,"Timeout (600s)")
        except Exception as e:
            self._log("ERROR","_run_thread",script,str(e))
        finally:
            self.mq.put(("run_done",))

    def _monitor_thread(self):
        t0       = time.time()
        known    = set()
        while self.is_running:
            elapsed = time.time() - t0
            cpu     = psutil.cpu_percent(interval=None)
            ram     = psutil.virtual_memory().used / 1024 / 1024

            # detect new child processes
            for mf in glob.glob(os.path.join(self.tmp_dir,"proc_*.meta.json")):
                try:
                    with open(mf) as f:
                        m = json.load(f)
                    pid = m.get("pid")
                    if pid and pid not in known:
                        known.add(pid)
                        self.mq.put(("new_proc", pid, m))
                except Exception:
                    pass

            # collect FPS
            fps = 0.0
            for ff in glob.glob(os.path.join(self.tmp_dir,"proc_*.fps.json")):
                try:
                    with open(ff) as f:
                        samples = json.load(f)
                    recent = [v for _,v in samples[-10:] if v > 0]
                    if recent:
                        fps = max(fps, sum(recent)/len(recent))
                except Exception:
                    pass

            self.mq.put(("metrics", elapsed, cpu, ram, fps))
            time.sleep(0.5)

    def _stop_run(self):
        self.is_running = False
        if self.root_proc:
            try:
                self.root_proc.terminate()
            except Exception:
                pass
        self.run_btn.configure(text="▶  RUN", fg=C_GREEN,
                               highlightbackground=C_GREEN)
        self._set_status("STOPPED", C_ORANGE)

    # ── poll loop ─────────────────────────────────────────────────────────────
    def _poll_ui(self):
        try:
            while True:
                msg  = self.mq.get_nowait()
                kind = msg[0]

                if kind == "metrics":
                    _, elapsed, cpu, ram, fps = msg
                    self.cpu_history.append(cpu)
                    self.ram_history.append(ram)
                    self.fps_history.append(fps)
                    self.t_labels.append(round(elapsed, 1))
                    self.cpu_card.configure(text=f"{cpu:.1f}%")
                    self.ram_card.configure(text=f"{ram:.0f} MB")
                    if fps > 0:
                        self.fps_card.configure(text=f"{fps:.0f}")
                    self.time_card.configure(text=f"{elapsed:.1f}s")
                    if HAS_MATPLOTLIB:
                        self._live_graph()

                elif kind == "new_proc":
                    _, pid, meta = msg
                    self.proc_meta[pid] = meta
                    script = os.path.basename(meta.get("script","?"))
                    lbl = tk.Label(self.proc_strip,
                                   text=f"  [{pid}] {script}",
                                   bg="#1a2639", fg=C_CYAN,
                                   font=("Consolas", 8), relief="flat", padx=4)
                    lbl.pack(side="left", padx=2)
                    self.proc_labels[pid] = lbl
                    self.procs_card.configure(text=str(len(self.proc_meta)))
                    self._log("SYSTEM","new_proc",script,
                              f"PID {pid} detected (ppid={meta.get('ppid')})")

                elif kind == "run_done":
                    self._finalize()

        except queue.Empty:
            pass

        if self.is_running:
            self.after(POLL_MS, self._poll_ui)

    # ── finalize ──────────────────────────────────────────────────────────────
    def _finalize(self):
        time.sleep(0.8)   # let child processes finish writing
        self.is_running = False
        elapsed = time.time() - self.start_time if self.start_time else 0
        self.run_btn.configure(text="▶  RUN", fg=C_GREEN,
                               highlightbackground=C_GREEN)
        self._set_status("COMPLETE", C_CYAN)
        self.time_card.configure(text=f"{elapsed:.2f}s")

        prof_files = sorted(glob.glob(os.path.join(self.tmp_dir,"proc_*.prof")))
        meta_files = glob.glob(os.path.join(self.tmp_dir,"proc_*.meta.json"))

        # pick up any late meta files
        for mf in meta_files:
            try:
                with open(mf) as f:
                    m = json.load(f)
                pid = m.get("pid")
                if pid and pid not in self.proc_meta:
                    self.proc_meta[pid] = m
            except Exception:
                pass

        if not prof_files:
            self._log("ERROR","_finalize","profiler.py",
                      "No .prof files found — sitecustomize.py may not have loaded. "
                      "Check that the target script runs Python (not a compiled exe).")
            self._run_health_checks([], elapsed)
            self._show_warnings()
            return

        self._log("SYSTEM","_finalize","profiler.py",
                  f"Collected {len(prof_files)} .prof file(s)")

        # collect real FPS
        all_fps = []
        for ff in glob.glob(os.path.join(self.tmp_dir,"proc_*.fps.json")):
            try:
                with open(ff) as f:
                    all_fps.extend(json.load(f))
            except Exception:
                pass
        if all_fps:
            all_fps.sort(key=lambda x: x[0])
            real = [v for _, v in all_fps if v > 0]
            if real:
                self.fps_history = real
                avg = sum(real)/len(real)
                self.fps_card.configure(text=f"{avg:.0f}")

        # parse per-process
        for pf in prof_files:
            m = re.search(r"proc_(\d+)\.prof", pf)
            pid    = int(m.group(1)) if m else 0
            meta   = self.proc_meta.get(pid,{})
            script = os.path.basename(meta.get("script", pf))
            try:
                rows = self._parse_prof(pf, self.sort_by.get(),
                                        self.result_size.get(), pid, script)
                self.proc_rows[pid] = rows
                total_c = sum(r["ncalls"] for r in rows)
                peak_c  = rows[0]["cumtime"] if rows else 0
                self.proc_list.insert("","end", iid=str(pid),
                    values=(pid, script[:22],
                            f"{peak_c:.3f}s", f"{total_c:,}"))
            except Exception as e:
                self._log("ERROR","_finalize",pf,str(e))

        # merge
        merged = self._merge_rows(self.proc_rows)
        merged.sort(key=lambda r: r["cumtime"], reverse=True)
        for i, r in enumerate(merged, 1):
            r["rank"] = i
        self.all_rows = merged

        self._populate_merged(merged)

        # raw merged stats
        raw_buf = io.StringIO()
        try:
            ms = pstats.Stats(prof_files[0], stream=raw_buf)
            for pf in prof_files[1:]:
                ms.add(pf)
            ms.sort_stats(self.sort_by.get())
            ms.print_stats(self.result_size.get())
        except Exception as e:
            raw_buf.write(f"Error merging stats: {e}\n")
        self.raw_text.delete("1.0","end")
        self.raw_text.insert("end", raw_buf.getvalue())

        total_calls = sum(r["ncalls"] for r in merged)
        self.calls_card.configure(text=f"{total_calls:,}")
        self.procs_card.configure(text=str(len(prof_files)))

        if HAS_MATPLOTLIB:
            self._update_graphs(merged)
        self._run_health_checks(merged, elapsed)
        self._show_warnings()

        self._log("SYSTEM","_finalize","profiler.py",
                  f"Done — {len(prof_files)} proc(s), "
                  f"{len(merged)} merged functions")

    # ── profile parsing ───────────────────────────────────────────────────────
    def _parse_prof(self, prof_path, sort_key, limit, pid=0, script=""):
        st = pstats.Stats(prof_path)
        try:
            st.sort_stats(sort_key)
        except Exception:
            st.sort_stats("cumulative")
        rows = []
        data = list(st.stats.items())
        data.sort(key=lambda x: x[1][3], reverse=True)
        for rank, ((fname, lineno, func), (prim, calls, tt, ct, _)) \
                in enumerate(data[:limit], 1):
            rows.append(dict(
                rank=rank, pid=pid, process=script,
                ncalls=calls, tottime=tt,
                percall_t=tt/calls if calls else 0,
                cumtime=ct,
                percall_c=ct/calls if calls else 0,
                filename=os.path.basename(fname) if fname else "?",
                filepath=fname or "",
                lineno=lineno, function=func or "?",
                category=self._categorize(fname or "", func or ""),
            ))
        return rows

    def _merge_rows(self, proc_rows):
        merged = {}
        for pid, rows in proc_rows.items():
            for r in rows:
                key = (r["filepath"], r["function"])
                if key in merged:
                    m = merged[key]
                    m["ncalls"]  += r["ncalls"]
                    m["tottime"] += r["tottime"]
                    m["cumtime"]  = max(m["cumtime"], r["cumtime"])
                    m["pids"].add(pid)
                    m["process"]  = (f"{len(m['pids'])} procs"
                                     if len(m["pids"]) > 1
                                     else r["process"])
                else:
                    merged[key] = dict(r)
                    merged[key]["pids"] = {pid}
        for r in merged.values():
            nc = r["ncalls"]
            r["percall_t"] = r["tottime"] / nc if nc else 0
            r["percall_c"] = r["cumtime"] / nc if nc else 0
        return list(merged.values())

    # ── categorisation ────────────────────────────────────────────────────────
    def _categorize(self, fname, func):
        fn = func.lower()
        fp = fname.lower()

        blocking_funcs = {"waitforsingleobject","waitformultipleobjects",
                          "createprocess","sleep","waitpid","_wait",
                          "communicate","semaphore","acquire","_winapi"}
        if any(b in fn for b in blocking_funcs):
            return "BLOCKING"
        if ("winapi" in fp or "_winapi" in fp) and \
                any(x in fn for x in ["wait","read","write","pipe","lock"]):
            return "BLOCKING"

        if any(x in fn for x in ["event","handle","on_","dispatch","callback",
                                   "listener","emit","signal","keydown","keyup",
                                   "mousedown","mousemove","click","press",
                                   "release","scroll"]):
            return "EVENT"

        if any(x in fn for x in ["update","tick","step","loop","process",
                                   "render","draw","blit","paint","frame",
                                   "run","execute","compute","physics",
                                   "collide","spawn","destroy","move"]):
            return "LOGIC"

        if any(x in fp for x in ["pygame","pyside","pyqt","tkinter","wx",
                                   "sdl","opengl","glfw","pyglet","arcade",
                                   "kivy","moderngl","pyopengl"]):
            return "ENGINE"

        if any(x in fp for x in ["importlib","<frozen","bootstrap","loader"]) \
                or fn in ("<module>","exec_module","_find_and_load",
                          "_load_unlocked","_call_with_frames_removed",
                          "_find_and_load_unlocked","module_from_spec",
                          "get_code","get_data","create_module"):
            return "IMPORT"

        if any(x in fp for x in ["lib/","lib\\","site-packages",
                                   "dist-packages","python3","python313",
                                   "appdata\\local\\programs\\python"]):
            return "STDLIB"

        return "LOGIC"

    # ── table population ──────────────────────────────────────────────────────
    def _populate_merged(self, rows):
        for item in self.tree.get_children():
            self.tree.delete(item)
        for r in rows:
            tag = "hot" if (rows and r["cumtime"] >= rows[0]["cumtime"]*0.5
                            and r["category"] not in ("BLOCKING","IMPORT")) \
                       else r["category"].lower()
            self.tree.insert("","end", tags=(tag,), values=(
                r["rank"], r.get("process","?"),
                f"{r['ncalls']:,}",
                f"{r['tottime']:.6f}",
                f"{r['percall_t']:.6f}",
                f"{r['cumtime']:.6f}",
                f"{r['percall_c']:.6f}",
                r["filename"], r["function"], r["category"],
            ))

    def _apply_filter(self):
        q = self.filter_var.get().lower()
        if not self.all_rows:
            return
        filtered = self.all_rows if not q else [
            r for r in self.all_rows
            if q in r["function"].lower()
            or q in r["filename"].lower()
            or q in r["category"].lower()
            or q in str(r.get("process","")).lower()
        ]
        self._populate_merged(filtered)

    def _on_proc_select(self, event):
        sel = self.proc_list.selection()
        if not sel:
            return
        try:
            pid = int(sel[0])
        except ValueError:
            return
        rows = self.proc_rows.get(pid, [])
        meta = self.proc_meta.get(pid, {})
        self.proc_detail_lbl.configure(
            text=f"PID {pid}  ·  {meta.get('script','?')}  ·  "
                 f"elapsed={meta.get('elapsed',0):.2f}s  ·  {len(rows)} fns")
        for item in self.proc_tree.get_children():
            self.proc_tree.delete(item)
        for r in rows:
            tag = "hot" if (rows and r["cumtime"] >= rows[0]["cumtime"]*0.5
                            and r["category"] not in ("BLOCKING","IMPORT")) \
                       else r["category"].lower()
            self.proc_tree.insert("","end", tags=(tag,), values=(
                r["rank"], f"{r['ncalls']:,}",
                f"{r['tottime']:.6f}", f"{r['percall_t']:.6f}",
                f"{r['cumtime']:.6f}", f"{r['percall_c']:.6f}",
                r["filename"], r["function"], r["category"],
            ))

    def _sort_tree(self, tree, col):
        items = [(tree.set(k, col), k) for k in tree.get_children("")]
        try:
            items.sort(key=lambda x: float(
                x[0].replace(",","").replace("s","")), reverse=True)
        except ValueError:
            items.sort(key=lambda x: x[0])
        for i, (_, k) in enumerate(items):
            tree.move(k,"",i)

    # ── graphs ────────────────────────────────────────────────────────────────
    def _live_graph(self):
        if not HAS_MATPLOTLIB or not hasattr(self,"canvas"):
            return
        try:
            t = self.t_labels
            for ax, data, col, title in [
                (self.ax_cpu,  self.cpu_history, C_CYAN,   "CPU %"),
                (self.ax_ram,  self.ram_history, C_PURPLE, "RAM MB"),
                (self.ax_fps,  [v for v in self.fps_history if v > 0],
                               C_GREEN, "FPS"),
            ]:
                ax.cla()
                ax.set_facecolor(BG_PANEL)
                ax.set_title(title, color=col, fontsize=8)
                d = data if data else [0]
                x = t[:len(d)]
                ax.plot(x, d, color=col, linewidth=1.2)
                ax.fill_between(x, d, color=col, alpha=0.08)
                ax.tick_params(colors=MUTED, labelsize=7)
                for sp in ax.spines.values():
                    sp.set_edgecolor(BORDER)
            self.canvas.draw_idle()
        except Exception:
            pass

    def _update_graphs(self, rows):
        if not HAS_MATPLOTLIB or not hasattr(self,"canvas"):
            return
        self._live_graph()
        try:
            top_n  = min(14, len(rows))
            names  = [f"[{str(r.get('process','?'))[:8]}] "
                      f"{r['function'][:22]}" for r in rows[:top_n]]
            times  = [r["cumtime"] for r in rows[:top_n]]
            colors = [CAT_COLOR.get(r["category"], MUTED) for r in rows[:top_n]]

            self.ax_top.cla()
            self.ax_top.set_facecolor(BG_PANEL)
            self.ax_top.set_title("Top Functions — Cumulative Time",
                                  color=C_ORANGE, fontsize=8)
            bars = self.ax_top.barh(names[::-1], times[::-1],
                                    color=colors[::-1], height=0.65)
            for bar, val in zip(bars, times[::-1]):
                self.ax_top.text(bar.get_width()*1.01,
                                 bar.get_y()+bar.get_height()/2,
                                 f"{val:.4f}s", va="center",
                                 fontsize=6.5, color=MUTED)
            self.ax_top.tick_params(colors=MUTED, labelsize=7)
            for sp in self.ax_top.spines.values():
                sp.set_edgecolor(BORDER)
            self.ax_top.set_xlabel("seconds", color=MUTED, fontsize=7)

            cats = {}
            for r in rows:
                cats[r["category"]] = cats.get(r["category"],0)+r["tottime"]
            if cats:
                self.ax_pie.cla()
                self.ax_pie.set_facecolor(BG_PANEL)
                self.ax_pie.set_title("Time by Category", color=TEXT, fontsize=8)
                lbls  = list(cats.keys())
                sizes = list(cats.values())
                clrs  = [CAT_COLOR.get(l, MUTED) for l in lbls]
                _,_,autos = self.ax_pie.pie(
                    sizes, labels=lbls, colors=clrs, autopct="%1.1f%%",
                    textprops={"color":MUTED,"fontsize":7}, pctdistance=0.75,
                    wedgeprops={"linewidth":0.5,"edgecolor":BG_DARK})
                for a in autos:
                    a.set_color(TEXT); a.set_fontsize(7)

            self.canvas.draw_idle()
        except Exception as e:
            self._log("ERROR","_update_graphs","profiler.py",str(e))

    # ── health checks ─────────────────────────────────────────────────────────
    def _run_health_checks(self, rows, elapsed):
        W = self.warnings

        # 1. Multi-process capture confirmation
        n_procs = len(self.proc_rows)
        blocking_pct = 0
        if rows and elapsed:
            b_time = sum(r["tottime"] for r in rows
                         if r["category"]=="BLOCKING")
            blocking_pct = b_time / elapsed * 100

        if n_procs == 0:
            W.append(("🔴 CRITICAL","NO PROFILES CAPTURED",
                "sitecustomize.py did not load — no .prof files were written.\n"
                "This happens if: (a) the target uses a compiled executable\n"
                "rather than Python, (b) it overrides PYTHONPATH itself, or\n"
                "(c) it runs in a venv that strips PYTHONPATH.\n"
                "→ Try profiling the child script directly.",
                "RED"))
        elif n_procs == 1 and blocking_pct > 60:
            W.append(("🔴 CRITICAL","ONLY LAUNCHER CAPTURED",
                f"Only 1 process profiled, {blocking_pct:.0f}% time in OS "
                "blocking calls. The real application likely ran as a child "
                "that was NOT intercepted (e.g. a compiled .exe or a venv "
                "that discarded PYTHONPATH).\n"
                "→ Profile the child entry point directly.",
                "RED"))
        else:
            proc_list = ", ".join(
                f"PID {p} ({os.path.basename(m.get('script','?'))})"
                for p,m in self.proc_meta.items())
            W.append(("🟢 OK","MULTI-PROCESS CAPTURE",
                f"{n_procs} process(es) profiled and merged: {proc_list}",
                "GREEN"))

        # 2. RAM
        if self.ram_history:
            peak = max(self.ram_history)
            if peak > 8000:
                W.append(("🔴 CRITICAL","EXTREME RAM USAGE",
                    f"Peak RAM {peak:.0f} MB ({peak/1024:.1f} GB).\n"
                    "Likely: large uncompressed textures, Qt widget leak, "
                    "or NumPy/audio arrays never freed.\n"
                    "→ Run with PYTHONTRACEMALLOC=1 to identify allocations.",
                    "RED"))
            elif peak > 2000:
                W.append(("🟡 WARNING","HIGH RAM USAGE",
                    f"Peak RAM {peak:.0f} MB — monitor for growth over time.",
                    "YELLOW"))

        # 3. FPS
        real_fps = [v for v in self.fps_history if v > 0]
        if real_fps:
            avg = sum(real_fps)/len(real_fps)
            mn  = min(real_fps)
            if mn < 20:
                W.append(("🔴 CRITICAL","FPS DROPS DETECTED",
                    f"Min FPS {mn:.0f}, Avg FPS {avg:.0f}.\n"
                    "→ Inspect LOGIC/ENGINE rows for per-frame costs.",
                    "RED"))
            elif avg < 45:
                W.append(("🟡 WARNING","BELOW 60 FPS",
                    f"Avg FPS {avg:.0f}. Review top LOGIC functions.",
                    "YELLOW"))
            else:
                W.append(("🟢 OK","FPS STABLE",
                    f"Avg {avg:.0f} FPS, min {mn:.0f} FPS.","GREEN"))

        # 4. Hot functions
        if rows:
            peak_cum = rows[0]["cumtime"]
            hot = [r for r in rows
                   if r["cumtime"] > peak_cum*0.5
                   and r["category"] not in ("BLOCKING","IMPORT","STDLIB")]
            if hot:
                desc = "\n".join(
                    f"  • {r['function']} ({r['filename']}) "
                    f"cum={r['cumtime']:.4f}s calls={r['ncalls']:,}"
                    for r in hot[:6])
                W.append(("🟠 HOTSPOT","TOP CUMULATIVE TIME FUNCTIONS",
                    f"Functions >50% of peak cumtime:\n{desc}","ORANGE"))

        # 5. Import cost
        import_time = sum(r["tottime"] for r in rows
                         if r["category"]=="IMPORT")
        if import_time > 0.5:
            W.append(("🟡 WARNING","SLOW IMPORTS",
                f"Import machinery used {import_time:.3f}s at startup.\n"
                "→ Lazy-import heavy modules (e.g. numpy, PySide6).",
                "YELLOW"))

        # 6. Dependency check
        dep = [r for r in rows
               if any(x in r["function"].lower()
                      for x in ["dependency","check_and_install",
                                 "get_installed_packages"])]
        if dep and max(r["cumtime"] for r in dep) > 0.2:
            t = max(r["cumtime"] for r in dep)
            W.append(("🟡 WARNING","DEPENDENCY CHECK OVERHEAD",
                f"pip dependency check runs every launch (~{t:.3f}s).\n"
                "→ Cache: write deps.lock keyed to requirements.txt hash.",
                "YELLOW"))

        # 7. High-call-count anomalies
        high = [r for r in rows
                if r["ncalls"] > 10000
                and r["category"] not in ("STDLIB","IMPORT")
                and r["tottime"] > 0.005]
        if high:
            desc = "\n".join(
                f"  • {r['function']} ({r['filename']}) {r['ncalls']:,} calls"
                for r in high[:5])
            W.append(("🟠 NOTE","HIGH CALL COUNT",
                f"User-code functions called very frequently:\n{desc}\n"
                "→ Verify call count doesn't grow unbounded per frame.",
                "ORANGE"))

    def _show_warnings(self):
        self.warn_text.delete("1.0","end")
        if not self.warnings:
            self.warn_text.insert("end","✅  No issues detected.\n","GREEN")
            return

        order = {"🔴 CRITICAL":0,"🟠 HOTSPOT":1,"🟠 NOTE":2,
                 "🟡 WARNING":3,"🟢 OK":4}
        self.warnings.sort(key=lambda w: order.get(w[0],9))

        counts = {}
        for sev,_,_,_ in self.warnings:
            counts[sev] = counts.get(sev,0)+1
        for sev, cnt in counts.items():
            tag = next(w[3] for w in self.warnings if w[0]==sev)
            self.warn_text.insert("end", f"{sev} ×{cnt}   ", tag)
        self.warn_text.insert("end","\n\n")

        for sev, title, body, tag in self.warnings:
            self.warn_text.insert("end",f"{'─'*72}\n")
            self.warn_text.insert("end",f"{sev}  {title}\n",("BOLD",tag))
            self.warn_text.insert("end",f"{body}\n\n")

        crits = [w for w in self.warnings if "CRITICAL" in w[0]]
        if crits:
            self.warn_frame.pack(fill="x")
            self.warn_lbl.configure(
                text="⚠  " + "  |  ".join(w[1] for w in crits))

    # ── log ───────────────────────────────────────────────────────────────────
    def _log(self, category, function, file_, details, pid="—"):
        ts    = datetime.now().strftime("%H:%M:%S")
        entry = (ts, str(pid), category, str(function)[:40],
                 str(file_)[:45], str(details)[:300])
        self.log_entries.append(entry)
        self.log_tree.insert("","end", values=entry,
                             tags=(category.upper(),))
        self.log_tree.yview_moveto(1.0)

    def _refresh_log(self):
        for item in self.log_tree.get_children():
            self.log_tree.delete(item)
        flt = self.log_filter.get()
        for e in self.log_entries:
            if flt == "ALL" or e[2] == flt:
                self.log_tree.insert("","end",values=e,tags=(e[2].upper(),))

    # ── export ────────────────────────────────────────────────────────────────
    def _export_report(self):
        if not self.all_rows:
            messagebox.showwarning("No Data","Run a profile first.")
            return
        script   = self.entry_point.get()
        app_name = Path(script).stem if script else "unknown"
        dt       = datetime.now().strftime("%Y-%m-%d_%H-%M-%S")

        desktop   = Path.home() / "Desktop"
        debug_dir = desktop / "Debug"
        try:
            debug_dir.mkdir(parents=True, exist_ok=True)
        except Exception:
            debug_dir = Path.home() / "Debug"
            debug_dir.mkdir(parents=True, exist_ok=True)

        outpath = debug_dir / f"{app_name}_{dt}.md"
        md = self._build_markdown(app_name, script, dt)
        try:
            with open(outpath,"w",encoding="utf-8") as f:
                f.write(md)
            messagebox.showinfo("Exported",f"Report saved:\n{outpath}")
            self._log("SYSTEM","export",str(outpath),f"{len(md):,} chars")
        except Exception as e:
            messagebox.showerror("Export Failed", str(e))

    def _build_markdown(self, app_name, script, dt):
        rows    = self.all_rows
        folder  = self.folder_path.get()
        elapsed = (time.time()-self.start_time) if self.start_time else 0
        cpu_h   = self.cpu_history
        ram_h   = self.ram_history
        fps_r   = [v for v in self.fps_history if v > 0]

        peak_cpu = max(cpu_h) if cpu_h else 0
        avg_cpu  = sum(cpu_h)/len(cpu_h) if cpu_h else 0
        peak_ram = max(ram_h) if ram_h else 0
        avg_ram  = sum(ram_h)/len(ram_h) if ram_h else 0
        avg_fps  = sum(fps_r)/len(fps_r) if fps_r else 0
        min_fps  = min(fps_r) if fps_r else 0
        max_fps  = max(fps_r) if fps_r else 0
        n_procs  = len(self.proc_meta)

        total_calls = sum(r["ncalls"]  for r in rows)
        total_self  = sum(r["tottime"] for r in rows)
        peak_cum    = rows[0]["cumtime"] if rows else 0

        def spark(data, w=50):
            d = [v for v in data if v >= 0]
            if not d: return "—"
            mn,mx = min(d),max(d)
            rng = mx-mn or 1
            chars = "▁▂▃▄▅▆▇█"
            s = d[::max(1,len(d)//w)][:w]
            return "".join(chars[min(7,int((v-mn)/rng*7))] for v in s)

        TH = ("| Rank | Process | Function | File | Calls |"
              " Tot Time | Cum Time | Per Call | Category |\n"
              "|-----:|:--------|:---------|:-----|------:|"
              "---------:|---------:|---------:|:---------:|")

        def TR(rlist):
            out = []
            for r in rlist:
                out.append(
                    f"| {r['rank']:>4} "
                    f"| `{str(r.get('process','?'))[:18]}` "
                    f"| `{r['function'][:34]}` "
                    f"| `{r['filename'][:22]}` "
                    f"| {r['ncalls']:>8,} "
                    f"| {r['tottime']:>9.6f} "
                    f"| {r['cumtime']:>9.6f} "
                    f"| {r['percall_t']:>9.6f} "
                    f"| {r['category']} |")
            return "\n".join(out)

        def sec(title, emoji, cat):
            r2 = [r for r in rows if r["category"]==cat]
            if not r2:
                return f"\n### {emoji} {title}\n\n_No entries._\n"
            return f"\n### {emoji} {title}\n\n{TH}\n{TR(r2)}\n"

        # warnings block
        warn_md = ""
        sev_order = {"🔴 CRITICAL":0,"🟠 HOTSPOT":1,"🟠 NOTE":2,
                     "🟡 WARNING":3,"🟢 OK":4}
        for sev,title,body,_ in sorted(self.warnings,
                                        key=lambda w: sev_order.get(w[0],9)):
            warn_md += f"\n#### {sev} — {title}\n\n{body}\n"

        # process inventory table
        proc_inv = ""
        for pid, meta in sorted(self.proc_meta.items()):
            sn   = os.path.basename(meta.get("script","?"))
            ppid = meta.get("ppid","?")
            pel  = meta.get("elapsed",0)
            rp   = self.proc_rows.get(pid,[])
            pc   = rp[0]["cumtime"] if rp else 0
            tc   = sum(r["ncalls"] for r in rp)
            proc_inv += (f"| `{pid}` | `{ppid}` | `{sn}` "
                         f"| {pel:.3f}s | {pc:.3f}s | {tc:,} |\n")

        # per-process tables
        per_proc_md = ""
        for pid, meta in sorted(self.proc_meta.items()):
            sn  = os.path.basename(meta.get("script","?"))
            rp  = self.proc_rows.get(pid,[])
            if not rp: continue
            per_proc_md += f"\n#### PID {pid} — `{sn}`\n\n{TH}\n{TR(rp[:20])}\n"

        # log tail
        log_md = ""
        for e in self.log_entries[-120:]:
            ts,pid_s,cat,func,fname,det = e
            log_md += (f"| `{ts}` | `{pid_s}` | **{cat}** "
                       f"| `{func}` | `{fname}` | {det} |\n")

        raw_out = self.raw_text.get("1.0","end")

        md = f"""# 🔬 DevProfiler v2 Report — `{app_name}`

> Generated: **{dt}**  ·  Script: `{script}`  ·  Folder: `{folder}`

---

## ⚠️ Health Warnings
{warn_md if warn_md else "_No warnings._"}

---

## 📋 Executive Summary

| Metric | Value |
|:-------|------:|
| Total Wall-Clock Runtime | `{elapsed:.3f}s` |
| Python Processes Profiled | `{n_procs}` |
| Total Function Calls (merged) | `{total_calls:,}` |
| Total Self Time | `{total_self:.6f}s` |
| Peak Cumulative Time | `{peak_cum:.6f}s` |
| Sort Mode | `{self.sort_by.get()}` |
| Top N Results | `{self.result_size.get()}` |

---

## 🔀 Process Inventory

| PID | PPID | Script | Elapsed | Peak Cum | Calls |
|----:|-----:|:-------|--------:|---------:|------:|
{proc_inv.strip()}

---

## 💻 System Metrics

| Metric | Peak | Average | Sparkline |
|:-------|-----:|--------:|:----------|
| CPU % | `{peak_cpu:.1f}%` | `{avg_cpu:.1f}%` | `{spark(cpu_h)}` |
| RAM MB | `{peak_ram:.0f} MB` | `{avg_ram:.0f} MB` | `{spark(ram_h)}` |
| FPS (real frames only) | `{max_fps:.0f}` | `{avg_fps:.0f}` | `{spark(fps_r)}` |
| Min FPS | `{min_fps:.0f}` | — | _(startup excluded)_ |

---

## 🏆 Top 20 Hotspots (Merged — All Processes)

{TH}
{TR(rows[:20])}

---

## 📂 Results by Category
{sec("Event Handlers", "🎮", "EVENT")}
{sec("Game Logic / Update", "⚙️", "LOGIC")}
{sec("Engine / Framework", "🔧", "ENGINE")}
{sec("OS Blocking Calls", "🔒", "BLOCKING")}
{sec("Standard Library", "📚", "STDLIB")}
{sec("Import Machinery", "📦", "IMPORT")}

---

## 🔀 Per-Process Breakdown
{per_proc_md}

---

## 🗒 Event Log (last 120 entries)

| Time | PID | Category | Function | File | Details |
|:-----|----:|:---------|:---------|:-----|:--------|
{log_md}
---

## 📄 Merged Raw pstats Output

```
{raw_out[:14000]}
```

---

_Report generated by **DevProfiler v2** — {datetime.now().strftime("%Y-%m-%d %H:%M:%S")}_
"""
        return md

    # ── helpers ───────────────────────────────────────────────────────────────
    def _set_status(self, text, color):
        self.status_lbl.configure(text=f"● {text}", fg=color)

    def _check_deps(self):
        missing = []
        if not HAS_PSUTIL:     missing.append("psutil")
        if not HAS_MATPLOTLIB: missing.append("matplotlib")
        if missing:
            self._log("SYSTEM","__init__","profiler.py",
                      f"Missing optional deps — run: pip install "
                      f"{' '.join(missing)}")


# ── entry point ───────────────────────────────────────────────────────────────
if __name__ == "__main__":
    app = ProfilerApp()
    app.mainloop()