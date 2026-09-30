using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Chengshi.Ipc;

public static class MessageSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Encode<T>(T message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        var framed = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(framed, json.Length);
        json.CopyTo(framed.AsSpan(4));
        return framed;
    }

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var payload = Encode(message);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
        where T : class
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > 1024 * 1024)
        {
            throw new InvalidDataException($"非法消息长度 {length}。");
        }

        var body = new byte[length];
        if (!await ReadExactAsync(stream, body, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return JsonSerializer.Deserialize<T>(body, Options);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    public static string ToDebugString<T>(T message) =>
        Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(message, Options));
}

public static class PipeFactory
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;

    /// <summary>要求本进程是这个名字的第一创建者：别的进程先占了管道名时创建失败，
    /// 守护服务能立刻发现，而不是让客户端连上一个冒充的服务。</summary>
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeReadmodeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;

    /// <summary>
    /// 服务以 SYSTEM 运行，.NET 默认 DACL 只给普通用户读权限，
    /// 界面程序连发消息都做不到。这里用 CreateNamedPipe 显式授权：
    /// SYSTEM/Administrators 完全控制，Authenticated Users 可读写。
    /// maxInstances 允许多个实例并存：服务端并行服务多条连接，
    /// 一条连接卡死时新客户端照样能连上（曾出过单实例串行导致整台服务失联的事故）。
    /// </summary>
    public static NamedPipeServerStream CreateServer(string name = PipeNames.Default, bool firstInstance = true)
    {
        const string sddl = "D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGW;;;AU)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl, 1, out var descriptor, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法构造管道安全描述符。");
        }

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = 0,
            };

            // FirstPipeInstance 只对名字的第一个实例有意义；第二个实例再带这个标志
            // 会直接失败（名字已被自己人占用）。
            var openMode = PipeAccessDuplex | FileFlagOverlapped;
            if (firstInstance)
            {
                openMode |= FileFlagFirstPipeInstance;
            }

            var handle = CreateNamedPipe(
                @"\\.\pipe\" + name,
                openMode,
                PipeTypeByte | PipeReadmodeByte | PipeWait,
                4,
                0,
                0,
                0,
                ref attributes);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建命名管道。");
            }

            // 句柄所有权移交给流，这里不能再释放。
            try
            {
                return new NamedPipeServerStream(
                    PipeDirection.InOut,
                    isAsync: true,
                    isConnected: false,
                    handle);
            }
            catch (Exception)
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    public static NamedPipeClientStream CreateClient(string name = PipeNames.Default) =>
        new(
            ".",
            name,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

    /// <summary>
    /// 管道名是谁都能先创建的：连接后核对管道另一端的服务进程镜像名，
    /// 不在名单里就断开——防止孩子进程抢先建同名管道钓家长输入的密码。
    /// 查镜像名必须用 PROCESS_QUERY_LIMITED_INFORMATION：界面程序是普通权限，
    /// 读 SYSTEM 服务进程的 MainModule 会拒绝访问，导致永远连不上真正的守护。
    /// </summary>
    public static void VerifyServerProcess(NamedPipeClientStream client, params string[] allowedImageNames)
    {
        if (allowedImageNames is null || allowedImageNames.Length == 0)
        {
            return;
        }

        if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverPid) || serverPid == 0)
        {
            throw new IOException("无法确认守护管道对端的进程。");
        }

        var image = TryQueryProcessImageName((int)serverPid);
        if (image is null || !allowedImageNames.Contains(image, StringComparer.OrdinalIgnoreCase))
        {
            throw new IOException("守护管道对端不是澄时进程，已拒绝连接。");
        }
    }

    /// <summary>低权限查询进程可执行文件路径（对 SYSTEM 进程也有效）；查不到返回 null。</summary>
    private static string? TryQueryProcessImageName(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var capacity = 1024u;
            var buffer = Marshal.AllocHGlobal((int)(capacity * sizeof(char)));
            try
            {
                if (!QueryFullProcessImageName(handle, 0, buffer, ref capacity))
                {
                    return null;
                }

                var full = Marshal.PtrToStringUni(buffer, (int)capacity);
                return string.IsNullOrEmpty(full) ? null : Path.GetFileName(full);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, IntPtr buffer, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        uint outBufferSize,
        uint inBufferSize,
        uint defaultTimeout,
        ref SecurityAttributes securityAttributes);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSDRevision,
        out IntPtr securityDescriptor,
        IntPtr securityDescriptorSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
