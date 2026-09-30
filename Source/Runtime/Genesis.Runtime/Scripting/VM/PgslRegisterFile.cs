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

        // Keep existing bytecode slot indices stable when adding instance identity.
        public const int SlotInstanceId = 48;
        public const int TotalSlots = 49;

        // ── Name → slot index lookup ────────────────────────────────────────────
        public static readonly Dictionary<string, int> Slots =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"]               = SlotInstanceId,
            ["instance_id"]      = SlotInstanceId,
            ["self"]             = SlotInstanceId,
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
        public static readonly Func<PgslContext, double>[] NumberGetters;

        // ── Compiled setter delegates, indexed by slot ──────────────────────────
        public static readonly Action<PgslContext, double>[] NumberSetters;

        static PgslRegisterFile()
        {
            NumberGetters = new Func<PgslContext, double>[TotalSlots];
            NumberSetters = new Action<PgslContext, double>[TotalSlots];

            NumberGetters[SlotInstanceId] = ctx => (double)ctx.InstanceId;
            NumberSetters[SlotInstanceId] = (ctx, v) => { /* identity is read-only */ };
            NumberGetters[SlotX]           = ctx => ctx.X;
            NumberGetters[SlotY]           = ctx => ctx.Y;
            NumberGetters[SlotZ]           = ctx => ctx.Z;
            NumberGetters[SlotHSpeed]      = ctx => ctx.HSpeed;
            NumberGetters[SlotVSpeed]      = ctx => ctx.VSpeed;
            NumberGetters[SlotSpeed]       = ctx => ctx.Speed;
            NumberGetters[SlotDirection]   = ctx => ctx.Direction;
            NumberGetters[SlotFriction]    = ctx => ctx.Friction;
            NumberGetters[SlotGravity]     = ctx => ctx.Gravity;
            NumberGetters[SlotGravityDir]  = ctx => ctx.GravityDirection;
            NumberGetters[SlotImageIndex]  = ctx => ctx.ImageIndex;
            NumberGetters[SlotImageSpeed]  = ctx => ctx.ImageSpeed;
            NumberGetters[SlotImageAlpha]  = ctx => ctx.ImageAlpha;
            NumberGetters[SlotImageAngle]  = ctx => ctx.ImageAngle;
            NumberGetters[SlotImageXScale] = ctx => ctx.ImageXScale;
            NumberGetters[SlotImageYScale] = ctx => ctx.ImageYScale;
            NumberGetters[SlotVisible]     = ctx => ctx.Visible ? 1.0 : 0.0;
            NumberGetters[SlotDepth]       = ctx => (double)ctx.Depth;
            NumberGetters[SlotSolid]       = ctx => ctx.Solid ? 1.0 : 0.0;
            NumberGetters[SlotRoomWidth]   = ctx => ctx.RoomWidth;
            NumberGetters[SlotRoomHeight]  = ctx => ctx.RoomHeight;
            NumberGetters[SlotDrawAlpha]   = ctx => ctx.DrawAlpha;
            NumberGetters[SlotDrawFontSize]= ctx => ctx.DrawFontSize;
            NumberGetters[SlotAlarm0]      = ctx => ctx.Alarm0;
            NumberGetters[SlotAlarm1]      = ctx => ctx.Alarm1;
            NumberGetters[SlotAlarm2]      = ctx => ctx.Alarm2;
            NumberGetters[SlotAlarm3]      = ctx => ctx.Alarm3;
            NumberGetters[SlotAlarm4]      = ctx => ctx.Alarm4;
            NumberGetters[SlotAlarm5]      = ctx => ctx.Alarm5;
            NumberGetters[SlotAlarm6]      = ctx => ctx.Alarm6;
            NumberGetters[SlotAlarm7]      = ctx => ctx.Alarm7;
            NumberGetters[SlotAlarm8]      = ctx => ctx.Alarm8;
            NumberGetters[SlotAlarm9]      = ctx => ctx.Alarm9;
            NumberGetters[SlotAlarm10]     = ctx => ctx.Alarm10;
            NumberGetters[SlotAlarm11]     = ctx => ctx.Alarm11;
            NumberGetters[SlotUserDefined0] = ctx => ctx.UserDefined0;
            NumberGetters[SlotUserDefined1] = ctx => ctx.UserDefined1;
            NumberGetters[SlotUserDefined2] = ctx => ctx.UserDefined2;
            NumberGetters[SlotUserDefined3] = ctx => ctx.UserDefined3;
            NumberGetters[SlotUserDefined4] = ctx => ctx.UserDefined4;
            NumberGetters[SlotUserDefined5] = ctx => ctx.UserDefined5;
            NumberGetters[SlotUserDefined6] = ctx => ctx.UserDefined6;
            NumberGetters[SlotUserDefined7] = ctx => ctx.UserDefined7;
            NumberGetters[SlotUserDefined8] = ctx => ctx.UserDefined8;
            NumberGetters[SlotUserDefined9] = ctx => ctx.UserDefined9;
            NumberGetters[SlotUserDefined10]= ctx => ctx.UserDefined10;
            NumberGetters[SlotUserDefined11]= ctx => ctx.UserDefined11;

            NumberSetters[SlotX]           = (ctx, v) => ctx.X           = v;
            NumberSetters[SlotY]           = (ctx, v) => ctx.Y           = v;
            NumberSetters[SlotZ]           = (ctx, v) => ctx.Z           = v;
            NumberSetters[SlotHSpeed]      = (ctx, v) => ctx.HSpeed      = v;
            NumberSetters[SlotVSpeed]      = (ctx, v) => ctx.VSpeed      = v;
            NumberSetters[SlotSpeed]       = (ctx, v) => ctx.Speed       = v;
            NumberSetters[SlotDirection]   = (ctx, v) => ctx.Direction   = v;
            NumberSetters[SlotFriction]    = (ctx, v) => ctx.Friction    = v;
            NumberSetters[SlotGravity]     = (ctx, v) => ctx.Gravity     = v;
            NumberSetters[SlotGravityDir]  = (ctx, v) => ctx.GravityDirection = v;
            NumberSetters[SlotImageIndex]  = (ctx, v) => ctx.ImageIndex  = v;
            NumberSetters[SlotImageSpeed]  = (ctx, v) => ctx.ImageSpeed  = v;
            NumberSetters[SlotImageAlpha]  = (ctx, v) => ctx.ImageAlpha  = v;
            NumberSetters[SlotImageAngle]  = (ctx, v) => ctx.ImageAngle  = v;
            NumberSetters[SlotImageXScale] = (ctx, v) => ctx.ImageXScale = v;
            NumberSetters[SlotImageYScale] = (ctx, v) => ctx.ImageYScale = v;
            NumberSetters[SlotVisible]     = (ctx, v) => ctx.Visible     = v != 0;
            NumberSetters[SlotDepth]       = (ctx, v) => ctx.Depth       = (int)v;
            NumberSetters[SlotSolid]       = (ctx, v) => ctx.Solid       = v != 0;
            NumberSetters[SlotRoomWidth]   = (ctx, v) => { /* read-only */ };
            NumberSetters[SlotRoomHeight]  = (ctx, v) => { /* read-only */ };
            NumberSetters[SlotDrawAlpha]   = (ctx, v) => ctx.DrawAlpha   = v;
            NumberSetters[SlotDrawFontSize]= (ctx, v) => ctx.DrawFontSize= v;
            NumberSetters[SlotAlarm0]      = (ctx, v) => ctx.Alarm0      = v;
            NumberSetters[SlotAlarm1]      = (ctx, v) => ctx.Alarm1      = v;
            NumberSetters[SlotAlarm2]      = (ctx, v) => ctx.Alarm2      = v;
            NumberSetters[SlotAlarm3]      = (ctx, v) => ctx.Alarm3      = v;
            NumberSetters[SlotAlarm4]      = (ctx, v) => ctx.Alarm4      = v;
            NumberSetters[SlotAlarm5]      = (ctx, v) => ctx.Alarm5      = v;
            NumberSetters[SlotAlarm6]      = (ctx, v) => ctx.Alarm6      = v;
            NumberSetters[SlotAlarm7]      = (ctx, v) => ctx.Alarm7      = v;
            NumberSetters[SlotAlarm8]      = (ctx, v) => ctx.Alarm8      = v;
            NumberSetters[SlotAlarm9]      = (ctx, v) => ctx.Alarm9      = v;
            NumberSetters[SlotAlarm10]     = (ctx, v) => ctx.Alarm10     = v;
            NumberSetters[SlotAlarm11]     = (ctx, v) => ctx.Alarm11     = v;
            NumberSetters[SlotUserDefined0] = (ctx, v) => ctx.UserDefined0 = v;
            NumberSetters[SlotUserDefined1] = (ctx, v) => ctx.UserDefined1 = v;
            NumberSetters[SlotUserDefined2] = (ctx, v) => ctx.UserDefined2 = v;
            NumberSetters[SlotUserDefined3] = (ctx, v) => ctx.UserDefined3 = v;
            NumberSetters[SlotUserDefined4] = (ctx, v) => ctx.UserDefined4 = v;
            NumberSetters[SlotUserDefined5] = (ctx, v) => ctx.UserDefined5 = v;
            NumberSetters[SlotUserDefined6] = (ctx, v) => ctx.UserDefined6 = v;
            NumberSetters[SlotUserDefined7] = (ctx, v) => ctx.UserDefined7 = v;
            NumberSetters[SlotUserDefined8] = (ctx, v) => ctx.UserDefined8 = v;
            NumberSetters[SlotUserDefined9] = (ctx, v) => ctx.UserDefined9 = v;
            NumberSetters[SlotUserDefined10]= (ctx, v) => ctx.UserDefined10= v;
            NumberSetters[SlotUserDefined11]= (ctx, v) => ctx.UserDefined11= v;
        }

        // ── Backward-compatible TryGet / TrySet (used by legacy LOAD_VAR path) ─
        internal static bool TryGet(PgslContext ctx, string name, out object value)
        {
            value = null;
            if (ctx == null || string.IsNullOrEmpty(name)) return false;
            if (!Slots.TryGetValue(name, out int slot)) return false;
            value = Read(ctx, slot).ToObject();
            return true;
        }

        internal static bool TrySet(PgslContext ctx, string name, object value)
        {
            if (ctx == null || string.IsNullOrEmpty(name)) return false;
            if (!Slots.TryGetValue(name, out int slot)) return false;
            Write(ctx, slot, VmValue.FromObject(value));
            return true;
        }

        internal static VmValue Read(PgslContext context, int slot) => slot == SlotSpriteIndex
            ? context.SpriteIndex ?? "" : (VmValue)NumberGetters[slot](context);

        internal static void Write(PgslContext context, int slot, in VmValue value)
        {
            if (slot == SlotSpriteIndex) context.SpriteIndex = value.ToString();
            else NumberSetters[slot](context, value.Number);
        }
    }
}
