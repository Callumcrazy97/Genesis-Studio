using System.Numerics;
using System.Text.Json;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private TerrainCreationSource? _drawingSection;
    private readonly List<Vector3> _sectionOutline = [];
    private bool _sectionDragging;
    private float _sectionLevel;
    private Point _sectionLastMouse;
    private ContextMenuStrip? _createMenu;

    private void ShowCreateMenu(bool drawingOnly = false)
    {
        _createMenu?.Dispose();
        var menu = _createMenu = new ContextMenuStrip();
        if (!drawingOnly) menu.Items.Add("Landscape Wizard…", null, (_,_)=>OpenCreationWizard());
        menu.Items.Add("Region · drag a rectangle", null, (_,_)=>BeginSectionDrawing(TerrainCreationSource.Region));
        menu.Items.Add("Lasso · draw an outline", null, (_,_)=>BeginSectionDrawing(TerrainCreationSource.Lasso));
        if (!drawingOnly)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Create From Heightmap…", null, (_,_)=>OpenTerrainSourceWizard(TerrainCreationSource.Heightmap));
            menu.Items.Add("Create From Code…", null, (_,_)=>OpenTerrainSourceWizard(TerrainCreationSource.Code));
        }
        // Keep the menu alive through ToolStrip's click/close dispatch. Dispose on reuse/owner disposal.
        menu.Show(Cursor.Position);
    }

    public void BeginSectionDrawing(TerrainCreationSource source)
    {
        if(source is not (TerrainCreationSource.Region or TerrainCreationSource.Lasso))throw new ArgumentOutOfRangeException(nameof(source));
        SetMode(TerrainEditorMode.Select);CancelSectionDrawing();_drawingSection=source;_placementEntityPath=null; RefreshActiveToolCard();
        _statusLabel.Text=source==TerrainCreationSource.Region?"Create Region · drag a rectangle; release to create · Esc cancels":"Create Lasso · drag a closed outline; release to create · Esc cancels";
        _viewport.Host.Focus();
    }

    private bool SectionPoint(Point point, out Vector3 world)
    {
        var ray=_viewport.PickRay(point);world=default;
        if(Math.Abs(ray.Direction.Y)<.00001f)return false;
        float distance=(_sectionLevel-ray.Origin.Y)/ray.Direction.Y;
        if(distance<=0||distance>100000)return false;
        world=ray.Origin+ray.Direction*distance;return true;
    }
    private bool SectionPointerDown(Point point)
    {
        if(_drawingSection is null)return false;
        _sectionLevel=PickTerrain(point,out Vector3 ground)?ground.Y:0;
        if(!SectionPoint(point,out var world))return true;
        _sectionOutline.Clear();_sectionOutline.Add(world);_sectionOutline.Add(world);_sectionLastMouse=point;
        _sectionDragging=true;_viewport.NavigationEnabled=false;_viewport.Host.Capture=true;_viewport.Invalidate();return true;
    }
    private bool SectionPointerMove(Point point)
    {
        if (_pendingTerrain is not null) { if (TerrainPlacementPoint(point, out var position)) _pendingTerrainPosition = position; _viewport.Invalidate(); return true; }
        if(!_sectionDragging)return false;
        if(!SectionPoint(point,out var world))return true;
        if(_drawingSection==TerrainCreationSource.Region)_sectionOutline[1]=world;
        else if(Vector2.Distance(new(point.X,point.Y),new(_sectionLastMouse.X,_sectionLastMouse.Y))>=8&&_sectionOutline.Count<256)
        {
            if(_sectionOutline.Count==2&&_sectionOutline[0]==_sectionOutline[1])_sectionOutline[1]=world;else _sectionOutline.Add(world);
            _sectionLastMouse=point;
        }
        _viewport.Invalidate();return true;
    }
    private bool SectionPointerUp(Point point)
    {
        if(!_sectionDragging)return false;
        SectionPointerMove(point);var source=_drawingSection!.Value;var outline=SectionPolygon();
        bool selection = _selectionDrawing;
        CancelSectionDrawing();
        if (selection) { SetPaintSelection(outline.Select(p => new Vector2(p.X, p.Z))); SetMode(TerrainEditorMode.Paint); return true; }
        try
        {
            if(outline.Count<3)return true;
            Vector3 min=outline.Aggregate(Vector3.Min),max=outline.Aggregate(Vector3.Max),centre=(min+max)/2;
            if(max.X-min.X<.05f||max.Z-min.Z<.05f)return true;
            var recipe=new TerrainCreationRecipe {Source=source,Name=source+" terrain",Width=max.X-min.X,Length=max.Z-min.Z,
                Boundary=outline.Select(p=>new[]{p.X-centre.X,p.Z-centre.Z}).ToArray()};
            AddTerrainSection(TerrainSectionGenerator.Generate(recipe),centre);
        }
        catch(Exception exception) { _statusLabel.Text=exception.Message; }
        return true;
    }
    private List<Vector3> SectionPolygon()
    {
        if(_drawingSection!=TerrainCreationSource.Region||_sectionOutline.Count<2)return [.._sectionOutline];
        var a=_sectionOutline[0];var b=_sectionOutline[1];return [a,new(b.X,a.Y,a.Z),b,new(a.X,a.Y,b.Z)];
    }
    private void CancelSectionDrawing()
    {
        _sectionDragging=false;_drawingSection=null;_selectionDrawing=false;_sectionOutline.Clear();_viewport.Host.Capture=false;_viewport.NavigationEnabled=true;_viewport.Invalidate(); RefreshActiveToolCard();
    }
    private void DrawSectionPreview(IRenderController renderer)
    {
        for (int i = 0; i < _paintSelection.Count; i++)
        {
            var p = _paintSelection[i]; var q = _paintSelection[(i + 1) % _paintSelection.Count];
            Vector3 a = _viewport.WorldToSurface(new(p.X, _terrain.SampleHeight(p.X, p.Y) + .1f, p.Y)), b = _viewport.WorldToSurface(new(q.X, _terrain.SampleHeight(q.X, q.Y) + .1f, q.Y));
            if (a.Z is > 0 and < 1 && b.Z is > 0 and < 1) renderer.DrawLine(a.X, a.Y, b.X, b.Y, new RenderColor(1, .8f, .25f, 1), 2, depth: -8997);
        }
        if(!_sectionDragging)return;
        var polygon=SectionPolygon();if(polygon.Count<2)return;
        var color=new RenderColor(.35f,.75f,1f,1);
        for(int i=0;i<polygon.Count;i++)
        {
            Vector3 a=_viewport.WorldToSurface(polygon[i]),b=_viewport.WorldToSurface(polygon[(i+1)%polygon.Count]);
            if(a.Z is >0 and <1&&b.Z is >0 and <1)renderer.DrawLine(a.X,a.Y,b.X,b.Y,color,2,depth:-8997);
        }
        if(_drawingSection==TerrainCreationSource.Region)
            DrawLevelPlane(renderer,polygon.Aggregate(Vector3.Min),polygon.Aggregate(Vector3.Max),_sectionLevel,new RenderColor(.35f,.75f,1f,.45f));
    }

    public void OpenTerrainSourceWizard(TerrainCreationSource source)
    {
        CancelSectionDrawing();
        using var wizard=new TerrainSourceWizard(new TerrainCreationRecipe {Source=source}, ProjectRoot);
        if(wizard.ShowDialog(FindForm())!=DialogResult.OK||wizard.Result is not { } result)return;
        if (wizard.PlaceManually) { ArmTerrainPlacement(result, wizard.ReplaceBase); return; }
        Vector3 centre = wizard.PlacementPosition;
        if (wizard.ReplaceBase) ApplyHeightfield(result, centre); else AddTerrainSection(result,centre);
        FrameSection(result,centre);
    }

    private void FrameSection(TerrainCreationResult result,Vector3 position)
    {
        _viewport.Camera.Target=position+(result.Model.Bounds.Min+result.Model.Bounds.Max)*.5f;
        _viewport.Camera.Distance=Math.Max(24,Vector3.Distance(result.Model.Bounds.Min,result.Model.Bounds.Max)*.85f);
        _viewport.Invalidate();
    }

    private void UpdateSectionCameraRange()
    {
        _viewport.FloorHeight = _terrain.MinHeight - 1;
        float extent=Math.Max(_terrain.ResolutionX,_terrain.ResolutionZ)*_terrain.CellSize;
        foreach(var placed in _nature.PlacedEntities)
        {
            if(TryLoadEntityDocument(ResolveEntityFullPath(placed.Entity))?.Creation is not { } recipe)continue;
            extent=Math.Max(extent,placed.Position.Length()+Math.Max(recipe.Width,recipe.Length)*Math.Max(1,placed.Scale)*2);
        }
        _viewport.FarPlane=Math.Max(1200,extent*4);_viewport.Camera.MaximumDistance=Math.Max(1600,extent*2);
        if(_viewport.SecondaryCamera is { } secondary) { secondary.FarPlane=_viewport.FarPlane;secondary.Camera.MaximumDistance=_viewport.Camera.MaximumDistance; }
    }

    /// <summary>Adds mesh, recipe and placement together; undo removes only this operation's owned files.</summary>
    public string AddTerrainSection(TerrainCreationResult result,Vector3 position)
    {
        string folder=Path.GetFullPath(ResourcePath+".parts");Directory.CreateDirectory(folder);
        string stem="section-"+Guid.NewGuid().ToString("N"),modelPath=Path.Combine(folder,stem+".gmodel"),entityPath=Path.Combine(folder,stem+".terrainpart.json");
        var model=new TerrainEntityComponent {Type=TerrainEntityComponentKinds.Model};model.Set("Model",ResourceNames.Name(ProjectRoot, modelPath));model.Set("Scale","1");
        var document=new TerrainEntityDocument {Name=result.Recipe.Name,Type=TerrainEntityType.Terrain,Creation=result.Recipe,Components=[model]};
        string relative=ResourceNames.Name(ProjectRoot, entityPath);
        byte[] entityBytes=JsonSerializer.SerializeToUtf8Bytes(document,new JsonSerializerOptions {WriteIndented=true});
        byte[] modelBytes=System.Text.Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(result.Model));
        var before=ClonePlaced(_nature.PlacedEntities);var after=ClonePlaced(before);
        var placed=new TerrainPlacedEntity {Entity=relative,Position=position};placed.Normalize();after.Add(placed);
        void Apply()
        {
            try { File.WriteAllBytes(modelPath,modelBytes);File.WriteAllBytes(entityPath,entityBytes); }
            catch { File.Delete(modelPath);File.Delete(entityPath);throw; }
            if(!_settings.Entities.Contains(relative))_settings.Entities.Add(relative);
            _nature.PlacedEntities=ClonePlaced(after);RefreshComponentsPanel();_entityListPanel.RefreshEntities();_viewport.Invalidate();
        }
        void Revert()
        {
            _nature.PlacedEntities=ClonePlaced(before);_settings.Entities.Remove(relative);File.Delete(entityPath);File.Delete(modelPath);
            RefreshComponentsPanel();_entityListPanel.RefreshEntities();_viewport.Invalidate();
        }
        Apply();PushEdit("Create "+result.Recipe.Name,Apply,Revert);_componentsPanel.Select(TerrainComponentsPanel.ComponentKind.Entity,placed.Id);
        _statusLabel.Text="Created "+result.Recipe.Name+" · move with the gizmo · File → Save persists this placement";
        return entityPath;
    }
}
