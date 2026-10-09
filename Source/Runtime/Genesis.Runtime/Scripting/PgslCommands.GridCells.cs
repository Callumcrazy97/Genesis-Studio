using System;
using System.Runtime.CompilerServices;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// A grid's cells. A grid made with DsGridCreate(width, height) holds numbers (8 bytes a cell), as
// grids always have; DsGridCreate(width, height, kind) makes a compact grid of small whole numbers
// or single-precision numbers instead, for a block world's ids and light levels:
//
//   "u8"  0..255                         1 byte a cell
//   "u16" 0..65 535                      2 bytes
//   "i32" -2 147 483 648..2 147 483 647  4 bytes
//   "f32" single precision (about 7 significant digits; whole numbers exact to 16 777 216), 4 bytes
//
// Every grid command works on every kind and reads give numbers. A value written to a whole-number
// kind loses its fraction (towards zero) and is clamped to the kind's range (300 in a u8 grid is
// 255, -1 is 0; NaN is 0), so a light level taken below 0 stays 0 instead of wrapping to 255.
public static partial class PgslCommands
{
    /// <summary>What a grid's cells hold.</summary>
    internal enum GridKind : byte { Number, U8, U16, I32, F32 }

    /// <summary>Most bytes one grid's cells may take: a number grid's 1 000 000 cells, so a u16 grid may have 4 000 000.</summary>
    private const long MaxGridBytes = (long)MaxCollectionElements * sizeof(double);

    private sealed class PgslGrid
    {
        /// <summary>A number grid's cells (empty for a compact grid).</summary>
        public double[] Cells = [];
        // A compact grid's cells: the one array of its kind; the others stay null.
        public byte[] U8;
        public ushort[] U16;
        public int[] I32;
        public float[] F32;
        public GridKind Kind;
        public int Width;
        public int Height;

        public static PgslGrid Create(GridKind kind, int width, int height)
        {
            var grid = new PgslGrid { Kind = kind, Width = width, Height = height };
            grid.Allocate(width * height);
            return grid;
        }

        private void Allocate(int count)
        {
            Cells = Kind == GridKind.Number ? new double[count] : [];
            U8 = Kind == GridKind.U8 ? new byte[count] : null;
            U16 = Kind == GridKind.U16 ? new ushort[count] : null;
            I32 = Kind == GridKind.I32 ? new int[count] : null;
            F32 = Kind == GridKind.F32 ? new float[count] : null;
        }

        /// <summary>The array holding the cells, whatever its kind.</summary>
        public Array Storage => Kind switch
        {
            GridKind.U8 => U8,
            GridKind.U16 => U16,
            GridKind.I32 => I32,
            GridKind.F32 => F32,
            _ => Cells,
        };

        public bool Inside(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        public double Get(int x, int y) => Inside(x, y) ? At((y * Width) + x) : 0;

        public void Set(int x, int y, double value)
        {
            if (Inside(x, y)) Put((y * Width) + x, value);
        }

        /// <summary>The cell at an index (y * width + x) as a number. A number grid reads its array straight.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double At(int index) => Kind == GridKind.Number ? Cells[index] : CompactAt(index);

        /// <summary>Writes the cell at an index, as the grid's kind holds it (see the top of this file).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Put(int index, double value)
        {
            if (Kind == GridKind.Number) Cells[index] = value;
            else PutCompact(index, value);
        }

        // Each arm a double of its own: left to itself the switch would take float, the arms' common
        // type, and round large i32 values.
        private double CompactAt(int index) => Kind switch
        {
            GridKind.U8 => (double)U8[index],
            GridKind.U16 => (double)U16[index],
            GridKind.I32 => (double)I32[index],
            _ => (double)F32[index],
        };

        private void PutCompact(int index, double value)
        {
            switch (Kind)
            {
                case GridKind.U8: U8[index] = ToU8(value); break;
                case GridKind.U16: U16[index] = ToU16(value); break;
                case GridKind.I32: I32[index] = ToI32(value); break;
                default: F32[index] = (float)value; break;
            }
        }

        /// <summary>Every cell set to a value (as this kind holds it).</summary>
        public void Fill(double value)
        {
            switch (Kind)
            {
                case GridKind.U8: Array.Fill(U8, ToU8(value)); break;
                case GridKind.U16: Array.Fill(U16, ToU16(value)); break;
                case GridKind.I32: Array.Fill(I32, ToI32(value)); break;
                case GridKind.F32: Array.Fill(F32, (float)value); break;
                default: Array.Fill(Cells, value); break;
            }
        }

        /// <summary>A grid of the same kind and size that shares no cells with this one.</summary>
        public PgslGrid Clone()
        {
            var copy = new PgslGrid { Kind = Kind, Width = Width, Height = Height };
            copy.CopyCellsFrom(this);
            return copy;
        }

        /// <summary>This grid becomes an exact copy of another: its kind, size and cells.</summary>
        public void CopyFrom(PgslGrid source)
        {
            if (ReferenceEquals(this, source)) return;
            Kind = source.Kind;
            Width = source.Width;
            Height = source.Height;
            CopyCellsFrom(source);
        }

        private void CopyCellsFrom(PgslGrid source)
        {
            Cells = source.Kind == GridKind.Number ? (double[])source.Cells.Clone() : [];
            U8 = (byte[])source.U8?.Clone();
            U16 = (ushort[])source.U16?.Clone();
            I32 = (int[])source.I32?.Clone();
            F32 = (float[])source.F32?.Clone();
        }

        /// <summary>New dimensions, keeping the cells both sizes have; new cells are 0.</summary>
        public void Resize(int width, int height)
        {
            Array before = Storage;
            int oldWidth = Width, copyWidth = Math.Min(width, Width), copyHeight = Math.Min(height, Height);
            Allocate(width * height);
            Array after = Storage;
            for (int y = 0; y < copyHeight; y++) Array.Copy(before, y * oldWidth, after, y * width, copyWidth);
            Width = width;
            Height = height;
        }

        /// <summary>
        /// Copies a run of cells from one grid to another (or within one): as they are between grids of
        /// one kind (overlapping runs too), converted to the destination's kind otherwise.
        /// </summary>
        public static void CopyRun(PgslGrid from, int fromIndex, PgslGrid to, int toIndex, int count)
        {
            if (count <= 0) return;
            if (from.Kind == to.Kind)
            {
                Array.Copy(from.Storage, fromIndex, to.Storage, toIndex, count);
                return;
            }
            for (int i = 0; i < count; i++) to.Put(toIndex + i, from.At(fromIndex + i));
        }

        public static byte ToU8(double value) => value >= byte.MaxValue ? byte.MaxValue : value > 0 ? (byte)value : (byte)0;

        public static ushort ToU16(double value) => value >= ushort.MaxValue ? ushort.MaxValue : value > 0 ? (ushort)value : (ushort)0;

        public static int ToI32(double value) =>
            value >= int.MaxValue ? int.MaxValue : value <= int.MinValue ? int.MinValue : double.IsNaN(value) ? 0 : (int)value;
    }

    /// <summary>Bytes one cell of a kind takes.</summary>
    private static int CellBytes(GridKind kind) => kind switch
    {
        GridKind.U8 => 1,
        GridKind.U16 => 2,
        GridKind.I32 or GridKind.F32 => 4,
        _ => 8,
    };

    /// <summary>A kind's name in DsGridCreate: "u8", "u16", "i32", "f32", or "f64" (also "", "double" or "number") for numbers.</summary>
    private static bool TryGridKind(string text, out GridKind kind)
    {
        kind = GridKind.Number;
        switch (text?.Trim().ToLowerInvariant())
        {
            case null or "" or "f64" or "double" or "number": return true;
            case "u8": kind = GridKind.U8; return true;
            case "u16": kind = GridKind.U16; return true;
            case "i32": kind = GridKind.I32; return true;
            case "f32": kind = GridKind.F32; return true;
            default: return false;
        }
    }

    private static string GridKindName(GridKind kind) => kind switch
    {
        GridKind.U8 => "u8",
        GridKind.U16 => "u16",
        GridKind.I32 => "i32",
        GridKind.F32 => "f32",
        _ => "f64",
    };

    /// <summary>Whether a grid of this kind and size is within the per-grid limit (dimensions are clamped already).</summary>
    private static bool GridFits(GridKind kind, int width, int height) => (long)width * height * CellBytes(kind) <= MaxGridBytes;

    [PgslCommand("DsGridKind", "DsGridKind(id) -> string",
        "What a grid's cells hold: \"f64\" (numbers), \"u8\", \"u16\", \"i32\" or \"f32\" (see DsGridCreate); empty when there is no such grid", "Grids")]
    public static string DsGridKind(double id)
    {
        PgslGrid grid = ResolveRead<PgslGrid>("grid", id);
        return grid is null ? string.Empty : GridKindName(grid.Kind);
    }

    [PgslCommand("DsGridBytes", "DsGridBytes(id) -> number",
        "How many bytes a grid's cells take (width x height x 8 for numbers, 1 for u8, 2 for u16, 4 for i32 and f32); 0 when there is no such grid", "Grids")]
    public static double DsGridBytes(double id)
    {
        PgslGrid grid = ResolveRead<PgslGrid>("grid", id);
        return grid is null ? 0 : (double)grid.Width * grid.Height * CellBytes(grid.Kind);
    }
}
