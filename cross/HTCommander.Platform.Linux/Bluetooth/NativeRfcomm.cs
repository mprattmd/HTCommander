/*
Copyright 2026 Ylian Saint-Hilaire

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

   http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace HTCommander.Platform.Linux.Bluetooth;

/// <summary>
/// Raw Linux Bluetooth RFCOMM (SPP) client socket over libc P/Invoke.
///
/// The radios speak Classic Bluetooth RFCOMM/SPP. BlueZ does not hand a usable
/// RFCOMM data stream over D-Bus, so we open an AF_BLUETOOTH / BTPROTO_RFCOMM
/// socket directly against the kernel — the same mechanism libbluetooth's
/// rfcomm tools use. The remote device must already be paired (do this once with
/// `bluetoothctl pair &lt;addr&gt;`); the kernel brings the ACL link up on connect.
/// </summary>
internal static class NativeRfcomm
{
    private const int AF_BLUETOOTH = 31;
    private const int SOCK_STREAM = 1;
    private const int BTPROTO_RFCOMM = 3;

    private const int F_GETFL = 3;
    private const int F_SETFL = 4;
    private const int O_NONBLOCK = 0x800;

    private const int SOL_SOCKET = 1;
    private const int SO_ERROR = 4;

    // RFCOMM links to these radios are authenticated/encrypted. A bare socket
    // defaults to BT_SECURITY_LOW, which the kernel rejects with EACCES; raising
    // to BT_SECURITY_MEDIUM makes it authenticate the (paired) link on connect.
    private const int SOL_BLUETOOTH = 274;
    private const int BT_SECURITY = 4;
    private const byte BT_SECURITY_MEDIUM = 2;

    private const short POLLIN = 0x0001;
    private const short POLLOUT = 0x0004;

    private const int EINPROGRESS = 115;
    private const int EINTR = 4;

    private const int SHUT_RDWR = 2;

    [DllImport("libc", SetLastError = true)]
    private static extern int socket(int domain, int type, int protocol);

    [DllImport("libc", SetLastError = true)]
    private static extern int connect(int sockfd, byte[] addr, uint addrlen);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, IntPtr buf, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, IntPtr buf, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int shutdown(int fd, int how);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(byte[] fds, nuint nfds, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern int getsockopt(int fd, int level, int optname, byte[] optval, ref uint optlen);

    [DllImport("libc", SetLastError = true)]
    private static extern int setsockopt(int fd, int level, int optname, byte[] optval, uint optlen);

    /// <summary>
    /// Parses "AA:BB:CC:DD:EE:FF" (colons optional) into a 6-byte BD_ADDR in the
    /// reversed byte order the kernel sockaddr_rc expects (little-endian).
    /// </summary>
    public static bool TryParseBdAddr(string mac, out byte[] bdaddr)
    {
        bdaddr = new byte[6];
        if (string.IsNullOrEmpty(mac)) return false;
        string hex = mac.Replace(":", "").Replace("-", "").Trim();
        if (hex.Length != 12) return false;
        try
        {
            // Normal order AA BB CC DD EE FF -> stored reversed FF EE DD CC BB AA.
            for (int i = 0; i < 6; i++)
            {
                bdaddr[5 - i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return true;
        }
        catch { return false; }
    }

    // struct sockaddr_rc { sa_family_t rc_family; bdaddr_t rc_bdaddr; uint8_t rc_channel; }
    // 2-byte family + 6-byte bdaddr + 1-byte channel, padded to 10 by alignment.
    private static byte[] BuildSockAddr(byte[] bdaddr, int channel)
    {
        byte[] addr = new byte[10];
        addr[0] = (byte)(AF_BLUETOOTH & 0xFF);
        addr[1] = (byte)((AF_BLUETOOTH >> 8) & 0xFF);
        Array.Copy(bdaddr, 0, addr, 2, 6);
        addr[8] = (byte)channel;
        return addr;
    }

    /// <summary>
    /// Attempts a non-blocking RFCOMM connect to (bdaddr, channel) bounded by
    /// <paramref name="timeoutMs"/>. Returns a connected, blocking fd, or -1.
    /// </summary>
    public static int TryConnect(byte[] bdaddr, int channel, int timeoutMs)
    {
        int fd = socket(AF_BLUETOOTH, SOCK_STREAM, BTPROTO_RFCOMM);
        if (fd < 0) return -1;

        bool connected = false;
        try
        {
            // Request an authenticated/encrypted link (BT_SECURITY_MEDIUM).
            // struct bt_security { uint8_t level; uint8_t key_size; }
            setsockopt(fd, SOL_BLUETOOTH, BT_SECURITY, new byte[] { BT_SECURITY_MEDIUM, 0 }, 2);

            // Switch to non-blocking so connect() returns immediately and we can
            // bound the wait with poll(). If we can't, a blocking connect() would
            // ignore timeoutMs and could hang the thread — fail fast instead.
            int flags = fcntl(fd, F_GETFL, 0);
            if (flags < 0 || fcntl(fd, F_SETFL, flags | O_NONBLOCK) < 0) return -1;

            byte[] addr = BuildSockAddr(bdaddr, channel);
            int rc = connect(fd, addr, (uint)addr.Length);
            if (rc == 0)
            {
                connected = true;
            }
            else
            {
                int err = Marshal.GetLastPInvokeError();
                if (err != EINPROGRESS) return -1;

                // pollfd { int fd; short events; short revents; }
                byte[] pfd = new byte[8];
                BitConverter.GetBytes(fd).CopyTo(pfd, 0);
                BitConverter.GetBytes(POLLOUT).CopyTo(pfd, 4);
                int pr = poll(pfd, 1, timeoutMs);
                if (pr <= 0) return -1; // timeout or error

                // connect result is reported via SO_ERROR.
                byte[] optval = new byte[4];
                uint optlen = 4;
                if (getsockopt(fd, SOL_SOCKET, SO_ERROR, optval, ref optlen) != 0) return -1;
                if (BitConverter.ToInt32(optval, 0) != 0) return -1;
                connected = true;
            }

            // Back to blocking for the steady-state read/write loop.
            int f2 = fcntl(fd, F_GETFL, 0);
            if (f2 >= 0) fcntl(fd, F_SETFL, f2 & ~O_NONBLOCK);
            return fd;
        }
        finally
        {
            if (!connected) { try { close(fd); } catch { } }
        }
    }

    public static void CloseFd(int fd)
    {
        if (fd < 0) return;
        try { shutdown(fd, SHUT_RDWR); } catch { }
        try { close(fd); } catch { }
    }

    /// <summary>Writes all bytes to the fd. Returns false on error.</summary>
    public static bool WriteAll(int fd, byte[] data)
    {
        IntPtr buf = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, buf, data.Length);
            int off = 0;
            while (off < data.Length)
            {
                nint n = write(fd, buf + off, (nuint)(data.Length - off));
                if (n < 0)
                {
                    if (Marshal.GetLastPInvokeError() == EINTR) continue;   // interrupted: retry
                    return false;
                }
                if (n == 0) return false;
                off += (int)n;
            }
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// Reads available bytes within <paramref name="timeoutMs"/>. Returns bytes
    /// read, or 0 on timeout. Used to probe a freshly-opened channel for a reply.
    /// </summary>
    public static int ReadWithTimeout(int fd, byte[] buf, int timeoutMs)
    {
        byte[] pfd = new byte[8];
        BitConverter.GetBytes(fd).CopyTo(pfd, 0);
        BitConverter.GetBytes(POLLIN).CopyTo(pfd, 4);
        if (poll(pfd, 1, timeoutMs) <= 0) return 0;

        IntPtr p = Marshal.AllocHGlobal(buf.Length);
        try
        {
            nint n = read(fd, p, (nuint)buf.Length);
            if (n <= 0) return 0;
            Marshal.Copy(p, buf, 0, (int)n);
            return (int)n;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>
    /// A blocking <see cref="Stream"/> over a connected RFCOMM fd. Read blocks
    /// until data arrives or the peer closes; closing the stream (Dispose/Close)
    /// shuts the fd down, unblocking any in-flight Read so the read loop can exit.
    /// </summary>
    public sealed class RfcommStream : Stream
    {
        private int _fd;
        private readonly object _closeLock = new object();

        public RfcommStream(int fd) { _fd = fd; }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0) return 0;
            int fd = _fd;
            if (fd < 0) return 0;
            IntPtr buf = Marshal.AllocHGlobal(count);
            try
            {
                nint n = read(fd, buf, (nuint)count);
                if (n < 0)
                {
                    // fd closed under us (Disconnect) -> behave like EOF.
                    if (_fd < 0) return 0;
                    throw new IOException($"RFCOMM read failed (errno {Marshal.GetLastPInvokeError()}).");
                }
                if (n > 0) Marshal.Copy(buf, buffer, offset, (int)n);
                return (int)n;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            int fd = _fd;
            if (fd < 0) throw new IOException("RFCOMM socket closed.");
            IntPtr buf = Marshal.AllocHGlobal(count);
            try
            {
                Marshal.Copy(buffer, offset, buf, count);
                int written = 0;
                while (written < count)
                {
                    nint n = write(fd, buf + written, (nuint)(count - written));
                    if (n <= 0) throw new IOException($"RFCOMM write failed (errno {Marshal.GetLastPInvokeError()}).");
                    written += (int)n;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        protected override void Dispose(bool disposing)
        {
            lock (_closeLock)
            {
                if (_fd >= 0) { CloseFd(_fd); _fd = -1; }
            }
            base.Dispose(disposing);
        }
    }
}
