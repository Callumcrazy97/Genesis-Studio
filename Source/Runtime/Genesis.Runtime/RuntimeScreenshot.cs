using System;
using Genesis.Runtime.Input;
using Genesis.Runtime.Project;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime
{
    /// <summary>F12 screenshot hook shared by standalone player and editor play/sandbox.</summary>
    public static class RuntimeScreenshot
    {
        public static string CaptureFrame(IRenderController renderer, string projectPath, string label = null)
        {
            if (renderer == null || string.IsNullOrWhiteSpace(projectPath))
                return null;

            string name = string.IsNullOrWhiteSpace(label)
                ? "screenshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                : label;
            return ProjectScreenshot.Capture(renderer, projectPath, name);
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
