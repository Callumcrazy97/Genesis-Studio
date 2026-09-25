using Genesis.Application.Headless.Suites;

int passed = 0, failed = 0;
void Check(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + Environment.NewLine + error); }
}
StudioFoundationCoreCases.Run(Check);
StudioPolishCoreCases.Run(Check);
ResourceLibraryCoreCases.Run(Check);
ResourceTagsCoreCases.Run(Check);
ParticleClockCoreCases.Run(Check);
Console.WriteLine($"Studio core: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;
