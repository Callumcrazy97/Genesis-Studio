using System;

namespace Genesis.Shared.Interfaces
{
    /// <summary>How the window is presented to the user.</summary>
    public enum WindowMode
    {
        /// <summary>Bordered, resizable window.</summary>
        Windowed,
        /// <summary>Borderless window sized to the work area (borderless "fullscreen").</summary>
        Borderless,
        /// <summary>Exclusive / true fullscreen.</summary>
        Fullscreen,
    }

    /// <summary>OS cursor presentation. Games can draw their own cursor when Hidden.</summary>
    public enum CursorMode
    {
        /// <summary>Normal OS cursor, absolute position.</summary>
        Normal,
        /// <summary>Cursor hidden but absolute position still reported (game draws its own).</summary>
        Hidden,
        /// <summary>Cursor hidden and locked; only relative motion (first-person look).</summary>
        Locked,
    }

    /// <summary>
    /// Backend-agnostic game window. The engine, editors, runtime and exported games all
    /// talk to this interface only — never to a concrete windowing library — so the same
    /// game/render loop runs on every platform by swapping the implementation (Silk.NET on
    /// desktop today; a console backend can implement the same contract later).
    ///
    /// The window owns the OS surface (and its <see cref="NativeHandle"/>) and the frame
    /// pacing; it does NOT own the renderer. A host creates an IRenderController, binds it to
    /// <see cref="NativeHandle"/> on <see cref="Load"/>, draws inside <see cref="Render"/>, and
    /// resizes the swap chain inside <see cref="Resize"/>.
    /// </summary>
    public interface IGameWindow : IDisposable
    {
        // ── Basic properties ──────────────────────────────────────────────────────
        string     Title       { get; set; }
        int        Width       { get; }   // framebuffer width  in pixels
        int        Height      { get; }   // framebuffer height in pixels
        int        TargetFps   { get; set; }  // 0 = uncapped
        double     CurrentFps  { get; }        // measured, smoothed
        bool       VSync       { get; set; }
        WindowMode Mode        { get; set; }
        bool       IsRunning   { get; }
        IntPtr     NativeHandle { get; }   // HWND on Windows; valid from Load onward

        // ── Lifecycle events ──────────────────────────────────────────────────────
        /// <summary>Surface created; NativeHandle is now valid. Create GPU resources here.</summary>
        event Action            Load;
        /// <summary>Variable-step game logic. Argument is delta time in seconds.</summary>
        event Action<double>    Update;
        /// <summary>Draw a frame. Argument is delta time in seconds.</summary>
        event Action<double>    Render;
        /// <summary>Framebuffer changed size. Arguments are new width/height in pixels.</summary>
        event Action<int, int>  Resize;
        /// <summary>Window is closing; release resources here.</summary>
        event Action            Closing;

        // ── Control ───────────────────────────────────────────────────────────────
        /// <summary>Runs the blocking window/event/render loop until the window closes.</summary>
        void Run();
        /// <summary>Requests the window to close, ending <see cref="Run"/>.</summary>
        void Close();

        /// <summary>Resizes the windowed client area (ignored while borderless/fullscreen).</summary>
        void SetSize(int width, int height);

        /// <summary>When true, the cursor is hidden and mouse deltas drive look.</summary>
        bool MouseCaptured { get; }

        /// <summary>Hide/capture or show/release the OS cursor for first-person play.</summary>
        void SetMouseCaptured(bool captured);

        /// <summary>Full control over cursor presentation (Normal / Hidden / Locked).</summary>
        void SetCursorMode(CursorMode mode);
    }
}
