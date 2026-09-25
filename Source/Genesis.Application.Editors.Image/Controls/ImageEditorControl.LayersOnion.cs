using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Controls;

public sealed partial class ImageEditorControl
{
    private void LoadOnionSettings()
    {
        var settings=_session.Document.OnionSkin;
        _onion.Checked=settings.Enabled;
        _onionPrevious.Value=Math.Clamp(settings.Previous,0,8); _onionNext.Value=Math.Clamp(settings.Next,0,8);
        _onionOpacity.Value=Math.Clamp(settings.OpacityPercent,1,100);
        foreach(var id in settings.LayerIds) _onionLayerIds.Add(id);
        _onionCurrent.Checked=settings.IncludeCurrent; _onionAbove.Checked=settings.AboveArtwork;
        _onionSource.SelectedIndex=settings.SpecifiedLayers ? 1 : 0;
    }
    private void SaveOnionSettings()=>_session.Document.OnionSkin=new()
    {
        Enabled=_onion.Checked,Previous=(int)_onionPrevious.Value,Next=(int)_onionNext.Value,
        OpacityPercent=(int)_onionOpacity.Value,SpecifiedLayers=_onionSource.SelectedIndex==1,
        IncludeCurrent=_onionCurrent.Checked,AboveArtwork=_onionAbove.Checked,LayerIds=_onionLayerIds.ToList()
    };
    private readonly ImageThemedComboBox _previewChannels = new() { Name="ImagePreviewChannels" };
    private readonly TextBox _layerName = new() { Name="ImageLayerName" };
    private readonly NumericUpDown _layerDepth = Number(1,1,1);
    private readonly ImageThemedComboBox _onionSource = new() { Name="OnionLayerSource" };
    private readonly CheckedListBox _onionLayers = new() { Name="OnionOtherLayers", CheckOnClick=true, BorderStyle=BorderStyle.FixedSingle };
    private readonly NumericUpDown _onionOpacity = Number(1,100,35);
    private readonly CheckBox _onionCurrent = new() { Text="Include other layers in this frame", AutoSize=true, Checked=true };
    private readonly CheckBox _onionAbove = new() { Text="Show ghosts above artwork", AutoSize=true, Checked=true };
    private readonly Label _onionHint = new() { AutoSize=false };
    private readonly HashSet<Guid> _onionLayerIds = [];
    private bool _syncingOnion;
    private sealed record OnionLayer(Guid Id,string Name) { public override string ToString()=>Name; }

    private void BuildOnionControls(Control panel)
    {
        _onionOpacity.SetBounds(148,98,68,26);
        _onionSource.Items.AddRange(["Same layer","Specified other layers"]);
        _onionSource.SelectedIndex=0; _onionSource.SetBounds(10,132,256,26);
        _onionLayers.SetBounds(10,165,256,85);
        _onionCurrent.SetBounds(10,255,256,24); _onionAbove.SetBounds(10,283,256,24);
        _onionHint.SetBounds(10,310,256,52);
        panel.Controls.AddRange([SectionLabel("Opacity (%)",10,102),_onionOpacity,_onionSource,_onionLayers,_onionCurrent,_onionAbove,_onionHint]);
        _onionSource.SelectedIndexChanged+=(_,_)=>{ RefreshOnionLayers(); RefreshCanvas(); };
        _onionLayers.ItemCheck+=(_,e)=>
        {
            if(_syncingOnion) return;
            var item=(OnionLayer)_onionLayers.Items[e.Index];
            if(e.NewValue==CheckState.Checked) _onionLayerIds.Add(item.Id); else _onionLayerIds.Remove(item.Id);
            RefreshCanvas();
        };
        _onionOpacity.ValueChanged+=(_,_)=>RefreshCanvas();
        _onionCurrent.CheckedChanged+=(_,_)=>RefreshCanvas();
        _onionAbove.CheckedChanged+=(_,_)=>RefreshCanvas();
    }

    private void SynchronizeLayerRows()
    {
        var layers=_workspace.CurrentFrame?.Layers.AsEnumerable().Reverse().ToArray() ?? [];
        bool changed=_layers.Items.Count!=layers.Length || layers.Where((layer,i)=>i>=_layers.Items.Count || !ReferenceEquals(_layers.Items[i],layer)).Any();
        if(changed)
        {
            int top=_layers.TopIndex;
            _layers.BeginUpdate();
            try { _layers.Items.Clear(); _layers.Items.AddRange(layers); if(layers.Length>0) _layers.TopIndex=Math.Clamp(top,0,layers.Length-1); }
            finally { _layers.EndUpdate(); }
        }
        if(layers.Length>0) _layers.SelectedIndex=layers.Length-1-Math.Clamp(_workspace.SelectedLayerIndex,0,layers.Length-1);
        _layers.Invalidate();
    }

    private void RefreshOnionLayers()
    {
        var choices=(_workspace.CurrentFrame?.Layers ?? []).Where(l=>l.Id!=_workspace.CurrentLayer?.Id).Reverse().Select(l=>new OnionLayer(l.Id,l.Name)).ToArray();
        _syncingOnion=true;
        _onionLayers.BeginUpdate();
        try
        {
            if(!_onionLayers.Items.Cast<OnionLayer>().SequenceEqual(choices))
            {
                _onionLayers.Items.Clear();
                foreach(var item in choices) _onionLayers.Items.Add(item,_onionLayerIds.Contains(item.Id));
            }
            bool other=_onionSource.SelectedIndex==1;
            _onionLayers.Enabled=other; _onionCurrent.Enabled=other;
        }
        finally { _onionLayers.EndUpdate(); _syncingOnion=false; }
    }

    public byte[] CompositeEditorFrame(int frameIndex) => _workspace.CompositeCurrentFrameFor(frameIndex,
        channel:(ImageMaterialChannel)Math.Max(0,_previewChannels.SelectedIndex-1),
        previewLayer:frameIndex==_workspace.SelectedFrameIndex ? _workspace.CurrentLayer : null,
        previewPixels:frameIndex==_workspace.SelectedFrameIndex ? _pendingPixels : null,
        allChannels:_previewChannels.SelectedIndex<=0).Pixels;

    private byte[] CompositeOnionSource(int frameIndex)
    {
        var active=_workspace.CurrentLayer;
        return _workspace.CompositeCurrentFrameFor(frameIndex,allChannels:true,includeHidden:true,
            include:l=>_onionSource.SelectedIndex==0
                ? active!=null && (l.Id==active.Id || (l.Name==active.Name && l.Channel==active.Channel))
                : _onionLayerIds.Contains(l.Id) && l.Id!=active?.Id).Pixels;
    }

    private byte[] CompositeWithOnionSkin()
    {
        byte[] artwork=CompositeEditorFrame(_workspace.SelectedFrameIndex);
        byte[] output=_onionAbove.Checked ? (byte[])artwork.Clone() : new byte[artwork.Length];
        int current=_workspace.SelectedFrameIndex, ghosts=0;
        float opacity=(float)_onionOpacity.Value/100;
        for(int distance=(int)_onionPrevious.Value;distance>=1;distance--)
            if(current-distance>=0) { TintOver(output,CompositeOnionSource(current-distance),Color.CornflowerBlue,opacity/distance); ghosts++; }
        for(int distance=(int)_onionNext.Value;distance>=1;distance--)
            if(current+distance<_workspace.Frames.Count) { TintOver(output,CompositeOnionSource(current+distance),Color.IndianRed,opacity/distance); ghosts++; }
        if(_onionSource.SelectedIndex==1 && _onionCurrent.Checked)
        { TintOver(output,CompositeOnionSource(current),Color.Goldenrod,opacity); ghosts++; }
        if(!_onionAbove.Checked) TintOver(output,artwork,Color.White,1,keepSourceColour:true);
        _onionHint.Text=_onionSource.SelectedIndex==1 && _onionLayerIds.Count==0 ? "Choose reference layers above.\nPrevious: blue · Next: red · This: gold"
            : ghosts==0 ? "No neighbouring frames in this direction.\nAdd frames or choose other layers."
            : "Previous: blue · Next: red · This: gold\nGhosts are a guide and are not exported.";
        return output;
    }

    private static void TintOver(byte[] destination,byte[] source,Color tint,float opacity,bool keepSourceColour=false)
    {
        for(int i=0;i<destination.Length;i+=4)
        {
            float sa=source[i+3]/255f*opacity, da=destination[i+3]/255f;
            float alpha=sa+da*(1-sa); if(sa<=0 || alpha<=0) continue;
            for(int c=0;c<3;c++)
            {
                float colour=keepSourceColour ? source[i+c] : c==0 ? tint.R : c==1 ? tint.G : tint.B;
                destination[i+c]=(byte)Math.Clamp((int)MathF.Round((colour*sa+destination[i+c]*da*(1-sa))/alpha),0,255);
            }
            destination[i+3]=(byte)Math.Clamp((int)MathF.Round(alpha*255),0,255);
        }
    }
}
