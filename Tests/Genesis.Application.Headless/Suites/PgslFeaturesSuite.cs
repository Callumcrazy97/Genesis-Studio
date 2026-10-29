using System.Globalization;
using Genesis.Runtime.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The PGSL features added since the language notes were first written, each asserted rather than
/// reported: strings counted from 1, <c>DsListInsert</c> taking text as well as numbers, numbers added
/// from one text (<c>DsListAddNumbers</c>), <c>Choose</c> with its random draw, increments in
/// <c>for</c> clauses, typed grids across the kinds, and what a <c>var</c> in an event belongs to.
/// The same scripts through the compiled tier are in <see cref="PgslTierSuite"/>.
/// </summary>
internal static class PgslFeaturesSuite
{
    private static ObjectSandboxResult Create(string code, int frames = 1, Dictionary<string, string>? events = null) =>
        ObjectSandbox.Run(events ?? new Dictionary<string, string> { ["Create"] = code }, frames);

    private static string Errors(ObjectSandboxResult result) => string.Join(" | ", result.Errors.Select(error => error.Message));

    private static double Number(ObjectSandboxResult result, string name) =>
        result.Numbers.TryGetValue(name, out double value) ? value : double.NaN;

    private static string Text(ObjectSandboxResult result, string name) =>
        result.Strings.TryGetValue(name, out string? value) ? value : "(not set)";

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "PgslFeatures");

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Strings.PositionsAndIndexesCountFromOne", () =>
        {
            ObjectSandboxResult r = Create("""
                first = StringPos("h", "hello"); inner = StringPos("l", "hello"); absent = StringPos("z", "hello"); last = StringLastPos("l", "hello");
                s_copyFromOne = StringCopy("hello", 1, 3); s_copyFromTwo = StringCopy("hello", 2, 2); s_copyTooLong = StringCopy("hello", 4, 99);
                s_firstChar = StringCharAt("abc", 1); s_lastChar = StringCharAt("abc", 3); s_charZero = StringCharAt("abc", 0); s_charPast = StringCharAt("abc", 4);
                ordFirst = StringOrdAt("AB", 1); ordSecond = StringOrdAt("AB", 2); ordZero = StringOrdAt("AB", 0);
                s_partFirst = StringSplitPart("a,b,c", ",", 1); s_partLast = StringSplitPart("a,b,c", ",", 3); s_partZero = StringSplitPart("a,b,c", ",", 0); parts = StringSplitCount("a,b,c", ",");
                s_insertFront = StringInsert("XY", "abc", 1); s_insertMiddle = StringInsert("XY", "abc", 3); s_insertEnd = StringInsert("XY", "abc", 4);
                s_deleteFront = StringDelete("hello", 1, 2); s_deleteMiddle = StringDelete("hello", 2, 3);
                counted = StringCount("l", "hello"); s_replaceFirst = StringReplace("a-b-c", "-", "+"); s_replaceAll = StringReplaceAll("a-b-c", "-", "+");
                """);
            HeadlessHarness.Assert(r.Ok, "The strings script reported: " + Errors(r));
            var wrong = new List<string>();
            void Is(string name, double expected) { if (Number(r, name) != expected) wrong.Add($"{name} = {Number(r, name)} (expected {expected})"); }
            void IsText(string name, string expected) { if (Text(r, name) != expected) wrong.Add($"{name} = \"{Text(r, name)}\" (expected \"{expected}\")"); }
            Is("first", 1); Is("inner", 3); Is("absent", 0); Is("last", 4);
            IsText("s_copyFromOne", "hel"); IsText("s_copyFromTwo", "el"); IsText("s_copyTooLong", "lo");
            IsText("s_firstChar", "a"); IsText("s_lastChar", "c"); IsText("s_charZero", ""); IsText("s_charPast", "");
            Is("ordFirst", 65); Is("ordSecond", 66); Is("ordZero", 0);
            IsText("s_partFirst", "a"); IsText("s_partLast", "c"); IsText("s_partZero", ""); Is("parts", 3);
            IsText("s_insertFront", "XYabc"); IsText("s_insertMiddle", "abXYc"); IsText("s_insertEnd", "abcXY");
            IsText("s_deleteFront", "llo"); IsText("s_deleteMiddle", "ho");
            Is("counted", 2); IsText("s_replaceFirst", "a+b-c"); IsText("s_replaceAll", "a+b+c");
            HeadlessHarness.Assert(wrong.Count == 0, "String positions are not counted from 1: " + string.Join("; ", wrong));
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Lists.InsertTakesNumbersAndTextAndKeepsTheOrder", () =>
        {
            ObjectSandboxResult r = Create("""
                l = DsListCreate();
                DsListAdd(l, 1); DsListAdd(l, 3);
                DsListInsert(l, 1, 2);
                DsListInsert(l, 0, "first");
                DsListInsert(l, 99, "last");
                DsListInsert(l, -5, 7);
                size = DsListSize(l);
                s_0 = DsListGetString(l, 0); s_1 = DsListGetString(l, 1); s_2 = DsListGetString(l, 2); s_5 = DsListGetString(l, 5);
                n_0 = DsListGet(l, 0); n_2 = DsListGet(l, 2); n_3 = DsListGet(l, 3); n_4 = DsListGet(l, 4);
                textAsNumber = DsListGet(l, 1);
                DsListSetString(l, 1, "over"); s_over = DsListGetString(l, 1);
                total = DsListSum(l);
                gone = DsListSize(424242); DsListInsert(424242, 0, "nothing"); still = DsListSize(424242);
                """);
            HeadlessHarness.Assert(r.Ok, "The list script reported: " + Errors(r));
            // [1,3] -> [1,2,3] -> [first,1,2,3] -> [first,1,2,3,last] -> [7,first,1,2,3,last]
            bool pass = Number(r, "size") == 6 && Text(r, "s_0") == "7" && Text(r, "s_1") == "first" && Text(r, "s_2") == "1" && Text(r, "s_5") == "last"
                && Number(r, "n_0") == 7 && Number(r, "n_2") == 1 && Number(r, "n_3") == 2 && Number(r, "n_4") == 3 && Number(r, "textAsNumber") == 0
                && Text(r, "s_over") == "over" && Number(r, "total") == 13 && Number(r, "gone") == 0 && Number(r, "still") == 0;
            HeadlessHarness.Assert(pass, "DsListInsert gave: " + string.Join(", ", r.Numbers.Where(pair => !pair.Key.StartsWith("__", StringComparison.Ordinal)).Select(pair => pair.Key + "=" + pair.Value))
                + " / " + string.Join(", ", r.Strings.Select(pair => pair.Key + "=\"" + pair.Value + "\"")));
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Lists.NumbersFromOneTextMatchNumbersAddedOneByOne", () =>
        {
            ObjectSandboxResult r = Create("""
                a = DsListCreate(); b = DsListCreate(); c = NumListCreate(); d = NumListCreate();
                DsListAddNumbers(a, "0.5, -2.25; 3e2  4\n7");
                DsListAdd(b, 0.5); DsListAdd(b, -2.25); DsListAdd(b, 300); DsListAdd(b, 4); DsListAdd(b, 7);
                NumListAddNumbers(c, "0.5, -2.25; 3e2  4\n7");
                NumListAdd(d, 0.5); NumListAdd(d, -2.25); NumListAdd(d, 300); NumListAdd(d, 4); NumListAdd(d, 7);
                sizeA = DsListSize(a); sizeB = DsListSize(b); sizeC = NumListSize(c); sizeD = NumListSize(d);
                same = 1;
                for (var i = 0; i < 5; i++) {
                    if (DsListGet(a, i) != DsListGet(b, i) || NumListGet(c, i) != NumListGet(d, i) || DsListGet(a, i) != NumListGet(c, i)) { same = 0; }
                }
                sumA = DsListSum(a); sumC = NumListSum(c, 0, 5);
                stopsAtText = DsListAddNumbers(a, "9, x, 8"); sizeAfter = DsListSize(a);
                """);
            HeadlessHarness.Assert(r.Ok, "The number list script reported: " + Errors(r));
            bool pass = Number(r, "sizeA") == 5 && Number(r, "sizeB") == 5 && Number(r, "sizeC") == 5 && Number(r, "sizeD") == 5 && Number(r, "same") == 1
                && Math.Abs(Number(r, "sumA") - 309.25) < 1e-9 && Math.Abs(Number(r, "sumC") - 309.25) < 1e-9 && Number(r, "stopsAtText") == 1 && Number(r, "sizeAfter") == 6;
            HeadlessHarness.Assert(pass, string.Join(", ", r.Numbers.Where(pair => pair.Key.StartsWith("size", StringComparison.Ordinal) || pair.Key is "same" or "sumA" or "sumC" or "stopsAtText").Select(pair => pair.Key + "=" + pair.Value)));
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Choose.DrawsEveryOptionAndNothingElseAndRepeatsWithASeed", () =>
        {
            const string draw = """
                RandomSeed(11);
                c1 = 0; c2 = 0; c3 = 0; c4 = 0; c5 = 0; other = 0; sequence = "";
                for (var i = 0; i < 2000; i++) {
                    var p = Choose(1, 2, 3, 4, 5);
                    if (p == 1) { c1++; } else if (p == 2) { c2++; } else if (p == 3) { c3++; } else if (p == 4) { c4++; } else if (p == 5) { c5++; } else { other++; }
                    if (i < 12) { sequence = sequence + p; }
                }
                single = Choose(9);
                word = Choose("yes", "yes", "yes");
                mixed = 0; var m = Choose("a", 2); if (m == "a" || m == 2) { mixed = 1; }
                """;
            ObjectSandboxResult first = Create(draw), second = Create(draw);
            HeadlessHarness.Assert(first.Ok && second.Ok, "The Choose script reported: " + Errors(first) + Errors(second));
            string[] countNames = ["c1", "c2", "c3", "c4", "c5"];
            double[] counts = countNames.Select(name => Number(first, name)).ToArray();
            HeadlessHarness.Assert(Number(first, "other") == 0 && counts.Sum() == 2000, $"Choose gave something that was not an option, or lost draws: {string.Join(",", counts)} and {Number(first, "other")} others.");
            HeadlessHarness.Assert(counts.All(count => count > 2000 * 0.12 && count < 2000 * 0.28), "Choose does not draw the options about equally often: " + string.Join(",", counts));
            HeadlessHarness.Assert(Text(first, "sequence") == Text(second, "sequence") && counts.SequenceEqual(countNames.Select(name => Number(second, name))),
                $"The same seed gave other draws: '{Text(first, "sequence")}' and '{Text(second, "sequence")}'.");
            HeadlessHarness.Assert(Number(first, "single") == 9 && Text(first, "word") == "yes" && Number(first, "mixed") == 1,
                $"Choose with one option, with text and with mixed options gave {Number(first, "single")}, '{Text(first, "word")}', {Number(first, "mixed")}.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.ForLoops.StepsWithIncrementsDecrementsAndCompoundAssignments", () =>
        {
            ObjectSandboxResult r = Create("""
                up = 0; for (var i = 0; i < 5; i++) { up += i; }
                down = 0; for (var j = 5; j > 0; j--) { down += j; }
                skip = 0; for (var k = 10; k > 0; k -= 3) { skip++; }
                grow = 0; for (var h = 1; h < 100; h *= 3) { grow++; }
                plus = 0; for (var g = 0; g < 10; g += 4) { plus += g; }
                plain = 0; for (var n = 0; n < 4; n = n + 2) { plain++; }
                nested = 0; for (var a = 0; a < 3; a++) { for (var b = a; b < 3; b++) { nested++; } }
                afterLoop = i;
                counter = 0; counter++; counter++; counter--;
                """);
            HeadlessHarness.Assert(r.Ok, "The loops script reported: " + Errors(r));
            bool pass = Number(r, "up") == 10 && Number(r, "down") == 15 && Number(r, "skip") == 4 && Number(r, "grow") == 5 && Number(r, "plus") == 12
                && Number(r, "plain") == 2 && Number(r, "nested") == 6 && Number(r, "afterLoop") == 5 && Number(r, "counter") == 1;
            HeadlessHarness.Assert(pass, "for-loop steps gave: " + string.Join(", ", new[] { "up", "down", "skip", "grow", "plus", "plain", "nested", "afterLoop", "counter" }.Select(name => name + "=" + Number(r, name)))
                + " (expected 10, 15, 4, 5, 12, 2, 6, 5, 1)");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.TypedGrids.EachKindKeepsItsRangeAndCopiesBetweenKinds", () =>
        {
            ObjectSandboxResult r = Create("""
                u8 = DsGridCreate(4, 2, "u8"); u16 = DsGridCreate(4, 2, "u16"); i32 = DsGridCreate(4, 2, "i32"); f32 = DsGridCreate(4, 2, "f32"); f64 = DsGridCreate(4, 2);
                DsGridSet(u8, 0, 0, 300); DsGridSet(u8, 1, 0, -5); DsGridSet(u8, 2, 0, 7.9);
                DsGridSet(u16, 0, 0, 70000); DsGridSet(u16, 1, 0, 1234);
                DsGridSet(i32, 0, 0, -123456789); DsGridSet(i32, 1, 0, 3.99);
                DsGridSet(f32, 0, 0, 0.1); DsGridSet(f64, 0, 0, 0.1);
                a_u8 = DsGridGet(u8, 0, 0); b_u8 = DsGridGet(u8, 1, 0); c_u8 = DsGridGet(u8, 2, 0);
                a_u16 = DsGridGet(u16, 0, 0); b_u16 = DsGridGet(u16, 1, 0);
                a_i32 = DsGridGet(i32, 0, 0); b_i32 = DsGridGet(i32, 1, 0);
                single = DsGridGet(f32, 0, 0); double64 = DsGridGet(f64, 0, 0);
                s_kinds = DsGridKind(u8) + DsGridKind(u16) + DsGridKind(i32) + DsGridKind(f32) + DsGridKind(f64);
                bytes8 = DsGridBytes(u8); bytes64 = DsGridBytes(f64);
                copied = DsGridCopyRegion(u8, 0, 1, u16, 0, 0, 3, 0);
                c_0 = DsGridGet(u8, 0, 1); c_1 = DsGridGet(u8, 1, 1);
                other = DsGridCreate(2, 2, "u12"); outside = DsGridGet(u8, 9, 9);
                """);
            HeadlessHarness.Assert(r.Ok, "The grids script reported: " + Errors(r));
            bool pass = Number(r, "a_u8") == 255 && Number(r, "b_u8") == 0 && Number(r, "c_u8") == 7
                && Number(r, "a_u16") == 65535 && Number(r, "b_u16") == 1234 && Number(r, "a_i32") == -123456789 && Number(r, "b_i32") == 3
                && Math.Abs(Number(r, "single") - 0.1) < 1e-7 && Number(r, "single") != 0.1 && Number(r, "double64") == 0.1
                && Text(r, "s_kinds") == "u8u16i32f32f64" && Number(r, "bytes8") == 8 && Number(r, "bytes64") == 64
                && Number(r, "copied") == 4 && Number(r, "c_0") == 255 && Number(r, "c_1") == 255 && Number(r, "other") == 0 && Number(r, "outside") == 0;
            HeadlessHarness.Assert(pass, "Typed grids gave: " + string.Join(", ", r.Numbers.Where(pair => !pair.Key.StartsWith("__", StringComparison.Ordinal)).Select(pair => pair.Key + "=" + pair.Value.ToString("R", CultureInfo.InvariantCulture)))
                + " / " + Text(r, "s_kinds"));
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.EventLocals.AVarInAnEventBelongsToTheInstanceTodayAndAFunctionsOwnDoesNot", () =>
        {
            // The plan (UnityParityPlan ST3) has Cal deciding whether a top-level var should become a
            // local of its event; until then PgslLanguage.md says it is the instance's, kept for later
            // events, and this case holds to that.
            ObjectSandboxResult r = Create("", frames: 3, events: new Dictionary<string, string>
            {
                ["Create"] = "var hp = 5; plain = 7; function Shadow() { var hp = 1; hp = 2; return hp; } shadowed = Shadow(); function Peek() { return hp; } peeked = Peek();",
                ["Step"] = "hp += 1; plain += 1;",
            });
            HeadlessHarness.Assert(r.Ok, "The events reported: " + Errors(r));
            bool pass = Number(r, "hp") == 8 && Number(r, "plain") == 10 && Number(r, "shadowed") == 2 && Number(r, "peeked") == 5;
            HeadlessHarness.Assert(pass, $"hp={Number(r, "hp")} (8), plain={Number(r, "plain")} (10), shadowed={Number(r, "shadowed")} (2), peeked={Number(r, "peeked")} (5).");
        });
    }
}
