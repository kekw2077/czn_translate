using System.Runtime.InteropServices;
using CznTranslator.Core.Models;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CznTranslator.Capture;

/// <summary>
/// Crops, converts to grayscale and downscales on the GPU, reading back only the small result
/// (TZ §2).
/// <para>
/// This is the load-bearing piece of the architecture. A 2560×1440 BGRA frame is 14 MB; copying
/// that to system memory 60 times a second is 840 MB/s of PCIe traffic and a stalled pipeline.
/// A 400×80 grayscale ROI is 32 KB, and the 64×64 hash tile is 4 KB. Everything upstream of the
/// readback therefore runs as a compute shader, and only the small buffers ever cross to the CPU.
/// </para>
/// </summary>
public sealed class GpuRoiExtractor : IDisposable
{
    /// <summary>
    /// Box-filters an arbitrary source rectangle down to the output size, weighting BGRA to luma
    /// on the way. One dispatch produces one ready-to-use grayscale buffer.
    /// </summary>
    private const string ShaderSource =
        """
        Texture2D<float4> Source : register(t0);
        RWStructuredBuffer<uint> Output : register(u0);

        cbuffer Params : register(b0)
        {
            int4 SourceRect;    // x, y, width, height
            int2 OutputSize;    // width, height
            int2 Padding;
        };

        [numthreads(8, 8, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= (uint)OutputSize.x || id.y >= (uint)OutputSize.y)
                return;

            // Source span for this output texel; at least one source pixel even when upscaling.
            int x0 = SourceRect.x + (int)(id.x * SourceRect.z / OutputSize.x);
            int x1 = SourceRect.x + (int)(((id.x + 1) * SourceRect.z + OutputSize.x - 1) / OutputSize.x);
            int y0 = SourceRect.y + (int)(id.y * SourceRect.w / OutputSize.y);
            int y1 = SourceRect.y + (int)(((id.y + 1) * SourceRect.w + OutputSize.y - 1) / OutputSize.y);

            x1 = max(x1, x0 + 1);
            y1 = max(y1, y0 + 1);

            float sum = 0.0;
            int count = 0;

            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    float4 texel = Source.Load(int3(x, y, 0));
                    // Rec. 601 luma. Text is drawn as a contrast against its panel, so a
                    // perceptual weighting separates glyph from background better than an
                    // unweighted average does.
                    sum += dot(texel.rgb, float3(0.299, 0.587, 0.114));
                    count++;
                }
            }

            Output[id.y * OutputSize.x + id.x] = (uint)(saturate(sum / count) * 255.0 + 0.5);
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderParams
    {
        public int SourceX;
        public int SourceY;
        public int SourceWidth;
        public int SourceHeight;
        public int OutputWidth;
        public int OutputHeight;
        public int Pad0;
        public int Pad1;
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11ComputeShader _shader;
    private readonly ID3D11Buffer _constants;
    private readonly object _gate = new();

    private ID3D11Buffer? _output;
    private ID3D11Buffer? _staging;
    private ID3D11UnorderedAccessView? _outputView;
    private int _capacity;

    public GpuRoiExtractor(ID3D11Device device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _context = device.ImmediateContext;

        var compiled = Compiler.Compile(ShaderSource, "main", "GpuRoiExtractor", "cs_5_0");
        _shader = device.CreateComputeShader(compiled.Span);

        _constants = device.CreateBuffer(
            (uint)Marshal.SizeOf<ShaderParams>(),
            BindFlags.ConstantBuffer,
            ResourceUsage.Default);
    }

    /// <summary>
    /// Extracts one region and hands back a CPU-side grayscale image. The frame texture stays on
    /// the GPU throughout; only <paramref name="outputWidth"/>×<paramref name="outputHeight"/>
    /// bytes are copied.
    /// </summary>
    public GrayImage Extract(ID3D11Texture2D frame, PixelRect region, int outputWidth, int outputHeight)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (outputWidth <= 0 || outputHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputWidth), "Output must be non-empty.");

        lock (_gate)
        {
            EnsureBuffers(outputWidth * outputHeight);

            using var view = _device.CreateShaderResourceView(frame);

            _context.UpdateSubresource(
                new ShaderParams
                {
                    SourceX = region.X,
                    SourceY = region.Y,
                    SourceWidth = region.Width,
                    SourceHeight = region.Height,
                    OutputWidth = outputWidth,
                    OutputHeight = outputHeight
                },
                _constants);

            _context.CSSetShader(_shader);
            _context.CSSetShaderResource(0, view);
            _context.CSSetUnorderedAccessView(0, _outputView);
            _context.CSSetConstantBuffer(0, _constants);

            _context.Dispatch((uint)((outputWidth + 7) / 8), (uint)((outputHeight + 7) / 8), 1);

            // Unbind before the copy: a resource cannot be a UAV and a copy source at once.
            _context.CSSetUnorderedAccessView(0, null);
            _context.CSSetShaderResource(0, null);

            _context.CopyResource(_staging!, _output!);

            var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var pixels = new byte[outputWidth * outputHeight];
                unsafe
                {
                    var source = (uint*)mapped.DataPointer;
                    for (var i = 0; i < pixels.Length; i++)
                        pixels[i] = (byte)Math.Clamp(source[i], 0u, 255u);
                }

                return new GrayImage(outputWidth, outputHeight, pixels);
            }
            finally
            {
                _context.Unmap(_staging!, 0);
            }
        }
    }

    /// <summary>
    /// Buffers are grown, never shrunk. Zone sizes are stable within a session, so after the
    /// first frame of each zone this allocates nothing at all.
    /// </summary>
    private void EnsureBuffers(int elements)
    {
        if (_capacity >= elements && _output is not null)
            return;

        _outputView?.Dispose();
        _output?.Dispose();
        _staging?.Dispose();

        _capacity = elements;

        _output = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(elements * sizeof(uint)),
            BindFlags = BindFlags.UnorderedAccess,
            Usage = ResourceUsage.Default,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(uint)
        });

        _staging = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(elements * sizeof(uint)),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(uint)
        });

        _outputView = _device.CreateUnorderedAccessView(_output, new UnorderedAccessViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)elements }
        });
    }

    public void Dispose()
    {
        _outputView?.Dispose();
        _output?.Dispose();
        _staging?.Dispose();
        _constants.Dispose();
        _shader.Dispose();
    }
}
