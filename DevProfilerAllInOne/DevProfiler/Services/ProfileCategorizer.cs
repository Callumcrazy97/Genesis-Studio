namespace DevProfiler.Services;

public static class ProfileCategorizer
{
    public static string Categorize(string file, string function)
    {
        string fp = (file ?? string.Empty).ToLowerInvariant();
        string fn = (function ?? string.Empty).ToLowerInvariant();

        string[] blocking = ["waitforsingleobject", "waitformultipleobjects", "sleep", "waitpid", "_wait", "communicate", "semaphore", "acquire", "monitor.wait", "thread.sleep", "ntwait"];
        if (blocking.Any(fn.Contains))
            return "BLOCKING";

        string[] events = ["event", "handle", "on_", "dispatch", "callback", "listener", "emit", "signal", "keydown", "keyup", "mousedown", "mousemove", "click", "press", "release", "scroll"];
        if (events.Any(fn.Contains))
            return "EVENT";

        string[] engineFiles = ["pygame", "pyside", "pyqt", "tkinter", "opengl", "glfw", "monogame", "unity", "godot", "directx", "d3d", "vulkan", "sdl", "wpf"];
        if (engineFiles.Any(fp.Contains))
            return "ENGINE";

        string[] imports = ["importlib", "<frozen", "bootstrap", "loader"];
        if (imports.Any(fp.Contains) || fn is "<module>" or "exec_module" or "_find_and_load" or "_load_unlocked")
            return "IMPORT";

        string[] standard = ["site-packages", "dist-packages", "python3", "python310", "python311", "python312", "python313", "system.private.corelib", "system.runtime", "mscorlib"];
        if (standard.Any(fp.Contains))
            return "STDLIB";

        string[] logic = ["update", "tick", "step", "loop", "process", "render", "draw", "blit", "paint", "frame", "run", "execute", "compute", "physics", "collide", "spawn", "destroy", "move", "generate", "chunk"];
        if (logic.Any(fn.Contains))
            return "LOGIC";

        return "LOGIC";
    }
}
