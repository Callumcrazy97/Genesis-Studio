using System;

namespace Genesis.Rendering.Core;

/// <summary>
/// Preferences that apply to the GPU viewports embedded in the editors.
/// </summary>
/// <remarks>
/// Editor viewports are not the game. They live inside a WinForms window that is already being
/// composited by the desktop, several of them can be on screen at once, and none of them is trying
/// to hit a frame deadline — so their presentation settings are the Studio's business rather than
/// the Room's. This is where those settings live, so a viewport in any editor assembly can read
/// them without any of them referencing the shell.
///
/// It exists because "Use VSync in editor previews" was a preference that saved, reloaded, and was
/// then ignored by every viewport in the application: each one hardcoded VSync off at construction.
/// </remarks>
public static class EditorPreviewSettings
{
    private static bool _vsync = true;

    /// <summary>Raised when a preview setting changes, so open viewports can apply it live.</summary>
    public static event Action Changed;

    /// <summary>Whether editor viewports present with VSync. Default matches the shipped preference.</summary>
    public static bool VSync => _vsync;

    public static void Configure(bool vsync)
    {
        if (_vsync == vsync) return;
        _vsync = vsync;
        Changed?.Invoke();
    }
}
