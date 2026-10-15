using System;
using Genesis.Runtime.Input;
using Genesis.Runtime.Project;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime
{
    /// <summary>F12 screenshot hook shared by standalone player and editor play/sandbox.</summary>
    public static class RuntimeScreenshot
    {
        /// <param name="frameAlreadySubmitted">True when the host has already ended the frame (between EndFrame and Present).</param>
        public static string CaptureFrame(IRenderController renderer, string projectPath, string label = null, bool frameAlreadySubmitted = false)
        {
            if (renderer == null || string.IsNullOrWhiteSpace(projectPath))
                return null;

            string name = string.IsNullOrWhiteSpace(label)
                ? "screenshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                : label;
            string path = ProjectScreenshot.Capture(renderer, projectPath, name, frameAlreadySubmitted);
            // Said either way: a picture that silently never appears is hard to tell from a key that never arrived.
            Genesis.Rendering.Diagnostics.RenderLog.Line(path != null ? "screenshot saved: " + path : "screenshot could not be saved: " + name);
            return path;
        }

        public static bool TryHandleKey(Key key, IRenderController renderer, string projectPath, out string savedPath)
        {
            savedPath = null;
            if (key != Key.F12 || renderer == null || string.IsNullOrWhiteSpace(projectPath))
                return false;

            savedPath = CaptureFrame(renderer, projectPath);
            return savedPath != null;
        }
    }
}
