using System.Numerics;
using System.Text.Json;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>Bounded, cancellable generation of ordinary meshes owned by a terrain resource.</summary>
public static class TerrainSectionGenerator
{
    public static TerrainCreationResult Generate(TerrainCreationRecipe input, CancellationToken cancellation = default, float[,]? heightmap = null)
    {
        var recipe = JsonSerializer.Deserialize<TerrainCreationRecipe>(JsonSerializer.Serialize(input))!;
        TerrainRecipeVm? vm = recipe.Source == TerrainCreationSource.Code ? new(recipe, cancellation) : null;
        if (!float.IsFinite(recipe.Width) || !float.IsFinite(recipe.Length) || !float.IsFinite(recipe.Spacing)
            || recipe.Width <= 0 || recipe.Length <= 0 || recipe.Spacing < .01f || recipe.Width > 100000 || recipe.Length > 100000)
            throw new InvalidOperationException("Width and length must be between 0 and 100,000 metres, with sample spacing of at least 0.01 metres.");
        if (!float.IsFinite(recipe.MinHeight) || !float.IsFinite(recipe.MaxHeight) || recipe.MaxHeight <= recipe.MinHeight)
            throw new InvalidOperationException("Maximum height must be above minimum height.");
        cancellation.ThrowIfCancellationRequested();
        var builder = new TerrainSectionMeshBuilder();
        const int previewSize = 256;
        byte[] preview = new byte[previewSize * previewSize * 4];
        if (recipe.Source is TerrainCreationSource.Region or TerrainCreationSource.Lasso)
        {
            Polygon(recipe, builder);
            Array.Fill(preview, (byte)180);
            return new(recipe, builder.Finish(recipe.Name), preview, previewSize, "Flat, untextured terrain section");
        }
        if (recipe.Surface == TerrainCodeSurface.Volume && vm is not null)
            return Volume(recipe, vm, builder, preview, previewSize, cancellation);

        using Bitmap? image = recipe.Source == TerrainCreationSource.Heightmap && heightmap is null ? new Bitmap(recipe.Image) : null;
        // Read once on the generation thread. Heightmap luminance spans the specified height range.
        float[,]? pixels = heightmap;
        if (image is not null)
        {
            if (image.Width > 8192 || image.Height > 8192) throw new InvalidOperationException("Use a heightmap no larger than 8192 × 8192.");
            using Bitmap sampled = new(image, new Size(Math.Min(1024, image.Width), Math.Min(1024, image.Height)));
            pixels = new float[sampled.Width, sampled.Height];
            for (int z = 0; z < sampled.Height; z++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < sampled.Width; x++) { Color c = sampled.GetPixel(x, z); pixels[x, z] = (c.R * .2126f + c.G * .7152f + c.B * .0722f) / 255; }
            }
        }
        int nx = Math.Clamp((int)Math.Ceiling(recipe.Width / recipe.Spacing), 1, 384);
        int nz = Math.Clamp((int)Math.Ceiling(recipe.Length / recipe.Spacing), 1, 384);
        float dx = recipe.Width / nx, dz = recipe.Length / nz;
        var heights = new float[nx + 1, nz + 1];
        float minimum = float.MaxValue, maximum = float.MinValue;
        for (int z = 0; z <= nz; z++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x <= nx; x++)
            {
                float height = vm?.Evaluate(x * dx - recipe.Width / 2, 0, z * dz - recipe.Length / 2) ?? 0;
                if (pixels is not null)
                {
                    float sx = x / (float)nx * (pixels.GetLength(0) - 1), sz = z / (float)nz * (pixels.GetLength(1) - 1);
                    int ax = (int)sx, az = (int)sz, bx = Math.Min(ax + 1, pixels.GetLength(0) - 1), bz = Math.Min(az + 1, pixels.GetLength(1) - 1);
                    float top = float.Lerp(pixels[ax, az], pixels[bx, az], sx - ax), bottom = float.Lerp(pixels[ax, bz], pixels[bx, bz], sx - ax);
                    height = float.Lerp(recipe.MinHeight, recipe.MaxHeight, float.Lerp(top, bottom, sz - az));
                }
                heights[x, z] = height; minimum = Math.Min(minimum, height); maximum = Math.Max(maximum, height);
            }
        }
        Vector3 P(int x, int z) => new(x * dx - recipe.Width / 2, heights[x, z], z * dz - recipe.Length / 2);
        Vector3 N(int x, int z)
        {
            int l = Math.Max(0, x - 1), r = Math.Min(nx, x + 1), t = Math.Max(0, z - 1), b = Math.Min(nz, z + 1);
            return Vector3.Normalize(new Vector3(-(heights[r, z] - heights[l, z]) / ((r - l) * dx), 1, -(heights[x, b] - heights[x, t]) / ((b - t) * dz)));
        }
        for (int z = 0; z < nz; z++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < nx; x++)
            {
                builder.Triangle(P(x,z), P(x+1,z), P(x,z+1), N(x,z), N(x+1,z), N(x,z+1), recipe.Width, recipe.Length);
                builder.Triangle(P(x+1,z), P(x+1,z+1), P(x,z+1), N(x+1,z), N(x+1,z+1), N(x,z+1), recipe.Width, recipe.Length);
            }
        }
        for (int z = 0; z < previewSize; z++) for (int x = 0; x < previewSize; x++)
        {
            float h = heights[x * nx / (previewSize - 1), z * nz / (previewSize - 1)];
            float level = (h - minimum) / Math.Max(.001f, maximum - minimum);
            byte gray = (byte)(level * 255);
            Pixel(preview, (z * previewSize + x) * 4, gray, gray, gray);
        }
        return new(recipe, builder.Finish(recipe.Name), preview, previewSize,
            $"{recipe.Width:N0} × {recipe.Length:N0} m · {nx * nz * 2:N0} triangles\nEffective spacing {dx:0.##} × {dz:0.##} m · heights {minimum:0.##} to {maximum:0.##} m") { Heights = heights };
    }

    private static TerrainCreationResult Volume(TerrainCreationRecipe r, TerrainRecipeVm vm, TerrainSectionMeshBuilder builder, byte[] preview, int size, CancellationToken cancel)
    {
        int nx = Math.Clamp((int)Math.Ceiling(r.Width / r.Spacing), 2, 64), nz = Math.Clamp((int)Math.Ceiling(r.Length / r.Spacing), 2, 64);
        int ny = Math.Clamp((int)Math.Ceiling((r.MaxHeight - r.MinHeight) / r.Spacing), 2, 64);
        float dx = r.Width / nx, dy = (r.MaxHeight - r.MinHeight) / ny, dz = r.Length / nz;
        var field = new float[nx+1,ny+1,nz+1];
        Vector3 P(int x,int y,int z) => new(x*dx-r.Width/2, r.MinHeight+y*dy, z*dz-r.Length/2);
        for (int z=0; z<=nz; z++) for (int y=0; y<=ny; y++) for (int x=0; x<=nx; x++) { Vector3 p=P(x,y,z); field[x,y,z]=vm.Evaluate(p.X,p.Y,p.Z); }
        Vector3 N(int x,int y,int z)
        {
            int l=Math.Max(0,x-1), rr=Math.Min(nx,x+1), b=Math.Max(0,y-1), t=Math.Min(ny,y+1), f=Math.Max(0,z-1), a=Math.Min(nz,z+1);
            Vector3 gradient = new((field[rr,y,z]-field[l,y,z])/((rr-l)*dx), (field[x,t,z]-field[x,b,z])/((t-b)*dy), (field[x,y,a]-field[x,y,f])/((a-f)*dz));
            return gradient.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(gradient);
        }
        int[][] tetra = [[0,5,1,6],[0,1,2,6],[0,2,3,6],[0,3,7,6],[0,7,4,6],[0,4,5,6]];
        (int X,int Y,int Z)[] corners = [(0,0,0),(1,0,0),(1,1,0),(0,1,0),(0,0,1),(1,0,1),(1,1,1),(0,1,1)];
        for(int z=0;z<nz;z++) for(int y=0;y<ny;y++) for(int x=0;x<nx;x++)
        {
            cancel.ThrowIfCancellationRequested();
            foreach(int[] tet in tetra)
            {
                var points = new List<(Vector3 P,Vector3 N)>(4);
                for(int i=0;i<4;i++) for(int j=i+1;j<4;j++)
                {
                    var a=corners[tet[i]]; var b=corners[tet[j]];
                    float va=field[x+a.X,y+a.Y,z+a.Z], vb=field[x+b.X,y+b.Y,z+b.Z];
                    if((va<0)==(vb<0)) continue;
                    float t=va/(va-vb);
                    Vector3 normal=Vector3.Lerp(N(x+a.X,y+a.Y,z+a.Z),N(x+b.X,y+b.Y,z+b.Z),t);
                    points.Add((Vector3.Lerp(P(x+a.X,y+a.Y,z+a.Z),P(x+b.X,y+b.Y,z+b.Z),t), normal.LengthSquared()>1e-12f?Vector3.Normalize(normal):Vector3.UnitY));
                }
                if(points.Count<3) continue;
                Vector3 centre=points.Aggregate(Vector3.Zero,(sum,p)=>sum+p.P)/points.Count;
                Vector3 n=points.Aggregate(Vector3.Zero,(sum,p)=>sum+p.N);
                n=n.LengthSquared()>1e-12f?Vector3.Normalize(n):Vector3.UnitY;
                Vector3 u=Vector3.Normalize(points[0].P-centre), v=Vector3.Cross(n,u);
                points.Sort((a,b)=>MathF.Atan2(Vector3.Dot(a.P-centre,v),Vector3.Dot(a.P-centre,u)).CompareTo(MathF.Atan2(Vector3.Dot(b.P-centre,v),Vector3.Dot(b.P-centre,u))));
                for(int i=1;i<points.Count-1;i++) builder.Triangle(points[0].P,points[i].P,points[i+1].P,points[0].N,points[i].N,points[i+1].N,r.Width,r.Length);
            }
        }
        for(int y=0;y<size;y++) for(int z=0;z<size;z++)
        {
            bool solid=field[nx/2,(size-1-y)*ny/(size-1),z*nz/(size-1)]<0;
            Pixel(preview,(y*size+z)*4,solid?(byte)130:(byte)27,solid?(byte)146:(byte)33,solid?(byte)118:(byte)43);
        }
        return new(r,builder.Finish(r.Name),preview,size,$"{r.Width:N0} × {r.Length:N0} m · volume surface\nCentre cross-section: green solid, dark air · samples {dx:0.##} × {dy:0.##} × {dz:0.##} m");
    }

    private static void Polygon(TerrainCreationRecipe r, TerrainSectionMeshBuilder builder)
    {
        if (r.Boundary.Length > 512 || r.Boundary.Any(p => p.Length < 2 || !float.IsFinite(p[0]) || !float.IsFinite(p[1])))
            throw new InvalidOperationException("An outline must contain at most 512 finite points.");
        var points = r.Boundary.Select(p=>new Vector2(p[0],p[1])).ToList();
        if(points.Count<3) points=[new(-r.Width/2,-r.Length/2),new(r.Width/2,-r.Length/2),new(r.Width/2,r.Length/2),new(-r.Width/2,r.Length/2)];
        float Cross(Vector2 a,Vector2 b,Vector2 c)=>(b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
        for(int i=0;i<points.Count;i++) for(int j=i+2;j<points.Count;j++)
        {
            if(i==0&&j==points.Count-1) continue;
            Vector2 a=points[i],b=points[(i+1)%points.Count],c=points[j],d=points[(j+1)%points.Count];
            if(Cross(a,b,c)*Cross(a,b,d)<0&&Cross(c,d,a)*Cross(c,d,b)<0) throw new InvalidOperationException("The lasso crosses itself. Draw a single closed outline.");
        }
        float area=0; for(int i=0;i<points.Count;i++) { var a=points[i];var b=points[(i+1)%points.Count];area+=a.X*b.Y-b.X*a.Y; }
        if(Math.Abs(area)<.001f) throw new InvalidOperationException("Draw a larger terrain section.");
        if(area<0) points.Reverse();
        while(points.Count>2)
        {
            bool clipped=false;
            for(int i=0;i<points.Count;i++)
            {
                var a=points[(i+points.Count-1)%points.Count];var b=points[i];var c=points[(i+1)%points.Count];
                if(Cross(a,b,c)<=1e-7f) continue;
                if(points.Any(p=>p!=a&&p!=b&&p!=c&&Cross(a,b,p)>=0&&Cross(b,c,p)>=0&&Cross(c,a,p)>=0)) continue;
                builder.Triangle(new(a.X,0,a.Y),new(b.X,0,b.Y),new(c.X,0,c.Y),Vector3.UnitY,Vector3.UnitY,Vector3.UnitY,r.Width,r.Length);
                points.RemoveAt(i);clipped=true;break;
            }
            if(!clipped) throw new InvalidOperationException("This outline is too narrow or overlaps. Draw a simpler closed outline.");
        }
    }
    private static void Pixel(byte[] pixels,int at,byte r,byte g,byte b) { pixels[at]=r;pixels[at+1]=g;pixels[at+2]=b;pixels[at+3]=255; }
}
