using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Sounds decoded ahead of time. The first play of a sound used to decode it in the frame that
/// played it; a game now has it decoded on a worker thread before it is needed.
/// </summary>
public static partial class PgslCommands
{
    [PgslCommand("SoundPreload", "SoundPreload(sound) -> bool",
        "Start decoding a sound on a worker thread now, so that its first PlaySound or PlaySoundAt starts at once. Never waits. "
        + "sound as PlaySound takes it: an Audio resource's name, its .audio.json or its sound file. A room's loading cover waits for the sounds "
        + "preloaded while it is up (in Create events). False when there is no such sound", "Audio")]
    public static bool SoundPreload(string sound)
    {
        var audio = ActiveGameContext?.Audio;
        if (audio == null || string.IsNullOrWhiteSpace(sound)) return false;
        return audio.PreloadSound(sound) != 0;
    }

    [PgslCommand("SoundsLoading", "SoundsLoading() -> number",
        "How many sounds asked for with SoundPreload (or by the project's preload setting) are still being decoded; 0 once all are ready", "Audio")]
    public static double SoundsLoading() => ActiveGameContext?.Audio?.SoundsLoading ?? 0;
}
