using System.Runtime.InteropServices;
using CznTranslator.Core.Abstractions;

namespace CznTranslator.Ocr;

/// <summary>
/// Physical / performance core counts from <c>GetLogicalProcessorInformationEx</c>.
/// <para>
/// The TZ suggests <c>Win32_Processor.NumberOfCores</c> via WMI, but that number lumps P and E
/// cores together on Intel 12th gen and newer — exactly the case the TZ then warns about. This API
/// reports an efficiency class per core, so the P-cores can be counted directly instead of
/// inferred, and it needs no System.Management dependency.
/// </para>
/// </summary>
public sealed class CpuTopology : ICpuTopology
{
    private CpuTopology(int physicalCores, int performanceCores, bool isHybrid)
    {
        PhysicalCores = physicalCores;
        PerformanceCores = performanceCores;
        IsHybrid = isHybrid;
    }

    public int PhysicalCores { get; }
    public int PerformanceCores { get; }
    public bool IsHybrid { get; }

    public static ICpuTopology Detect()
    {
        if (!OperatingSystem.IsWindows())
            return Fallback();

        try
        {
            return DetectWindows() ?? Fallback();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Fallback();
        }
    }

    /// <summary>
    /// Halving the logical processor count is the usual hyperthreading assumption. It is only a
    /// guess, which is why it is the fallback and not the primary path.
    /// </summary>
    private static CpuTopology Fallback()
    {
        var cores = Math.Max(1, Environment.ProcessorCount / 2);
        return new CpuTopology(cores, cores, isHybrid: false);
    }

    private const int RelationProcessorCore = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorRelationshipHeader
    {
        public int Relationship;
        public int Size;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType,
        IntPtr buffer,
        ref int returnedLength);

    private const int ErrorInsufficientBuffer = 122;

    private static CpuTopology? DetectWindows()
    {
        var length = 0;
        if (GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length))
            return null;

        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || length <= 0)
            return null;

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                return null;

            var physicalCores = 0;
            var efficiencyClasses = new HashSet<byte>();
            var coresByClass = new Dictionary<byte, int>();

            var offset = 0;
            while (offset < length)
            {
                var header = Marshal.PtrToStructure<ProcessorRelationshipHeader>(buffer + offset);
                if (header.Size <= 0)
                    break;

                if (header.Relationship == RelationProcessorCore)
                {
                    physicalCores++;

                    // PROCESSOR_RELATIONSHIP layout: Flags (byte) then EfficiencyClass (byte),
                    // immediately after the 8-byte header.
                    var efficiencyClass = Marshal.ReadByte(buffer + offset + 8 + 1);
                    efficiencyClasses.Add(efficiencyClass);
                    coresByClass[efficiencyClass] = coresByClass.GetValueOrDefault(efficiencyClass) + 1;
                }

                offset += header.Size;
            }

            if (physicalCores == 0)
                return null;

            var isHybrid = efficiencyClasses.Count > 1;

            // A higher efficiency class is the faster core, so the P-cores are the top class.
            var performanceCores = isHybrid
                ? coresByClass[efficiencyClasses.Max()]
                : physicalCores;

            return new CpuTopology(physicalCores, Math.Max(1, performanceCores), isHybrid);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Explicit values, for tests and for a manual override in the config.</summary>
    public static ICpuTopology Fixed(int physicalCores, int performanceCores, bool isHybrid) =>
        new CpuTopology(
            Math.Max(1, physicalCores),
            Math.Max(1, performanceCores),
            isHybrid);
}
