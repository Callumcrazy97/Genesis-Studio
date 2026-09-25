using System.Reflection;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Image.Rigging;

namespace Genesis.Application.Headless;

internal static class RigReviewCaptureRunner
{
    public static int Run(string imagePath,string output)
    {
        Directory.CreateDirectory(output);
        var document=ImageDocumentSerializer.LoadAtomic(imagePath).Document;
        var rig=PixelRigRasterizer.Copy(document.PixelRigs.First());
        foreach(var pose in rig.Poses)
        {
            rig.FillJointGaps=false;var before=new PixelRigRasterizer(rig).Render(pose.Bones,poseJoints:pose.Joints);
            rig.FillJointGaps=true;var after=new PixelRigRasterizer(rig).Render(pose.Bones,poseJoints:pose.Joints);
            string key=rig.Poses.IndexOf(pose).ToString();
            ImageWorkspaceStorage.WritePng(Path.Combine(output,$"pose-{key}-before.png"),rig.Width,rig.Height,before);
            ImageWorkspaceStorage.WritePng(Path.Combine(output,$"pose-{key}-infill.png"),rig.Width,rig.Height,after);
            int filled=Enumerable.Range(0,before.Length/4).Count(i=>before[i*4+3]==0&&after[i*4+3]>0);
            Console.WriteLine($"Pose {pose.Name}: {filled} joint-gap pixels filled.");
        }
        foreach(var size in new[]{new System.Drawing.Size(1280,850),new System.Drawing.Size(900,660)})
        {
            using var dialog=new PixelRigStudioDialog([rig],rig.BindPixels,rig.Width,rig.Height,rig.LayerId,rig.SourceFrameId,_=>{},_=>{},_=>{},(_,_,_)=>Task.CompletedTask,1) { ClientSize=size };
            Genesis.Application.Studio.Theme.ThemeService.Apply(dialog);
            var poses=(ListBox)typeof(PixelRigStudioDialog).GetField("_poses",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dialog)!;
            poses.SelectedIndex=Math.Min(1,poses.Items.Count-1);
            typeof(PixelRigStudioDialog).GetMethod("LoadPose",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(dialog,null);
            var picker=(ComboBox)typeof(PixelRigStudioDialog).GetField("_bonePicker",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dialog)!;
            picker.SelectedIndex=rig.Bones.Count-1;
            VisualCapture.Capture(dialog,Path.Combine(output,$"rig-workspace-{size.Width}.png"),captureFromScreen:true);
        }
        return 0;
    }
}
