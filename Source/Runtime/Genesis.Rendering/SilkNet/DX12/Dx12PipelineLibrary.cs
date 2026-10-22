using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Genesis.Rendering.Diagnostics;
using Genesis.Rendering.Primitives;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace Genesis.Rendering.SilkNet.DX12
{
    /// <summary>
    /// The pipeline states this engine build has made on this PC, kept on disk (an
    /// ID3D12PipelineLibrary), so a later run loads each one instead of compiling it again: a
    /// pipeline state is made the first time a draw needs it, and a first compile can hold a frame.
    /// </summary>
    /// <remarks>
    /// Each pipeline is filed under a hash of everything that makes it (shader code, blend, depth
    /// and raster states, attachment formats, topology, vertex layout), never under a handle of the
    /// session. One file per engine build lives beside the shader cache; files of other builds are
    /// deleted when a new one is made. A file another driver or GPU wrote is not accepted by D3D12,
    /// and an empty library takes its place. Nothing here may stop a game: any failure only means
    /// the pipeline is compiled as before.
    /// </remarks>
    internal sealed unsafe class Dx12PipelineLibrary : IDisposable
    {
        private readonly object _gate = new();
        private readonly string _path;
        private ComPtr<ID3D12PipelineLibrary> _library;
        // The file's bytes: D3D12 reads the library from them for as long as it exists.
        private void* _blob;
        private bool _dirty;
        private long _lastSave = Stopwatch.GetTimestamp();

        /// <summary>Pipeline states loaded from the file, and compiled because the file did not have them, in this process.</summary>
        internal static int Loaded, Compiled;

        private Dx12PipelineLibrary(string path) => _path = path;

        /// <summary>The library for a device; null when the device or the folder cannot keep one.</summary>
        public static Dx12PipelineLibrary Open(ComPtr<ID3D12Device> device)
        {
            if (Environment.GetEnvironmentVariable("GENESIS_DX12_PIPELINE_LIBRARY") == "0") return null;
            ComPtr<ID3D12Device1> device1 = default;
            if (device.Handle->QueryInterface(SilkMarshal.GuidPtrOf<ID3D12Device1>(), (void**)device1.GetAddressOf()) < 0)
                return null;
            try
            {
                string folder = Path.Combine(ShaderBinaryCache.ResolveRoot(null), "DX12Pipelines");
                string build = typeof(Dx12PipelineLibrary).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..16];
                var library = new Dx12PipelineLibrary(Path.Combine(folder, "pipelines-" + build + ".bin"));
                library.Create(device1, folder);
                return library._library.Handle == null ? null : library;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                RenderLog.Line("[DX12] Pipeline library not kept: " + error.Message);
                return null;
            }
            finally
            {
                device1.Dispose();
            }
        }

        private void Create(ComPtr<ID3D12Device1> device1, string folder)
        {
            byte[] bytes = null;
            if (File.Exists(_path))
            {
                bytes = File.ReadAllBytes(_path);
            }
            else if (Directory.Exists(folder))
            {
                // Libraries of other engine builds: their pipelines are never asked for again.
                foreach (string old in Directory.EnumerateFiles(folder, "pipelines-*.bin"))
                    try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            if (bytes is { Length: > 0 })
            {
                _blob = NativeMemory.Alloc((nuint)bytes.Length);
                bytes.CopyTo(new Span<byte>(_blob, bytes.Length));
                int hr = device1.Handle->CreatePipelineLibrary(_blob, (nuint)bytes.Length,
                    SilkMarshal.GuidPtrOf<ID3D12PipelineLibrary>(), (void**)_library.GetAddressOf());
                if (hr >= 0)
                {
                    RenderLog.Line($"[DX12] Pipeline library read: {bytes.Length / 1024} KB");
                    return;
                }
                // Another driver or GPU wrote it, or it is damaged: start an empty one.
                RenderLog.Line($"[DX12] Pipeline library not accepted (0x{hr:X8}); starting a new one");
                NativeMemory.Free(_blob);
                _blob = null;
            }
            int created = device1.Handle->CreatePipelineLibrary(null, 0,
                SilkMarshal.GuidPtrOf<ID3D12PipelineLibrary>(), (void**)_library.GetAddressOf());
            if (created < 0) RenderLog.Line($"[DX12] Pipeline library could not be made (0x{created:X8})");
        }

        /// <summary>The name a pipeline is filed under: a hash of everything that makes it.</summary>
        public static string NameOf(GraphicsPipelineStateDesc* desc, byte[] vertexShader, byte[] pixelShader)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(vertexShader ?? []);
            hash.AppendData([0x7C]);
            hash.AppendData(pixelShader ?? []);
            AppendStruct(hash, desc->BlendState);
            AppendStruct(hash, desc->DepthStencilState);
            AppendStruct(hash, desc->RasterizerState);
            AppendStruct(hash, desc->NumRenderTargets);
            for (int i = 0; i < 8; i++) AppendStruct(hash, desc->RTVFormats[i]);
            AppendStruct(hash, desc->DSVFormat);
            AppendStruct(hash, desc->PrimitiveTopologyType);
            AppendStruct(hash, desc->SampleMask);
            for (uint i = 0; i < desc->InputLayout.NumElements; i++)
            {
                InputElementDesc element = desc->InputLayout.PInputElementDescs[i];
                hash.AppendData(Encoding.ASCII.GetBytes(Marshal.PtrToStringAnsi((nint)element.SemanticName) ?? string.Empty));
                AppendStruct(hash, element.SemanticIndex);
                AppendStruct(hash, element.Format);
                AppendStruct(hash, element.InputSlot);
                AppendStruct(hash, element.AlignedByteOffset);
                AppendStruct(hash, element.InputSlotClass);
                AppendStruct(hash, element.InstanceDataStepRate);
            }
            return Convert.ToHexString(hash.GetHashAndReset(), 0, 16);
        }

        private static void AppendStruct<T>(IncrementalHash hash, T value) where T : unmanaged =>
            hash.AppendData(new ReadOnlySpan<byte>(&value, sizeof(T)));

        /// <summary>The pipeline filed under this name, made for <paramref name="desc"/>; 0 when the library has none.</summary>
        public nint Load(string name, GraphicsPipelineStateDesc* desc)
        {
            lock (_gate)
            {
                if (_library.Handle == null) return 0;
                ID3D12PipelineState* pso = null;
                int hr;
                fixed (char* key = name)
                    hr = _library.Handle->LoadGraphicsPipeline(key, desc, SilkMarshal.GuidPtrOf<ID3D12PipelineState>(), (void**)&pso);
                if (hr < 0 || pso == null) return 0;
                System.Threading.Interlocked.Increment(ref Loaded);
                return (nint)pso;
            }
        }

        /// <summary>Files a pipeline just compiled; the file is written now and then and when the device closes.</summary>
        public void Store(string name, nint pipeline)
        {
            System.Threading.Interlocked.Increment(ref Compiled);
            lock (_gate)
            {
                if (_library.Handle == null || pipeline == 0) return;
                int hr;
                fixed (char* key = name)
                    hr = _library.Handle->StorePipeline(key, (ID3D12PipelineState*)pipeline);
                if (hr < 0) return;
                _dirty = true;
            }
            if (Stopwatch.GetElapsedTime(_lastSave).TotalSeconds >= 10) Save();
        }

        /// <summary>Writes the library to its file when pipelines were added since it was last written.</summary>
        public void Save()
        {
            lock (_gate)
            {
                if (!_dirty || _library.Handle == null) return;
                _dirty = false;
                _lastSave = Stopwatch.GetTimestamp();
                try
                {
                    nuint size = _library.Handle->GetSerializedSize();
                    if (size == 0 || size > int.MaxValue) return;
                    byte[] bytes = new byte[(int)size];
                    fixed (byte* data = bytes)
                        if (_library.Handle->Serialize(data, size) < 0) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    string temporary = _path + "." + Environment.ProcessId + ".tmp";
                    File.WriteAllBytes(temporary, bytes);
                    File.Move(temporary, _path, overwrite: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Another process writing it, or a folder that cannot be written: next time.
                    RenderLog.Line("[DX12] Pipeline library not written: " + error.Message);
                }
            }
        }

        public void Dispose()
        {
            Save();
            lock (_gate)
            {
                _library.Dispose();
                _library = default;
                if (_blob != null) NativeMemory.Free(_blob);
                _blob = null;
            }
        }
    }
}
