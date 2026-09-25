using System;

namespace Genesis.Rendering.Abstractions
{
    /// <summary>
    /// Backend-neutral byte layout rules for tightly-packed 2D texture uploads.
    /// </summary>
    /// <remarks>
    /// Cooked BC5/BC7 files store mip levels consecutively. Keeping row/slice calculations here
    /// means DX11 and the future Vulkan backend consume exactly the same payload contract.
    /// </remarks>
    public static class GpuTextureLayout
    {
        public static bool IsBlockCompressed(GpuFormat format) => format switch
        {
            GpuFormat.BC5UNorm or GpuFormat.BC7UNorm or GpuFormat.BC7UNormSrgb => true,
            _ => false,
        };

        public static int GetRowPitch(GpuFormat format, int width)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (IsBlockCompressed(format))
                return checked(Math.Max(1, (width + 3) / 4) * 16);
            return checked(width * BytesPerTexel(format));
        }

        public static int GetSlicePitch(GpuFormat format, int width, int height)
        {
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            int rows = IsBlockCompressed(format) ? Math.Max(1, (height + 3) / 4) : height;
            return checked(GetRowPitch(format, width) * rows);
        }

        public static int GetMipChainSize(GpuFormat format, int width, int height, int mipLevels)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (mipLevels <= 0) throw new ArgumentOutOfRangeException(nameof(mipLevels));

            int total = 0;
            int mipWidth = width;
            int mipHeight = height;
            for (int mip = 0; mip < mipLevels; mip++)
            {
                total = checked(total + GetSlicePitch(format, mipWidth, mipHeight));
                mipWidth = Math.Max(1, mipWidth >> 1);
                mipHeight = Math.Max(1, mipHeight >> 1);
            }
            return total;
        }

        public static int FullMipCount(int width, int height)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            int count = 1;
            for (int size = Math.Max(width, height); size > 1; size >>= 1) count++;
            return count;
        }

        private static int BytesPerTexel(GpuFormat format) => format switch
        {
            GpuFormat.R8G8B8A8UNorm or GpuFormat.R8G8B8A8UNormSrgb or GpuFormat.B8G8R8A8UNorm
                or GpuFormat.R11G11B10Float or GpuFormat.R32Float or GpuFormat.D32Float
                or GpuFormat.D24UNormS8UInt or GpuFormat.R32UInt => 4,
            GpuFormat.R16G16B16A16Float or GpuFormat.R32Float2 => 8,
            GpuFormat.R8UNorm => 1,
            GpuFormat.R16Float => 2,
            GpuFormat.R32Float3 => 12,
            GpuFormat.R32Float4 => 16,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown texture byte layout."),
        };
    }
}
