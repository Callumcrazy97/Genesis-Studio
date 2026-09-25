using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ModelEditorControl
{
    private readonly HashSet<(int Mesh,int Vertex)> _selection=[];
    private readonly List<Point> _lasso=[];
    private Point _gestureStart,_gestureEnd;
    private Vector3 _planeStart,_planeEnd;
    private bool _gesture;
    private Keys _selectionModifiers;
    private readonly List<Vector3> _openEdgeChain = [];
    private string _openEdgePlane = "";
    public int SelectedVertexCount=>_selection.Count;

    private bool BeginAuthoringGesture(Point point)
    {
        // Programmatic mode selection used by other authoring actions takes precedence.
        if (_mode is ModelEditorMode.Paint or ModelEditorMode.Sculpt && _tool!=ModelAuthoringTool.Line) return false;
        if (_tool==ModelAuthoringTool.Wand) { SelectConnected(point,ModifierKeys); return true; }
        if (_tool is not (ModelAuthoringTool.Region or ModelAuthoringTool.Lasso or ModelAuthoringTool.BrushSelect
            or ModelAuthoringTool.Line or ModelAuthoringTool.Cube or ModelAuthoringTool.Sphere or ModelAuthoringTool.Cylinder
            or ModelAuthoringTool.SquareFace or ModelAuthoringTool.CircleFace or ModelAuthoringTool.TriangleFace)) return false;
        _gestureStart=_gestureEnd=point;_selectionModifiers=ModifierKeys;_lasso.Clear();_lasso.Add(point);
        _planeStart=Asset.Bounds.Center;
        if (_tool is not (ModelAuthoringTool.Region or ModelAuthoringTool.Lasso or ModelAuthoringTool.BrushSelect))
        {
            if (TryPickSurface(point,out var surface)) _planeStart=surface;
            if (!PlanePoint(point,_planeStart,out _planeStart)) {Status.Text="Choose a drawing plane facing the camera (XY, XZ or YZ).";return true;}
        }
        _planeEnd=_planeStart;_gesture=true;Surface.NavigationEnabled=false;Surface.Host.Capture=true;return true;
    }
    public void PointerMove(Point point,MouseButtons buttons)
    {
        if (UpdatePrimitivePlacement(point)) return;
        if (_meshGizmoDragging && buttons == MouseButtons.Left) { UpdateMeshGizmoDrag(point); return; }
        _hover=TryPickSurface(point,out var hit)?hit:null;
        if(_gesture && buttons==MouseButtons.Left)
        {
            _gestureEnd=point;if(_tool is ModelAuthoringTool.Lasso or ModelAuthoringTool.BrushSelect && (_lasso.Count==0 || Distance(point,_lasso[^1])>3))_lasso.Add(point);
            PlanePoint(point,_planeStart,out _planeEnd);Surface.Invalidate(true);
        }
        else if (_pushPullActive && buttons == MouseButtons.Left) UpdatePushPull(point);
        else if(_stroke && buttons==MouseButtons.Left)ApplyPointerBrush(point);
    }
    public void PointerUp(Point point,MouseButtons button)
    {
        if(button!=MouseButtons.Left)return;
        if (_meshGizmoDragging) { FinishMeshGizmoDrag(); return; }
        if (_pushPullActive) { FinishPushPull(); return; }
        if(!_gesture){FinishStroke();return;}
        _gestureEnd=point;PlanePoint(point,_planeStart,out _planeEnd);_gesture=false;Surface.NavigationEnabled=true;Surface.Host.Capture=false;
        if(_tool is ModelAuthoringTool.Region or ModelAuthoringTool.Lasso)
        {
            SelectRegion(_gestureStart,point,_tool==ModelAuthoringTool.Lasso?_lasso:null,_selectionModifiers);
            return;
        }
        if (_tool == ModelAuthoringTool.BrushSelect)
        {
            SelectBrushStroke(_lasso, _selectionModifiers);
            return;
        }
        if(Distance(_gestureStart,point)<3)return;
        if(_tool==ModelAuthoringTool.Line)
        {
            PlaceLine(_planeStart, _planeEnd);
            return;
        }
        PlaceShape(_tool,_planeStart,_planeEnd);
    }
    private static float Distance(Point a,Point b)=>Vector2.Distance(new(a.X,a.Y),new(b.X,b.Y));
    private void CancelAuthoringGesture(){_gesture=false;_lasso.Clear();Surface.NavigationEnabled=true;Surface.Host.Capture=false;}
    private bool PlanePoint(Point point,Vector3 origin,out Vector3 hit)
    {
        var ray=Surface.PickRay(point);Vector3 normal=DrawingPlane switch{"XZ"=>Vector3.UnitY,"YZ"=>Vector3.UnitX,_=>Vector3.UnitZ};
        float denominator=Vector3.Dot(ray.Direction,normal);hit=origin;if(Math.Abs(denominator)<1e-5f)return false;
        float distance=Vector3.Dot(origin-Asset.Pivot.Position-ray.Origin,normal)/denominator;if(distance<=0)return false;
        hit=ray.Origin+ray.Direction*distance+Asset.Pivot.Position;
        hit = SnapDrawingPoint(hit, origin, point);
        return true;
    }

    private Vector3 SnapDrawingPoint(Vector3 point, Vector3 planeOrigin, Point client)
    {
        if (_snapToVertex)
        {
            float best = 12f * 12f;
            Vector3? nearest = null;
            foreach (GModelMesh mesh in Asset.Meshes)
            foreach (MeshVertex vertex in Vertices(mesh))
            {
                Vector3 projected = Surface.WorldToSurface(vertex.Position - Asset.Pivot.Position);
                if (projected.Z is < 0f or > 1f) continue;
                PointF control = Surface.SurfaceToControl(new PointF(projected.X, projected.Y));
                float dx = control.X - client.X, dy = control.Y - client.Y;
                float distance = dx * dx + dy * dy;
                if (distance >= best) continue;
                best = distance;
                nearest = vertex.Position;
            }
            if (nearest is { } snapped)
            {
                if (DrawingPlane == "YZ") snapped.X = planeOrigin.X;
                else if (DrawingPlane == "XZ") snapped.Y = planeOrigin.Y;
                else snapped.Z = planeOrigin.Z;
                return snapped;
            }
        }
        if (!_snapToGrid) return point;
        float step = Math.Max(.001f, _gridSnap);
        Vector3 result = point;
        if (DrawingPlane != "YZ") result.X = MathF.Round(result.X / step) * step;
        if (DrawingPlane != "XZ") result.Y = MathF.Round(result.Y / step) * step;
        if (DrawingPlane != "XY") result.Z = MathF.Round(result.Z / step) * step;
        if (DrawingPlane == "YZ") result.X = planeOrigin.X;
        else if (DrawingPlane == "XZ") result.Y = planeOrigin.Y;
        else result.Z = planeOrigin.Z;
        return result;
    }
    public void SelectRegion(Point start,Point end,IReadOnlyList<Point>? polygon=null,Keys modifiers=Keys.None)
    {
        var candidates=new HashSet<(int,int)>();var bounds=Rectangle.FromLTRB(Math.Min(start.X,end.X),Math.Min(start.Y,end.Y),Math.Max(start.X,end.X)+1,Math.Max(start.Y,end.Y)+1);
        for(int m=0;m<Asset.Meshes.Count;m++)
        {
            if(_hiddenGroups.Contains(m))continue;var vertices=Vertices(Asset.Meshes[m]);
            for(int v=0;v<vertices.Length;v++)
            {
                var p=Surface.WorldToSurface(vertices[v].Position-Asset.Pivot.Position);
                if(p.Z<0||p.Z>1||(_frontFacesOnly&&!IsVertexFrontFacing(m,v)))continue;
                PointF point=Surface.SurfaceToControl(new PointF(p.X,p.Y));
                if(polygon is {Count:>=3}?Inside(point,polygon):bounds.Contains(Point.Round(point)))candidates.Add((m,v));
            }
        }
        ApplyRegionElementSelection(candidates, polygon, bounds, modifiers);
    }
    private static bool Inside(PointF point,IReadOnlyList<Point> polygon)
    {
        bool inside=false;
        for(int i=0,j=polygon.Count-1;i<polygon.Count;j=i++)
            if((polygon[i].Y>point.Y)!=(polygon[j].Y>point.Y) && point.X<(polygon[j].X-polygon[i].X)*(point.Y-polygon[i].Y)/(float)(polygon[j].Y-polygon[i].Y)+polygon[i].X)inside=!inside;
        return inside;
    }

    private void SelectBrushStroke(IReadOnlyList<Point> stroke, Keys modifiers)
    {
        if (stroke.Count == 0) return;
        float radius = Math.Clamp(_radius * 18f, 7f, 64f);
        HashSet<(int, int)> candidates = [];
        for (int mesh = 0; mesh < Asset.Meshes.Count; mesh++)
        {
            MeshVertex[] vertices = Vertices(Asset.Meshes[mesh]);
            for (int vertex = 0; vertex < vertices.Length; vertex++)
            {
                if (_frontFacesOnly && !IsVertexFrontFacing(mesh, vertex)) continue;
                Vector3 surface = Surface.WorldToSurface(vertices[vertex].Position - Asset.Pivot.Position);
                if (surface.Z is < 0f or > 1f) continue;
                PointF control = Surface.SurfaceToControl(new PointF(surface.X, surface.Y));
                if (stroke.Any(point => Vector2.Distance(new Vector2(point.X, point.Y), new Vector2(control.X, control.Y)) <= radius))
                    candidates.Add((mesh, vertex));
            }
        }
        ApplyRegionElementSelection(candidates, null, Rectangle.Empty, modifiers);
    }
    public void SelectConnected(Point point,Keys modifiers=Keys.None)
    {
        if(!TryPickMesh(point,out int mesh,out var hit)){ApplySelection([],modifiers);return;}
        var vertices=Asset.Meshes.Select(Vertices).ToArray();
        Vector3 start=vertices[mesh].MinBy(v=>Vector3.DistanceSquared(v.Position,hit)).Position;
        var adjacency=new Dictionary<Vector3,HashSet<Vector3>>();
        for(int m=0;m<vertices.Length;m++)
        {
            if(_hiddenGroups.Contains(m))continue;var indices=Asset.Meshes[m].Indices;
            for(int i=0;i+2<indices.Length;i+=3)
            for(int k=0;k<3;k++)
            {
                var a=vertices[m][indices[i+k]].Position;var b=vertices[m][indices[i+(k+1)%3]].Position;
                if(!adjacency.TryGetValue(a,out var neighbours))adjacency[a]=neighbours=[];neighbours.Add(b);
                if(!adjacency.TryGetValue(b,out neighbours))adjacency[b]=neighbours=[];neighbours.Add(a);
            }
        }
        var reached=new HashSet<Vector3>{start};var queue=new Queue<Vector3>();queue.Enqueue(start);
        while(queue.TryDequeue(out var a))if(adjacency.TryGetValue(a,out var neighbours))foreach(var b in neighbours)if(reached.Add(b))queue.Enqueue(b);
        var candidates=new HashSet<(int,int)>();for(int m=0;m<vertices.Length;m++)if(!_hiddenGroups.Contains(m))for(int v=0;v<vertices[m].Length;v++)if(reached.Contains(vertices[m][v].Position))candidates.Add((m,v));
        ApplySelection(candidates,modifiers);
    }
    private void ApplySelection(HashSet<(int,int)> selected,Keys modifiers)
    {
        if((modifiers&Keys.Shift)!=0)_selection.ExceptWith(selected);
        else {if((modifiers&Keys.Control)==0)_selection.Clear();_selection.UnionWith(selected);}
        Status.Text=$"Selected {_selection.Count:N0} vertices · {(_frontFacesOnly ? "front faces only" : "front and back faces")}";Surface.Invalidate(true);
    }
    public void PlaceShape(ModelAuthoringTool tool,Vector3 start,Vector3 end)
    {
        Vector3 u=DrawingPlane=="YZ"?Vector3.UnitY:Vector3.UnitX,v=DrawingPlane=="XY"?Vector3.UnitY:Vector3.UnitZ;
        float width=Math.Abs(Vector3.Dot(end-start,u)),height=Math.Abs(Vector3.Dot(end-start,v));if(width<.0001f||height<.0001f)return;
        Vector3 centre=(start+end)*.5f;Vector3 normal=Vector3.Cross(u,v);
        ChangeAsset("Place "+ToolName(tool),()=>
        {
            int material = NeutralMaterialIndex(); MeshVertex[] vertices;ushort[] indices;
            if(tool is ModelAuthoringTool.Cube or ModelAuthoringTool.Sphere or ModelAuthoringTool.Cylinder)
            {
                var primitive=tool==ModelAuthoringTool.Cube?ModelPrimitiveKind.Cube:tool==ModelAuthoringTool.Sphere?ModelPrimitiveKind.Sphere:ModelPrimitiveKind.Cylinder;
                (vertices,indices)=ModelPartBuilder.Bake([new ModelPart{Primitive=primitive}]);
                var min=vertices.Select(x=>x.Position).Aggregate(Vector3.Min);var max=vertices.Select(x=>x.Position).Aggregate(Vector3.Max);var c=(min+max)*.5f;var size=Vector3.Max(max-min,new Vector3(.0001f));
                for(int i=0;i<vertices.Length;i++)
                {
                    Vector3 q=(vertices[i].Position-c)/size;vertices[i].Position=centre+u*q.X*width+v*q.Y*height+normal*q.Z*Math.Min(width,height);
                    var n=vertices[i].Normal;vertices[i].Normal=Vector3.Normalize(u*n.X/width+v*n.Y/height+normal*n.Z/Math.Min(width,height));
                }
            }
            else
            {
                int count=tool==ModelAuthoringTool.SquareFace?4:tool==ModelAuthoringTool.TriangleFace?3:32;
                vertices=new MeshVertex[count];
                for(int i=0;i<count;i++)
                {
                    Vector2 q=tool==ModelAuthoringTool.SquareFace?new[]{new Vector2(-.5f,-.5f),new Vector2(.5f,-.5f),new Vector2(.5f,.5f),new Vector2(-.5f,.5f)}[i]:new Vector2(MathF.Cos(i*MathF.Tau/count+MathF.PI/2),MathF.Sin(i*MathF.Tau/count+MathF.PI/2))*.5f;
                    vertices[i]=new MeshVertex{Position=centre+u*q.X*width+v*q.Y*height,Normal=normal,UV=q+new Vector2(.5f),Color=Vector4.One};
                }
                ushort[] front=Enumerable.Range(1,count-2).SelectMany(i=>new ushort[]{0,(ushort)i,(ushort)(i+1)}).ToArray();
                indices=MakeDoubleSided(front);
            }
            Asset.Meshes.Add(new GModelMesh{Name=ToolName(tool)+" "+(Asset.Meshes.Count+1),Vertices=vertices,Indices=indices,MaterialIndex=material});
        });
        _selection.Clear();SelectPart(Asset.Meshes.Count-1);
    }

    private void PlaceLine(Vector3 start, Vector3 end)
    {
        Vector3 direction = end - start;
        float length = direction.Length();
        if (length < .0001f) return;
        Vector3 normal = DrawingPlane switch { "XZ" => Vector3.UnitY, "YZ" => Vector3.UnitX, _ => Vector3.UnitZ };
        Vector3 side = Vector3.Cross(normal, direction);
        if (side.LengthSquared() < 1e-8f) return;
        side = Vector3.Normalize(side) * MathF.Max(.0005f, MathF.Min(_gridSnap * .0025f, length * .0015f));
        MeshVertex[] vertices =
        [
            Vertex(start - side, normal, new Vector2(0, 0)), Vertex(start + side, normal, new Vector2(0, 1)),
            Vertex(end + side, normal, new Vector2(1, 1)), Vertex(end - side, normal, new Vector2(1, 0)),
        ];
        ushort[] indices = MakeDoubleSided([0, 1, 2, 0, 2, 3]);
        IReadOnlyList<Vector3>? closedFace = ExtendEdgeChain(start, end);
        ChangeAsset(closedFace is null ? "Create edge" : "Create connected face", () =>
        {
            int material = NeutralMaterialIndex();
            Asset.Meshes.Add(new GModelMesh
            {
                Name = "Edge " + (Asset.Meshes.Count + 1), Vertices = vertices, Indices = indices, MaterialIndex = material,
            });
            if (closedFace is not null) Asset.Meshes.Add(BuildConnectedFace(closedFace, normal, material));
        });
        SelectPart(Asset.Meshes.Count - 1);
        Status.Text = closedFace is null
            ? "Created thin snapped edge · connect back to the first point to create a face"
            : "Closed edge loop created an opaque untextured face.";
        static MeshVertex Vertex(Vector3 position, Vector3 normal, Vector2 uv) => new() { Position = position, Normal = normal, UV = uv, Color = Vector4.One };
    }

    private IReadOnlyList<Vector3>? ExtendEdgeChain(Vector3 start, Vector3 end)
    {
        float tolerance = MathF.Max(.001f, _snapToGrid ? _gridSnap * .01f : .005f);
        bool Near(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) <= tolerance * tolerance;
        if (!string.Equals(_openEdgePlane, DrawingPlane, StringComparison.Ordinal))
        {
            _openEdgeChain.Clear();
            _openEdgePlane = DrawingPlane;
        }
        if (_openEdgeChain.Count == 0)
        {
            _openEdgeChain.Add(start);
            _openEdgeChain.Add(end);
            return null;
        }

        Vector3 last = _openEdgeChain[^1];
        Vector3 next;
        if (Near(start, last)) next = end;
        else if (Near(end, last)) next = start;
        else
        {
            _openEdgeChain.Clear();
            _openEdgeChain.Add(start);
            _openEdgeChain.Add(end);
            return null;
        }

        if (_openEdgeChain.Count >= 3 && Near(next, _openEdgeChain[0]))
        {
            Vector3[] face = [.. _openEdgeChain];
            _openEdgeChain.Clear();
            return face;
        }
        _openEdgeChain.Add(next);
        return null;
    }

    private GModelMesh BuildConnectedFace(IReadOnlyList<Vector3> points, Vector3 normal, int material)
    {
        MeshVertex[] vertices = new MeshVertex[points.Count];
        Vector3 u = DrawingPlane == "YZ" ? Vector3.UnitY : Vector3.UnitX;
        Vector3 v = DrawingPlane == "XY" ? Vector3.UnitY : Vector3.UnitZ;
        float minU = points.Min(point => Vector3.Dot(point, u));
        float maxU = points.Max(point => Vector3.Dot(point, u));
        float minV = points.Min(point => Vector3.Dot(point, v));
        float maxV = points.Max(point => Vector3.Dot(point, v));
        float sizeU = MathF.Max(.0001f, maxU - minU);
        float sizeV = MathF.Max(.0001f, maxV - minV);
        for (int index = 0; index < points.Count; index++)
        {
            Vector3 point = points[index];
            vertices[index] = new MeshVertex
            {
                Position = point,
                Normal = normal,
                UV = new Vector2((Vector3.Dot(point, u) - minU) / sizeU, (Vector3.Dot(point, v) - minV) / sizeV),
                Color = Vector4.One,
            };
        }
        ushort[] front = Enumerable.Range(1, points.Count - 2)
            .SelectMany(index => new ushort[] { 0, (ushort)index, (ushort)(index + 1) }).ToArray();
        return new GModelMesh
        {
            Name = "Face " + (Asset.Meshes.Count + 2),
            Vertices = vertices,
            Indices = MakeDoubleSided(front),
            MaterialIndex = material,
        };
    }
    private void DrawAuthoringOverlay(IRenderController renderer)
    {
        var colour=new RenderColor(.4f,.8f,1);
        Dictionary<int, MeshVertex[]> selectedVertices = [];
        foreach(var (m,vertex) in _selection.Where((_,index)=>index%Math.Max(1,_selection.Count/5000)==0))
        {
            if(m>=Asset.Meshes.Count)continue;
            if (!selectedVertices.TryGetValue(m, out MeshVertex[]? vertices))
                selectedVertices[m] = vertices = Vertices(Asset.Meshes[m]);
            if(vertex>=vertices.Length)continue;
            var point=Surface.WorldToSurface(vertices[vertex].Position-Asset.Pivot.Position);if(point.Z>=0&&point.Z<=1)renderer.DrawRect(point.X-1,point.Y-1,3,3,colour,true,-9500);
        }
        if(!_gesture)return;
        var start=Surface.ControlToSurface(_gestureStart);var end=Surface.ControlToSurface(_gestureEnd);
        if(_tool is ModelAuthoringTool.Lasso or ModelAuthoringTool.BrushSelect)
        {for(int i=1;i<_lasso.Count;i++){var a=Surface.ControlToSurface(_lasso[i-1]);var b=Surface.ControlToSurface(_lasso[i]);renderer.DrawLine(a.X,a.Y,b.X,b.Y,colour,1);}return;}
        if(_tool==ModelAuthoringTool.Line){renderer.DrawLine(start.X,start.Y,end.X,end.Y,colour,2);return;}
        if(_tool==ModelAuthoringTool.Region){renderer.DrawRect(Math.Min(start.X,end.X),Math.Min(start.Y,end.Y),Math.Abs(start.X-end.X),Math.Abs(start.Y-end.Y),colour,false,-9500);return;}
        Vector3 u=DrawingPlane=="YZ"?Vector3.UnitY:Vector3.UnitX,v=DrawingPlane=="XY"?Vector3.UnitY:Vector3.UnitZ;
        Vector3 corner=_planeStart+u*Vector3.Dot(_planeEnd-_planeStart,u);Vector3 other=_planeStart+v*Vector3.Dot(_planeEnd-_planeStart,v);
        Vector3[] corners=[_planeStart,corner,_planeEnd,other,_planeStart];
        for(int i=1;i<corners.Length;i++){var a=Surface.WorldToSurface(corners[i-1]-Asset.Pivot.Position);var b=Surface.WorldToSurface(corners[i]-Asset.Pivot.Position);renderer.DrawLine(a.X,a.Y,b.X,b.Y,colour,2);}
    }
}
