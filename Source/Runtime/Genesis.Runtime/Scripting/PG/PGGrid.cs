using System;

namespace Genesis.Runtime.Scripting.PG
{
    /// <summary>2D grid of numeric cells for PGSL grid scripts.</summary>
    public sealed class PGGrid
    {
        private readonly double[] _cells;
        public int Width { get; }
        public int Height { get; }

        public PGGrid(int width, int height)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            _cells = new double[Width * Height];
        }

        public static PGGrid Create(int width, int height) => new PGGrid(width, height);

        public double Get(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return 0;
            return _cells[y * Width + x];
        }

        public void Set(int x, int y, double value)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return;
            _cells[y * Width + x] = value;
        }
    }
}
