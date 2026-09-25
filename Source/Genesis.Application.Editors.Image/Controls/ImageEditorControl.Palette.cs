using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Controls;

public sealed partial class ImageEditorControl
{
    private FlowLayoutPanel? _paletteSwatches;
    private List<ImageColor>? _shownPalette;
    private static readonly string[] DefaultPalette = ["#171C30", "#3A4466", "#5B6B8C", "#8B9BB4", "#C0CBDC", "#FFFFFF", "#7D283B", "#C83C47", "#F26B55", "#F4A45B", "#FFDD85", "#FFEFD0", "#315243", "#4E8C55", "#8BC56A", "#BAE693", "#24577B", "#368CC1", "#5BC5E8", "#A2E5ED", "#563F78", "#8463A8", "#BB86BC", "#EDA8BC"];
    public IReadOnlyList<Color> PaletteColours => _session.Document.Palette.Count==0?DefaultPalette.Select(ColorTranslator.FromHtml).ToList():_session.Document.Palette.Select(c=>Color.FromArgb((int)Math.Round(c.Alpha*255),(int)Math.Round(c.Red*255),(int)Math.Round(c.Green*255),(int)Math.Round(c.Blue*255))).ToList();
    public void SetPalette(IReadOnlyList<Color> colours)
    {
        if(colours.Count is <1 or >256)throw new ArgumentException("A palette needs 1–256 colours.");
        var before=_session.Document.Palette;var after=colours.Select(c=>new ImageColor {Red=c.R/255d,Green=c.G/255d,Blue=c.B/255d,Alpha=c.A/255d}).ToList();
        EditStructure("Edit palette",()=>_session.Document.Palette=after,()=>_session.Document.Palette=before);RefreshPalette();
    }
    public void ImportPalette(string path)=>SetPalette(ImagePaletteStorage.Read(File.ReadAllText(path)));
    public void ExportPalette(string path)=>File.WriteAllText(path,ImagePaletteStorage.Write(PaletteColours,Path.GetExtension(path).Equals(".gpl",StringComparison.OrdinalIgnoreCase)));
    private void RefreshPalette()
    {
        if(_paletteSwatches==null || ReferenceEquals(_shownPalette,_session.Document.Palette))return;
        _shownPalette=_session.Document.Palette;
        foreach(Control control in _paletteSwatches.Controls.Cast<Control>().ToArray())control.Dispose();
        foreach(var colour in PaletteColours)
        {
            var swatch=new ImagePaletteButton(colour) {Size=new Size(27,22),Margin=new Padding(2),Tag=colour,AccessibleName=ImagePaletteStorage.Hex(colour),TabStop=true};
            swatch.MouseDown+=(_,e)=>{if(e.Button==MouseButtons.Right)SetBackgroundColor(colour);else SetForegroundColor(colour);};swatch.Click+=(_,_)=>SetForegroundColor(colour);
            _toolHelp.SetToolTip(swatch,ImagePaletteStorage.Hex(colour));_paletteSwatches.Controls.Add(swatch);
        }
    }
    private Control BuildPalette()
    {
        var section=new CollapsibleSection("Palette",132);
        _paletteSwatches=new FlowLayoutPanel{Location=new Point(6,4),Size=new Size(264,88),AutoScroll=true,WrapContents=true};section.Content.Controls.Add(_paletteSwatches);
        var edit=SectionActionButton("Edit palette…",10,98);edit.Click+=(_,_)=>PromptPalette();section.Content.Controls.Add(edit);RefreshPalette();return section;
    }
    private void PromptPalette()
    {
        if(Genesis.Application.Core.Diagnostics.UnattendedSession.IsActive)return;
        var colours=PaletteColours.ToList();using var dialog=new DpiAwareForm{Text="Palette",ClientSize=new Size(490,520),StartPosition=FormStartPosition.CenterParent};
        ImageFrameTargetControl? target=null;
        var list=new ListBox{Dock=DockStyle.Fill,DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=30,IntegralHeight=false};
        var commands=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=90,Padding=new Padding(10),WrapContents=true};
        CancellationTokenSource? extraction = null;
        var extractionStatus = new Label { Dock = DockStyle.Top, Height = 48, Padding = new Padding(8), Text = "Extract frame uses the 256 most frequent RGBA colours in the current composite." };
        var cancelExtraction = new Button { Dock = DockStyle.Bottom, Height = 32, Text = "Cancel extraction", Visible = false };
        cancelExtraction.Click += (_,_) => extraction?.Cancel();
        dialog.FormClosing += (_,args) => { if (extraction != null) { extraction.Cancel(); args.Cancel = true; } };
        void Reload(int index=0){list.Items.Clear();foreach(var c in colours)list.Items.Add(ImagePaletteStorage.Hex(c));if(colours.Count>0)list.SelectedIndex=Math.Clamp(index,0,colours.Count-1);}
        void AddButton(string text,Action click){var button=new Button{Text=text,AutoSize=true,MinimumSize=new Size(80,32)};button.Click+=(_,_)=>click();commands.Controls.Add(button);}
        void Edit(){int i=list.SelectedIndex;if(i>=0&&RgbaColourDialog.TryShow(dialog,colours[i],out var c)){colours[i]=c;Reload(i);}}
        list.DrawItem+=(_,e)=>{if(e.Index<0)return;e.DrawBackground();using var fill=new SolidBrush(colours[e.Index]);e.Graphics.FillRectangle(fill,e.Bounds.X+6,e.Bounds.Y+4,36,22);TextRenderer.DrawText(e.Graphics,ImagePaletteStorage.Hex(colours[e.Index]),Font,new Point(e.Bounds.X+54,e.Bounds.Y+7),e.ForeColor);};list.DoubleClick+=(_,_)=>Edit();
        AddButton("Add",()=>{if(colours.Count<256&&RgbaColourDialog.TryShow(dialog,_foreground.Colour,out var c)){colours.Add(c);Reload(colours.Count-1);}});
        AddButton("Edit",Edit);AddButton("Remove",()=>{if(list.SelectedIndex>=0&&colours.Count>1){int i=list.SelectedIndex;colours.RemoveAt(i);Reload(i);}});
        AddButton("Extract frames",async () =>
        {
            if (extraction != null) return;
            extraction = new CancellationTokenSource(); var token = extraction.Token;
            var pixels = target!.FrameIndices.Select(i=>_workspace.CompositeCurrentFrameFor(i).Pixels).ToArray();
            commands.Enabled = false; cancelExtraction.Visible = true; extractionStatus.Text = "Extracting colours…";
            try
            {
                var result = await Task.Run(() => ImagePaletteStorage.Extract(pixels,256,token),token);
                token.ThrowIfCancellationRequested(); colours = result.ToList(); Reload();
                extractionStatus.Text = $"Extracted {colours.Count} colours. Save palette to keep them, or Cancel to keep the previous palette.";
            }
            catch (OperationCanceledException) { extractionStatus.Text = "Extraction cancelled; the palette was kept."; }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Palette extraction: " + error); extractionStatus.Text = error.Message; }
            finally { extraction.Dispose(); extraction = null; commands.Enabled = true; cancelExtraction.Visible = false; }
        });
        AddButton("Import…",()=>{using var file=new OpenFileDialog{Filter="Palettes|*.gpl;*.pal;*.hex;*.txt"};if(file.ShowDialog(dialog)!=DialogResult.OK)return;try{colours=ImagePaletteStorage.Read(File.ReadAllText(file.FileName)).ToList();Reload();}catch(Exception error)when(error is IOException or InvalidDataException or UnauthorizedAccessException){System.Diagnostics.Trace.TraceError("Palette import: "+error);ThemeMessageBox.Show(dialog,error.Message,"Palette import");}});
        AddButton("Export…",()=>{using var file=new SaveFileDialog{Filter="RGBA hex|*.hex|GIMP palette|*.gpl",FileName="palette.hex"};if(file.ShowDialog(dialog)!=DialogResult.OK)return;try{File.WriteAllText(file.FileName,ImagePaletteStorage.Write(colours,file.FilterIndex==2));}catch(Exception error)when(error is IOException or InvalidDataException or UnauthorizedAccessException){System.Diagnostics.Trace.TraceError("Palette export: "+error);ThemeMessageBox.Show(dialog,error.Message,"Palette export");}});
        var ok=new Button{Text="Save palette",DialogResult=DialogResult.OK,AutoSize=true,MinimumSize=new Size(105,32)};var cancel=new Button{Text="Cancel",DialogResult=DialogResult.Cancel,AutoSize=true,MinimumSize=new Size(80,32)};commands.Controls.AddRange([ok,cancel]);
        dialog.Controls.Add(list);dialog.Controls.Add(commands);dialog.Controls.Add(extractionStatus);dialog.Controls.Add(cancelExtraction);
        dialog.AcceptButton=ok;dialog.CancelButton=cancel;Reload();ThemeMessageBox.ApplyTheme?.Invoke(dialog);
        target=AttachFrameTargets(dialog,"Extract frames reads this range. Saved palette swatches are shared by the sprite.");
        if(dialog.ShowDialog(this)==DialogResult.OK)SetPalette(colours);
    }
}
