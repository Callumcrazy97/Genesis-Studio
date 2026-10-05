using System.Globalization;
using System.Text;
using Genesis.Application.Core.Projects;
using Genesis.Runtime.Scripting;

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
    ];

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
