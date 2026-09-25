using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Inspector;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainSourceWizard : Form
{
    private readonly TextBox _name = new() { Text = "Terrain section" };
    private readonly TextBox _image = new() { ReadOnly = true };
    private readonly RichTextBox _code = new() { AcceptsTab = true, WordWrap = false, Font = new Font("Consolas", 10), Height = 250 };
    private readonly ComboBox _source = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _surface = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _width = Number(1,100000,1000), _length = Number(1,100000,1000), _spacing = Number(.1m,10000,8);
    private readonly NumericUpDown _min = Number(-100000,100000,-100), _max = Number(-100000,100000,200), _seed = Number(0,int.MaxValue,1337);
    private readonly PictureBox _preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(23,28,34) };
    private readonly Label _summary = new() { Dock = DockStyle.Bottom, Height = 90, Padding = new Padding(14), Text = "Choose a source, then Generate preview. Nothing is added until you click Create section." };
    private readonly Button _generate = new() { Text = "Generate preview", AutoSize = true, Height = 36 }, _apply = new() { Text = "Create section", AutoSize = true, Height = 36, Enabled = false };
    private CancellationTokenSource? _generation;
    private int _revision;
    public TerrainCreationResult? Result { get; private set; }
    public TerrainSourceWizard(TerrainCreationRecipe recipe, string projectRoot)
    {
        Text = "Create terrain · Heightmap / Code"; ClientSize = new Size(1050,740); MinimumSize = new Size(900,640);
        StartPosition = FormStartPosition.CenterParent; BackColor = EditorChrome.Canvas; ForeColor = EditorChrome.Text;
        Font = EditorChrome.BaseFont; MinimizeBox = false; ShowInTaskbar = false;
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10), BackColor = EditorChrome.Surface };
        var cancel = new Button { Text = "Cancel", AutoSize = true, Height = 36 };
        footer.Controls.AddRange([cancel,_apply,_generate]); cancel.Click += (_,_)=>Close();
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1050,680), FixedPanel = FixedPanel.Panel1, SplitterDistance = 410, Panel1MinSize = 370, Panel2MinSize = 300 };
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(16), BackColor = EditorChrome.Surface };
        split.Panel1.AutoScroll = true; split.Panel1.Controls.Add(fields);
        void FitFields() => fields.MaximumSize = new Size(Math.Max(300, split.Panel1.ClientSize.Width - SystemInformation.VerticalScrollBarWidth), 0);
        split.Panel1.SizeChanged += (_,_)=>FitFields(); FitFields();
        split.Panel2.Controls.Add(_preview); split.Panel2.Controls.Add(_summary);
        Controls.Add(split); Controls.Add(footer);
        _source.Items.AddRange(["Code / PGSL", "Heightmap image"]); _source.SelectedIndex = recipe.Source == TerrainCreationSource.Heightmap ? 1 : 0;
        _surface.Items.AddRange(["Heightfield · hills and ravines", "Volume · caves and overhangs"]); _surface.SelectedIndex = (int)recipe.Surface;
        _name.Text = recipe.Name; _code.Text = recipe.Code; _image.Text = recipe.Image;
        SetNumber(_width,recipe.Width); SetNumber(_length,recipe.Length); SetNumber(_spacing,recipe.Spacing); SetNumber(_min,recipe.MinHeight);SetNumber(_max,recipe.MaxHeight);SetNumber(_seed,recipe.Seed);
        Label Field(string title,Control control)
        {
            var label = new Label { Text=title,AutoSize=true,Margin=new Padding(0,12,0,5), ForeColor=EditorChrome.Muted }; fields.Controls.Add(label);
            control.Dock=DockStyle.Top;control.Margin=new Padding(0);EditorChrome.StyleField(control);fields.Controls.Add(control);
            return label;
        }
        Field("Name",_name);Field("Source",_source);
        var dimensions = new TableLayoutPanel { ColumnCount=3, RowCount=2, Dock=DockStyle.Top,Height=132,Width=350,Margin=new Padding(0,14,0,0) };
        dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,34));dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33));dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33));
        var values = new[]{("Width (m)",_width),("Length (m)",_length),("Spacing (m)",_spacing),("Min height (m)",_min),("Max height (m)",_max),("Seed",_seed)};
        for(int i=0;i<values.Length;i++)
        {
            var group=new Panel { Height=58,Width=105,Dock=DockStyle.Top,Margin=new Padding(0,0,8,8) };
            var label=new Label { Text=values[i].Item1,Dock=DockStyle.Top,Height=24,ForeColor=EditorChrome.Muted };
            var value=values[i].Item2;value.Dock=DockStyle.Bottom;EditorChrome.StyleField(value);group.Controls.Add(value);group.Controls.Add(label);dimensions.Controls.Add(group,i%3,i/3);
        }
        fields.Controls.Add(dimensions);
        var browse = new Button { Text = "Choose heightmap image…", Height=32, Dock=DockStyle.Top };
        var imageLabel=Field("Heightmap (dark = low, light = high)",_image);fields.Controls.Add(browse);
        browse.Click += (_,_)=>
        {
            ProjectAssetEntry? selected = AssetPickerService.PickAsset(
                new AssetPickerRequest(projectRoot, ResourceKind.Image, _image.Text, "Choose Heightmap Image"), this);
            if (selected is null) return;
            string? pixels = ProjectAssetIndex.ResolveSpriteImage(projectRoot, selected.Reference);
            if (string.IsNullOrWhiteSpace(pixels)) return;
            _editedHeightmap = null;
            _image.Text = pixels;
        };
        var surfaceLabel=Field("Code surface",_surface);
        var presets=new FlowLayoutPanel { AutoSize=true,Dock=DockStyle.Top,Margin=new Padding(0,8,0,0) };
        foreach(var entry in new[]{("Hills",TerrainCreationRecipe.HillsCode),("Ravine",TerrainCreationRecipe.RavineCode),("Cave",TerrainCreationRecipe.CaveCode)})
        {
            var button=new Button { Text=entry.Item1,AutoSize=true,Height=30 };EditorChrome.StyleField(button);presets.Controls.Add(button);
            button.Click+=(_,_)=>{ _code.Text=entry.Item2;_surface.SelectedIndex=entry.Item1=="Cave"?1:0;if(entry.Item1=="Cave"){_min.Value=-8;_max.Value=55;} };
        }
        var loadCode = new Button { Text="Load code…",AutoSize=true,Height=30 };presets.Controls.Add(loadCode);
        loadCode.Click+=(_,_)=>
        {
            ProjectAssetEntry? selected = AssetPickerService.PickAsset(
                new AssetPickerRequest(projectRoot, ResourceKind.PgslScript, null, "Choose Terrain Generator Script"), this);
            if (selected is not null) _code.Text = File.ReadAllText(selected.FullPath);
        };
        fields.Controls.Add(presets);var codeLabel=Field("PGSL · output height or density",_code);
        var help=new Label { AutoSize=true,MaximumSize=new Size(350,0),Margin=new Padding(0,14,0,12),ForeColor=EditorChrome.Muted,
            Text="x, y, z are metres. u, v span 0–1. Use sin, cos, abs, sqrt, min, max, pow, exp, floor, clamp and noise(x,z). TerrainSize(width,length) and TerrainSpacing(metres) override the fields.\n\nHeightfields: up to 384 × 384 cells. Volumes: up to 64³ cells. Large areas keep their dimensions; effective spacing is shown after generation. Heightmap luminance is sampled at 8-bit precision. Sections can be moved and assigned components; terrain brushes edit the base landscape." };fields.Controls.Add(help);
        void Change(object? sender,EventArgs e) { _revision++;Result=null;_apply.Enabled=false;_generation?.Cancel(); }
        foreach(var control in new Control[]{_name,_image,_code})control.TextChanged+=Change;
        foreach(var number in new[]{_width,_length,_spacing,_min,_max,_seed})number.ValueChanged+=Change;
        _surface.SelectedIndexChanged+=Change;
        void SourceChanged()
        {
            bool code=_source.SelectedIndex==0;
            foreach(var control in new Control[]{_code,_surface,presets,surfaceLabel,codeLabel,help})control.Visible=code;
            foreach(var control in new Control[]{browse,_image,imageLabel})control.Visible=!code;
        }
        _source.SelectedIndexChanged+=(_,e)=>{Change(this,e);SourceChanged();};SourceChanged();
        foreach(Control control in footer.Controls)EditorChrome.StyleField(control);
        _generate.Click+=async (_,_)=>await GeneratePreviewAsync();
        _apply.Click+=(_,_)=>{ if(Result is not null) {DialogResult=DialogResult.OK;Close();} };
        FormClosing+=(_,_)=>_generation?.Cancel();
        FormClosed+=(_,_)=>{_preview.Image?.Dispose();_preview.Image=null;};
        InstallAuthoringPreview(split, fields);
    }
    private static NumericUpDown Number(decimal min,decimal max,decimal value)=>new(){Minimum=min,Maximum=max,Value=value,DecimalPlaces=2,ThousandsSeparator=true};
    private static void SetNumber(NumericUpDown field,float value)=>field.Value=Math.Clamp((decimal)value,field.Minimum,field.Maximum);
    public async Task GeneratePreviewAsync()
    {
        if(_generation is not null) { _generation.Cancel();return; }
        var recipe=new TerrainCreationRecipe { Name=_name.Text.Trim(), Source=_source.SelectedIndex==0?TerrainCreationSource.Code:TerrainCreationSource.Heightmap,
            Surface=_source.SelectedIndex==0?(TerrainCodeSurface)_surface.SelectedIndex:TerrainCodeSurface.Heightfield, Code=_code.Text,Image=_image.Text,
            Width=(float)_width.Value,Length=(float)_length.Value,Spacing=(float)_spacing.Value,MinHeight=(float)_min.Value,MaxHeight=(float)_max.Value,Seed=(int)_seed.Value };
        using var cancellation=new CancellationTokenSource();_generation=cancellation;int revision=_revision;
        _generate.Text="Cancel generation";_apply.Enabled=false;_summary.Text="Generating terrain preview…";
        try
        {
            var heightmap = _editedHeightmap;
            var result=await Task.Run(()=>TerrainSectionGenerator.Generate(recipe,cancellation.Token,heightmap));
            if(IsDisposed||cancellation.IsCancellationRequested||revision!=_revision)return;
            Result=result;
            _livePreview.SetMesh(result.Model);
            _replaceBase.Enabled = result.Heights is not null;
            if (result.Heights is null) _replaceBase.Checked = false;
            var bitmap=new Bitmap(result.PreviewSize,result.PreviewSize,PixelFormat.Format32bppArgb);
            var bits=bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
            byte[] bgra=(byte[])result.Preview.Clone();for(int i=0;i<bgra.Length;i+=4)(bgra[i],bgra[i+2])=(bgra[i+2],bgra[i]);
            Marshal.Copy(bgra,0,bits.Scan0,bgra.Length);bitmap.UnlockBits(bits);var previous=_preview.Image;_preview.Image=bitmap;previous?.Dispose();
            _summary.Text=result.Summary;_apply.Enabled=true;
        }
        catch(OperationCanceledException) { if(!IsDisposed)_summary.Text="Generation cancelled. Adjust the recipe and try again."; }
        catch(Exception exception) { if(!IsDisposed)_summary.Text=exception.Message; }
        finally { _generation=null;if(!IsDisposed)_generate.Text="Generate preview"; }
    }
}
