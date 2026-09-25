using System;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Input;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>Single source of truth for PGSL native commands (Command | C# | Description via <see cref="PgslCommandAttribute"/>).</summary>
// Partial: each command family lives in its own PgslCommands.<Family>.cs so the surface can grow
// (99 → the full catalogue, see Documentation/README.md Appendix A) without this file becoming
// unreviewable, and so families can be worked on independently.
public static partial class PgslCommands
{
    public static IGameContext ActiveGameContext;
    public static string ProjectPath;

    private static readonly AsyncLocal<PgslContext> _ctx = new();
    internal static PgslContext Ctx => _ctx.Value ?? throw new InvalidOperationException("No active PGSL context.");

    internal static void SetContext(PgslContext ctx) => _ctx.Value = ctx;
    public static PgslContext GetContext() => _ctx.Value;

    /// <summary>
    /// Binds a context (and its <see cref="PgslContext.DrawSurface"/>) for direct command
    /// invocation from outside the script host — used by hosts that drive PGSL drawing themselves
    /// and by headless verification of the draw pipeline. Returns the previously bound context so
    /// callers can restore it.
    /// </summary>
    public static PgslContext BindContext(PgslContext ctx)
    {
        PgslContext previous = _ctx.Value;
        _ctx.Value = ctx;
        return previous;
    }

    private static IPgslDrawSurface Draw => Ctx.DrawSurface as IPgslDrawSurface;

    internal static string PreProcessScript(string pgsl, bool isForVm = false, string projectPath = null)
    {
        if (string.IsNullOrEmpty(pgsl)) return pgsl;
        // Shorthand normalization must only see code. URLs, comment markers and text such as
        // "if (a=b)" inside an authored or Inspector-supplied string remain literal data.
        PgslSourcePreprocessor protectedSource = new(pgsl);
        pgsl = protectedSource.Text;
        pgsl = PgslNamespaceSyntax.NormalizeIndentedBlocks(pgsl);

        if (isForVm && !string.IsNullOrEmpty(projectPath))
        {
            if (!string.Equals(ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase))
                ScriptAssetRegistry.LoadFromProject(projectPath);
            pgsl = ScriptAssetRegistry.ApplyScriptCallSyntax(pgsl);
        }

        pgsl = Regex.Replace(pgsl, @"^(\s*)if\s+(.+?):\s*$", "$1if ($2)", RegexOptions.Multiline);
        pgsl = Regex.Replace(pgsl, @"^(\s*)else\s+if\s+(.+?):\s*$", "$1else if ($2)", RegexOptions.Multiline);
        pgsl = Regex.Replace(pgsl, @"^(\s*)else:\s*$", "$1else", RegexOptions.Multiline);
        pgsl = Regex.Replace(pgsl, @"^(\s*)while\s+(.+?):\s*$", "$1while ($2)", RegexOptions.Multiline);
        pgsl = Regex.Replace(pgsl, @"^(\s*)if\s+([^(\s][^{]*?)\s*({|$)", "$1if ($2) $3", RegexOptions.Multiline);
        pgsl = Regex.Replace(pgsl, @"^(\s*)while\s+([^(\s][^{]*?)\s*({|$)", "$1while ($2) $3", RegexOptions.Multiline);
        pgsl = Regex.Replace(pgsl, @"\b(if|while)\s*\(([^)]+)\)", m =>
        {
            string cond = Regex.Replace(m.Groups[2].Value, @"(?<![!<>=])=(?![=])", " == ");
            return $"{m.Groups[1].Value} ({cond})";
        });
        return protectedSource.RestoreLiterals(pgsl);
    }

    #region Engine variables

    [PgslCommand("Fps", "Fps", "Current frames per second", "Engine Variables")]
    public static double Fps => (ActiveGameContext?.DeltaTime ?? 0.016f) > 0 ? 1.0 / (ActiveGameContext?.DeltaTime ?? 0.016f) : 60;

    [PgslCommand("RoomSpeed", "RoomSpeed", "Target frames per second", "Engine Variables")]
    public static int RoomSpeed { get => ActiveGameContext?.TargetFps ?? 60; set { if (ActiveGameContext != null) ActiveGameContext.SetTargetFps(value); } }

    [PgslCommand("RoomWidth", "RoomWidth", "Current room width in pixels", "Engine Variables")]
    public static double RoomWidth => Ctx?.RoomWidth ?? 640;

    [PgslCommand("RoomHeight", "RoomHeight", "Current room height in pixels", "Engine Variables")]
    public static double RoomHeight => Ctx?.RoomHeight ?? 480;

    [PgslCommand("DeltaTime", "DeltaTime", "Time since last frame", "Engine Variables")]
    public static double DeltaTime => ActiveGameContext?.DeltaTime ?? 0.016f;

    [PgslCommand("MouseX", "MouseX", "Mouse X", "Engine Variables")]
    public static double MouseX => ActiveGameContext?.Input?.MousePosition.X ?? 0;

    [PgslCommand("MouseY", "MouseY", "Mouse Y", "Engine Variables")]
    public static double MouseY => ActiveGameContext?.Input?.MousePosition.Y ?? 0;

    [PgslCommand("MouseRawX", "MouseRawX", "Raw mouse X", "Engine Variables")]
    public static double MouseRawX => ActiveGameContext?.Input?.MousePosition.X ?? 0;

    [PgslCommand("MouseRawY", "MouseRawY", "Raw mouse Y", "Engine Variables")]
    public static double MouseRawY => ActiveGameContext?.Input?.MousePosition.Y ?? 0;

    #endregion

    #region Instance variables

    [PgslCommand("X", "X", "Position X", "Instance Variables")]
    public static double X { get => Ctx.X; set => Ctx.X = value; }

    [PgslCommand("Y", "Y", "Position Y", "Instance Variables")]
    public static double Y { get => Ctx.Y; set => Ctx.Y = value; }

    [PgslCommand("Z", "Z", "Position Z", "Instance Variables")]
    public static double Z { get => Ctx.Z; set => Ctx.Z = value; }

    [PgslCommand("HSpeed", "HSpeed", "Horizontal speed", "Instance Variables")]
    public static double HSpeed { get => Ctx.HSpeed; set => Ctx.HSpeed = value; }

    [PgslCommand("VSpeed", "VSpeed", "Vertical speed", "Instance Variables")]
    public static double VSpeed { get => Ctx.VSpeed; set => Ctx.VSpeed = value; }

    [PgslCommand("Speed", "Speed", "Movement speed", "Instance Variables")]
    public static double Speed { get => Ctx.Speed; set => Ctx.Speed = value; }

    [PgslCommand("Direction", "Direction", "Direction in degrees", "Instance Variables")]
    public static double Direction { get => Ctx.Direction; set => Ctx.Direction = value; }

    [PgslCommand("SpriteIndex", "SpriteIndex", "Sprite name", "Instance Variables")]
    public static string SpriteIndex { get => Ctx.SpriteIndex; set => Ctx.SpriteIndex = value; }

    [PgslCommand("ImageIndex", "ImageIndex", "Animation frame", "Instance Variables")]
    public static double ImageIndex { get => Ctx.ImageIndex; set => Ctx.ImageIndex = value; }

    [PgslCommand("ImageAlpha", "ImageAlpha", "Alpha 0-1", "Instance Variables")]
    public static double ImageAlpha { get => Ctx.ImageAlpha; set => Ctx.ImageAlpha = value; }

    [PgslCommand("ImageAngle", "ImageAngle", "Rotation degrees", "Instance Variables")]
    public static double ImageAngle { get => Ctx.ImageAngle; set => Ctx.ImageAngle = value; }

    [PgslCommand("ImageXScale", "ImageXScale", "Horizontal scale", "Instance Variables")]
    public static double ImageXScale { get => Ctx.ImageXScale; set => Ctx.ImageXScale = value; }

    [PgslCommand("ImageYScale", "ImageYScale", "Vertical scale", "Instance Variables")]
    public static double ImageYScale { get => Ctx.ImageYScale; set => Ctx.ImageYScale = value; }

    [PgslCommand("Visible", "Visible", "Visible flag", "Instance Variables")]
    public static bool Visible { get => Ctx.Visible; set => Ctx.Visible = value; }

    [PgslCommand("Depth", "Depth", "Draw depth", "Instance Variables")]
    public static int Depth { get => Ctx.Depth; set => Ctx.Depth = value; }

    [PgslCommand("Solid", "Solid", "Solid flag", "Instance Variables")]
    public static bool Solid { get => Ctx.Solid; set => Ctx.Solid = value; }

    #endregion

    #region Math

    [PgslCommand("Random", "Random(n)", "Random 0..n", "Math")]
    public static double Random(double n) => System.Random.Shared.NextDouble() * n;

    [PgslCommand("RandomRange", "RandomRange(min, max)", "Random in range", "Math")]
    public static double RandomRange(double min, double max) =>
        System.Random.Shared.NextDouble() * (max - min) + min;

    [PgslCommand("Choose", "Choose(a, b, ...)", "Pick random argument", "Math")]
    public static object Choose(params object[] options)
    {
        if (options == null || options.Length == 0) return null;
        return options[System.Random.Shared.Next(options.Length)];
    }

    // ── Core maths ───────────────────────────────────────────────────────────────
    // Without these, movement, aiming, and distance-based collision are unwritable in
    // PGSL — they were catalogued but unimplemented (Track B). Angles are DEGREES to
    // match the instance Direction/ImageAngle convention.

    [PgslCommand("Abs", "Abs(x)", "Absolute value", "Math")]
    public static double Abs(double x) => Math.Abs(x);

    [PgslCommand("Sign", "Sign(x)", "-1, 0 or 1", "Math")]
    public static double Sign(double x) => Math.Sign(x);

    [PgslCommand("Sqrt", "Sqrt(x)", "Square root (0 for negatives)", "Math")]
    public static double Sqrt(double x) => x <= 0 ? 0 : Math.Sqrt(x);

    [PgslCommand("Power", "Power(x, n)", "x raised to n", "Math")]
    public static double Power(double x, double n) => Math.Pow(x, n);

    [PgslCommand("Floor", "Floor(x)", "Round down", "Math")]
    public static double Floor(double x) => Math.Floor(x);

    [PgslCommand("Ceil", "Ceil(x)", "Round up", "Math")]
    public static double Ceil(double x) => Math.Ceiling(x);

    [PgslCommand("Round", "Round(x)", "Round to nearest", "Math")]
    public static double Round(double x) => Math.Round(x, MidpointRounding.AwayFromZero);

    [PgslCommand("Min", "Min(a, b)", "Smaller of two values", "Math")]
    public static double Min(double a, double b) => Math.Min(a, b);

    [PgslCommand("Max", "Max(a, b)", "Larger of two values", "Math")]
    public static double Max(double a, double b) => Math.Max(a, b);

    [PgslCommand("Clamp", "Clamp(x, min, max)", "Constrain to a range", "Math")]
    public static double Clamp(double x, double min, double max) =>
        min <= max ? Math.Clamp(x, min, max) : Math.Clamp(x, max, min);

    [PgslCommand("Lerp", "Lerp(a, b, t)", "Linear blend from a to b", "Math")]
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    [PgslCommand("Approach", "Approach(value, target, step)", "Move value toward target without overshooting", "Math")]
    public static double Approach(double value, double target, double step)
    {
        step = Math.Abs(step);
        if (value < target) return Math.Min(value + step, target);
        if (value > target) return Math.Max(value - step, target);
        return target;
    }

    [PgslCommand("DegToRad", "DegToRad(deg)", "Degrees to radians", "Math")]
    public static double DegToRad(double deg) => deg * Math.PI / 180.0;

    [PgslCommand("RadToDeg", "RadToDeg(rad)", "Radians to degrees", "Math")]
    public static double RadToDeg(double rad) => rad * 180.0 / Math.PI;

    [PgslCommand("Sin", "Sin(deg)", "Sine of an angle in degrees", "Math")]
    public static double Sin(double deg) => Math.Sin(deg * Math.PI / 180.0);

    [PgslCommand("Cos", "Cos(deg)", "Cosine of an angle in degrees", "Math")]
    public static double Cos(double deg) => Math.Cos(deg * Math.PI / 180.0);

    [PgslCommand("Tan", "Tan(deg)", "Tangent of an angle in degrees", "Math")]
    public static double Tan(double deg) => Math.Tan(deg * Math.PI / 180.0);

    [PgslCommand("PointDistance", "PointDistance(x1,y1,x2,y2)", "Distance between two points", "Math")]
    public static double PointDistance(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1, dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    [PgslCommand("PointDirection", "PointDirection(x1,y1,x2,y2)", "Angle in degrees from point 1 to point 2", "Math")]
    public static double PointDirection(double x1, double y1, double x2, double y2)
    {
        // Screen-space Y grows downward, so negate dy to keep 0°=right, 90°=up.
        double deg = Math.Atan2(-(y2 - y1), x2 - x1) * 180.0 / Math.PI;
        return deg < 0 ? deg + 360.0 : deg;
    }

    [PgslCommand("LengthDirX", "LengthDirX(len, dir)", "X component of a length at an angle", "Math")]
    public static double LengthDirX(double len, double dir) => len * Math.Cos(dir * Math.PI / 180.0);

    [PgslCommand("LengthDirY", "LengthDirY(len, dir)", "Y component of a length at an angle", "Math")]
    public static double LengthDirY(double len, double dir) => -len * Math.Sin(dir * Math.PI / 180.0);

    #endregion

    #region Audio

    // The runtime audio service (buses, spatial panning, looping) already existed behind
    // IGameContext.Audio but had no PGSL surface, so a scripted game could not make a sound.
    // Channel handles are plain ints, which map straight onto PGSL numbers.

    [PgslCommand("PlaySound", "PlaySound(path, volume, pitch, loop)", "Play a sound; returns a channel handle", "Audio")]
    public static double PlaySound(string path, double volume = 1.0, double pitch = 1.0, bool loop = false)
    {
        var audio = ActiveGameContext?.Audio;
        if (audio == null || string.IsNullOrWhiteSpace(path)) return 0;

        int soundId = audio.LoadSound(path);
        if (soundId == 0) return 0;

        return audio.Play(
            soundId,
            (float)Math.Clamp(volume, 0, 1),
            (float)Math.Clamp(pitch, 0.01, 4.0),
            loop).Id;
    }

    [PgslCommand("StopSound", "StopSound(channel)", "Stop one playing channel", "Audio")]
    public static void StopSound(double channel) =>
        ActiveGameContext?.Audio?.Stop(new Genesis.Shared.Audio.AudioChannel((int)channel));

    [PgslCommand("StopAllSounds", "StopAllSounds()", "Stop every playing sound", "Audio")]
    public static void StopAllSounds() => ActiveGameContext?.Audio?.StopAll();

    [PgslCommand("IsSoundPlaying", "IsSoundPlaying(channel)", "True while a channel is still audible", "Audio")]
    public static bool IsSoundPlaying(double channel) =>
        ActiveGameContext?.Audio?.IsPlaying(new Genesis.Shared.Audio.AudioChannel((int)channel)) ?? false;

    [PgslCommand("SetMasterVolume", "SetMasterVolume(volume)", "Set the overall output volume (0..1)", "Audio")]
    public static void SetMasterVolume(double volume)
    {
        var audio = ActiveGameContext?.Audio;
        if (audio != null) audio.MasterVolume = (float)Math.Clamp(volume, 0, 1);
    }

    [PgslCommand("GetMasterVolume", "GetMasterVolume()", "Current overall output volume", "Audio")]
    public static double GetMasterVolume() => ActiveGameContext?.Audio?.MasterVolume ?? 0;

    [PgslCommand("SetAudioListener", "SetAudioListener(x, y, z)", "Move the listener for 3D positional audio", "Audio")]
    public static void SetAudioListener(double x, double y, double z) =>
        ActiveGameContext?.Audio?.SetListener(
            new System.Numerics.Vector3((float)x, (float)y, (float)z),
            new System.Numerics.Vector3(0f, 0f, -1f));

    #endregion

    #region Drawing

    [PgslCommand("DrawSetColor", "DrawSetColor(color)", "Set draw color", "Drawing 2D")]
    public static void DrawSetColor(Color c) => Ctx.DrawColor = c;

    [PgslCommand("DrawSetAlpha", "DrawSetAlpha(a)", "Set draw alpha", "Drawing 2D")]
    public static void DrawSetAlpha(double a) => Ctx.DrawAlpha = Math.Clamp(a, 0, 1);

    [PgslCommand("DrawClear", "DrawClear(color)", "Clear viewport", "Drawing 2D")]
    public static void DrawClear(Color c) => Draw?.Clear(c);

    [PgslCommand("DrawLine", "DrawLine(x1,y1,x2,y2)", "Draw line", "Drawing 2D")]
    public static void DrawLine(double x1, double y1, double x2, double y2)
    {
        var c = Color.FromArgb((int)(Ctx.DrawAlpha * 255), Ctx.DrawColor);
        Draw?.DrawLine((float)x1, (float)y1, (float)x2, (float)y2, c, 1f);
    }

    [PgslCommand("DrawRectangle", "DrawRectangle(x1,y1,x2,y2)", "Filled rectangle", "Drawing 2D")]
    public static void DrawRectangle(double x1, double y1, double x2, double y2)
    {
        var c = Color.FromArgb((int)(Ctx.DrawAlpha * 255), Ctx.DrawColor);
        Draw?.FillRectangle(c, new RectangleF((float)x1, (float)y1, (float)(x2 - x1), (float)(y2 - y1)));
    }

    [PgslCommand("DrawCircle", "DrawCircle(x,y,radius,outline)", "Circle — filled, or outline when outline is true", "Drawing 2D")]
    public static void DrawCircle(double x, double y, double radius, bool outline = false)
    {
        var c = Color.FromArgb((int)(Ctx.DrawAlpha * 255), Ctx.DrawColor);
        if (outline)
        {
            Draw?.DrawCircle(c, (float)x, (float)y, (float)radius, 1f);
        }
        else
        {
            Draw?.FillCircle(c, (float)x, (float)y, (float)radius);
        }
    }

    [PgslCommand("DrawText", "DrawText(x,y,text)", "Draw text", "Drawing 2D")]
    public static void DrawText(double x, double y, string text) =>
        Draw?.DrawText(text ?? "", Ctx.DrawFont, (float)Ctx.DrawFontSize, Ctx.DrawColor,
            new Rectangle((int)x, (int)y, 2000, 200));

    [PgslCommand("DrawSprite", "DrawSprite(spr,frame,x,y)", "Draw sprite", "Drawing 2D")]
    public static void DrawSprite(string spr, double frame, double x, double y) =>
        DrawSprite(spr, frame, x, y, Ctx.ImageXScale, Ctx.ImageYScale, Ctx.ImageAngle, Ctx.ImageBlend, Ctx.ImageAlpha);

  public static void DrawSprite(string spr, double frame, double x, double y,
      double xscale, double yscale, double angle, Color blend, double alpha)
  {
      Draw?.DrawSprite(spr, (float)x, (float)y, (int)frame,
          (float)xscale, (float)yscale, (float)angle, blend, (float)alpha);
  }

    [PgslCommand("DrawSelf", "DrawSelf()", "Draw instance using its own sprite properties", "Drawing 2D")]
    public static void DrawSelf()
    {
        var ctx = Ctx;
        if (ctx == null || !ctx.Visible || string.IsNullOrEmpty(ctx.SpriteIndex))
            return;
        DrawSprite(ctx.SpriteIndex, ctx.ImageIndex, ctx.X, ctx.Y,
            ctx.ImageXScale, ctx.ImageYScale, ctx.ImageAngle, ctx.ImageBlend, ctx.ImageAlpha);
    }

    #endregion

    #region Instances

    private static int _createDepth;

    [PgslCommand("CreateInstance", "CreateInstance(obj,x,y,z)", "Create object instance", "Instances")]
    public static int CreateInstance(string obj, double x, double y, double z = 0)
    {
        if (_createDepth > 8) return -1;
        _createDepth++;
        try
        {
            if (ActiveGameContext != null && !string.IsNullOrEmpty(obj))
            {
                string path = Scene.RoomSceneBuilder.ResolvePrefabPath(ProjectPath, obj);
                if (System.IO.File.Exists(path))
                {
                    var definition = Scene.ObjectDefinitionResolver.Load(ProjectPath, path);
                    var host = (ActiveGameContext as Project.ProjectGameContext)?.ScriptHost;
                    bool wasDeferring = host?.DeferCreateEvents ?? false;
                    if (host != null) host.DeferCreateEvents = true;
                    try
                    {
                        using var events = host?.UseEventSources(definition.Events);
                        var entity = Scene.PrefabSpawner.Spawn(ActiveGameContext.World, definition.Prefab, host, (float)x, (float)y);
                        ref var transform = ref ActiveGameContext.World.GetRef<TransformComponent>(entity);
                        transform.X = (float)x; transform.Y = (float)y; transform.Z = (float)z;
                        if (string.Equals((string)definition.Prefab["dimension"], "ThreeD", StringComparison.OrdinalIgnoreCase))
                        {
                            ActiveGameContext.World.Set(entity, new Genesis.Shared.ECS.Components.Transform3DComponent
                            { Position = new((float)x, (float)y, (float)z), Rotation = System.Numerics.Quaternion.Identity,
                                Scale = new(transform.ScaleX, transform.ScaleY, transform.ScaleZ) });
                            new Scene.RoomSceneBuilder(ProjectPath).AttachAuthoredPhysics(ActiveGameContext.World, entity, definition.Prefab, transform);
                        }
                        if (definition.Events.Count > 0 && host != null && host.FindBehaviorForEntity(entity) == null)
                            host.Attach(ActiveGameContext.World, entity, ResourceNames.Name(ProjectPath, path, ResourceType.Object));
                        var assets = Rendering.ObjectDrawAssetRegistry.TryGet(entity, out var entry) ? entry : new Rendering.ObjectDrawAssetEntry();
                        assets.Prefab = obj; Rendering.ObjectDrawAssetRegistry.Set(entity, assets);
                        if (!wasDeferring) host?.FlushDeferredCreates();
                        return entity.Id;
                    }
                    finally { if (host != null) host.DeferCreateEvents = wasDeferring; }
                }
            }
            return -1;
        }
        finally { _createDepth--; }
    }

    [PgslCommand("InstanceDestroy", "InstanceDestroy(id)", "Destroy instance", "Instances")]
    public static void InstanceDestroy(int id)
    {
        if (ActiveGameContext != null && ActiveGameContext.World != null)
        {
            var entity = ActiveGameContext.World.GetEntity(id);
            if (!entity.IsNull)
            {
                (ActiveGameContext as Project.ProjectGameContext)?.ScriptHost?.Detach(entity);
                ActiveGameContext.World.DestroyEntity(entity);
            }
        }
    }

    [PgslCommand("InstanceExists", "InstanceExists(id)", "Instance exists", "Instances")]
    public static bool InstanceExists(int id)
    {
        if (ActiveGameContext != null && ActiveGameContext.World != null)
        {
            var entity = ActiveGameContext.World.GetEntity(id);
            return !entity.IsNull && ActiveGameContext.World.IsAlive(entity);
        }
        return false;
    }

    #endregion

    #region Input Checks

    /// <summary>
    /// Truthiness for a transpiled condition.
    /// </summary>
    /// <remarks>
    /// PGSL is dynamically typed and most commands are declared as returning <see cref="object"/>,
    /// so <c>if (KeyCheck("A"))</c> transpiles to C# that will not compile — CS0266, object to bool.
    /// The tree-walking VM coerces at run time and never hit this, which is why every PGSL
    /// conditional over a command result worked in the editor sandbox and failed only on the F5
    /// path that actually AOT-compiles. Follows the usual dynamic-language rules: null and false are
    /// false, zero is false, an empty string is false, everything else is true.
    /// </remarks>
    public static bool Truthy(object value) => value switch
    {
        null => false,
        bool b => b,
        double d => d != 0d,
        float f => f != 0f,
        int i => i != 0,
        long l => l != 0L,
        string s => s.Length > 0,
        _ => true,
    };

    /// <summary>Overload so an already-boolean condition costs no boxing.</summary>
    public static bool Truthy(bool value) => value;

    /// <summary>PGSL's numeric coercion, shared by the VM and generated C#.</summary>
    public static double Num(object value) => value switch
    {
        null => 0d,
        double d => d,
        float f => f,
        decimal m => (double)m,
        long l => l,
        ulong ul => ul,
        int i => i,
        uint ui => ui,
        short s => s,
        ushort us => us,
        byte b => b,
        sbyte sb => sb,
        bool boolean => boolean ? 1d : 0d,
        string text => double.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double parsed) ? parsed : 0d,
        _ => 0d,
    };

    // Typed overloads avoid boxing for the values generated PGSL uses most often.
    public static double Num(double value) => value;
    public static double Num(float value) => value;
    public static double Num(int value) => value;
    public static double Num(long value) => value;
    public static double Num(bool value) => value ? 1d : 0d;

    /// <summary>PGSL addition: concatenate when either operand is text, otherwise add numbers.</summary>
    public static object Add(object left, object right) =>
        left is string || right is string
            ? Convert.ToString(left, CultureInfo.InvariantCulture)
              + Convert.ToString(right, CultureInfo.InvariantCulture)
            : Num(left) + Num(right);

    /// <summary>PGSL equality, including numeric and boolean coercion used by the VM.</summary>
    public static bool Equal(object left, object right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (IsNumber(left) && IsNumber(right)) return Math.Abs(Num(left) - Num(right)) < 1e-9;
        if (left is bool leftBoolean && IsNumber(right)) return leftBoolean == (Num(right) != 0d);
        if (right is bool rightBoolean && IsNumber(left)) return rightBoolean == (Num(left) != 0d);
        return Equals(left, right);
    }

    private static bool IsNumber(object value) => value is
        double or float or decimal or long or ulong or int or uint or short or ushort or byte or sbyte;

    private static Key ParseKey(string keyStr)
    {
        if (string.IsNullOrEmpty(keyStr)) return Key.Unknown;
        if (Enum.TryParse<Key>(keyStr, true, out var k)) return k;
        if (keyStr.Length == 1)
        {
            char c = char.ToUpperInvariant(keyStr[0]);
            if (c >= 'A' && c <= 'Z')
                return (Key)((int)Key.A + (c - 'A'));
            if (c >= '0' && c <= '9')
                return (Key)((int)Key.D0 + (c - '0'));
        }
        return Key.Unknown;
    }

    [PgslCommand("KeyCheck", "KeyCheck(key) → bool", "Check if key is held", "Input")]
    public static bool KeyCheck(string key)
    {
        if (ActiveGameContext?.Input == null) return false;
        return ActiveGameContext.Input.IsDown(ParseKey(key));
    }

    [PgslCommand("KeyPressed", "KeyPressed(key) → bool", "Check if key was pressed this frame", "Input")]
    public static bool KeyPressed(string key)
    {
        if (ActiveGameContext?.Input == null) return false;
        return ActiveGameContext.Input.WasPressed(ParseKey(key));
    }

    [PgslCommand("KeyReleased", "KeyReleased(key) → bool", "Check if key was released this frame", "Input")]
    public static bool KeyReleased(string key)
    {
        if (ActiveGameContext?.Input == null) return false;
        return ActiveGameContext.Input.WasReleased(ParseKey(key));
    }

    [PgslCommand("MbLeft", "MbLeft", "Mouse left button constant", "Input")]
    public static int MbLeft => 0;

    [PgslCommand("MbMiddle", "MbMiddle", "Mouse middle button constant", "Input")]
    public static int MbMiddle => (int)MouseButton.Middle;

    [PgslCommand("MbRight", "MbRight", "Mouse right button constant", "Input")]
    public static int MbRight => (int)MouseButton.Right;

    [PgslCommand("MouseCheck", "MouseCheck(button) → bool", "Check if mouse button is held", "Input")]
    public static bool MouseCheck(int button)
    {
        if (ActiveGameContext?.Input == null) return false;
        return ActiveGameContext.Input.IsDown((MouseButton)button);
    }

    [PgslCommand("MousePressed", "MousePressed(button) → bool", "Check if mouse button was pressed this frame", "Input")]
    public static bool MousePressed(int button)
    {
        if (ActiveGameContext?.Input == null) return false;
        return ActiveGameContext.Input.WasPressed((MouseButton)button);
    }

    [PgslCommand("MouseReleased", "MouseReleased(button) → bool", "Check if mouse button was released this frame", "Input")]
    public static bool MouseReleased(int button)
    {
        if (ActiveGameContext?.Input == null) return false;
        return ActiveGameContext.Input.WasReleased((MouseButton)button);
    }

    #endregion

    #region Collisions

    [PgslCommand("PlaceMeeting", "PlaceMeeting(x,y,obj)", "Check collision at coordinate x, y", "Physics")]
    public static bool PlaceMeeting(double x, double y, string objName)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
        return FindRectangleCollision(CallerBounds(x, y), objName, out _);
    }

    [PgslCommand("PlaceFree", "PlaceFree(x,y)", "Check if coordinate x, y is free from solid collisions", "Physics")]
    public static bool PlaceFree(double x, double y) => !PlaceMeeting(x, y, "obj_solid");

    [PgslCommand("PositionEmpty", "PositionEmpty(x,y)", "Check if coordinate x, y has no objects", "Physics")]
    public static bool PositionEmpty(double x, double y) => !PlaceMeeting(x, y, "all");

    [PgslCommand("CollisionRectangle", "CollisionRectangle(x1,y1,x2,y2,obj)", "Check rectangle area for object collision", "Physics")]
    public static int CollisionRectangle(double x1, double y1, double x2, double y2, string objName)
    {
        if (!double.IsFinite(x1) || !double.IsFinite(y1) || !double.IsFinite(x2) || !double.IsFinite(y2)) return 0;
        RectangleF rect = RectangleF.FromLTRB((float)Math.Min(x1, x2), (float)Math.Min(y1, y2),
            (float)Math.Max(x1, x2), (float)Math.Max(y1, y2));
        return FindRectangleCollision(rect, objName, out int id) ? id : 0;
    }

    // Query live components, not the optional debug grid (which can be empty or stale).
    // Exact prefab matching and authored masks work before a renderer has loaded any texture.
    private static bool FindRectangleCollision(RectangleF rect, string objectName, out int id)
    {
        id = 0;
        var world = ActiveGameContext?.World;
        if (world == null) return false;
        int self = GetContext()?.InstanceId ?? -1;
        string requested = string.Equals(objectName, "all", StringComparison.OrdinalIgnoreCase) ? null : objectName;
        foreach (var entry in world.Query<TransformComponent>())
        {
            var entity = entry.Entity;
            if (entity.Id == self || !MatchesObject(entity.Id, requested)) continue;
            // Tile draw placeholders and unrelated 3D transforms are not object colliders.
            if (!Genesis.Runtime.Rendering.ObjectDrawAssetRegistry.TryGet(entity, out var assets)
                || string.IsNullOrEmpty(assets.Prefab) || assets.Is3D == true) continue;
            if (!rect.IntersectsWith(Genesis.Runtime.Spatial.SpriteCollisionBounds.ForEntity(world, entity, ProjectPath))) continue;
            id = entity.Id;
            Ctx.Variables["Other"] = entity;
            return true;
        }
        return false;
    }

    #endregion

    #region General

    [PgslCommand("Print", "Print(text)", "Print to output", "General")]
    public static void Print(string text)
    {
        string msg = text ?? "";
        Ctx.OutputCallback?.Invoke(msg);
        try { VMLogger.Log(msg); } catch { }
    }

    [PgslCommand("String", "String(val)", "To string", "General")]
    public static string PGSLToString(object val) => val?.ToString() ?? "";

    [PgslCommand("Real", "Real(val)", "To number", "General")]
    public static double Real(object val) { try { return Convert.ToDouble(val); } catch { return 0; } }

    #endregion

    #region Camera and Input Extensions

    [PgslCommand("SetCameraPosition", "SetCameraPosition(x,y,z)", "Set the play camera position", "Camera")]
    public static void SetCameraPosition(double x, double y, double z)
    {
        if (ActiveGameContext?.Camera != null)
        {
            ActiveGameContext.Camera.Position = new System.Numerics.Vector3((float)x, (float)y, (float)z);
        }
    }

    [PgslCommand("GetCameraX", "GetCameraX() -> float", "Get play camera X position", "Camera")]
    public static double GetCameraX() => ActiveGameContext?.Camera?.Position.X ?? 0;

    [PgslCommand("GetCameraY", "GetCameraY() -> float", "Get play camera Y position", "Camera")]
    public static double GetCameraY() => ActiveGameContext?.Camera?.Position.Y ?? 0;

    [PgslCommand("GetCameraZ", "GetCameraZ() -> float", "Get play camera Z position", "Camera")]
    public static double GetCameraZ() => ActiveGameContext?.Camera?.Position.Z ?? 0;

    [PgslCommand("SetCameraTarget", "SetCameraTarget(x,y,z)", "Set play camera look-at target", "Camera")]
    public static void SetCameraTarget(double x, double y, double z)
    {
        if (ActiveGameContext?.Camera != null)
        {
            var target = new System.Numerics.Vector3((float)x, (float)y, (float)z);
            var delta = target - ActiveGameContext.Camera.Position;
            if (delta.LengthSquared() < 0.000001f) return;
            var dir = System.Numerics.Vector3.Normalize(delta);
            // Camera3D yaw 0 faces -Z and positive pitch raises Forward.Y.
            float yaw = (float)Math.Atan2(dir.X, -dir.Z);
            float pitch = (float)Math.Asin(dir.Y);
            // Camera3D stores radians (the room loader converts authored degrees on entry).
            ActiveGameContext.Camera.Yaw = yaw;
            ActiveGameContext.Camera.Pitch = pitch;
        }
    }

    [PgslCommand("SetMouseCaptured", "SetMouseCaptured(captured)", "Capture or release the mouse cursor", "Input")]
    public static void SetMouseCaptured(bool captured)
    {
        ActiveGameContext?.SetMouseCaptured(captured);
    }

    [PgslCommand("GetCameraForwardX", "GetCameraForwardX() -> float", "Get camera forward look vector X", "Camera")]
    public static double GetCameraForwardX() => ActiveGameContext?.Camera?.Forward.X ?? 0;

    [PgslCommand("GetCameraForwardY", "GetCameraForwardY() -> float", "Get camera forward look vector Y", "Camera")]
    public static double GetCameraForwardY() => ActiveGameContext?.Camera?.Forward.Y ?? 0;

    [PgslCommand("GetCameraForwardZ", "GetCameraForwardZ() -> float", "Get camera forward look vector Z", "Camera")]
    public static double GetCameraForwardZ() => ActiveGameContext?.Camera?.Forward.Z ?? 0;

    [PgslCommand("GetCameraRightX", "GetCameraRightX() -> float", "Get camera right strafe vector X", "Camera")]
    public static double GetCameraRightX() => ActiveGameContext?.Camera?.Right.X ?? 0;

    [PgslCommand("GetCameraRightY", "GetCameraRightY() -> float", "Get camera right strafe vector Y", "Camera")]
    public static double GetCameraRightY() => ActiveGameContext?.Camera?.Right.Y ?? 0;

    [PgslCommand("GetCameraRightZ", "GetCameraRightZ() -> float", "Get camera right strafe vector Z", "Camera")]
    public static double GetCameraRightZ() => ActiveGameContext?.Camera?.Right.Z ?? 0;

    [PgslCommand("GetCameraPitch", "GetCameraPitch() -> float", "Get play camera Pitch", "Camera")]
    public static double GetCameraPitch() => ActiveGameContext?.Camera?.Pitch ?? 0;

    [PgslCommand("GetCameraYaw", "GetCameraYaw() -> float", "Get play camera Yaw", "Camera")]
    public static double GetCameraYaw() => ActiveGameContext?.Camera?.Yaw ?? 0;

    [PgslCommand("SetCameraPitch", "SetCameraPitch(pitch)", "Set play camera Pitch", "Camera")]
    public static void SetCameraPitch(double pitch)
    {
        if (ActiveGameContext?.Camera != null) ActiveGameContext.Camera.Pitch = (float)pitch;
    }

    [PgslCommand("SetCameraYaw", "SetCameraYaw(yaw)", "Set play camera Yaw", "Camera")]
    public static void SetCameraYaw(double yaw)
    {
        if (ActiveGameContext?.Camera != null) ActiveGameContext.Camera.Yaw = (float)yaw;
    }

    [PgslCommand("GetMouseLookDeltaX", "GetMouseLookDeltaX() -> float", "Get mouse look horizontal delta", "Input")]
    public static double GetMouseLookDeltaX() => ActiveGameContext?.Input?.LookDelta.X ?? 0;

    [PgslCommand("GetMouseLookDeltaY", "GetMouseLookDeltaY() -> float", "Get mouse look vertical delta", "Input")]
    public static double GetMouseLookDeltaY() => ActiveGameContext?.Input?.LookDelta.Y ?? 0;

    #endregion

    /// <summary>Builds the PGSL command catalog at startup (editor autocomplete + VM bridge).</summary>
    public static void WarmRegistry() => PgslCommandRegistry.Build(typeof(PgslCommands));
}
