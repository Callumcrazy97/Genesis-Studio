using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Genesis.Runtime.Project;

namespace Genesis.Application.Headless.Showcase;

/// <summary>
/// Records the actual game: compiles and launches the Player exactly as File → Run (F5) does, sends
/// real keystrokes to its window, and captures that window.
/// </summary>
/// <remarks>
/// Deliberately not a simulation. An earlier version of this recording drove the ECS in-process and
/// rendered through the Room Editor's 2D view, which showed the right positions but was still the
/// editor — the Player's own window, its own render loop and its own input stack were never
/// exercised. This runs `GenesisEngine.exe` as a separate process and talks to it the way a person
/// does: OS-level key events into the focused window, screen capture of its client area.
/// </remarks>
internal static class GameRunRecorder
{
    // ── Win32 ───────────────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;

    /// <summary>
    /// Presses or releases a key for the game window.
    /// </summary>
    /// <remarks>
    /// Two mechanisms on purpose. <c>keybd_event</c> is the real OS-level path and is what a
    /// keyboard does, but it only reaches the window that currently has focus — and Windows blocks
    /// <c>SetForegroundWindow</c> from a background process often enough that relying on it alone
    /// produced a recording where nothing moved. <c>PostMessage</c> delivers WM_KEYDOWN/WM_KEYUP to
    /// the window regardless of focus, which is what GLFW's Win32 backend reads. Sending both means
    /// the capture does not silently depend on winning the foreground race.
    /// </remarks>
    private static void SetKey(IntPtr window, char key, bool down)
    {
        byte vk = VirtualKey(key);
        uint scan = MapVirtualKey(vk, 0);

        keybd_event(vk, (byte)scan, down ? 0u : KeyEventKeyUp, UIntPtr.Zero);

        // lParam: repeat count 1, scan code, and for key-up the transition/previous-state bits.
        IntPtr lParam = down
            ? (IntPtr)(1 | (int)(scan << 16))
            : (IntPtr)(1 | (int)(scan << 16) | (1 << 30) | (1 << 31));
        PostMessage(window, down ? WmKeyDown : WmKeyUp, (IntPtr)vk, lParam);
    }

    /// <summary>Foregrounds the window, attaching to its input queue so Windows allows it.</summary>
    private static bool ForceForeground(IntPtr window)
    {
        uint target = GetWindowThreadProcessId(window, IntPtr.Zero);
        uint self = GetCurrentThreadId();

        AttachThreadInput(self, target, true);
        try
        {
            SetForegroundWindow(window);
        }
        finally
        {
            AttachThreadInput(self, target, false);
        }

        Thread.Sleep(250);
        return GetForegroundWindow() == window;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const uint KeyEventKeyUp = 0x0002;

    /// <summary>Virtual-key codes. W/A/S/D are their ASCII values.</summary>
    private static byte VirtualKey(char c) => (byte)char.ToUpperInvariant(c);

    // ── Recording ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Launches the project's start room and records a scripted WASD sequence.
    /// </summary>
    /// <returns>The captured frames; the caller owns and disposes them.</returns>
    public static List<Bitmap> Record(string projectRoot, string roomName, out string diagnostics)
    {
        var frames = new List<Bitmap>();
        var log = new List<string>();

        ProjectRunLauncher.CompileOutcome compile = ProjectRunLauncher.CompileScripts(projectRoot);
        if (!compile.Success)
        {
            throw new InvalidOperationException(
                $"File → Run would refuse this project: script compile failed.{Environment.NewLine}"
                + compile.ErrorMessage);
        }

        log.Add("compiled designer scripts");

        ProjectRunLauncher.LaunchOutcome launch = ProjectRunLauncher.Launch(
            projectRoot, roomName: roomName);
        if (!launch.Success || launch.Process is null)
        {
            throw new InvalidOperationException(
                $"The Player did not launch: {launch.ErrorMessage ?? "no process"}. "
                + "Build the player (Build.bat publishes it to 'Genesis Application/Player') first.");
        }

        Process game = launch.Process;
        log.Add($"launched {Path.GetFileName(launch.RuntimeDir ?? "?")}/GenesisEngine.exe → room '{launch.RoomName}'");

        try
        {
            IntPtr window = WaitForWindow(game, TimeSpan.FromSeconds(25));
            log.Add($"window 0x{window.ToInt64():X} appeared");

            // Let the room load and the first frames settle before recording.
            bool focused = ForceForeground(window);
            log.Add(focused ? "window focused" : "window NOT focused (posting messages instead)");

            // The Player shows a boot splash and warms shaders first — around eight seconds from
            // launch on this machine. Recording on a fixed delay caught the splash and produced a
            // GIF of a blank white window, so wait for the scene itself to appear instead of
            // guessing a duration.
            TimeSpan warmup = WaitForRenderedScene(window, TimeSpan.FromSeconds(40));
            log.Add($"scene rendered after {warmup.TotalSeconds:0.0}s of boot/splash");

            // Idle frames first, so the GIF opens on the game as it starts.
            for (int i = 0; i < 5; i++)
            {
                frames.Add(CaptureWindow(window));
                Thread.Sleep(60);
            }

            // Matches the 2D Platformer template's controls (A/D to move, W to jump): run right,
            // jump, run right again, then back left — so the recording shows locomotion, gravity
            // and the coin pickups rather than a shape sliding around.
            foreach ((char key, int frameCount) in new[]
                     { ('D', 16), ('W', 8), ('D', 14), ('W', 8), ('A', 16) })
            {
                SetKey(window, key, down: true);
                try
                {
                    for (int i = 0; i < frameCount; i++)
                    {
                        frames.Add(CaptureWindow(window));
                        Thread.Sleep(55);
                    }
                }
                finally
                {
                    SetKey(window, key, down: false);
                }

                frames.Add(CaptureWindow(window));
                Thread.Sleep(90);
            }

            log.Add($"{frames.Count} frames captured from the running game");
        }
        finally
        {
            // Always release the keys, whatever happened, or they stay stuck down for the desktop.
            foreach (char key in "WASD")
            {
                keybd_event(VirtualKey(key), 0, KeyEventKeyUp, UIntPtr.Zero);
            }

            try
            {
                if (!game.HasExited)
                {
                    game.CloseMainWindow();
                    if (!game.WaitForExit(3000))
                    {
                        game.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or SystemException)
            {
                log.Add($"(the game process had already gone: {exception.GetType().Name})");
            }

            game.Dispose();
        }

        diagnostics = string.Join("; ", log);
        return frames;
    }

    /// <summary>
    /// Waits until the window is showing an actual scene rather than the boot splash.
    /// </summary>
    /// <remarks>
    /// The splash is essentially flat, and the room is not — a sky gradient, tiles and a sprite give
    /// far more distinct colours. Sampling for colour variety is a cheap, self-adjusting stand-in
    /// for "the game has started", and it does not care how long the machine takes to warm shaders.
    /// </remarks>
    private static TimeSpan WaitForRenderedScene(IntPtr window, TimeSpan timeout)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            using Bitmap probe = CaptureWindow(window);
            if (DistinctSampledColours(probe) >= 12 && GameplayRegionHasDetail(probe))
            {
                // A couple of extra frames so the first recorded one is not mid-fade.
                Thread.Sleep(600);
                return clock.Elapsed;
            }

            Thread.Sleep(250);
        }

        throw new InvalidOperationException(
            $"The game never rendered a scene within {timeout.TotalSeconds:0} s — the window stayed on "
            + "the boot splash or a blank surface. Its log under the project's Debug/Logs folder will "
            + "say how far the boot sequence got.");
    }

    private static int DistinctSampledColours(Bitmap bitmap)
    {
        var seen = new HashSet<int>();
        int stepX = Math.Max(1, bitmap.Width / 60);
        int stepY = Math.Max(1, bitmap.Height / 40);
        for (int y = 0; y < bitmap.Height; y += stepY)
        {
            for (int x = 0; x < bitmap.Width; x += stepX)
            {
                seen.Add(bitmap.GetPixel(x, y).ToArgb());
            }
        }

        return seen.Count;
    }

    /// <summary>
    /// The animated boot logo has enough colours to fool a whole-window variety check. The played
    /// platformer always has detailed sky/HUD pixels in the upper-left gameplay region, while the
    /// splash remains flat there, so require both signals before recording begins.
    /// </summary>
    private static bool GameplayRegionHasDetail(Bitmap bitmap)
    {
        var seen = new HashSet<int>();
        int left = Math.Min(bitmap.Width - 1, Math.Max(0, bitmap.Width / 80));
        int top = Math.Min(bitmap.Height - 1, Math.Max(0, bitmap.Height / 30));
        int right = Math.Min(bitmap.Width, Math.Max(left + 1, bitmap.Width / 4));
        int bottom = Math.Min(bitmap.Height, Math.Max(top + 1, bitmap.Height / 5));
        int stepX = Math.Max(1, (right - left) / 40);
        int stepY = Math.Max(1, (bottom - top) / 20);
        for (int y = top; y < bottom; y += stepY)
        {
            for (int x = left; x < right; x += stepX)
            {
                seen.Add(bitmap.GetPixel(x, y).ToArgb());
                if (seen.Count >= 8) return true;
            }
        }

        return false;
    }

    private static IntPtr WaitForWindow(Process game, TimeSpan timeout)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            game.Refresh();
            if (game.HasExited)
            {
                throw new InvalidOperationException(
                    $"The Player exited with code {game.ExitCode} before showing a window. "
                    + "Its log under the project's Build folder will say why.");
            }

            if (game.MainWindowHandle != IntPtr.Zero)
            {
                return game.MainWindowHandle;
            }

            Thread.Sleep(120);
        }

        throw new InvalidOperationException(
            $"The Player never opened a window within {timeout.TotalSeconds:0} s.");
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    /// <summary>PW_RENDERFULLCONTENT — renders composited (DWM/D3D) content, not just GDI.</summary>
    private const uint PwRenderFullContent = 2;

    private const uint GaRoot = 2;

    /// <summary>
    /// Captures the game window's own pixels.
    /// </summary>
    /// <remarks>
    /// Tries <c>PrintWindow(PW_RENDERFULLCONTENT)</c> first, which reads the window's composited
    /// surface and is therefore safe regardless of what is in front of it. Screen copying is the
    /// fallback because the game renders through D3D and GDI's <c>DrawToBitmap</c> returns black —
    /// but a screen copy takes whatever pixels occupy those coordinates, so it is gated on the game
    /// genuinely being the window at that point on screen. It has to be: an earlier version of this
    /// recorded the user's browser when the Player lost focus, which is a privacy failure, not a
    /// glitch. If neither route is safe this throws rather than returning someone else's screen.
    /// </remarks>
    private static Bitmap CaptureWindow(IntPtr window)
    {
        if (!GetClientRect(window, out RECT rect))
        {
            throw new InvalidOperationException("Could not read the game window's client rectangle.");
        }

        int width = Math.Max(1, rect.Right - rect.Left);
        int height = Math.Max(1, rect.Bottom - rect.Top);

        Bitmap shot = new(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(shot))
        {
            IntPtr hdc = graphics.GetHdc();
            try
            {
                if (PrintWindow(window, hdc, PwRenderFullContent))
                {
                    graphics.ReleaseHdc(hdc);
                    if (!IsEffectivelyBlank(shot)) return shot;

                    // PrintWindow succeeds but yields an empty surface on some flip-model swap
                    // chains; fall through to the guarded screen copy.
                    graphics.GetHdc();
                }
            }
            finally
            {
                try { graphics.ReleaseHdc(); }
                catch (ArgumentException) { /* already released above */ }
            }
        }

        AssertWindowIsOnTop(window, width, height);

        POINT origin = default;
        ClientToScreen(window, ref origin);
        using (Graphics graphics = Graphics.FromImage(shot))
        {
            graphics.CopyFromScreen(
                new Point(origin.X, origin.Y), Point.Empty, new Size(width, height),
                CopyPixelOperation.SourceCopy);
        }

        return shot;
    }

    /// <summary>
    /// Refuses to screen-copy unless the game really is the window occupying that area.
    /// </summary>
    private static void AssertWindowIsOnTop(IntPtr window, int width, int height)
    {
        POINT centre = new() { X = width / 2, Y = height / 2 };
        ClientToScreen(window, ref centre);

        IntPtr atPoint = GetAncestor(WindowFromPoint(centre), GaRoot);
        IntPtr expected = GetAncestor(window, GaRoot);

        if (atPoint != expected || GetForegroundWindow() != expected)
        {
            throw new InvalidOperationException(
                "Refusing to capture: the game window is not the frontmost window at its own centre, "
                + "so a screen copy would record whatever is covering it. Re-run without other "
                + "windows over the game, or fix PrintWindow capture for this swap chain.");
        }
    }

    /// <summary>True when the image is a single flat colour — an empty PrintWindow result.</summary>
    private static bool IsEffectivelyBlank(Bitmap bitmap)
    {
        int first = bitmap.GetPixel(0, 0).ToArgb();
        int stepX = Math.Max(1, bitmap.Width / 40);
        int stepY = Math.Max(1, bitmap.Height / 30);
        for (int y = 0; y < bitmap.Height; y += stepY)
        {
            for (int x = 0; x < bitmap.Width; x += stepX)
            {
                if (bitmap.GetPixel(x, y).ToArgb() != first) return false;
            }
        }

        return true;
    }
}
