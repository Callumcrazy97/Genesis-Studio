using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors.Image.Rigging;

namespace Genesis.Application.Headless;

internal static class ImageLayoutCaptureRunner
{
    public static int Run(string outputDirectory, int width, int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        string workspaceRoot = Path.Combine(
            AppContext.BaseDirectory,
            "TestResults",
            "ImageLayoutWorkspace",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        ProjectSession project = new ProjectService().CreateProject(workspaceRoot, "Image Layout Probe", "Blank");
        ResourceService resources = new(project);
        string imagePath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Layout Probe Sprite");
        SaveProbeImage(imagePath);

        CaptureEditor(imagePath, Path.Combine(outputDirectory, "image-editor-standard.png"), new Size(width, height));
        CaptureViewer(imagePath, Path.Combine(outputDirectory, "image-viewer-standard.png"), new Size(width, height));
        CaptureEditor(imagePath, Path.Combine(outputDirectory, "image-editor-narrow.png"), new Size(760, 620));
        CaptureViewer(imagePath, Path.Combine(outputDirectory, "image-viewer-narrow.png"), new Size(760, 620));
        CaptureRig(imagePath,outputDirectory);
        CaptureInteraction(imagePath,outputDirectory);
        CaptureCanvasTools(imagePath,outputDirectory);

        Console.WriteLine("IMAGE LAYOUT CAPTURES PASSED");
        Console.WriteLine($"Output: {outputDirectory}");
        return 0;
    }

    private static void CaptureEditor(string imagePath, string outputPath, Size size)
    {
        ImageDocumentSession session = LoadSession(imagePath);
        ImageWorkspace workspace = ImageWorkspaceStorage.Load(session);
        ImageEditorControl editor = new(session, workspace);
        editor.SetOnionSkin(enabled: true, previousFrames: 1, nextFrames: 1);
        using Form host = Host(editor, "Genesis Studio - Image Editor", size);
        VisualCapture.Capture(host, outputPath, captureFromScreen: true);
    }

    private static void CaptureViewer(string imagePath, string outputPath, Size size)
    {
        ImageDocumentSession session = LoadSession(imagePath);
        ImageWorkspace workspace = ImageWorkspaceStorage.Load(session);
        using Form host = Host(new ImageViewerControl(session, workspace), "Genesis Studio - Image Viewer", size);
        VisualCapture.Capture(host, outputPath, captureFromScreen: true);
    }

    private static ImageDocumentSession LoadSession(string imagePath) =>
        new(ImageDocumentSerializer.LoadAtomic(imagePath).Document, imagePath, ImageDocumentAccess.Editor);

    private static void CaptureRig(string imagePath, string output)
    {
        var session = LoadSession(imagePath); var workspace = ImageWorkspaceStorage.Load(session);
        var bone = new ImagePixelBone { Name = "Blade", Start = new(){X=28,Y=36}, End = new(){X=49,Y=15} };
        var rig = new ImagePixelRig { Name = "Sword", Width=64,Height=64,LayerId=workspace.CurrentLayer!.Id.ToString("N"), SourceFrameId=workspace.CurrentFrame!.Id.ToString("N"),
            BindPixels=workspace.CurrentLayer.Pixels.ToArray(),Bones=[bone],Poses=[new(){Name="Default",Bones=[PixelRigRasterizer.Copy(bone)]}] };
        var raised = PixelRigRasterizer.Copy(bone); raised.End=new(){X=28,Y=6};
        rig.Poses.Add(new ImagePixelPose{Name="Raised",Bones=[raised]});
        rig.Animations.Add(new ImagePoseAnimation{Name="Sword swing",Keys=[new(){Frame=1,PoseId=rig.Poses[0].Id},new(){Frame=30,PoseId=rig.Poses[1].Id},new(){Frame=60,PoseId=rig.Poses[0].Id}]});
        for(int tab=0;tab<3;tab++)
        {
            using var dialog = new PixelRigStudioDialog([rig],rig.BindPixels,64,64,rig.LayerId,rig.SourceFrameId,_=>{},_=>{},_=>{},(_,_,_)=>Task.CompletedTask,tab);
            Genesis.Application.Studio.Theme.ThemeService.Apply(dialog);
            if(tab==2)
            {
                ((ListBox)dialog.Controls.Find("RigAnimations",true).Single()).SelectedIndex=0;
            }
            VisualCapture.Capture(dialog,Path.Combine(output,$"image-rig-{tab}.png"),captureFromScreen:true);
        }
    }

    private static void CaptureInteraction(string imagePath,string output)
    {
        foreach(var tool in new[]{ImageToolKind.RectSelect,ImageToolKind.Eraser,ImageToolKind.Line})
        {
            var session=LoadSession(imagePath);var workspace=ImageWorkspaceStorage.Load(session);
            var editor=new ImageEditorControl(session,workspace);editor.SetActiveTool(tool);editor.SetForegroundColor(Color.Red);editor.SetBrushSize(tool==ImageToolKind.Eraser?5:1);
            editor.SimulateImagePointer(new(26,34),true,false);editor.SimulateImagePointer(new(48,14),false,false);
            if(tool==ImageToolKind.RectSelect)editor.SimulateImagePointer(new(48,14),false,true);
            using var form=Host(editor,"Image interaction · "+tool,new Size(1360,840));
            VisualCapture.Capture(form,Path.Combine(output,$"image-interaction-{tool}.png"),captureFromScreen:true);
        }
    }

    private static void CaptureCanvasTools(string imagePath,string output)
    {
        var session = LoadSession(imagePath); var workspace = ImageWorkspaceStorage.Load(session);
        using (var rotate = new ImageRotateDialog(workspace.CompositeCurrentFrame(),64,64))
        {
            Genesis.Application.Studio.Theme.ThemeService.Apply(rotate);
            VisualCapture.Capture(rotate,Path.Combine(output,"image-rotate.png"),captureFromScreen:true);
        }
        using (var rotate = new ImageRotateDialog(workspace.CompositeCurrentFrame(),64,64) { ClientSize = new Size(640,460) })
        {
            Genesis.Application.Studio.Theme.ThemeService.Apply(rotate);
            VisualCapture.Capture(rotate,Path.Combine(output,"image-rotate-narrow.png"),captureFromScreen:true);
        }
        var panel = new byte[32*32*4];
        PaintBlock(panel,32,0,0,32,32,Color.FromArgb(255,43,52,78));
        PaintBlock(panel,32,2,2,28,28,Color.FromArgb(255,73,101,145));
        PaintBlock(panel,32,4,4,24,24,Color.FromArgb(255,28,34,51));
        foreach (int x in new[]{3,25}) foreach (int y in new[]{3,25}) PaintBlock(panel,32,x,y,4,4,Color.FromArgb(255,240,182,97));
        using var slices = new ImageNineSliceDialog(panel,32,32,new ImageNineSlice{Enabled=true,Left=8,Top=8,Right=8,Bottom=8});
        Genesis.Application.Studio.Theme.ThemeService.Apply(slices);
        VisualCapture.Capture(slices,Path.Combine(output,"image-nine-slice.png"),captureFromScreen:true);
        using var narrowSlices = new ImageNineSliceDialog(panel,32,32,new ImageNineSlice{Enabled=true,Left=8,Top=8,Right=8,Bottom=8}) { ClientSize = new Size(860,590) };
        Genesis.Application.Studio.Theme.ThemeService.Apply(narrowSlices);
        VisualCapture.Capture(narrowSlices,Path.Combine(output,"image-nine-slice-narrow.png"),captureFromScreen:true);
    }

    private static void SaveProbeImage(string imagePath)
    {
        ImageDocument document = ImageDocument.CreateDefault(64, 64);
        document.Usage.Allowed = ImageUsage.Sprite | ImageUsage.Tileset | ImageUsage.Texture;
        document.Usage.Tileset.TileWidth = 16;
        document.Usage.Tileset.TileHeight = 16;
        document.Usage.Tileset.Collision = [5, 6, 9];
        document.Origin.X = 32;
        document.Origin.Y = 32;
        document.Origin.Space = ImageCoordinateSpace.Pixels;

        ImageWorkspace workspace = ImageWorkspace.CreateBlank(64, 64, Color.Transparent);
        workspace.CurrentLayer!.Name = "Blade";
        void Polygon(ImageLayerBuffer layer, string colour, params Point[] points) => RasterOperations.DrawPolygon(
            layer.Pixels,64,64,points,ColorTranslator.FromHtml(colour),new ImageBrushSettings(),true);
        Polygon(workspace.CurrentLayer,"#171C30",new(25,32),new(46,11),new(54,10),new(53,19),new(32,40));
        Polygon(workspace.CurrentLayer,"#8B9BB4",new(28,32),new(47,13),new(51,13),new(51,18),new(32,37));
        Polygon(workspace.CurrentLayer,"#E9F5FF",new(28,32),new(47,13),new(51,13),new(31,34));
        var hilt = workspace.AddLayer("Hilt");
        Polygon(hilt,"#171C30",new(18,31),new(22,28),new(39,45),new(36,49));
        Polygon(hilt,"#F4A45B",new(21,32),new(22,31),new(36,45),new(36,46));
        Polygon(hilt,"#171C30",new(25,37),new(31,43),new(20,54),new(14,48));
        Polygon(hilt,"#7D283B",new(25,40),new(28,43),new(20,51),new(17,48));
        Polygon(hilt,"#FFDD85",new(14,46),new(22,54),new(19,57),new(11,49));
        PaintBlock(hilt.Pixels,64,14,49,3,3,Color.FromArgb(255,54,140,193));
        var glint = workspace.AddLayer("Glint");
        for(int frame=0;frame<4;frame++)
        {
            if(frame>0) workspace.AddFrame(duplicateCurrent:true);
            var layer=workspace.CurrentFrame!.Layers[2]; Array.Clear(layer.Pixels);
            int x=36+frame*3, y=25-frame*3;
            PaintBlock(layer.Pixels,64,x-2,y,5,1,Color.White);
            PaintBlock(layer.Pixels,64,x,y-2,1,5,Color.White);
            workspace.CurrentFrame.DurationMilliseconds=100+frame*20;
        }
        workspace.SelectedFrameIndex=0; workspace.SelectedLayerIndex=0; workspace.Touch();
        ImageWorkspaceStorage.SynchronizeDocument(document, workspace);
        document.Tags.Add(new ImageAnimationTag
        {
            Name = "Sword shimmer",
            StartFrameId = document.Frames[0].Id,
            EndFrameId = document.Frames[^1].Id,
            Direction = ImagePlaybackDirection.Forward,
            Loop = true,
        });

        ImageDocumentSession session = new(document, imagePath, ImageDocumentAccess.Editor);
        ImageWorkspaceStorage.Save(session, workspace);
    }

    private static void PaintBlock(byte[] rgba, int width, int x0, int y0, int w, int h, Color color)
    {
        int height = rgba.Length / (width * 4);
        for (int y = y0; y < y0 + h; y++)
        for (int x = x0; x < x0 + w; x++)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height) continue;
            int i = (y * width + x) * 4;
            rgba[i] = color.R;
            rgba[i + 1] = color.G;
            rgba[i + 2] = color.B;
            rgba[i + 3] = color.A;
        }
    }

    private static Form Host(Control control, string title, Size size)
    {
        Form form = new()
        {
            Text = title,
            ClientSize = size,
            StartPosition = FormStartPosition.Manual,
            BackColor = Color.FromArgb(20, 22, 28),
        };
        control.Dock = DockStyle.Fill;
        form.Controls.Add(control);
        Genesis.Application.Studio.Theme.ThemeService.Apply(form);
        return form;
    }
}
