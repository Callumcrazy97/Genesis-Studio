using System.Diagnostics;
using Genesis.Runtime;
using Genesis.Runtime.Project;

namespace Genesis.Application.Headless;

internal static class RunnerLifecycleRunner
{
    private static Dictionary<string,string> EnvironmentForTest() => new()
    {
        ["GENESIS_RENDER_BACKEND"] = "Direct3D11", ["GENESIS_UNATTENDED_WINDOW"] = "1",
    };

    public static int Run(string project)
    {
        try
        {
            var launch = ProjectRunLauncher.Launch(project, supervised:true, extraEnvironment:EnvironmentForTest());
            if (!launch.Success || launch.Session == null) throw new InvalidOperationException(launch.ErrorMessage);
            using var session = launch.Session;
            Wait(()=>session.ConfirmedState=="running", "Player did not finish starting.");
            session.Pause(); Wait(()=>session.ConfirmedState=="paused", "Player did not acknowledge a real pause.");
            session.Resume(); Wait(()=>session.ConfirmedState=="running", "Player did not acknowledge resume.");
            session.Dispose(); Wait(()=>!session.IsRunning,"Stop left the player running.");
            Console.WriteLine("PASS Runner.StartPauseResumeStop");

            string activeRoom = ProjectRoomResolver.ResolveRoomFile(project,ProjectRoomResolver.ResolveRoomName(project,null));
            using (var roomEditor = new Genesis.Application.Editors.Suite.Rooms.RoomEditorControl(activeRoom,project))
            using (var host = new System.Windows.Forms.Form())
            {
                host.Controls.Add(roomEditor); UnattendedWindowing.Configure(host); UnattendedWindowing.ShowWithoutFocus(host);
                var play = roomEditor.EditorToolbar.Strip.Items.OfType<System.Windows.Forms.ToolStripSplitButton>().Single();
                play.PerformButtonClick();
                var roomSession = (ProjectRunSession?)typeof(Genesis.Application.Editors.Suite.Rooms.RoomEditorControl)
                    .GetField("_roomPlayer",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(roomEditor);
                if(roomSession==null) throw new InvalidOperationException("Primary Room Play did not launch gameplay.");
                Wait(()=>roomSession.ConfirmedState=="running","Room Player failed to start.");
                roomEditor.EditorToolbar.Strip.Items.OfType<System.Windows.Forms.ToolStripButton>().Single(button=>button.Text?.Contains("Pause")==true).PerformClick();
                Wait(()=>roomSession.ConfirmedState=="paused","Room Pause did not pause gameplay.");
                play.PerformButtonClick(); Wait(()=>roomSession.ConfirmedState=="running","Room Resume did not resume gameplay.");
                roomEditor.EditorToolbar.Strip.Items.OfType<System.Windows.Forms.ToolStripButton>().Single(button=>button.Text?.Contains("Stop")==true).PerformClick();
                Wait(()=>!roomSession.IsRunning,"Room Stop left the player running.");
                host.Close();
                Console.WriteLine("PASS Runner.RoomToolbarPlayPauseResumeStop");
            }

            string room = ProjectRoomResolver.ResolveRoomFile(project,ProjectRoomResolver.ResolveRoomName(project,null));
            string original = File.ReadAllText(room);
            var failure = new TaskCompletionSource<(int Code,string Details)>();
            try
            {
                File.WriteAllText(room,"{ invalid room JSON");
                var failed = ProjectRunLauncher.Launch(project,supervised:true,extraEnvironment:EnvironmentForTest(),
                    exited:(code,details)=>failure.TrySetResult((code,details)));
                using var failedSession = failed.Session;
                Wait(()=>failure.Task.IsCompleted,"Startup failure was not delivered to the editor.");
                var result = failure.Task.Result;
                if (result.Code==0 || !result.Details.Contains("Json")) throw new InvalidOperationException("Missing managed exception diagnostics.");
                Console.WriteLine("PASS Runner.StartupErrorDelivered");
            }
            finally { File.WriteAllText(room,original); }

            string pidFile = Path.Combine(project,"orphan-player.pid");
            var info = new ProcessStartInfo(System.Environment.ProcessPath!) { UseShellExecute=false, CreateNoWindow=true };
            info.ArgumentList.Add("--runner-parent-fixture"); info.ArgumentList.Add(project); info.ArgumentList.Add(pidFile);
            using var parent = Process.Start(info)!;
            if (!parent.WaitForExit(15000) || parent.ExitCode!=0) throw new InvalidOperationException("Parent fixture failed.");
            int pid = int.Parse(File.ReadAllText(pidFile));
            Wait(()=>!Exists(pid),"The editor's player survived its parent's exit.");
            Console.WriteLine("PASS Runner.ParentExitCleansPlayer");
            return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    public static int ParentFixture(string project,string pidFile)
    {
        var launch = ProjectRunLauncher.Launch(project,supervised:true,extraEnvironment:EnvironmentForTest());
        if (!launch.Success) return 1;
        File.WriteAllText(pidFile,launch.Process.Id.ToString());
        // Deliberately bypass disposal, reproducing a disappearing/crashed editor.
        System.Environment.Exit(0);
        return 0;
    }

    private static bool Exists(int pid)
    {
        try { using var process=Process.GetProcessById(pid); return !process.HasExited; }
        catch(ArgumentException) { return false; }
    }
    private static void Wait(Func<bool> condition,string failure)
    {
        var clock=Stopwatch.StartNew();
        while(!condition()) { if(clock.ElapsedMilliseconds>20000) throw new InvalidOperationException(failure); System.Windows.Forms.Application.DoEvents(); Thread.Sleep(30); }
    }
}
