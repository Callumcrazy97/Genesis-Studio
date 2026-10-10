using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Post effects for shader packs: an effect's own pictures, Full screen shaders read from plain
/// .hlsl files of the game's folder while it runs, and why an effect does not run.
/// </summary>
public static partial class PgslCommands
{
    [PgslCommand("PostEffectAddFile", "PostEffectAddFile(path) -> bool",
        "Run a Full screen shader written as a plain .hlsl file of the game's folder (from the project folder or its Assets folder, \"ShaderPacks/Sepia.hlsl\") "
        + "after the effects already running, as PostEffectAdd runs a Shader resource. Its name is the path given; it is compiled on a worker and again "
        + "whenever the file changes. False when there is no such file (PostEffectLastError says why)", "Lighting")]
    public static bool PostEffectAddFile(string path) => Genesis.Runtime.Rendering.ProjectPostEffects.AddFile(ProjectPath, path);

    [PgslCommand("PostEffectSetTexture", "PostEffectSetTexture(shader, slot, image) -> bool",
        "Give a running post effect a picture to read: image an Image resource's name or a texture made with TextureCreate (\"\" takes it away); "
        + "slot the texture's register, 3 to 15 (Texture2D Noise : register(t3)), or its name in the shader (\"Noise\"). t0 to t2 are the frame, "
        + "its depth and its flags. False when the effect is not running or the slot is not one it may be given", "Lighting")]
    public static bool PostEffectSetTexture(string shader, object slot, object image) =>
        Genesis.Runtime.Rendering.ProjectPostEffects.SetTexture(shader, slot, image);

    [PgslCommand("PostEffectLastError", "PostEffectLastError() -> string",
        "Why the post effect that most recently failed does not run, as \"effect: reason\" (the compiler's message for a shader that does not compile, "
        + "or a file or resource not found); empty once every effect asked for runs or is being compiled", "Lighting")]
    public static string PostEffectLastError() => Genesis.Runtime.Rendering.ProjectPostEffects.LastError;

    [PgslCommand("PostEffectError", "PostEffectError(shader) -> string",
        "Why one post effect does not run; empty when it runs or is still being compiled", "Lighting")]
    public static string PostEffectError(string shader) => Genesis.Runtime.Rendering.ProjectPostEffects.ErrorFor(shader);

    [PgslCommand("PostEffectIsRunning", "PostEffectIsRunning(shader) -> bool",
        "True once a post effect is compiled and drawn over the frame; false while it is being compiled, when it failed, or when it was not added", "Lighting")]
    public static bool PostEffectIsRunning(string shader) => Genesis.Runtime.Rendering.ProjectPostEffects.IsRunning(shader);
}
