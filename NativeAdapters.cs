using System.Runtime.InteropServices;

namespace Flow;

public static class NativeAdapters
{
    // Windows x64 MIB_IF_ROW2 ABI: the flags distinguish real NICs from their WFP/QoS filter layers.
    // https://learn.microsoft.com/windows/win32/api/netioapi/ns-netioapi-mib_if_row2
    [StructLayout(LayoutKind.Explicit, Size = 1352)]
    private struct InterfaceRow
    {
        [FieldOffset(8)] public uint Index;
        [FieldOffset(1152)] public byte Flags;
    }
    [DllImport("iphlpapi.dll")]
    private static extern uint GetIfEntry2(ref InterfaceRow row);

    public static (bool Filter, bool Hardware)? Flags(int index)
    {
        var row = new InterfaceRow { Index = checked((uint)index) };
        return GetIfEntry2(ref row) == 0 ? ((row.Flags & 2) != 0, (row.Flags & 1) != 0) : null;
    }
}
