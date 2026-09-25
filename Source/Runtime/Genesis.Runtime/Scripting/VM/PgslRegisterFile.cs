using System;
using System.Collections.Generic;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting.VM
{
    /// <summary>
    /// Fusion Register Slot system: maps known instance-variable names to integer slot indices
    /// and provides compiled getter/setter delegates so LOAD_REG / STORE_REG opcodes can access
    /// PgslContext fields with a single array-index dereference instead of a string switch.
    /// </summary>
    internal static class PgslRegisterFile
    {
        // ── Slot indices 0–21 : core instance variables ────────────────────────
        public const int SlotX              = 0;
        public const int SlotY              = 1;
        public const int SlotZ              = 2;
        public const int SlotHSpeed         = 3;
        public const int SlotVSpeed         = 4;
        public const int SlotSpeed          = 5;
        public const int SlotDirection      = 6;
        public const int SlotFriction       = 7;
        public const int SlotGravity        = 8;
        public const int SlotGravityDir     = 9;
        public const int SlotSpriteIndex    = 10;
        public const int SlotImageIndex     = 11;
        public const int SlotImageSpeed     = 12;
        public const int SlotImageAlpha     = 13;
        public const int SlotImageAngle     = 14;
        public const int SlotImageXScale    = 15;
        public const int SlotImageYScale    = 16;
        public const int SlotVisible        = 17;
        public const int SlotDepth          = 18;
        public const int SlotSolid          = 19;
        public const int SlotRoomWidth      = 20;
        public const int SlotRoomHeight     = 21;
        // ── Slot indices 22–25 : draw state ────────────────────────────────────
        public const int SlotDrawAlpha      = 22;
        public const int SlotDrawFontSize   = 23;
        // ── Slot indices 24–35 : alarms ────────────────────────────────────────
        public const int SlotAlarm0         = 24;
        public const int SlotAlarm1         = 25;
        public const int SlotAlarm2         = 26;
        public const int SlotAlarm3         = 27;
        public const int SlotAlarm4         = 28;
        public const int SlotAlarm5         = 29;
        public const int SlotAlarm6         = 30;
        public const int SlotAlarm7         = 31;
        public const int SlotAlarm8         = 32;
        public const int SlotAlarm9         = 33;
        public const int SlotAlarm10        = 34;
        public const int SlotAlarm11        = 35;
        // ── Slot indices 36–47 : user-defined variables ─────────────────────────
        public const int SlotUserDefined0   = 36;
        public const int SlotUserDefined1   = 37;
        public const int SlotUserDefined2   = 38;
        public const int SlotUserDefined3   = 39;
        public const int SlotUserDefined4   = 40;
        public const int SlotUserDefined5   = 41;
        public const int SlotUserDefined6   = 42;
        public const int SlotUserDefined7   = 43;
        public const int SlotUserDefined8   = 44;
        public const int SlotUserDefined9   = 45;
        public const int SlotUserDefined10  = 46;
        public const int SlotUserDefined11  = 47;

        public const int TotalSlots = 48;

        // ── Name → slot index lookup ────────────────────────────────────────────
        public static readonly Dictionary<string, int> Slots =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["x"]                = SlotX,
            ["y"]                = SlotY,
            ["z"]                = SlotZ,
            ["hspeed"]           = SlotHSpeed,
            ["vspeed"]           = SlotVSpeed,
            ["speed"]            = SlotSpeed,
            ["direction"]        = SlotDirection,
            ["friction"]         = SlotFriction,
            ["gravity"]          = SlotGravity,
            ["gravity_direction"]= SlotGravityDir,
            ["sprite_index"]     = SlotSpriteIndex,
            ["image_index"]      = SlotImageIndex,
            ["image_speed"]      = SlotImageSpeed,
            ["image_alpha"]      = SlotImageAlpha,
            ["image_angle"]      = SlotImageAngle,
            ["image_xscale"]     = SlotImageXScale,
            ["image_yscale"]     = SlotImageYScale,
            ["visible"]          = SlotVisible,
            ["depth"]            = SlotDepth,
            ["solid"]            = SlotSolid,
            ["room_width"]       = SlotRoomWidth,
            ["room_height"]      = SlotRoomHeight,
            ["draw_alpha"]       = SlotDrawAlpha,
            ["draw_font_size"]   = SlotDrawFontSize,
            ["alarm0"]           = SlotAlarm0,
            ["alarm1"]           = SlotAlarm1,
            ["alarm2"]           = SlotAlarm2,
            ["alarm3"]           = SlotAlarm3,
            ["alarm4"]           = SlotAlarm4,
            ["alarm5"]           = SlotAlarm5,
            ["alarm6"]           = SlotAlarm6,
            ["alarm7"]           = SlotAlarm7,
            ["alarm8"]           = SlotAlarm8,
            ["alarm9"]           = SlotAlarm9,
            ["alarm10"]          = SlotAlarm10,
            ["alarm11"]          = SlotAlarm11,
            ["user_var0"]        = SlotUserDefined0,
            ["user_var1"]        = SlotUserDefined1,
            ["user_var2"]        = SlotUserDefined2,
            ["user_var3"]        = SlotUserDefined3,
            ["user_var4"]        = SlotUserDefined4,
            ["user_var5"]        = SlotUserDefined5,
            ["user_var6"]        = SlotUserDefined6,
            ["user_var7"]        = SlotUserDefined7,
            ["user_var8"]        = SlotUserDefined8,
            ["user_var9"]        = SlotUserDefined9,
            ["user_var10"]       = SlotUserDefined10,
            ["user_var11"]       = SlotUserDefined11,
        };

        // ── Compiled getter delegates, indexed by slot ──────────────────────────
        public static readonly Func<PgslContext, object>[] SlotGetters;

        // ── Compiled setter delegates, indexed by slot ──────────────────────────
        public static readonly Action<PgslContext, object>[] SlotSetters;

        static PgslRegisterFile()
        {
            SlotGetters = new Func<PgslContext, object>[TotalSlots];
            SlotSetters = new Action<PgslContext, object>[TotalSlots];

            SlotGetters[SlotX]           = ctx => ctx.X;
            SlotGetters[SlotY]           = ctx => ctx.Y;
            SlotGetters[SlotZ]           = ctx => ctx.Z;
            SlotGetters[SlotHSpeed]      = ctx => ctx.HSpeed;
            SlotGetters[SlotVSpeed]      = ctx => ctx.VSpeed;
            SlotGetters[SlotSpeed]       = ctx => ctx.Speed;
            SlotGetters[SlotDirection]   = ctx => ctx.Direction;
            SlotGetters[SlotFriction]    = ctx => ctx.Friction;
            SlotGetters[SlotGravity]     = ctx => ctx.Gravity;
            SlotGetters[SlotGravityDir]  = ctx => ctx.GravityDirection;
            SlotGetters[SlotSpriteIndex] = ctx => (object)(ctx.SpriteIndex ?? "");
            SlotGetters[SlotImageIndex]  = ctx => ctx.ImageIndex;
            SlotGetters[SlotImageSpeed]  = ctx => ctx.ImageSpeed;
            SlotGetters[SlotImageAlpha]  = ctx => ctx.ImageAlpha;
            SlotGetters[SlotImageAngle]  = ctx => ctx.ImageAngle;
            SlotGetters[SlotImageXScale] = ctx => ctx.ImageXScale;
            SlotGetters[SlotImageYScale] = ctx => ctx.ImageYScale;
            SlotGetters[SlotVisible]     = ctx => (object)(ctx.Visible ? 1.0 : 0.0);
            SlotGetters[SlotDepth]       = ctx => (object)(double)ctx.Depth;
            SlotGetters[SlotSolid]       = ctx => (object)(ctx.Solid ? 1.0 : 0.0);
            SlotGetters[SlotRoomWidth]   = ctx => ctx.RoomWidth;
            SlotGetters[SlotRoomHeight]  = ctx => ctx.RoomHeight;
            SlotGetters[SlotDrawAlpha]   = ctx => ctx.DrawAlpha;
            SlotGetters[SlotDrawFontSize]= ctx => ctx.DrawFontSize;
            SlotGetters[SlotAlarm0]      = ctx => ctx.Alarm0;
            SlotGetters[SlotAlarm1]      = ctx => ctx.Alarm1;
            SlotGetters[SlotAlarm2]      = ctx => ctx.Alarm2;
            SlotGetters[SlotAlarm3]      = ctx => ctx.Alarm3;
            SlotGetters[SlotAlarm4]      = ctx => ctx.Alarm4;
            SlotGetters[SlotAlarm5]      = ctx => ctx.Alarm5;
            SlotGetters[SlotAlarm6]      = ctx => ctx.Alarm6;
            SlotGetters[SlotAlarm7]      = ctx => ctx.Alarm7;
            SlotGetters[SlotAlarm8]      = ctx => ctx.Alarm8;
            SlotGetters[SlotAlarm9]      = ctx => ctx.Alarm9;
            SlotGetters[SlotAlarm10]     = ctx => ctx.Alarm10;
            SlotGetters[SlotAlarm11]     = ctx => ctx.Alarm11;
            SlotGetters[SlotUserDefined0] = ctx => ctx.UserDefined0;
            SlotGetters[SlotUserDefined1] = ctx => ctx.UserDefined1;
            SlotGetters[SlotUserDefined2] = ctx => ctx.UserDefined2;
            SlotGetters[SlotUserDefined3] = ctx => ctx.UserDefined3;
            SlotGetters[SlotUserDefined4] = ctx => ctx.UserDefined4;
            SlotGetters[SlotUserDefined5] = ctx => ctx.UserDefined5;
            SlotGetters[SlotUserDefined6] = ctx => ctx.UserDefined6;
            SlotGetters[SlotUserDefined7] = ctx => ctx.UserDefined7;
            SlotGetters[SlotUserDefined8] = ctx => ctx.UserDefined8;
            SlotGetters[SlotUserDefined9] = ctx => ctx.UserDefined9;
            SlotGetters[SlotUserDefined10]= ctx => ctx.UserDefined10;
            SlotGetters[SlotUserDefined11]= ctx => ctx.UserDefined11;

            SlotSetters[SlotX]           = (ctx, v) => ctx.X           = ToDouble(v);
            SlotSetters[SlotY]           = (ctx, v) => ctx.Y           = ToDouble(v);
            SlotSetters[SlotZ]           = (ctx, v) => ctx.Z           = ToDouble(v);
            SlotSetters[SlotHSpeed]      = (ctx, v) => ctx.HSpeed      = ToDouble(v);
            SlotSetters[SlotVSpeed]      = (ctx, v) => ctx.VSpeed      = ToDouble(v);
            SlotSetters[SlotSpeed]       = (ctx, v) => ctx.Speed       = ToDouble(v);
            SlotSetters[SlotDirection]   = (ctx, v) => ctx.Direction   = ToDouble(v);
            SlotSetters[SlotFriction]    = (ctx, v) => ctx.Friction    = ToDouble(v);
            SlotSetters[SlotGravity]     = (ctx, v) => ctx.Gravity     = ToDouble(v);
            SlotSetters[SlotGravityDir]  = (ctx, v) => ctx.GravityDirection = ToDouble(v);
            SlotSetters[SlotSpriteIndex] = (ctx, v) => ctx.SpriteIndex = v?.ToString() ?? "";
            SlotSetters[SlotImageIndex]  = (ctx, v) => ctx.ImageIndex  = ToDouble(v);
            SlotSetters[SlotImageSpeed]  = (ctx, v) => ctx.ImageSpeed  = ToDouble(v);
            SlotSetters[SlotImageAlpha]  = (ctx, v) => ctx.ImageAlpha  = ToDouble(v);
            SlotSetters[SlotImageAngle]  = (ctx, v) => ctx.ImageAngle  = ToDouble(v);
            SlotSetters[SlotImageXScale] = (ctx, v) => ctx.ImageXScale = ToDouble(v);
            SlotSetters[SlotImageYScale] = (ctx, v) => ctx.ImageYScale = ToDouble(v);
            SlotSetters[SlotVisible]     = (ctx, v) => ctx.Visible     = ToDouble(v) != 0;
            SlotSetters[SlotDepth]       = (ctx, v) => ctx.Depth       = (int)ToDouble(v);
            SlotSetters[SlotSolid]       = (ctx, v) => ctx.Solid       = ToDouble(v) != 0;
            SlotSetters[SlotRoomWidth]   = (ctx, v) => { /* read-only */ };
            SlotSetters[SlotRoomHeight]  = (ctx, v) => { /* read-only */ };
            SlotSetters[SlotDrawAlpha]   = (ctx, v) => ctx.DrawAlpha   = ToDouble(v);
            SlotSetters[SlotDrawFontSize]= (ctx, v) => ctx.DrawFontSize= ToDouble(v);
            SlotSetters[SlotAlarm0]      = (ctx, v) => ctx.Alarm0      = ToDouble(v);
            SlotSetters[SlotAlarm1]      = (ctx, v) => ctx.Alarm1      = ToDouble(v);
            SlotSetters[SlotAlarm2]      = (ctx, v) => ctx.Alarm2      = ToDouble(v);
            SlotSetters[SlotAlarm3]      = (ctx, v) => ctx.Alarm3      = ToDouble(v);
            SlotSetters[SlotAlarm4]      = (ctx, v) => ctx.Alarm4      = ToDouble(v);
            SlotSetters[SlotAlarm5]      = (ctx, v) => ctx.Alarm5      = ToDouble(v);
            SlotSetters[SlotAlarm6]      = (ctx, v) => ctx.Alarm6      = ToDouble(v);
            SlotSetters[SlotAlarm7]      = (ctx, v) => ctx.Alarm7      = ToDouble(v);
            SlotSetters[SlotAlarm8]      = (ctx, v) => ctx.Alarm8      = ToDouble(v);
            SlotSetters[SlotAlarm9]      = (ctx, v) => ctx.Alarm9      = ToDouble(v);
            SlotSetters[SlotAlarm10]     = (ctx, v) => ctx.Alarm10     = ToDouble(v);
            SlotSetters[SlotAlarm11]     = (ctx, v) => ctx.Alarm11     = ToDouble(v);
            SlotSetters[SlotUserDefined0] = (ctx, v) => ctx.UserDefined0 = ToDouble(v);
            SlotSetters[SlotUserDefined1] = (ctx, v) => ctx.UserDefined1 = ToDouble(v);
            SlotSetters[SlotUserDefined2] = (ctx, v) => ctx.UserDefined2 = ToDouble(v);
            SlotSetters[SlotUserDefined3] = (ctx, v) => ctx.UserDefined3 = ToDouble(v);
            SlotSetters[SlotUserDefined4] = (ctx, v) => ctx.UserDefined4 = ToDouble(v);
            SlotSetters[SlotUserDefined5] = (ctx, v) => ctx.UserDefined5 = ToDouble(v);
            SlotSetters[SlotUserDefined6] = (ctx, v) => ctx.UserDefined6 = ToDouble(v);
            SlotSetters[SlotUserDefined7] = (ctx, v) => ctx.UserDefined7 = ToDouble(v);
            SlotSetters[SlotUserDefined8] = (ctx, v) => ctx.UserDefined8 = ToDouble(v);
            SlotSetters[SlotUserDefined9] = (ctx, v) => ctx.UserDefined9 = ToDouble(v);
            SlotSetters[SlotUserDefined10]= (ctx, v) => ctx.UserDefined10= ToDouble(v);
            SlotSetters[SlotUserDefined11]= (ctx, v) => ctx.UserDefined11= ToDouble(v);
        }

        // ── Backward-compatible TryGet / TrySet (used by legacy LOAD_VAR path) ─
        internal static bool TryGet(PgslContext ctx, string name, out object value)
        {
            value = null;
            if (ctx == null || string.IsNullOrEmpty(name)) return false;
            if (!Slots.TryGetValue(name, out int slot)) return false;
            value = SlotGetters[slot](ctx);
            return true;
        }

        internal static bool TrySet(PgslContext ctx, string name, object value)
        {
            if (ctx == null || string.IsNullOrEmpty(name)) return false;
            if (!Slots.TryGetValue(name, out int slot)) return false;
            SlotSetters[slot](ctx, value);
            return true;
        }

        private static double ToDouble(object value) => value switch
        {
            double d  => d,
            float  f  => f,
            int    i  => i,
            bool   b  => b ? 1 : 0,
            _         => Convert.ToDouble(value)
        };
    }
}
