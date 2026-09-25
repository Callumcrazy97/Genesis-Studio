using System.Runtime.CompilerServices;

// Lets the headless test host drive a handful of shell actions (e.g. the F5/Run
// room-count guard) directly instead of simulating keystrokes/menu clicks.
[assembly: InternalsVisibleTo("Genesis.Application.Headless")]
