using System.Globalization;
using System.Text;
using Genesis.Application.Core.Projects;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// PGSL as a language: operators, control flow, scoping, and the value command families, run as
/// scripts in the object sandbox. Each script sets <c>t_*</c> variables to 1 (pass) or 0 (fail),
/// and <c>b_*</c> / <c>s_*</c> variables to measured behaviour that is reported, not asserted.
/// Writes <c>pgsl-logic.md</c> beside the captures: every check, every behaviour, how bad
/// arguments are handled, and the callability sweep of every registered command.
/// </summary>
internal static class PgslLogicSuite
{
    private sealed record Row(string Group, string Name, string Result, string Detail);

    /// <summary>The language checks, by group: shared with the project test, which runs them in the Player.</summary>
    internal static readonly (string Name, string Code)[] LogicScripts =
    [
        ("Operators", """
            function Bump() { GlobalSet("hits", GlobalGet("hits") + 1); return 1; }
            t_precedence = (2 + 3 * 4 == 14) ? 1 : 0;
            t_parentheses = ((2 + 3) * 4 == 20) ? 1 : 0;
            t_modulo = (7 % 3 == 1) ? 1 : 0;
            b_negative_modulo = -7 % 3;
            t_division_is_real = (7 / 2 == 3.5) ? 1 : 0;
            t_comparisons = (3 >= 3 && 2 < 3 && 4 != 5 && 4 <= 4 && 5 > 4 && 2 == 2) ? 1 : 0;
            t_not = (!0 == 1 && !1 == 0) ? 1 : 0;
            t_or = (0 || 1) ? 1 : 0;
            t_and = (1 && 0) ? 0 : 1;
            t_ternary_nested = ((1 > 2 ? 10 : (2 > 1 ? 20 : 30)) == 20) ? 1 : 0;
            t_bit_and = ((6 & 3) == 2) ? 1 : 0;
            t_bit_or = ((6 | 3) == 7) ? 1 : 0;
            t_bit_xor = ((6 ^ 3) == 5) ? 1 : 0;
            t_shift_left = ((1 << 4) == 16) ? 1 : 0;
            t_shift_right = ((256 >> 4) == 16) ? 1 : 0;
            t_bit_not = (~0 == -1) ? 1 : 0;
            b_add_before_shift = 1 + 2 << 1;
            t_string_concat = ("ab" + "cd" == "abcd") ? 1 : 0;
            s_string_plus_number = "n" + 5;
            v = 10; v += 5; v -= 3; v *= 2; v /= 4;
            t_compound_assignment = (v == 6) ? 1 : 0;
            inc = 1; inc++; inc++; inc--;
            t_increment = (inc == 2) ? 1 : 0;
            GlobalSet("hits", 0);
            if (0 && Bump()) { never = 1; }
            t_and_skips_right_side_when_left_false = (GlobalGet("hits") == 0) ? 1 : 0;
            GlobalSet("hits", 0);
            if (1 || Bump()) { always = 1; }
            t_or_skips_right_side_when_left_true = (GlobalGet("hits") == 0) ? 1 : 0;
            GlobalSet("hits", 0);
            both = (1 && Bump()) ? 1 : 0;
            either = (0 || Bump()) ? 1 : 0;
            t_right_side_runs_when_needed = (GlobalGet("hits") == 2 && both == 1 && either == 1) ? 1 : 0;
            t_guard_pattern = (0 > 1 && Sqrt(-1) > 0) ? 0 : 1;
            GlobalSet("hits", 0);
            q = 0 ? Bump() : 2;
            t_ternary_skips_untaken_side = (GlobalGet("hits") == 0 && q == 2) ? 1 : 0;
            """),
        ("Maths", """
            t_floor = (Floor(-1.5) == -2) ? 1 : 0;
            t_ceil = (Ceil(1.2) == 2) ? 1 : 0;
            b_round_two_and_a_half = Round(2.5);
            b_round_minus_two_and_a_half = Round(-2.5);
            t_min_max = (Min(3, 7) == 3 && Max(3, 7) == 7) ? 1 : 0;
            t_clamp = (Clamp(5, 0, 3) == 3 && Clamp(-1, 0, 3) == 0) ? 1 : 0;
            t_approach = (Approach(0, 10, 3) == 3 && Approach(9, 10, 3) == 10 && Approach(10, 0, 4) == 6) ? 1 : 0;
            t_lerp = (Lerp(0, 10, 0.25) == 2.5) ? 1 : 0;
            t_sin_in_degrees = (Abs(Sin(90) - 1) < 0.000001) ? 1 : 0;
            t_cos_in_degrees = (Abs(Cos(180) + 1) < 0.000001) ? 1 : 0;
            t_sqrt_power = (Sqrt(16) == 4 && Power(2, 10) == 1024) ? 1 : 0;
            t_abs_sign = (Abs(-3) == 3 && Sign(-2) == -1 && Sign(0) == 0) ? 1 : 0;
            t_point_distance = (PointDistance(0, 0, 3, 4) == 5) ? 1 : 0;
            b_point_direction_to_minus_y = PointDirection(0, 0, 0, -1);
            t_lengthdir_x = (Abs(LengthDirX(10, 0) - 10) < 0.000001) ? 1 : 0;
            inRange = 1;
            repeat (200) { r = RandomRange(2, 5); if (r < 2 || r > 5) { inRange = 0; } }
            t_random_range_stays_in_range = inRange;
            inRange = 1;
            repeat (200) { r = Random(3); if (r < 0 || r > 3) { inRange = 0; } }
            t_random_stays_in_range = inRange;
            picked = Choose(4, 4, 4);
            t_choose = (picked == 4) ? 1 : 0;
            t_time_ms_moves_forward = (TimeMs() >= 0) ? 1 : 0;
            """),
        ("ControlFlow", """
            function Fact(n) { if (n <= 1) { return 1; } return n * Fact(n - 1); }
            function Early(n) { if (n > 5) { return 1; } return 0; }
            function Deep(n) { if (n <= 0) { return 0; } return 1 + Deep(n - 1); }
            function Depth(n) { return 7; }
            sum = 0;
            for (i = 0; i < 10; i = i + 1) { sum += i; }
            t_for = (sum == 45) ? 1 : 0;
            w = 0; n = 0;
            while (n < 5) { w += 2; n += 1; }
            t_while = (w == 10) ? 1 : 0;
            rp = 0;
            repeat (7) { rp += 1; }
            t_repeat = (rp == 7) ? 1 : 0;
            nest = 0;
            for (a = 0; a < 3; a = a + 1) { for (b = 0; b < 4; b = b + 1) { nest += 1; } }
            t_nested_loops = (nest == 12) ? 1 : 0;
            t_recursion = (Fact(10) == 3628800) ? 1 : 0;
            t_early_return = (Early(9) == 1 && Early(2) == 0) ? 1 : 0;
            t_recursion_190_deep = (Deep(190) == 190) ? 1 : 0;
            t_function_named_like_a_builtin_is_the_scripts = (Depth(1) == 7) ? 1 : 0;
            found = -1;
            for (i = 0; i < 100; i = i + 1) { if (i * i > 50) { found = i; break; } }
            t_break_in_for = (found == 8) ? 1 : 0;
            odd = 0;
            for (i = 0; i < 10; i = i + 1) { if (i % 2 == 0) { continue; } odd += 1; }
            t_continue_in_for = (odd == 5) ? 1 : 0;
            k = 0;
            while (1) { k += 1; if (k >= 4) { break; } }
            t_break_in_while = (k == 4) ? 1 : 0;
            skipped = 0; ran = 0;
            repeat (6) { ran += 1; if (ran > 3) { continue; } skipped += 1; }
            t_continue_in_repeat = (ran == 6 && skipped == 3) ? 1 : 0;
            outer = 0;
            for (a = 0; a < 3; a = a + 1) { for (b = 0; b < 3; b = b + 1) { if (b == 1) { break; } outer += 1; } }
            t_break_leaves_inner_loop_only = (outer == 3) ? 1 : 0;
            g = 5;
            if (g < 3) { c = 1; } else if (g < 6) { c = 2; } else { c = 3; }
            t_else_if = (c == 2) ? 1 : 0;
            t_loop_variable_survives = (i == 10) ? 1 : 0;
            """),
        ("Variables", """
            function SetHp() { hp = 5; }
            function SetWithVariableSet() { VariableSet("hp2", 6); }
            function ReadSecret() { return secret; }
            function Outer() { var secret = 7; return ReadSecret(); }
            function Clobber() { var shared = 99; return shared; }
            function Twice(value) { return value * 2; }
            function SetHpSelf() { self.hp3 = 8; self.x = 12; }
            function ReadHpSelf() { var hp3 = 1; return self.hp3; }
            function CaseCopy() { var mbx = DsListGet(mbX, 0); DsListSet(mbX, 0, mbx + 1); return mbx; }
            function LocalCase() { var Count = 3; count = 5; return Count; }
            function BuiltinAnyCase() { return Speed; }
            hp = 1;
            SetHp();
            b_plain_assignment_in_function_reaches_instance = (hp == 5) ? 1 : 0;
            SetWithVariableSet();
            b_variableset_in_function_reaches_instance = (VariableGet("hp2") == 6) ? 1 : 0;
            b_callee_reads_callers_local = Outer();
            shared = 1;
            Clobber();
            t_callee_cannot_overwrite_callers_variable = (shared == 1) ? 1 : 0;
            t_parameters = (Twice(21) == 42) ? 1 : 0;
            SetHpSelf();
            t_self_assignment_in_function_reaches_instance = (hp3 == 8 && x == 12) ? 1 : 0;
            t_self_reads_the_instance_past_a_local = (ReadHpSelf() == 8) ? 1 : 0;
            GlobalSet("score", 3);
            t_global_number = (GlobalGet("score") == 3) ? 1 : 0;
            GlobalSetString("who", "Ana");
            t_global_string = (GlobalGetString("who") == "Ana") ? 1 : 0;
            t_global_exists = (GlobalExists("score") == 1) ? 1 : 0;
            GlobalDelete("score");
            t_global_delete = (GlobalExists("score") == 0) ? 1 : 0;
            speed = 4;
            t_register_speed = (speed == 4) ? 1 : 0;
            var local = 3;
            t_event_var = (local == 3) ? 1 : 0;
            text = "hi";
            t_string_variable = (text == "hi") ? 1 : 0;
            victim = DsListCreate(); DsListAdd(victim, 100);
            mbX = DsListCreate(); DsListAdd(mbX, victim);
            got = CaseCopy();
            t_function_local_mbx_is_not_instance_mbX = (got == victim && DsListGet(mbX, 0) == victim + 1 && DsListGet(victim, 0) == 100) ? 1 : 0;
            t_function_names_differ_by_case = (LocalCase() == 3) ? 1 : 0;
            t_builtin_instance_variables_ignore_case = (BuiltinAnyCase() == 4) ? 1 : 0;
            """),
        ("Strings", """
            t_length = (StringLength("hello") == 5) ? 1 : 0;
            t_upper_lower = (StringUpper("abC") == "ABC" && StringLower("AbC") == "abc") ? 1 : 0;
            b_pos_of_l_in_hello = StringPos("l", "hello");
            b_pos_missing = StringPos("z", "hello");
            s_copy_hello_1_3 = StringCopy("hello", 1, 3);
            s_char_at_0 = StringCharAt("abc", 0);
            s_split_part_1 = StringSplitPart("a,b,c", ",", 1);
            t_replace_all = (StringReplaceAll("a-b-c", "-", "+") == "a+b+c") ? 1 : 0;
            s_replace = StringReplace("a-b-c", "-", "+");
            t_split_count = (StringSplitCount("a,b,c", ",") == 3) ? 1 : 0;
            t_trim = (StringTrim("  x  ") == "x") ? 1 : 0;
            t_contains = (StringContains("haystack", "st") == 1 && StringContains("haystack", "zz") == 0) ? 1 : 0;
            t_repeat = (StringRepeat("ab", 3) == "ababab") ? 1 : 0;
            t_reverse = (StringReverse("abc") == "cba") ? 1 : 0;
            t_format_number = (StringFormatNumber(3.14159, 2) == "3.14") ? 1 : 0;
            t_real = (Real("12.5") == 12.5) ? 1 : 0;
            s_string_of_three = String(3);
            s_string_of_half = String(0.5);
            t_pad_left = (StringPadLeft("7", 3, "0") == "007") ? 1 : 0;
            """),
        ("Collections", """
            list = DsListCreate();
            DsListAdd(list, 3); DsListAdd(list, 1); DsListAdd(list, 2);
            t_list_size = (DsListSize(list) == 3) ? 1 : 0;
            DsListSort(list, 1);
            t_list_sort = (DsListGet(list, 0) == 1 && DsListGet(list, 2) == 3) ? 1 : 0;
            t_list_find = (DsListFind(list, 2) == 1) ? 1 : 0;
            DsListDelete(list, 0);
            t_list_delete = (DsListSize(list) == 2) ? 1 : 0;
            DsListAddString(list, "x");
            t_list_string = (DsListGetString(list, 2) == "x") ? 1 : 0;
            map = DsMapCreate();
            DsMapSet(map, "a", 1);
            DsMapSetString(map, "b", "two");
            t_map_get = (DsMapGet(map, "a") == 1 && DsMapGetString(map, "b") == "two") ? 1 : 0;
            t_map_exists_size = (DsMapExists(map, "a") == 1 && DsMapExists(map, "zz") == 0 && DsMapSize(map) == 2) ? 1 : 0;
            DsMapDelete(map, "a");
            t_map_delete = (DsMapExists(map, "a") == 0 && DsMapSize(map) == 1) ? 1 : 0;
            grid = DsGridCreate(3, 2);
            DsGridSet(grid, 2, 1, 5);
            t_grid = (DsGridGet(grid, 2, 1) == 5 && DsGridGetSum(grid, 0, 0, 2, 1) == 5) ? 1 : 0;
            stack = DsStackCreate();
            DsStackPush(stack, 1); DsStackPush(stack, 2);
            t_stack = (DsStackPop(stack) == 2 && DsStackSize(stack) == 1) ? 1 : 0;
            queue = DsQueueCreate();
            DsQueueEnqueue(queue, 1); DsQueueEnqueue(queue, 2);
            t_queue = (DsQueueDequeue(queue) == 1 && DsQueueSize(queue) == 1) ? 1 : 0;
            ArraySet("arr", 0, 4);
            ArrayPush("arr", 6);
            t_array = (ArrayLength("arr") == 2 && ArrayGet("arr", 1) == 6) ? 1 : 0;
            save = DsMapCreate();
            DsMapSet(save, "hp", 10);
            DsMapSetString(save, "who", "Ana");
            json = JsonEncode(save, "map");
            back = JsonDecode(json, "map");
            t_json_round_trip = (back != 0 && DsMapGet(back, "hp") == 10 && DsMapGetString(back, "who") == "Ana") ? 1 : 0;
            bad = JsonDecode("{oops", "map");
            t_json_bad_text_gives_zero_and_an_error = (bad == 0 && StringLength(JsonLastError()) > 0) ? 1 : 0;
            t_save_slot_write = (SaveSlotWrite("pgsl_logic", json) == 1) ? 1 : 0;
            t_save_slot_read = (SaveSlotExists("pgsl_logic") == 1 && SaveSlotRead("pgsl_logic") == json) ? 1 : 0;
            t_save_slot_delete = (SaveSlotDelete("pgsl_logic") == 1 && SaveSlotExists("pgsl_logic") == 0) ? 1 : 0;
            """),
        ("Clock and sky", """
            // 1782000000 is 21 June 2026 in every time zone's local date.
            t_now_is_recent = (DateNow() > 1780000000) ? 1 : 0;
            t_date_parts = (DateYear(1782000000) == 2026 && DateMonth(1782000000) == 6 && DateDay(1782000000) >= 20) ? 1 : 0;
            t_date_text = (DateText(1782000000, "yyyy-MM") == "2026-06") ? 1 : 0;
            t_weekday = (DateWeekday(1782000000) >= 0 && DateWeekday(1782000000) <= 6) ? 1 : 0;
            sx = Engine.Sky.SunDirectionX; sy = Engine.Sky.SunDirectionY; sz = Engine.Sky.SunDirectionZ;
            t_sun_direction_is_unit = (Abs(Sqrt(sx * sx + sy * sy + sz * sz) - 1) < 0.001) ? 1 : 0;
            t_window_mode_named = (WindowSetMode("sideways") == 0) ? 1 : 0;
            // How long a namespaced setting read takes against a plain command call (reported, in microseconds).
            t0 = TimeMs(); k = 0; acc = 0;
            while (k < 2000) { acc += Engine.Sky.SunDirectionX; k += 1; }
            b_sky_setting_read_us = (TimeMs() - t0) * 1000 / 2000;
            t0 = TimeMs(); k = 0;
            while (k < 2000) { acc += GameGetSpeed(); k += 1; }
            b_command_call_us = (TimeMs() - t0) * 1000 / 2000;
            """),
        ("Parameters", """
            x = 5;
            y = 7;
            function Twice(x) { return x * 2; }
            function Offset(id, y) { return id + y; }
            t_parameter_x = (Twice(3) == 6) ? 1 : 0;
            t_instance_x_kept = (x == 5) ? 1 : 0;
            t_parameters_id_y = (Offset(2, 3) == 5 && y == 7) ? 1 : 0;
            function LocalExposure() { var exposure = 3; exposure = exposure + 1; return exposure; }
            t_local_named_like_an_engine_setting = (LocalExposure() == 4) ? 1 : 0;
            """),
        ("Lists", """
            var a = [1, 2, 3];
            a[1] = 9;
            t_literal_index = (a[1] + a[2] == 12) ? 1 : 0;
            var e = [];
            t_empty = (PgListSize(e) == 0) ? 1 : 0;
            var words = ["oak", "birch"];
            t_strings = (words[1] == "birch" && PgListSize(words) == 2) ? 1 : 0;
            var grid = [[1, 2], [3, 4]];
            t_nested = (grid[1][0] == 3) ? 1 : 0;
            grid[0][1] = 7;
            t_nested_write = (grid[0][1] == 7 && grid[0][0] == 1) ? 1 : 0;
            a[5] = 1;
            t_write_grows = (PgListSize(a) == 6 && a[4] == 0) ? 1 : 0;
            PgListAdd(e, 4);
            PgListInsert(e, 0, 5);
            t_add_insert = (e[0] == 5 && e[1] == 4) ? 1 : 0;
            PgListSort(e, true);
            t_sort = (e[0] == 4 && e[1] == 5) ? 1 : 0;
            t_find = (PgListFind(words, "birch") == 1 && PgListFind(words, "elm") == -1) ? 1 : 0;
            var b = a;
            b[0] = 42;
            t_shared_reference = (a[0] == 42) ? 1 : 0;
            var c = PgListCopy(a);
            c[0] = 1;
            t_copy_is_separate = (a[0] == 42 && c[0] == 1) ? 1 : 0;
            t_pop = (PgListPop(a) == 1 && PgListSize(a) == 5) ? 1 : 0;
            t_is_list = (IsList(a) == 1 && IsList(3) == 0) ? 1 : 0;
            t_text = (String([1, "x", [2.5]]) == "[1, \"x\", [2.5]]") ? 1 : 0;
            """),
        ("Noise", """
            t_same_arguments_same_value = (Noise2D(12.3, 4.5, 7) == Noise2D(12.3, 4.5, 7) && Noise3D(1.5, 2.5, 3.5, 7) == Noise3D(1.5, 2.5, 3.5, 7)) ? 1 : 0;
            t_seed_changes_value = (Noise2D(12.3, 4.5, 7) != Noise2D(12.3, 4.5, 8) && ValueNoise2D(3, 4, 1) != ValueNoise2D(3, 4, 2)) ? 1 : 0;
            lo = 9; hi = -9; vlo = 9; vhi = -9; flo = 9; fhi = -9; jump = 0; whole = 0;
            for (k = 0; k < 400; k = k + 1) {
                px = k * 0.37 - 50; py = k * 0.61 + 3;
                n = Noise2D(px, py, 3); m = Noise3D(px, py, k * 0.13, 3);
                lo = Min(lo, Min(n, m)); hi = Max(hi, Max(n, m));
                v = ValueNoise2D(px, py, 3); w = ValueNoise3D(px, py, k * 0.13, 3);
                vlo = Min(vlo, Min(v, w)); vhi = Max(vhi, Max(v, w));
                f = FractalNoise2D(px, py, 3, 5, 2, 0.5);
                flo = Min(flo, f); fhi = Max(fhi, f);
                jump = Max(jump, Abs(Noise2D(px + 0.001, py, 3) - n));
                if (Noise2D(k, 0, 3) != 0) { whole = whole + 1; }
            }
            t_gradient_in_minus_one_to_one = (lo >= -1 && hi <= 1 && lo < -0.4 && hi > 0.4) ? 1 : 0;
            t_value_in_zero_to_one = (vlo >= 0 && vhi < 1 && vlo < 0.2 && vhi > 0.8) ? 1 : 0;
            t_fractal_in_minus_one_to_one = (flo >= -1 && fhi <= 1 && flo < -0.3 && fhi > 0.3) ? 1 : 0;
            t_continuous = (jump < 0.01) ? 1 : 0;
            b_largest_step_for_0_001 = jump;
            t_whole_numbers_vary = (whole > 390) ? 1 : 0;
            t_one_octave_is_noise2d = (FractalNoise2D(4.2, 9.1, 5, 1, 2, 0.5) == Noise2D(4.2, 9.1, 5)) ? 1 : 0;
            g = DsGridCreate(17, 9);
            t_fill_counts_cells = (NoiseFillGrid(g, -3.5, 11, 0.25, 42, 4, 2, 0.5, 30, 64) == 153) ? 1 : 0;
            same = 1;
            for (j = 0; j < 9; j = j + 1) { for (k = 0; k < 17; k = k + 1) {
                if (DsGridGet(g, k, j) != 64 + 30 * FractalNoise2D(-3.5 + k * 0.25, 11 + j * 0.25, 42, 4, 2, 0.5)) { same = 0; }
            } }
            t_fill_equals_per_cell_calls = same;
            NoiseFillGrid3D(g, 2, 7.5, -4, 0.5, "xz", 9, 3, 2, 0.5, 1, 0);
            same = 1;
            for (j = 0; j < 9; j = j + 1) { for (k = 0; k < 17; k = k + 1) {
                if (DsGridGet(g, k, j) != FractalNoise3D(2 + k * 0.5, 7.5, -4 + j * 0.5, 9, 3, 2, 0.5)) { same = 0; }
            } }
            t_fill3d_equals_per_cell_calls = same;
            t_fill_bad_grid_or_plane_is_zero = (NoiseFillGrid(987654, 0, 0, 1, 1, 1, 2, 0.5, 1, 0) == 0
                && NoiseFillGrid3D(g, 0, 0, 0, 1, "up", 1, 1, 2, 0.5, 1, 0) == 0) ? 1 : 0;
            t_hash_is_value_noise_at_whole_points = (Hash2(3, -7, 11) == ValueNoise2D(3, -7, 11) && Hash3(1, 2, -3, 0.5) == ValueNoise3D(1, 2, -3, 0.5)) ? 1 : 0;
            t_hash_drops_fractions_down = (Hash2(3.9, -6.5, 11) == Hash2(3, -7, 11) && Hash3(1.2, 2.99, -2.5, 0.5) == Hash3(1, 2, -3, 0.5)) ? 1 : 0;
            t_hash_salt_and_point_matter = (Hash2(3, 4, 1) != Hash2(3, 4, 2) && Hash2(3, 4, 1) != Hash2(4, 3, 1) && Hash3(3, 4, 5, 1) != Hash3(3, 4, 6, 1)) ? 1 : 0;
            t_hash_not_a_number_is_zero = (Hash2(Sqrt(-1), 1, 1) == 0 || Sqrt(-1) == 0) ? 1 : 0;
            // 1 when every value is in 0..1 and both ends are reached, plus 2 when the means are near 0.5.
            function HashSpread(n) {
                var lo = 9; var hi = -9; var s2 = 0; var s3 = 0;
                for (var k = 0; k < n; k = k + 1) {
                    var hv = Hash2(k % 50 - 25, Floor(k / 50), 77); var hw = Hash3(k, k * 3, -k, 77);
                    lo = Min(lo, Min(hv, hw)); hi = Max(hi, Max(hv, hw)); s2 = s2 + hv; s3 = s3 + hw;
                }
                var ok = 0;
                if (lo >= 0 && hi < 1 && lo < 0.01 && hi > 0.99) { ok = ok + 1; }
                if (Abs(s2 / n - 0.5) < 0.04 && Abs(s3 / n - 0.5) < 0.04) { ok = ok + 2; }
                return ok;
            }
            spread = HashSpread(1000);
            t_hash_in_zero_to_one = (spread % 2 == 1) ? 1 : 0;
            t_hash_even_spread = (spread >= 2) ? 1 : 0;
            """),
        ("Grid batches", """
            g = DsGridCreate(6, 4);
            DsGridSetRegion(g, 1, 1, 2, 2, 7);
            t_count = (DsGridCount(g, 0, 0, 5, 3, 7) == 4 && DsGridCount(g, 0, 0, 5, 3, 0) == 20 && DsGridCount(g, -9, -9, 99, 99, 7) == 4) ? 1 : 0;
            t_find_first = (DsGridFind(g, 0, 0, 5, 3, 7) == 1 + 1 * 6) ? 1 : 0;
            t_find_backwards = (DsGridFind(g, 5, 3, 0, 0, 7) == 2 + 2 * 6) ? 1 : 0;
            t_find_other_top_down = (DsGridFindOther(g, 2, 3, 2, 0, 0) == 2 + 2 * 6) ? 1 : 0;
            t_find_none = (DsGridFind(g, 0, 0, 5, 3, 99) == -1 && DsGridFind(55555, 0, 0, 1, 1, 0) == -1) ? 1 : 0;
            l = DsListCreate();
            t_to_list = (DsGridToList(g, 1, 1, 3, 2, l) == 6 && DsListSize(l) == 6 && DsListGet(l, 0) == 7 && DsListGet(l, 2) == 0 && DsListGet(l, 4) == 7) ? 1 : 0;
            h = DsGridCreate(6, 4);
            t_from_list = (DsGridFromList(h, 2, 0, 4, 1, l) == 6 && DsGridGet(h, 2, 0) == 7 && DsGridGet(h, 4, 0) == 0 && DsGridGet(h, 3, 1) == 7) ? 1 : 0;
            t_copy_region_count = (DsGridCopyRegion(h, 0, 2, g, 1, 1, 2, 2) == 4) ? 1 : 0;
            t_copy_region = (DsGridGet(h, 0, 2) == 7 && DsGridGet(h, 1, 3) == 7 && DsGridGet(h, 2, 2) == 0) ? 1 : 0;
            s = DsGridCreate(5, 1);
            for (k = 0; k < 5; k = k + 1) { DsGridSet(s, k, 0, k); }
            DsGridCopyRegion(s, 1, 0, s, 0, 0, 3, 0);
            t_overlapping_copy = (DsGridGet(s, 0, 0) == 0 && DsGridGet(s, 1, 0) == 0 && DsGridGet(s, 2, 0) == 1 && DsGridGet(s, 4, 0) == 3) ? 1 : 0;
            DsGridAddRegion(g, 0, 0, 5, 3, 1.5);
            DsGridMultiplyRegion(g, 0, 0, 5, 0, 2);
            DsGridClampRegion(g, 0, 0, 5, 3, 0, 8);
            DsGridFloorRegion(g, 0, 3, 5, 3);
            t_region_maths = (DsGridGet(g, 0, 0) == 3 && DsGridGet(g, 1, 1) == 8 && DsGridGet(g, 0, 1) == 1.5 && DsGridGet(g, 4, 3) == 1) ? 1 : 0;
            a = DsGridCreate(3, 3); b = DsGridCreate(2, 2); DsGridClear(a, 1); DsGridClear(b, 2);
            t_add_grid = (DsGridAddGrid(a, b, 0.5) == 4 && DsGridGet(a, 1, 1) == 2 && DsGridGet(a, 2, 2) == 1) ? 1 : 0;
            f = DsListCreate(); DsListFill(f, 5, 3);
            t_list_fill = (DsListSize(f) == 5 && DsListSum(f) == 15) ? 1 : 0;
            c = DsListCreate(); DsListAdd(c, 9); DsListCopy(c, l);
            t_list_copy = (DsListSize(c) == 6 && DsListGet(c, 0) == 7) ? 1 : 0;
            """),
        ("Compact grids", """
            b8 = DsGridCreate(4, 3, "u8"); b16 = DsGridCreate(4, 3, "u16"); b32 = DsGridCreate(4, 3, "i32"); bf = DsGridCreate(4, 3, "f32"); bd = DsGridCreate(4, 3);
            t_kinds = (DsGridKind(b8) == "u8" && DsGridKind(b16) == "u16" && DsGridKind(b32) == "i32" && DsGridKind(bf) == "f32" && DsGridKind(bd) == "f64" && DsGridKind(424242) == "") ? 1 : 0;
            t_kind_names_in_any_case = (DsGridKind(DsGridCreate(2, 2, "U16")) == "u16" && DsGridKind(DsGridCreate(2, 2, "f64")) == "f64" && DsGridKind(DsGridCreate(2, 2, "")) == "f64") ? 1 : 0;
            t_unknown_kind_makes_no_grid = (DsGridCreate(2, 2, "u12") == 0 && DsGridCreate(2, 2, "int") == 0) ? 1 : 0;
            t_bytes = (DsGridBytes(b8) == 12 && DsGridBytes(b16) == 24 && DsGridBytes(b32) == 48 && DsGridBytes(bf) == 48 && DsGridBytes(bd) == 96 && DsGridBytes(424242) == 0) ? 1 : 0;
            DsGridSet(b8, 0, 0, 300); DsGridSet(b8, 1, 0, -5); DsGridSet(b8, 2, 0, 7.9); DsGridSet(b8, 3, 0, 255);
            t_u8_clamps_and_drops_fractions = (DsGridGet(b8, 0, 0) == 255 && DsGridGet(b8, 1, 0) == 0 && DsGridGet(b8, 2, 0) == 7 && DsGridGet(b8, 3, 0) == 255) ? 1 : 0;
            DsGridSet(b16, 0, 0, 3071 + 4096); DsGridSet(b16, 1, 0, 70000); DsGridSet(b16, 2, 0, -1); DsGridSet(b16, 3, 0, Sqrt(-1));
            t_u16 = (DsGridGet(b16, 0, 0) == 7167 && DsGridGet(b16, 1, 0) == 65535 && DsGridGet(b16, 2, 0) == 0 && DsGridGet(b16, 3, 0) == 0) ? 1 : 0;
            DsGridSet(b32, 0, 0, -123456789); DsGridSet(b32, 1, 0, 5000000000000); DsGridSet(b32, 2, 0, -2.7); DsGridSet(b32, 3, 0, -5000000000000);
            t_i32 = (DsGridGet(b32, 0, 0) == -123456789 && DsGridGet(b32, 1, 0) == 2147483647 && DsGridGet(b32, 2, 0) == -2 && DsGridGet(b32, 3, 0) == -2147483648) ? 1 : 0;
            DsGridSet(bf, 0, 0, 0.5); DsGridSet(bf, 1, 0, 16777216); DsGridSet(bf, 2, 0, 0.1);
            t_f32_single_precision = (DsGridGet(bf, 0, 0) == 0.5 && DsGridGet(bf, 1, 0) == 16777216 && Abs(DsGridGet(bf, 2, 0) - 0.1) < 0.0000001 && DsGridGet(bf, 2, 0) != 0.1) ? 1 : 0;
            t_outside_reads_zero = (DsGridGet(b8, 9, 9) == 0 && DsGridGet(b16, -1, 0) == 0 && DsGridGet(b32, 0, 3) == 0) ? 1 : 0;
            DsGridAdd(b8, 3, 0, 10); DsGridAdd(b8, 1, 0, -3); DsGridMultiply(b8, 2, 0, 0.5);
            t_add_and_multiply_clamp = (DsGridGet(b8, 3, 0) == 255 && DsGridGet(b8, 1, 0) == 0 && DsGridGet(b8, 2, 0) == 3) ? 1 : 0;
            DsGridClear(b16, 9.5);
            t_clear = (DsGridGetSum(b16, 0, 0, 3, 2) == 108 && DsGridCount(b16, 0, 0, 3, 2, 9) == 12) ? 1 : 0;
            DsGridSetRegion(b16, 1, 1, 2, 2, 40000);
            t_region_reads = (DsGridGetMax(b16, 0, 0, 3, 2) == 40000 && DsGridGetMin(b16, 0, 0, 3, 2) == 9 && DsGridValueExists(b16, 0, 0, 3, 2, 40000) == 1) ? 1 : 0;
            t_find = (DsGridFind(b16, 0, 0, 3, 2, 40000) == 1 + 1 * 4 && DsGridFindOther(b16, 0, 2, 0, 0, 0) == 0 + 2 * 4) ? 1 : 0;
            DsGridAddRegion(b16, 0, 0, 3, 0, -20); DsGridMultiplyRegion(b16, 1, 1, 1, 1, 2); DsGridClampRegion(b16, 2, 2, 2, 2, 0, 100); DsGridFloorRegion(b16, 0, 0, 3, 2);
            t_region_maths_clamp = (DsGridGet(b16, 0, 0) == 0 && DsGridGet(b16, 1, 1) == 65535 && DsGridGet(b16, 2, 2) == 100 && DsGridGet(b16, 3, 2) == 9) ? 1 : 0;
            tl = DsListCreate();
            t_to_list_reads_numbers = (DsGridToList(b32, 0, 0, 3, 0, tl) == 4 && DsListGet(tl, 0) == -123456789 && DsListGet(tl, 2) == -2 && DsListGet(tl, 3) == -2147483648) ? 1 : 0;
            l = DsListCreate();
            for (k = 0; k < 6; k = k + 1) { DsListAdd(l, k * 100); }
            col = DsGridCreate(3, 8, "u8");
            t_column_from_list = (DsGridSetColumnFromList(col, 1, 2, l) == 6 && DsGridGet(col, 1, 2) == 0 && DsGridGet(col, 1, 4) == 200 && DsGridGet(col, 1, 5) == 255 && DsGridGet(col, 1, 1) == 0) ? 1 : 0;
            back = DsListCreate();
            t_column_to_list = (DsGridGetColumnToList(col, 1, 3, 5, back) == 3 && DsListSize(back) == 3 && DsListGet(back, 0) == 100 && DsListGet(back, 2) == 255) ? 1 : 0;
            t_column_past_the_bottom = (DsGridSetColumnFromList(col, 0, 6, l) == 6 && DsGridGet(col, 0, 6) == 0 && DsGridGet(col, 0, 7) == 100) ? 1 : 0;
            t_column_on_a_number_grid = (DsGridSetColumnFromList(bd, 2, 0, l) == 6 && DsGridGet(bd, 2, 2) == 200 && DsGridGetColumnToList(bd, 2, 0, 2, back) == 3 && DsListGet(back, 1) == 100) ? 1 : 0;
            src = DsGridCreate(4, 2); DsGridSet(src, 0, 0, 1.5); DsGridSet(src, 1, 0, 70000); DsGridSet(src, 2, 1, -4);
            dst = DsGridCreate(4, 2, "u16");
            t_copy_region_converts = (DsGridCopyRegion(dst, 0, 0, src, 0, 0, 3, 1) == 8 && DsGridGet(dst, 0, 0) == 1 && DsGridGet(dst, 1, 0) == 65535 && DsGridGet(dst, 2, 1) == 0) ? 1 : 0;
            t_copy_region_back_to_numbers = (DsGridCopyRegion(src, 1, 1, dst, 0, 0, 1, 0) == 2 && DsGridGet(src, 1, 1) == 1 && DsGridGet(src, 2, 1) == 65535) ? 1 : 0;
            s = DsGridCreate(5, 1, "u8");
            for (k = 0; k < 5; k = k + 1) { DsGridSet(s, k, 0, k + 1); }
            DsGridCopyRegion(s, 1, 0, s, 0, 0, 3, 0);
            t_overlapping_copy_in_a_row = (DsGridGet(s, 0, 0) == 1 && DsGridGet(s, 1, 0) == 1 && DsGridGet(s, 2, 0) == 2 && DsGridGet(s, 4, 0) == 4) ? 1 : 0;
            v = DsGridCreate(1, 4, "u16");
            for (k = 0; k < 4; k = k + 1) { DsGridSet(v, 0, k, k + 10); }
            DsGridCopyRegion(v, 0, 1, v, 0, 0, 0, 2);
            t_overlapping_copy_down_rows = (DsGridGet(v, 0, 0) == 10 && DsGridGet(v, 0, 1) == 10 && DsGridGet(v, 0, 2) == 11 && DsGridGet(v, 0, 3) == 12) ? 1 : 0;
            c = DsGridCreate(1, 1);
            DsGridCopy(c, b8);
            t_copy_takes_kind_and_size = (DsGridKind(c) == "u8" && DsGridWidth(c) == 4 && DsGridHeight(c) == 3 && DsGridGet(c, 3, 0) == 255) ? 1 : 0;
            DsGridSet(c, 3, 0, 1);
            t_copy_is_separate = (DsGridGet(b8, 3, 0) == 255) ? 1 : 0;
            DsGridResize(c, 6, 5);
            t_resize_keeps_kind_and_cells = (DsGridKind(c) == "u8" && DsGridWidth(c) == 6 && DsGridHeight(c) == 5 && DsGridGet(c, 3, 0) == 1 && DsGridGet(c, 0, 0) == 255 && DsGridGet(c, 5, 4) == 0) ? 1 : 0;
            a = DsGridCreate(2, 2, "u8"); DsGridClear(a, 200); f = DsGridCreate(2, 2); DsGridClear(f, 100);
            t_add_grid_clamps = (DsGridAddGrid(a, f, 1) == 4 && DsGridGet(a, 1, 1) == 255 && DsGridAddGrid(f, a, -1) == 4 && DsGridGet(f, 0, 0) == -155) ? 1 : 0;
            nf = DsGridCreate(16, 8, "u8");
            t_noise_fill_counts = (NoiseFillGrid(nf, 0, 0, 0.1, 5, 3, 2, 0.5, 127.5, 127.5) == 128) ? 1 : 0;
            same = 1;
            for (j = 0; j < 8; j = j + 1) { for (k = 0; k < 16; k = k + 1) {
                if (DsGridGet(nf, k, j) != Floor(127.5 + 127.5 * FractalNoise2D(k * 0.1, j * 0.1, 5, 3, 2, 0.5))) { same = 0; }
            } }
            t_noise_fill_keeps_whole_numbers = same;
            nf3 = DsGridCreate(8, 8, "f32");
            NoiseFillGrid3D(nf3, 2, 7.5, -4, 0.5, "xz", 9, 3, 2, 0.5, 1, 0);
            t_noise_fill_3d_single_precision = (Abs(DsGridGet(nf3, 3, 5) - FractalNoise3D(2 + 3 * 0.5, 7.5, -4 + 5 * 0.5, 9, 3, 2, 0.5)) < 0.000001) ? 1 : 0;
            big = DsGridCreate(4096, 256, "u16");
            t_a_256_high_strip_fits_in_u16 = (big != 0 && DsGridBytes(big) == 2097152 && DsGridCreate(4096, 256) == 0) ? 1 : 0;
            DsGridDestroy(big);
            huge = DsGridCreate(4096, 1900, "u8"); most = DsGridCreate(1000, 1000);
            t_limit_is_8_mb_of_cells = (huge != 0 && most != 0 && DsGridCreate(4096, 1000, "u16") == 0 && DsGridCreate(1000, 1001) == 0) ? 1 : 0;
            DsGridDestroy(huge); DsGridDestroy(most);
            """),
    ];

    /// <summary>
    /// Noise values recorded once: the noise commands must give exactly these numbers on every
    /// machine and in every later build (a generated world must not change under a player's feet).
    /// </summary>
    internal static readonly (string Call, Func<double> Value, double Expected)[] NoiseGolden =
    [
        ("Noise2D(0.5, 0.5, 1)", () => PgslCommands.Noise2D(0.5, 0.5, 1), -0.09974696520159272),
        ("Noise2D(-1234.25, 98765.5, 31337)", () => PgslCommands.Noise2D(-1234.25, 98765.5, 31337), -0.19039658728790923),
        ("Noise3D(1.25, -2.5, 3.75, 7)", () => PgslCommands.Noise3D(1.25, -2.5, 3.75, 7), 0.4426199302369703),
        ("ValueNoise2D(10.5, 20.25, 3)", () => PgslCommands.ValueNoise2D(10.5, 20.25, 3), 0.608684549061031),
        ("ValueNoise3D(4, 5, 6, 0.137)", () => PgslCommands.ValueNoise3D(4, 5, 6, 0.137), 0.7653131783933516),
        ("FractalNoise2D(100.3, -7.7, 42, 6, 2, 0.5)", () => PgslCommands.FractalNoise2D(100.3, -7.7, 42, 6, 2, 0.5), 0.35875553476842487),
        ("FractalNoise3D(0.1, 0.2, 0.3, 5, 4, 2.1, 0.45)", () => PgslCommands.FractalNoise3D(0.1, 0.2, 0.3, 5, 4, 2.1, 0.45), 0.05016823444985461),
        // Worked out from the documented hash (GameFeatures.md, Noise) outside the engine.
        ("Hash2(12, -34, 5)", () => PgslCommands.Hash2(12, -34, 5), 0.8856642354882706),
        ("Hash3(7, -2, 1000, 0.25)", () => PgslCommands.Hash3(7, -2, 1000, 0.25), 0.708995648141272),
        ("Hash2(0, 0, 0)", () => PgslCommands.Hash2(0, 0, 0), 0.07850153939724036),
    ];

    /// <summary>
    /// F5's script check (strict, as Run uses it) over the project named by GENESIS_VALIDATE_PROJECT:
    /// every error and warning goes to validate-project.txt beside the captures and to the log.
    /// </summary>
    internal static void ValidateProjectFromEnvironment(HeadlessContext ctx)
    {
        string path = Environment.GetEnvironmentVariable("GENESIS_VALIDATE_PROJECT") ?? string.Empty;
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.ValidateProject", () =>
        {
            HeadlessHarness.Assert(Directory.Exists(path), $"GENESIS_VALIDATE_PROJECT is not a folder: '{path}'.");
            string previous = PgslCommands.ProjectPath;
            try
            {
                PgslCommands.ProjectPath = path;
                ScriptAssetRegistry.ClearCache();
                // What loading the project's Scripts costs a game's start, and the Script call
                // rewrite over every .pgsl in the project matching the per-name rewrite it replaced.
                var load = System.Diagnostics.Stopwatch.StartNew();
                ScriptAssetRegistry.LoadFromProject(path);
                load.Stop();
                IReadOnlyList<string> scriptNames = ScriptAssetRegistry.GetScriptNames();
                int rewritten = 0;
                double rewriteMs = 0, referenceMs = 0;
                foreach (string file in Directory.EnumerateFiles(path, "*.pgsl", SearchOption.AllDirectories))
                {
                    string source = File.ReadAllText(file);
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    string now = ScriptCallSyntax.Apply(source, scriptNames);
                    rewriteMs += clock.Elapsed.TotalMilliseconds;
                    clock.Restart();
                    string before = ScriptCallSyntaxChecks.Reference(source, scriptNames);
                    referenceMs += clock.Elapsed.TotalMilliseconds;
                    HeadlessHarness.Assert(now == before, $"The Script call rewrite changed {Path.GetRelativePath(path, file)}.");
                    rewritten++;
                }
                string loadLine = $"Loading {scriptNames.Count} Scripts: {load.Elapsed.TotalMilliseconds:F0} ms. "
                    + $"Script call rewrite of {rewritten} files: {rewriteMs:F0} ms (the per-name rewrite: {referenceMs:F0} ms), same text.";
                Console.WriteLine(loadLine);
                ScriptAssetRegistry.ClearCache();
                PgslValidationReport report = PgslScriptValidator.ValidateProject(path, strict: true);
                var text = new StringBuilder().AppendLine(loadLine).AppendLine(report.Summary);
                foreach (string error in report.Errors) text.AppendLine("ERROR   " + error);
                foreach (string warning in report.Warnings) text.AppendLine("WARNING " + warning);
                Directory.CreateDirectory(ctx.Captures);
                File.WriteAllText(Path.Combine(ctx.Captures, "validate-project.txt"), text.ToString());
                Console.WriteLine(text.ToString());
                HeadlessHarness.Assert(report.Errors.Count == 0, $"{report.Errors.Count} error(s) in {report.ScriptCount} script(s).");
            }
            finally { PgslCommands.ProjectPath = previous; ScriptAssetRegistry.ClearCache(); }
        });
    }

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "PgslLogic");
        string parent = Path.Combine(ctx.Workspace, "PgslLogic");
        Directory.CreateDirectory(parent);
        ProjectSession project = new ProjectService().CreateProject(parent, "Logic" + Guid.NewGuid().ToString("N")[..6], "Blank");
        List<Row> rows = [];

        ObjectSandboxResult RunScript(string code, int frames = 1, Dictionary<string, string>? events = null)
        {
            string previous = PgslCommands.ProjectPath;
            try
            {
                PgslCommands.ProjectPath = project.RootPath;
                ScriptAssetRegistry.ClearCache();
                return ObjectSandbox.Run(events ?? new Dictionary<string, string> { ["Create"] = code }, frames);
            }
            finally { PgslCommands.ProjectPath = previous; ScriptAssetRegistry.ClearCache(); }
        }

        void Group(string group, string code)
        {
            HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic." + group, () =>
            {
                ObjectSandboxResult result = RunScript(code);
                string errors = string.Join(" | ", result.Errors);
                foreach ((string name, double value) in result.Numbers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (name.StartsWith("t_", StringComparison.Ordinal))
                        rows.Add(new Row(group, name[2..], value == 1 ? "PASS" : "FAIL", value.ToString(CultureInfo.InvariantCulture)));
                    else if (name.StartsWith("b_", StringComparison.Ordinal))
                        rows.Add(new Row(group, name[2..], "behaviour", value.ToString(CultureInfo.InvariantCulture)));
                }
                foreach ((string name, string value) in result.Strings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    if (name.StartsWith("s_", StringComparison.Ordinal))
                        rows.Add(new Row(group, name[2..], "behaviour", "\"" + value + "\""));
                List<string> failed = result.Numbers.Where(pair => pair.Key.StartsWith("t_", StringComparison.Ordinal) && pair.Value != 1)
                    .Select(pair => pair.Key[2..]).ToList();
                if (errors.Length > 0) rows.Add(new Row(group, "(script errors)", "FAIL", errors));
                HeadlessHarness.Assert(errors.Length == 0, $"The {group} script stopped: {errors}");
                HeadlessHarness.Assert(failed.Count == 0, $"{group} checks failed: {string.Join(", ", failed)}");
            });
        }







        foreach ((string name, string code) in LogicScripts) Group(name, code);

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.NoiseIsTheSameEverywhere", () =>
        {
            List<string> wrong = [];
            foreach ((string call, Func<double> value, double expected) in NoiseGolden)
            {
                double actual = value();
                bool same = BitConverter.DoubleToInt64Bits(actual) == BitConverter.DoubleToInt64Bits(expected);
                rows.Add(new Row("Noise values", call, same ? "PASS" : "FAIL", actual.ToString("R", CultureInfo.InvariantCulture)));
                if (!same) wrong.Add($"{call} = {actual.ToString("R", CultureInfo.InvariantCulture)}, recorded {expected.ToString("R", CultureInfo.InvariantCulture)}");
            }
            HeadlessHarness.Assert(wrong.Count == 0, "Noise gave other numbers than recorded: " + string.Join("; ", wrong));
        });

        // What noise costs (reported; only a generous ceiling is asserted, as the machine is shared).
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.NoiseSpeed", () =>
        {
            PgslContext context = new();
            PgslContext? previous = PgslCommands.BindContext(context);
            try
            {
                static double Best(Func<double> run)
                {
                    // Long enough for the runtime's optimised recompile, which this busy process postpones.
                    long warm = System.Diagnostics.Stopwatch.GetTimestamp();
                    while (System.Diagnostics.Stopwatch.GetElapsedTime(warm).TotalMilliseconds < 250) run();
                    Thread.Sleep(150);
                    double best = double.MaxValue;
                    for (int round = 0; round < 7; round++)
                    {
                        long start = System.Diagnostics.Stopwatch.GetTimestamp();
                        run();
                        best = Math.Min(best, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    }
                    return best;
                }
                const int samples = 200_000;
                double sink = 0;
                double noise2 = Best(() => { for (int i = 0; i < samples; i++) sink += PgslCommands.Noise2D(i * 0.37, i * 0.11, 5); return sink; }) * 1e6 / samples;
                double noise3 = Best(() => { for (int i = 0; i < samples; i++) sink += PgslCommands.Noise3D(i * 0.37, i * 0.11, i * 0.07, 5); return sink; }) * 1e6 / samples;
                double value2 = Best(() => { for (int i = 0; i < samples; i++) sink += PgslCommands.ValueNoise2D(i * 0.37, i * 0.11, 5); return sink; }) * 1e6 / samples;
                double hash2 = Best(() => { for (int i = 0; i < samples; i++) sink += PgslCommands.Hash2(i, i >> 3, 5); return sink; }) * 1e6 / samples;
                double hash3 = Best(() => { for (int i = 0; i < samples; i++) sink += PgslCommands.Hash3(i, i >> 3, i >> 5, 5); return sink; }) * 1e6 / samples;
                double grid = PgslCommands.DsGridCreate(64, 64);
                double fill1 = Best(() => { for (int i = 0; i < 10; i++) sink += PgslCommands.NoiseFillGrid(grid, i, 0, 0.05, 5, 1, 2, 0.5, 1, 0); return sink; }) * 1e6 / (10 * 64 * 64);
                double fill4 = Best(() => { for (int i = 0; i < 10; i++) sink += PgslCommands.NoiseFillGrid(grid, i, 0, 0.05, 5, 4, 2, 0.5, 1, 0); return sink; }) * 1e6 / (10 * 64 * 64);
                double fill3d = Best(() => { for (int i = 0; i < 10; i++) sink += PgslCommands.NoiseFillGrid3D(grid, i, 0, 3, 0.05, "xz", 5, 3, 2, 0.5, 1, 0); return sink; }) * 1e6 / (10 * 64 * 64);
                string F(double ns) => ns.ToString("F1", CultureInfo.InvariantCulture) + " ns";
                rows.Add(new Row("Noise speed", "Noise2D per sample (called from C#)", "behaviour", F(noise2)));
                rows.Add(new Row("Noise speed", "Noise3D per sample (called from C#)", "behaviour", F(noise3)));
                rows.Add(new Row("Noise speed", "ValueNoise2D per sample (called from C#)", "behaviour", F(value2)));
                rows.Add(new Row("Noise speed", "Hash2 per point (called from C#)", "behaviour", F(hash2)));
                rows.Add(new Row("Noise speed", "Hash3 per point (called from C#)", "behaviour", F(hash3)));
                rows.Add(new Row("Noise speed", "NoiseFillGrid per cell, 1 octave", "behaviour", F(fill1)));
                rows.Add(new Row("Noise speed", "NoiseFillGrid per cell, 4 octaves", "behaviour", F(fill4)));
                rows.Add(new Row("Noise speed", "NoiseFillGrid3D per cell, 3 octaves", "behaviour", F(fill3d)));
                Console.WriteLine($"Noise speed: Noise2D {F(noise2)}, Noise3D {F(noise3)}, ValueNoise2D {F(value2)}, Hash2 {F(hash2)}, Hash3 {F(hash3)}, fill 1 octave {F(fill1)}/cell, 4 octaves {F(fill4)}/cell, 3D slice 3 octaves {F(fill3d)}/cell ({sink:E1})");
                HeadlessHarness.Assert(noise2 < 2000 && fill4 < 8000, $"Noise is far slower than expected: {F(noise2)} a sample, {F(fill4)} a 4-octave cell.");
            }
            finally { PgslCommands.BindContext(previous); }
        });

        // F5 checks scripts with the Studio checker (PgslAstBuilder + PgslSemanticChecker) before
        // the game compiles them with its own parser: both must accept everything the game runs.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.StudioCheckAcceptsWhatTheGameRuns", () =>
        {
            (string Name, string Code)[] constructs =
            [
                .. LogicScripts,
                ("Property assignment", "Engine.Sky.AmbientScale = 1.5; Engine.Sky.Visibility = 520; t_ok = 1;"),
                ("Namespace block", "from Engine.Sky: { AmbientScale = 1.2; Visibility = 600; } t_ok = 1;"),
                ("self", "self.score = 3; t_ok = (self.score + score == 6) ? 1 : 0;"),
                ("List literals and index assignment", "var a = [1, 2, 3]; a[1] = 9; var g = [[1], [2]]; g[0][0] = 4; var e = []; t_ok = a[1] + g[0][0];"),
                ("Compound assignment", "n = 1; n += 2; n -= 1; n *= 3; n /= 2; t_ok = n;"),
                ("A built-in in any case", "sPeEd = 2; t_ok = (SPEED == 2) ? 1 : 0;"),
                ("Worker jobs", """
                    j = JobScriptCreate("NoSuchFunction"); g = DsGridCreate(2, 2); l = DsListCreate();
                    JobScriptGrid(j, g, true); JobScriptList(j, l, false); JobScriptBudget(j, 5000);
                    started = JobScriptStart(j, g, 1, "x");
                    k = JobRunScript("NoSuchFunction", 1, 2);
                    t_ok = (JobTake(j) == 0 && k == 0 && started == 0 && JobStatus(j) == "invalid") ? 1 : 0;
                    """),
            ];
            var refused = new List<string>();
            string previous = PgslCommands.ProjectPath;
            // Each construct as an Object's Create event of a project of its own, checked the way F5
            // checks a project (strict, every script).
            string checkRoot = Path.Combine(parent, "StudioCheck" + Guid.NewGuid().ToString("N")[..6]);
            string objectsRoot = Path.Combine(checkRoot, "Assets", "Objects");
            Directory.CreateDirectory(objectsRoot);
            try
            {
                PgslCommands.ProjectPath = project.RootPath;
                int index = 0;
                foreach ((string name, string code) in constructs)
                {
                    string objectName = "Check" + index++;
                    File.WriteAllText(Path.Combine(objectsRoot, objectName + ".object.json"), "{}");
                    Directory.CreateDirectory(Path.Combine(objectsRoot, objectName));
                    File.WriteAllText(Path.Combine(objectsRoot, objectName, "Create.pgsl"), code);

                    string gameError = "";
                    try { if (Genesis.Runtime.Scripting.VM.VMEngine.Compile(code) == null) gameError = "no result"; }
                    catch (Exception exception) { gameError = exception.Message; }
                    HeadlessHarness.Assert(gameError.Length == 0, $"The game's compiler refused the {name} script: {gameError}");

                    try { PgslAstBuilder.Parse(code); }
                    catch (Exception exception) { refused.Add($"{name}: the Studio parser refused it ({exception.Message})"); }
                }
                PgslValidationReport report = PgslScriptValidator.ValidateProject(checkRoot, strict: true);
                refused.AddRange(report.Errors);
                rows.Add(new Row("Studio check", $"{constructs.Length} scripts checked as F5 does",
                    report.Errors.Count == 0 ? "PASS" : "FAIL",
                    report.Errors.Count == 0 ? $"{report.Warnings.Count} warning(s)" : string.Join(" | ", report.Errors)));
            }
            finally
            {
                PgslCommands.ProjectPath = previous;
                try { Directory.Delete(checkRoot, recursive: true); } catch (IOException) { }
            }
            HeadlessHarness.Assert(refused.Count == 0, "F5's script check refused what the game runs:\n" + string.Join("\n", refused));
        });

        // How each kind of mistake is reported: the script's error, or the value it got instead.
        (string Name, string Code)[] mistakes =
        [
            ("DsListAdd given text", "l = DsListCreate(); DsListAdd(l, \"x\"); after = 1;"),
            ("DsListGet on a list that does not exist", "v = DsListGet(424242, 0); after = 1;"),
            ("DsMapGet on a missing key", "m = DsMapCreate(); v = DsMapGet(m, \"nope\"); after = 1;"),
            ("StringCopy past the end", "s = StringCopy(\"abc\", 10, 5); after = 1;"),
            ("Real of text that is not a number", "v = Real(\"abc\"); after = 1;"),
            ("Sqrt of a negative number", "v = Sqrt(-1); after = 1;"),
            ("Division by zero", "v = 1 / 0; after = 1;"),
            ("JsonDecode with an unknown kind", "v = JsonDecode(\"{}\", \"tree\"); after = 1;"),
            ("Reading a variable never set", "v = neverSetAnywhere + 1; after = 1;"),
            ("Calling a command that does not exist", "NoSuchCommand(1); after = 1;"),
            ("A list read past its end", "var a = [1, 2]; v = a[5]; after = 1;"),
            ("Calling a command with too few arguments", "v = Clamp(1); after = 1;"),
            ("A syntax error", "v = (1 + ; after = 1;"),
            ("from used as a variable name", "from = 1; after = 1;"),
            ("break outside a loop", "break; after = 1;"),
            ("Recursion 5000 deep", "function Down(n) { if (n <= 0) { return 0; } return 1 + Down(n - 1); } v = Down(5000); after = 1;"),
            ("An endless loop at event level", "k = 0; while (1) { k = k + 1; } after = 1;"),
            ("An endless loop in a function", "function Spin() { var k = 0; while (1) { k = k + 1; } return k; } v = Spin(); after = 1;"),
            ("Three heavy calls in one event (instruction budget per call?)",
                "function Heavy() { var k = 0; while (k < 9000) { k = k + 1; } return k; } a = Heavy(); b = Heavy(); c = Heavy(); d = Heavy(); after = 1;"),
        ];
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.MistakesAreReported", () =>
        {
            foreach ((string name, string code) in mistakes)
            {
                ObjectSandboxResult result = RunScript(code);
                string errors = string.Join(" | ", result.Errors);
                bool continued = result.Numbers.TryGetValue("after", out double after) && after == 1;
                string value = result.Numbers.TryGetValue("v", out double v) ? v.ToString(CultureInfo.InvariantCulture)
                    : result.Strings.TryGetValue("s", out string? s) ? "\"" + s + "\"" : "-";
                rows.Add(new Row("Mistakes", name, errors.Length > 0 ? "error" : continued ? "no error" : "stopped",
                    errors.Length > 0 ? errors : "value " + value));
                if (name == "DsListAdd given text")
                    HeadlessHarness.Assert(errors.Contains("DsListAddString", StringComparison.Ordinal),
                        $"Giving DsListAdd text did not point to DsListAddString: {errors}");
                if (name == "A list read past its end")
                    HeadlessHarness.Assert(errors.Contains("it has 2 entries, 0 to 1", StringComparison.Ordinal),
                        $"Reading past a list's end did not say how long the list is: {errors}");
                if (name == "Recursion 5000 deep")
                    HeadlessHarness.Assert(errors.Contains("Too much recursion", StringComparison.Ordinal),
                        $"Deep recursion did not end in a recursion error: {errors}");
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.ALibraryThatDoesNotCompileSaysWhy", () =>
        {
            string scripts = Path.Combine(project.RootPath, "Assets", "Scripts");
            Directory.CreateDirectory(scripts);
            string broken = Path.Combine(scripts, "BrokenLib.pgsl");
            File.WriteAllText(broken, "function Helper() {\n    from = 1;\n    return 2;\n}\n");
            string previous = PgslCommands.ProjectPath;
            try
            {
                PgslCommands.ProjectPath = project.RootPath;
                ScriptAssetRegistry.LoadFromProject(project.RootPath);
                string load = string.Join("; ", ScriptAssetRegistry.LoadErrors.Values);
                ObjectSandboxResult result = ObjectSandbox.Run(new Dictionary<string, string> { ["Create"] = "v = Helper();" }, 1);
                string errors = string.Join(" | ", result.Errors);
                rows.Add(new Row("Mistakes", "Calling a function of a library that did not compile", "error", errors));
                HeadlessHarness.Assert(load.Contains("BrokenLib.pgsl", StringComparison.Ordinal) && load.Contains("line 2", StringComparison.Ordinal),
                    $"The library's compile error does not name its file and line: '{load}'.");
                HeadlessHarness.Assert(errors.Contains("BrokenLib.pgsl", StringComparison.Ordinal),
                    $"Calling its function did not say the library failed to compile: '{errors}'.");
            }
            finally
            {
                PgslCommands.ProjectPath = previous;
                File.Delete(broken);
                ScriptAssetRegistry.LoadFromProject(project.RootPath);
                ScriptAssetRegistry.ClearCache();
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.EventOrder", () =>
        {
            ObjectSandboxResult result = RunScript("", frames: 2, events: new Dictionary<string, string>
            {
                ["Create"] = "order = \"C\";",
                ["Step"] = "order = order + \"S\";",
                ["Draw"] = "order = order + \"D\";",
                ["DrawGui"] = "order = order + \"G\";",
            });
            string order = result.Strings.GetValueOrDefault("order", "");
            rows.Add(new Row("Events", "order over two frames", order == "CSDGSDG" ? "PASS" : "FAIL", "\"" + order + "\""));
            HeadlessHarness.Assert(order == "CSDGSDG", $"Events ran as {order}, not Create then Step, Draw, Draw GUI each frame.");
        });

        PgslWorkerJobChecks.Run(ctx, (group, name, result, detail) => rows.Add(new Row(group, name, result, detail)));

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.EveryCommandIsCallable", () =>
        {
            PgslCommandTestReport sweep = PgslCommandAutoTester.Run(repeats: 1);
            foreach (PgslCommandTestResult command in sweep.Results.OrderBy(r => r.Category).ThenBy(r => r.Name))
                rows.Add(new Row("Commands: " + command.Category, command.Name,
                    command.Outcome.ToString(), command.Outcome == PgslCommandOutcome.Callable ? "" : command.Detail));
            HeadlessHarness.Assert(sweep.Failed == 0, $"{sweep.Failed} commands threw when called with dummy arguments: "
                + string.Join(", ", sweep.Results.Where(r => r.Outcome == PgslCommandOutcome.Threw).Select(r => r.Name)));
        });

        var report = new StringBuilder();
        report.AppendLine("# PGSL logic test");
        report.AppendLine();
        foreach (IGrouping<string, Row> group in rows.GroupBy(row => row.Group.StartsWith("Commands", StringComparison.Ordinal) ? "Commands" : row.Group))
        {
            if (group.Key == "Commands")
            {
                report.AppendLine("## Every registered command, called with dummy arguments");
                report.AppendLine();
                foreach (IGrouping<string, Row> outcome in group.GroupBy(row => row.Result))
                    report.AppendLine($"- {outcome.Key}: {outcome.Count()}");
                report.AppendLine();
                report.AppendLine("| Category | Command | Outcome | Detail |");
                report.AppendLine("|---|---|---|---|");
                foreach (Row row in group.Where(row => row.Result != "Callable"))
                    report.AppendLine($"| {row.Group[10..]} | {row.Name} | {row.Result} | {Escape(row.Detail)} |");
                report.AppendLine();
                continue;
            }
            report.AppendLine("## " + group.Key);
            report.AppendLine();
            report.AppendLine("| Check | Result | Value or error |");
            report.AppendLine("|---|---|---|");
            foreach (Row row in group) report.AppendLine($"| {row.Name.Replace('_', ' ')} | {row.Result} | {Escape(row.Detail)} |");
            report.AppendLine();
        }
        File.WriteAllText(Path.Combine(ctx.Captures, "pgsl-logic.md"), report.ToString());
    }

    private static string Escape(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
