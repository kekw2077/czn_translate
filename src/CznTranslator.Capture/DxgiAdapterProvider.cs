using CznTranslator.Core.Abstractions;
using Vortice.DXGI;

namespace CznTranslator.Capture;

/// <summary>
/// DXGI adapter enumeration for the §4 provider cascade. The ordering and the WARP rejection
/// live in <c>CznTranslator.Ocr.AdapterSelection</c>; this only reports what the system has.
/// </summary>
public sealed class DxgiAdapterProvider : IGraphicsAdapterProvider
{
    public IReadOnlyList<GraphicsAdapter> Enumerate()
    {
        var adapters = new List<GraphicsAdapter>();

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (var index = 0; ; index++)
        {
            if (factory.EnumAdapters1((uint)index, out var adapter).Failure)
                break;

            using (adapter)
            {
                var description = adapter.Description1;

                adapters.Add(new GraphicsAdapter(
                    index,
                    description.Description,
                    (ulong)description.DedicatedVideoMemory,
                    description.Flags.HasFlag(AdapterFlags.Software)));
            }
        }

        return adapters;
    }
}
