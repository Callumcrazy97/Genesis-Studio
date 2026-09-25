using System;
using System.Buffers.Binary;
using System.IO;
using Genesis.Rendering.Abstractions;
using Genesis.Shared.Materials;

namespace Genesis.Rendering.Textures
{
    /// <summary>One tightly-packed BC5/BC7 DDS mip chain ready for <see cref="IGpuDevice.CreateTexture"/>.</summary>
    public sealed class DdsTextureData
    {
        private const uint DdsMagic = 0x20534444; // "DDS "
        private const uint FourCcDx10 = 0x30315844; // "DX10"
        private const uint FourCcAti2 = 0x32495441; // "ATI2" (legacy BC5)
        private const uint FourCcBc5U = 0x55354342; // "BC5U"
        private const uint DxgiBc5Unorm = 83;
        private const uint DxgiBc7Unorm = 98;
        private const uint DxgiBc7UnormSrgb = 99;
        private const uint ResourceDimensionTexture2D = 3;

        private DdsTextureData(int width, int height, int mipLevels, GpuFormat format, byte[] payload)
        {
            Width = width;
            Height = height;
            MipLevels = mipLevels;
            Format = format;
            Payload = payload;
        }

        public int Width { get; }
        public int Height { get; }
        public int MipLevels { get; }
        public GpuFormat Format { get; }
        public byte[] Payload { get; }

        public static bool TryLoad(string path, TextureColorSpace colorSpace, out DdsTextureData data)
        {
            data = null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length < 128 || ReadUInt32(bytes, 0) != DdsMagic || ReadUInt32(bytes, 4) != 124)
                    return false;

                int height = checked((int)ReadUInt32(bytes, 12));
                int width = checked((int)ReadUInt32(bytes, 16));
                int mipLevels = Math.Max(1, checked((int)ReadUInt32(bytes, 28)));
                uint fourCc = ReadUInt32(bytes, 84);
                int dataOffset = 128;
                GpuFormat format;

                if (fourCc == FourCcDx10)
                {
                    if (bytes.Length < 148) return false;
                    uint dxgiFormat = ReadUInt32(bytes, 128);
                    uint resourceDimension = ReadUInt32(bytes, 132);
                    uint arraySize = ReadUInt32(bytes, 140);
                    if (resourceDimension != ResourceDimensionTexture2D || arraySize != 1) return false;

                    format = dxgiFormat switch
                    {
                        DxgiBc5Unorm => GpuFormat.BC5UNorm,
                        DxgiBc7Unorm or DxgiBc7UnormSrgb => colorSpace == TextureColorSpace.Srgb
                            ? GpuFormat.BC7UNormSrgb
                            : GpuFormat.BC7UNorm,
                        _ => GpuFormat.Unknown,
                    };
                    dataOffset = 148;
                }
                else if (fourCc == FourCcAti2 || fourCc == FourCcBc5U)
                {
                    format = GpuFormat.BC5UNorm;
                }
                else
                {
                    return false;
                }

                if (format == GpuFormat.Unknown || width <= 0 || height <= 0 ||
                    mipLevels > GpuTextureLayout.FullMipCount(width, height))
                    return false;
                int expectedBytes = GpuTextureLayout.GetMipChainSize(format, width, height, mipLevels);
                if (bytes.Length - dataOffset < expectedBytes) return false;

                byte[] payload = new byte[expectedBytes];
                Buffer.BlockCopy(bytes, dataOffset, payload, 0, expectedBytes);
                data = new DdsTextureData(width, height, mipLevels, format, payload);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static uint ReadUInt32(byte[] bytes, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    }
}
