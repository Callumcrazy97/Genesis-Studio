#nullable enable
using System;
using System.Runtime.InteropServices;

namespace Genesis.Runtime.Recording;

/// <summary>
/// Writes H.264 MP4 video through Windows Media Foundation, which ships with Windows: no codec to
/// install or licence to carry. Frames are 32-bit BGRA, rows from the top. Windows "N" editions
/// without the media pack have no H.264 encoder; the constructor throws there and the recorder
/// keeps the frames as pictures instead.
/// </summary>
internal sealed class Mp4Writer : IDisposable
{
    private const uint MfVersion = 0x00020070;
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid AverageBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private const uint Progressive = 2;

    private readonly IMFSinkWriter _writer;
    private readonly uint _stream;
    private readonly int _width;
    private readonly int _height;
    private readonly long _frameTicks;
    private long _time;
    private bool _finished;

    public Mp4Writer(string path, int width, int height, int fps)
    {
        // H.264 needs even dimensions; the frames are cropped by a pixel when they are odd.
        _width = Math.Max(2, width & ~1);
        _height = Math.Max(2, height & ~1);
        fps = Math.Clamp(fps, 1, 120);
        _frameTicks = 10_000_000L / fps;
        Check(MFStartup(MfVersion, 0), "start");
        Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out _writer), "create the file");

        Check(MFCreateMediaType(out IMFMediaType output), "make the output type");
        Guid key = MajorType, value = Video;
        Check(output.SetGUID(ref key, ref value), "set the output kind");
        key = Subtype; value = H264;
        Check(output.SetGUID(ref key, ref value), "choose H.264");
        key = AverageBitrate;
        Check(output.SetUINT32(ref key, (uint)Math.Clamp((long)_width * _height * fps / 5, 1_000_000, 40_000_000)), "set the bitrate");
        key = InterlaceMode;
        Check(output.SetUINT32(ref key, Progressive), "set progressive");
        key = FrameSize;
        Check(output.SetUINT64(ref key, Pack((uint)_width, (uint)_height)), "set the frame size");
        key = FrameRate;
        Check(output.SetUINT64(ref key, Pack((uint)fps, 1)), "set the frame rate");
        key = PixelAspectRatio;
        Check(output.SetUINT64(ref key, Pack(1, 1)), "set square pixels");
        Check(_writer.AddStream(output, out _stream), "add the video stream");
        Marshal.ReleaseComObject(output);

        Check(MFCreateMediaType(out IMFMediaType input), "make the input type");
        key = MajorType; value = Video;
        Check(input.SetGUID(ref key, ref value), "set the input kind");
        key = Subtype; value = Rgb32;
        Check(input.SetGUID(ref key, ref value), "choose RGB32 input");
        key = InterlaceMode;
        Check(input.SetUINT32(ref key, Progressive), "set progressive input");
        key = FrameSize;
        Check(input.SetUINT64(ref key, Pack((uint)_width, (uint)_height)), "set the input size");
        key = FrameRate;
        Check(input.SetUINT64(ref key, Pack((uint)fps, 1)), "set the input rate");
        key = PixelAspectRatio;
        Check(input.SetUINT64(ref key, Pack(1, 1)), "set square input pixels");
        key = DefaultStride;
        // A positive stride: rows run from the top, as the renderer reads them.
        Check(input.SetUINT32(ref key, (uint)(_width * 4)), "set the row order");
        Check(_writer.SetInputMediaType(_stream, input, IntPtr.Zero), "accept BGRA frames");
        Marshal.ReleaseComObject(input);
        Check(_writer.BeginWriting(), "begin writing");
    }

    /// <summary>Adds one frame shown for <paramref name="repeat"/> frame times (a held frame).</summary>
    public void WriteFrame(byte[] bgra, int width, int height, int repeat = 1)
    {
        int stride = _width * 4;
        uint length = (uint)(stride * _height);
        Check(MFCreateMemoryBuffer(length, out IMFMediaBuffer buffer), "make a frame buffer");
        try
        {
            Check(buffer.Lock(out IntPtr data, out _, out _), "lock a frame");
            try
            {
                int rows = Math.Min(_height, height), copy = Math.Min(stride, width * 4);
                for (int y = 0; y < rows; y++) Marshal.Copy(bgra, y * width * 4, data + y * stride, copy);
            }
            finally { buffer.Unlock(); }
            Check(buffer.SetCurrentLength(length), "size a frame");
            Check(MFCreateSample(out IMFSample sample), "make a sample");
            try
            {
                Check(sample.AddBuffer(buffer), "add a frame");
                Check(sample.SetSampleTime(_time), "time a frame");
                long duration = _frameTicks * Math.Max(1, repeat);
                Check(sample.SetSampleDuration(duration), "time a frame");
                Check(_writer.WriteSample(_stream, sample), "write a frame");
                _time += duration;
            }
            finally { Marshal.ReleaseComObject(sample); }
        }
        finally { Marshal.ReleaseComObject(buffer); }
    }

    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        Check(_writer.DoFinalize(), "finish the file");
    }

    public void Dispose()
    {
        try { Finish(); }
        catch (InvalidOperationException) { }
        Marshal.ReleaseComObject(_writer);
        MFShutdown();
    }

    private static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    private static void Check(int hr, string step)
    {
        if (hr < 0) throw new InvalidOperationException($"Windows could not {step} for the video (0x{hr:X8}).");
    }

    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateSample(out IMFSample sample);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attributes, out IMFSinkWriter writer);

    // Only the slots up to the last one called need to be declared, in vtable order; the
    // placeholders stand for IMFAttributes methods Genesis never calls.
    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType
    {
        [PreserveSig] int GetItem();
        [PreserveSig] int GetItemType();
        [PreserveSig] int CompareItem();
        [PreserveSig] int Compare();
        [PreserveSig] int GetUINT32();
        [PreserveSig] int GetUINT64();
        [PreserveSig] int GetDouble();
        [PreserveSig] int GetGUID();
        [PreserveSig] int GetStringLength();
        [PreserveSig] int GetString();
        [PreserveSig] int GetAllocatedString();
        [PreserveSig] int GetBlobSize();
        [PreserveSig] int GetBlob();
        [PreserveSig] int GetAllocatedBlob();
        [PreserveSig] int GetUnknown();
        [PreserveSig] int SetItem();
        [PreserveSig] int DeleteItem();
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble();
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        // IMFAttributes (30 slots).
        [PreserveSig] int A01(); [PreserveSig] int A02(); [PreserveSig] int A03(); [PreserveSig] int A04(); [PreserveSig] int A05();
        [PreserveSig] int A06(); [PreserveSig] int A07(); [PreserveSig] int A08(); [PreserveSig] int A09(); [PreserveSig] int A10();
        [PreserveSig] int A11(); [PreserveSig] int A12(); [PreserveSig] int A13(); [PreserveSig] int A14(); [PreserveSig] int A15();
        [PreserveSig] int A16(); [PreserveSig] int A17(); [PreserveSig] int A18(); [PreserveSig] int A19(); [PreserveSig] int A20();
        [PreserveSig] int A21(); [PreserveSig] int A22(); [PreserveSig] int A23(); [PreserveSig] int A24(); [PreserveSig] int A25();
        [PreserveSig] int A26(); [PreserveSig] int A27(); [PreserveSig] int A28(); [PreserveSig] int A29(); [PreserveSig] int A30();
        // IMFSample.
        [PreserveSig] int GetSampleFlags();
        [PreserveSig] int SetSampleFlags();
        [PreserveSig] int GetSampleTime();
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration();
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount();
        [PreserveSig] int GetBufferByIndex();
        [PreserveSig] int ConvertToContiguousBuffer();
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint length);
        [PreserveSig] int SetCurrentLength(uint length);
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSinkWriter
    {
        [PreserveSig] int AddStream(IMFMediaType targetType, out uint streamIndex);
        [PreserveSig] int SetInputMediaType(uint streamIndex, IMFMediaType inputType, IntPtr encodingParameters);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(uint streamIndex, IMFSample sample);
        [PreserveSig] int SendStreamTick();
        [PreserveSig] int PlaceMarker();
        [PreserveSig] int NotifyEndOfSegment();
        [PreserveSig] int Flush();
        [PreserveSig] int DoFinalize();
    }
}
