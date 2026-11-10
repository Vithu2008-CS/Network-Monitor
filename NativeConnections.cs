using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Flow;

public static class NativeConnections
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, bool order, int family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref uint size, bool order, int family, int tableClass, uint reserved);

    public static List<ConnectionRow> Read()
    {
        var result = new List<ConnectionRow>();
        foreach (var ipv6 in new[] { false, true })
        foreach (var tcp in new[] { true, false })
        {
            var family = ipv6 ? 23 : 2;
            uint size = 0;
            uint Query(IntPtr buffer, ref uint length) => tcp
                ? GetExtendedTcpTable(buffer, ref length, true, family, 5, 0)
                : GetExtendedUdpTable(buffer, ref length, true, family, 1, 0);
            Query(IntPtr.Zero, ref size);
            if (size == 0) continue;
            // The table can grow between calls; retry only ERROR_INSUFFICIENT_BUFFER.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(checked((int)size));
                try
                {
                    var status = Query(buffer, ref size);
                    if (status == 122) continue;
                    if (status != 0) throw new Win32Exception((int)status);
                    var count = Marshal.ReadInt32(buffer);
                    var rowSize = tcp ? (ipv6 ? 56 : 24) : (ipv6 ? 28 : 12);
                    if (count < 0 || (long)count * rowSize + 4 > size) throw new InvalidDataException("Invalid connection table size.");
                    for (var i = 0; i < count; i++)
                    {
                        var row = IntPtr.Add(buffer, 4 + i * rowSize);
                        string Address(int offset, int scopeOffset = -1)
                        {
                            if (!ipv6) return new IPAddress(unchecked((uint)Marshal.ReadInt32(row, offset))).ToString();
                            var bytes = new byte[16];
                            Marshal.Copy(IntPtr.Add(row, offset), bytes, 0, 16);
                            return new IPAddress(bytes, unchecked((uint)Marshal.ReadInt32(row, scopeOffset))).ToString();
                        }
                        int Port(int offset) => (Marshal.ReadByte(row, offset) << 8) | Marshal.ReadByte(row, offset + 1);
                        var local = Address(ipv6 ? 0 : (tcp ? 4 : 0), 16);
                        var localPort = Port(ipv6 ? 20 : (tcp ? 8 : 4));
                        var remote = tcp ? Address(ipv6 ? 24 : 12, 40) : "*";
                        var remotePort = tcp ? Port(ipv6 ? 44 : 16) : 0;
                        var pid = Marshal.ReadInt32(row, tcp ? (ipv6 ? 52 : 20) : (ipv6 ? 24 : 8));
                        var state = tcp ? ((TcpState)Marshal.ReadInt32(row, ipv6 ? 48 : 0)).ToString() : "Bound";
                        result.Add(new(pid, "", tcp ? "TCP" : "UDP", local, localPort, remote, remotePort, state));
                    }
                    break;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        return result;
    }
}
