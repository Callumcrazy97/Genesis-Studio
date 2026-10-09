using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Rendering.Core;
using Genesis.Runtime;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The real Player on every renderer, with GPU particles bursting every frame (ParticleBurstBatched
/// and ParticleBurstAt) over a 3D scene, while this process does to its window what a person does:
/// minimise and restore it (long enough for bursts to pile up, and quickly over and over), take
/// focus away and give it back, resize it (down to nothing and back), and toggle fullscreen (F11),
/// also minimising while fullscreen. After each the game must still be running, still drawing
/// frames, and a picture of it must not be black. Then it must close cleanly.
/// </summary>
/// <remarks>
/// <para>Unattended throughout: the window is cloaked and never activated, focus changes are
/// messages to the game's own window (it is never brought to the front), and fullscreen is toggled
/// only while the window is cloaked, so nothing covers the screen. <see cref="AttendedVariable"/>
/// adds the real thing: the game's window uncloaked and made the foreground window for the
/// fullscreen and focus steps. That covers the monitor, so it is off unless asked for.</para>
/// <para><see cref="BackendsVariable"/> limits a run to some renderers (comma separated short names).</para>
/// </remarks>
internal static class WindowLifecycleSuite
{
    public const string AttendedVariable = "GENESIS_LIFECYCLE_ATTENDED";
    public const string BackendsVariable = "GENESIS_LIFECYCLE_BACKENDS";
    /// <summary>A Player folder to drive instead of the newest one within reach (an older build, to show a fault).</summary>
    public const string PlayerVariable = "GENESIS_LIFECYCLE_PLAYER";

    private const string Spark = "Assets/Particles/Spark.particle.json";
    private const string Puff = "Assets/Particles/Puff.particle.json";

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "WindowLifecycle");
        bool attended = Environment.GetEnvironmentVariable(AttendedVariable) == "1";
        string[] only = (Environment.GetEnvironmentVariable(BackendsVariable) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? asked = Environment.GetEnvironmentVariable(PlayerVariable);
        // An older Player may not know WindowHasFocus / WindowIsMinimized: its game leaves them out.
        bool older = !string.IsNullOrWhiteSpace(asked) && File.Exists(Path.Combine(asked, RuntimePaths.RuntimeExeName));
        string? runtime = older ? asked : NewestPlayer();
        string parent = Path.Combine(ctx.Workspace, "WindowLifecycle");
        if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        Directory.CreateDirectory(parent);
        string project = WriteProject(parent, windowState: !older);
        var summary = new List<string>();

        // A script that does not compile would show up only as a game that draws nothing.
        bool scriptsCompile = false;
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Window.Lifecycle.GameScriptsCompile", () =>
        {
            PgslValidationReport report = PgslScriptValidator.ValidateProject(project, strict: true);
            HeadlessHarness.Assert(report.Success && report.ScriptCount >= 3,
                $"The lifecycle game's scripts do not compile ({report.ScriptCount} scripts): " + string.Join(" | ", report.Errors));
            scriptsCompile = true;
        });

        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
        {
            if (only.Length > 0 && !only.Contains(backend.ShortName, StringComparer.OrdinalIgnoreCase)) continue;
            HeadlessHarness.RunCase(ctx.Report, "Runtime.Window.Lifecycle." + backend.ShortName, () =>
            {
                if (runtime == null) throw new CheckNotRunException("No built Player was found.");
                if (!scriptsCompile) throw new CheckNotRunException("The game's scripts do not compile.");
                summary.Add(new Session(ctx, runtime, project, backend, attended, windowState: !older).Exercise());
            });
        }

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Window.Lifecycle.AttendedFullscreenAndFocus", () =>
        {
            if (!attended)
                throw new CheckNotRunException($"Real fullscreen and foreground changes cover the screen; set {AttendedVariable}=1 to run them.");
        });

        summary.Insert(0, "Player: " + (runtime ?? "(none)"));
        File.WriteAllLines(Path.Combine(ctx.Logs, "window-lifecycle.txt"), summary);
        foreach (string line in summary) Console.WriteLine(line);
    }

    /// <summary>One Player on one renderer, driven through every window change.</summary>
    private sealed class Session(HeadlessContext ctx, string runtime, string project, RenderBackendDescriptor backend, bool attended, bool windowState)
    {
        private readonly StringBuilder _output = new();
        private readonly List<string> _steps = [];
        private Process? _game;
        private IntPtr _window;
        private int _shot;

        private string Control => Path.Combine(project, "Control");
        private string Images => ProjectPaths.ImagesDir(project);

        public string Exercise()
        {
            Directory.CreateDirectory(Control);
            foreach (string file in Directory.GetFiles(Control)) File.Delete(file);
            if (Directory.Exists(Images)) Directory.Delete(Images, recursive: true);
            string playerLog = Path.Combine(ProjectPaths.LogsDir(project), "project_player.log");
            if (File.Exists(playerLog)) File.Delete(playerLog);

            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                Launch();
                _window = WaitForWindow(TimeSpan.FromSeconds(60));
                WaitForFrames(5, TimeSpan.FromSeconds(90), "start");
                Check("start", picture: true);
                RECT startRect = WindowRect();

                // Long enough minimised for a game bursting every update to queue thousands of
                // batched bursts: restoring used to give the renderer more steps than it takes.
                Minimise();
                ExpectWindowState("minimized=1", "WindowIsMinimized() is not true while the window is minimised");
                Thread.Sleep(8000);
                Alive("minimised 8 s");
                Restore();
                ExpectWindowState("minimized=0", "WindowIsMinimized() is still true after the window was restored");
                Check("restored after 8 s minimised", picture: true);

                for (int i = 0; i < 6; i++) { Minimise(); Thread.Sleep(120); Restore(); Thread.Sleep(120); }
                Check("minimised and restored 6 times quickly", picture: true);

                // Focus taken away and given back, as the window sees it when another window is
                // brought to the front (GLFW reads these; nothing is activated).
                Post(WmActivateApp, (IntPtr)1, IntPtr.Zero);
                Post(WmSetFocus, IntPtr.Zero, IntPtr.Zero);
                ExpectWindowState("focus=1", "WindowHasFocus() is not true after the window was given the focus");
                Post(WmKillFocus, IntPtr.Zero, IntPtr.Zero);
                Post(WmActivateApp, IntPtr.Zero, IntPtr.Zero);
                ExpectWindowState("focus=0", "WindowHasFocus() is still true after the window lost the focus");
                Check("focus lost", picture: false);
                Post(WmActivateApp, (IntPtr)1, IntPtr.Zero);
                Post(WmSetFocus, IntPtr.Zero, IntPtr.Zero);
                ExpectWindowState("focus=1", "WindowHasFocus() is not true after the window regained the focus");
                Check("focus regained", picture: true);

                foreach ((int width, int height) in new[] { (960, 540), (320, 180), (64, 40), (1, 1), (1600, 900), (startRect.Width, startRect.Height) })
                {
                    Resize(width, height);
                    Thread.Sleep(600);
                    (int clientWidth, int clientHeight) = ClientSize();
                    string label = $"resized to {width}x{height} (client {clientWidth}x{clientHeight})";
                    if (clientWidth <= 0 || clientHeight <= 0) { Thread.Sleep(1000); Alive(label); continue; }
                    Check(label, picture: clientWidth >= 64 && clientHeight >= 32);
                }

                // Resizing over and over while particles dispatch: swap chains rebuilt every few frames.
                for (int i = 0; i < 24; i++) { Resize(i % 2 == 0 ? 800 : 1100, i % 3 == 0 ? 450 : 620); Thread.Sleep(40); }
                Resize(startRect.Width, startRect.Height);
                Check("resized 24 times quickly", picture: true);

                // F11 is borderless fullscreen over the window's monitor. Cloaked, it covers nothing.
                if (attended || IsCloaked())
                {
                    if (attended) Cloak(false);
                    (int before, _) = ClientSize();
                    PressF11();
                    Thread.Sleep(1500);
                    (int fullWidth, int fullHeight) = ClientSize();
                    Check($"fullscreen (client {fullWidth}x{fullHeight})", picture: true);
                    HeadlessHarness.Assert(fullWidth > before, $"{backend.ShortName}: F11 did not make the window fullscreen ({before} -> {fullWidth} wide).");

                    // Going to the desktop and back while fullscreen: what crashed Direct3D 12.
                    Minimise();
                    Thread.Sleep(3000);
                    Alive("fullscreen, minimised");
                    Restore();
                    Check("fullscreen, restored", picture: true);
                    if (attended) { TakeForeground(); Thread.Sleep(500); Check("fullscreen, foreground", picture: true); }

                    PressF11();
                    Thread.Sleep(1500);
                    (int backWidth, int backHeight) = ClientSize();
                    Check($"windowed again (client {backWidth}x{backHeight})", picture: true);
                    HeadlessHarness.Assert(backWidth < fullWidth, $"{backend.ShortName}: a second F11 did not leave fullscreen.");
                    if (attended) Cloak(true);
                }
                else
                {
                    _steps.Add("fullscreen: NOT RUN (the window is not cloaked, so it would cover the screen)");
                }

                Close();
            }
            catch (Exception)
            {
                Kill();
                throw;
            }
            finally
            {
                File.WriteAllText(Path.Combine(ctx.Logs, $"window-lifecycle-{backend.ShortName}.log"),
                    string.Join(Environment.NewLine, _steps) + Environment.NewLine + Logs());
            }

            string line = $"{backend.ShortName}: {_steps.Count} steps in {clock.Elapsed.TotalSeconds:F0} s; " + string.Join("; ", _steps);
            return line;
        }

        private void Launch()
        {
            ProcessStartInfo start = new(Path.Combine(runtime, RuntimePaths.RuntimeExeName))
            {
                WorkingDirectory = runtime, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.Environment["GENESIS_PROJECT_PATH"] = project;
            start.Environment["GENESIS_UNATTENDED_WINDOW"] = "1";
            start.Environment["GENESIS_RENDER_BACKEND"] = RenderBackendSelection.ToEnvironmentValue(backend.Backend);
            foreach (string name in new[] { "GENESIS_START_ROOM", "GENESIS_AUTOSHOT", "GENESIS_BOOT_COORDINATED" }) start.Environment.Remove(name);
            _game = Process.Start(start) ?? throw new InvalidOperationException("The Player did not start.");
            try { _game.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
            // Read as it comes: waiting for the end of the output would also wait for any child
            // process (a shader compiler) that inherited the pipes.
            _game.OutputDataReceived += (_, e) => { if (e.Data != null) lock (_output) _output.AppendLine(e.Data); };
            _game.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_output) _output.AppendLine(e.Data); };
            _game.BeginOutputReadLine();
            _game.BeginErrorReadLine();
        }

        private void Close()
        {
            Post(WmClose, IntPtr.Zero, IntPtr.Zero);
            // The timed wait only: the untimed one also waits for the end of the redirected output.
            if (!_game!.WaitForExit(30_000))
                throw new InvalidOperationException($"{backend.ShortName}: the game did not close within 30 s of being asked to.");
            HeadlessHarness.Assert(_game.ExitCode == 0, $"{backend.ShortName}: the game closed with exit code {_game.ExitCode}.\r\n{Tail()}");
            string faults = Faults();
            HeadlessHarness.Assert(faults.Length == 0, $"{backend.ShortName}: the game logged failures:\r\n{faults}");
            _steps.Add("closed cleanly");
        }

        private void Kill()
        {
            try
            {
                if (_game is { HasExited: false }) { _game.Kill(entireProcessTree: true); _game.WaitForExit(10_000); }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        /// <summary>Running, drawing new frames, and (when asked) a picture of it that is not black.</summary>
        private void Check(string step, bool picture)
        {
            Alive(step);
            int before = Frames();
            int after = WaitForFrames(before + 3, TimeSpan.FromSeconds(15), step);
            string shot = "";
            if (picture)
            {
                string name = $"{backend.ShortName.ToLowerInvariant()}-{++_shot:00}";
                Picture picture1 = TakePicture(name, step);
                shot = $", picture {picture1.Width}x{picture1.Height} {picture1.LitShare:P0} lit";
                HeadlessHarness.Assert(picture1.LitShare > 0.5, $"{backend.ShortName}: after '{step}' the picture is black ({picture1.LitShare:P0} of it lit): {name}.png");
                Alive(step);
            }
            _steps.Add($"{step}: frames {before} -> {after}{shot}");
        }

        private void Alive(string step)
        {
            _game!.Refresh();
            if (_game.HasExited)
            {
                Thread.Sleep(300); // the last lines of its output
                throw new InvalidOperationException(
                    $"{backend.ShortName}: the game ended (exit code {_game.ExitCode}) at '{step}'.\r\n{Tail()}");
            }
        }

        private int WaitForFrames(int atLeast, TimeSpan timeout, string step)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (true)
            {
                int frames = Frames();
                if (frames >= atLeast) return frames;
                Alive(step);
                if (clock.Elapsed > timeout)
                    throw new InvalidOperationException(
                        $"{backend.ShortName}: no new frames for {timeout.TotalSeconds:F0} s after '{step}' (drawn {frames}, waiting for {atLeast}).\r\n{Tail()}");
                Thread.Sleep(100);
            }
        }

        /// <summary>Waits for the game's Step event to report <paramref name="expected"/> (WindowHasFocus / WindowIsMinimized).</summary>
        private void ExpectWindowState(string expected, string failure)
        {
            if (!windowState) return;
            Stopwatch clock = Stopwatch.StartNew();
            string seen = "";
            while (clock.Elapsed < TimeSpan.FromSeconds(5))
            {
                Alive(expected);
                seen = LastReport("LIFECYCLE focus=");
                if (seen.Contains(expected, StringComparison.Ordinal))
                {
                    _steps.Add($"script saw {expected}");
                    return;
                }
                Thread.Sleep(50);
            }
            throw new InvalidOperationException($"{backend.ShortName}: {failure} (the script last wrote '{seen.Trim()}').");
        }

        /// <summary>Frames the game's Draw event has run, as it last printed them.</summary>
        private int Frames()
        {
            string text = LastReport("LIFECYCLE frames=");
            return double.TryParse(text.Length > 0 ? text["LIFECYCLE frames=".Length..].Trim() : "", NumberStyles.Float,
                CultureInfo.InvariantCulture, out double value) ? (int)value : 0;
        }

        /// <summary>The last line of the game's output that starts with <paramref name="prefix"/>, or empty.</summary>
        private string LastReport(string prefix)
        {
            string output;
            lock (_output) output = _output.ToString();
            int at = output.LastIndexOf(prefix, StringComparison.Ordinal);
            if (at < 0) return "";
            int end = output.IndexOf('\n', at);
            return (end < 0 ? output[at..] : output[at..end]).Trim();
        }

        private readonly record struct Picture(int Width, int Height, double LitShare);

        private Picture TakePicture(string name, string step)
        {
            string path = Path.Combine(Images, name + ".png");
            string request = Path.Combine(Control, "shot.txt");
            File.WriteAllText(request + ".tmp", name);
            File.Move(request + ".tmp", request, overwrite: true);
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                Alive(step);
                if (File.Exists(path))
                {
                    try
                    {
                        using var bitmap = new Bitmap(path);
                        int lit = 0, total = 0;
                        for (int y = 0; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 24))
                            for (int x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 32))
                            {
                                Color pixel = bitmap.GetPixel(x, y);
                                if (pixel.R + pixel.G + pixel.B > 48) lit++;
                                total++;
                            }
                        File.Copy(path, Path.Combine(ctx.Captures, "window-lifecycle-" + name + ".png"), overwrite: true);
                        return new Picture(bitmap.Width, bitmap.Height, total == 0 ? 0 : lit / (double)total);
                    }
                    catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
                    {
                        // Still being written.
                    }
                }
                Thread.Sleep(150);
            }
            throw new InvalidOperationException($"{backend.ShortName}: the picture asked for after '{step}' was not taken within 20 s ({name}).\r\n{Tail()}");
        }

        // ── The window ──────────────────────────────────────────────────────────

        private IntPtr WaitForWindow(TimeSpan timeout)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.Elapsed < timeout)
            {
                Alive("window");
                IntPtr found = IntPtr.Zero;
                int id = _game!.Id;
                EnumWindows((hwnd, _) =>
                {
                    GetWindowThreadProcessId(hwnd, out int owner);
                    if (owner != id || !IsWindowVisible(hwnd)) return true;
                    var name = new StringBuilder(64);
                    GetClassName(hwnd, name, name.Capacity);
                    if (!name.ToString().StartsWith("GLFW", StringComparison.Ordinal)) return true;
                    found = hwnd;
                    return false;
                }, IntPtr.Zero);
                if (found != IntPtr.Zero) return found;
                Thread.Sleep(100);
            }
            throw new InvalidOperationException($"{backend.ShortName}: the game showed no window within {timeout.TotalSeconds:F0} s.\r\n{Tail()}");
        }

        private void Minimise()
        {
            // Not SW_MINIMIZE: that activates the next window, which may be someone's work.
            ShowWindow(_window, ShowMinNoActive);
            Wait(() => IsIconic(_window), "minimise");
        }

        private void Restore()
        {
            ShowWindow(_window, ShowNoActivate);
            Wait(() => !IsIconic(_window), "restore");
        }

        private void Resize(int width, int height) =>
            SetWindowPos(_window, IntPtr.Zero, 0, 0, width, height, SwpNoMove | SwpNoZOrder | SwpNoActivate);

        private void PressF11()
        {
            const int f11 = 0x7A;
            uint scan = MapVirtualKey(f11, 0);
            Post(WmKeyDown, (IntPtr)f11, (IntPtr)(1 | (int)(scan << 16)));
            Thread.Sleep(80);
            Post(WmKeyUp, (IntPtr)f11, (IntPtr)(1 | (int)(scan << 16) | (1 << 30) | unchecked((int)(1u << 31))));
        }

        private void Post(uint message, IntPtr wParam, IntPtr lParam) => PostMessage(_window, message, wParam, lParam);

        private void Wait(Func<bool> condition, string what)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!condition())
            {
                Alive(what);
                if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException($"{backend.ShortName}: the window did not {what}.");
                Thread.Sleep(20);
            }
        }

        private RECT WindowRect() { GetWindowRect(_window, out RECT rect); return rect; }

        private (int Width, int Height) ClientSize()
        {
            GetClientRect(_window, out RECT rect);
            return (rect.Right - rect.Left, rect.Bottom - rect.Top);
        }

        private bool IsCloaked() => DwmGetWindowAttribute(_window, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

        private void Cloak(bool cloak)
        {
            int value = cloak ? 1 : 0;
            DwmSetWindowAttribute(_window, DwmwaCloak, ref value, sizeof(int));
        }

        private void TakeForeground()
        {
            uint target = GetWindowThreadProcessId(_window, out _);
            uint self = GetCurrentThreadId();
            AttachThreadInput(self, target, true);
            try { SetForegroundWindow(_window); }
            finally { AttachThreadInput(self, target, false); }
        }

        // ── Logs ────────────────────────────────────────────────────────────────

        private string Logs()
        {
            string player = ReadShared(Path.Combine(ProjectPaths.LogsDir(project), "project_player.log"));
            string render = _game == null ? "" : ReadShared(RenderLogPath(_game.Id));
            string output;
            lock (_output) output = _output.ToString();
            return player + Environment.NewLine + render + Environment.NewLine + output;
        }

        private string Faults()
        {
            string[] markers = ["Render error", "Runtime failure", "EndFrame error", "Present error", "OnWindowResize error", "Fatal error", "Unhandled exception"];
            return string.Join(Environment.NewLine, Logs().Split('\n')
                .Where(line => markers.Any(marker => line.Contains(marker, StringComparison.Ordinal)))
                .Select(line => line.Length > 400 ? line[..400] : line.TrimEnd()));
        }

        private string Tail()
        {
            string log = Logs();
            return log.Length > 3000 ? log[^3000..] : log;
        }

        private static string RenderLogPath(int processId) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenesisRuntime", "Logs", $"render-{processId}.log");
    }

    private static string ReadShared(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    // ── The game ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A 3D room with the engine's sky at noon, three cubes, twenty batched spark bursts in every
    /// update and a puff burst every fourth. Its Draw event prints how many frames it has drawn
    /// (and its Step event the window's focus and minimised state) on the game's output; it takes
    /// a picture when Control/shot.txt names a new one.
    /// </summary>
    private static string WriteProject(string parent, bool windowState)
    {
        // Reported on the game's output (cheap) rather than in files: a file write each frame cost
        // the test game most of its frame rate.
        string reportWindow = windowState
            ? """
                focused = 0; if (WindowHasFocus()) { focused = 1; }
                minimized = 0; if (WindowIsMinimized()) { minimized = 1; }
                state = "focus=" + String(focused) + ";minimized=" + String(minimized) + ";";
                if (state != lastState) { Print("LIFECYCLE " + state); lastState = state; }
              """
            : "";
        ProjectSession project = new ProjectService().CreateProject(parent, "Lifecycle Game", "Blank");
        ResourceService resources = new(project);
        string control = Path.Combine(project.RootPath, "Control").Replace('\\', '/') + "/";

        string particles = Path.Combine(project.AssetsPath, "Particles");
        Directory.CreateDirectory(particles);
        File.WriteAllText(Path.Combine(particles, "Spark.particle.json"), ParticleJson("Spark", burst: 2, lifetime: 1.2, size: 0.12, r: 1, g: 0.8, b: 0.3, blend: "Additive"));
        File.WriteAllText(Path.Combine(particles, "Puff.particle.json"), ParticleJson("Puff", burst: 12, lifetime: 1.5, size: 0.4, r: 0.8, g: 0.85, b: 0.9, blend: "Alpha"));

        string objects = Path.Combine(project.AssetsPath, "Objects");
        Directory.CreateDirectory(objects);
        string objectFile = resources.CreateResource(objects, ResourceKind.GameObject, "Burster");
        File.WriteAllText(objectFile, "{\"schemaVersion\":2,\"dimension\":\"ThreeD\",\"components\":[{\"type\":\"ScriptComponent\",\"props\":{\"ScriptClass\":\"Burster\"}}],\"events\":[\"Create\",\"Step\",\"Draw\"]}");
        string scripts = Path.Combine(objects, "Burster");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "Create.pgsl"),
            $"drawn = 0; steps = 0; nextPoll = 0; nextBeat = 0; focused = 0; minimized = 0; state = \"\"; lastState = \"\"; lastShot = \"\"; want = \"\"; ctl = \"{control}\"; ParticleSetBurstLimit(64);");
        File.WriteAllText(Path.Combine(scripts, "Step.pgsl"), $$"""
            SetCameraPosition(0, 4, -14); SetCameraTarget(0, 2, 4);
            for (var i = 0; i < 20; i = i + 1) { ParticleBurstBatched("{{Spark}}", RandomRange(-8, 8), RandomRange(1, 6), RandomRange(0, 10), 1); }
            steps += 1;
            if (steps >= 4) { steps = 0; ParticleBurstAt("{{Puff}}", RandomRange(-6, 6), 2, RandomRange(2, 8), 1, 0); }
            {{reportWindow}}
            if (TimeMs() >= nextPoll) {
                nextPoll = TimeMs() + 250;
                if (FileExists(ctl + "shot.txt")) {
                    want = FileReadText(ctl + "shot.txt");
                    if (want != "" && want != lastShot) { ScreenshotSave(want); lastShot = want; }
                }
            }
            """);
        File.WriteAllText(Path.Combine(scripts, "Draw.pgsl"), """
            drawn += 1;
            DrawCubeColoured3D(-4, 1, 6, 2, 220, 60, 50);
            DrawCubeColoured3D(0, 1, 8, 2, 60, 200, 80);
            DrawCubeColoured3D(4, 1, 6, 2, 60, 90, 220);
            if (TimeMs() >= nextBeat) { Print("LIFECYCLE frames=" + String(drawn)); nextBeat = TimeMs() + 100; }
            """);

        string roomFile = ProjectRoomResolver.ResolveRoomFile(project.RootPath, project.Manifest.StartRoom);
        RoomAsset start = RoomAsset.Create("Start", RoomDimension.ThreeD);
        start.Settings.CaptureMouse = false;
        start.Environment.DynamicSky = true;
        start.Environment.Weather = "Clear";
        start.Environment.TimeOfDayHours = 12f;
        start.Environment.TimeScale = 0f;
        start.Nodes.Add(new RoomNode
        {
            Kind = RoomNodeKind.GameObject, Name = "Burster", LayerId = start.Layers[0].Id,
            GameObject = new RoomGameObjectData { Prefab = Path.GetRelativePath(project.RootPath, objectFile).Replace('\\', '/') },
        });
        RoomAssetLoader.Save(start, roomFile);
        ResourceCatalog.Invalidate(project.RootPath);
        return project.RootPath;
    }

    private static string ParticleJson(string name, int burst, double lifetime, double size, double r, double g, double b, string blend) =>
        string.Create(CultureInfo.InvariantCulture, $$"""
            {
                "effectName": "{{name}}",
                "maxParticles": {{burst * 4}},
                "emitRate": 0,
                "burstCount": {{burst}},
                "loop": false,
                "shape": "Sphere",
                "spreadDegrees": 180,
                "emitRadius": 0.2,
                "speed": 1.5,
                "speedVariance": 0.5,
                "gravity": -1.5,
                "drag": 0.8,
                "lifetime": {{lifetime}},
                "lifetimeVariance": 0.3,
                "startSize": {{size}},
                "endSize": {{size * 0.5}},
                "emissive": 1,
                "blendMode": "{{blend}}",
                "startColor": { "r": {{r}}, "g": {{g}}, "b": {{b}}, "a": 1 },
                "endColor": { "r": {{r}}, "g": {{g}}, "b": {{b}}, "a": 0 }
            }
            """);

    // The newest Player within reach (as PostEffectsSuite finds it).
    private static string? NewestPlayer()
    {
        List<string> candidates = [];
        if (RuntimePaths.ResolveRuntimeDir() is { } resolved) candidates.Add(resolved);
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string published = Path.Combine(directory.FullName, "Genesis Application", "Player");
            if (File.Exists(Path.Combine(published, RuntimePaths.RuntimeExeName))) { candidates.Add(published); break; }
        }
        return candidates
            .Where(path => File.Exists(Path.Combine(path, "Genesis.Runtime.dll")))
            .OrderByDescending(path => File.GetLastWriteTimeUtc(Path.Combine(path, "Genesis.Runtime.dll")))
            .FirstOrDefault();
    }

    // ── Win32 ───────────────────────────────────────────────────────────────────

    private const int ShowNoActivate = 4;       // SW_SHOWNOACTIVATE: restores without activating
    private const int ShowMinNoActive = 7;      // SW_SHOWMINNOACTIVE
    private const uint SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;
    private const uint WmSetFocus = 0x0007, WmKillFocus = 0x0008, WmClose = 0x0010, WmActivateApp = 0x001C;
    private const uint WmKeyDown = 0x0100, WmKeyUp = 0x0101;
    private const int DwmwaCloak = 13, DwmwaCloaked = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
