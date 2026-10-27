using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace PDM.Core.Ipc;

/// <summary>
/// Local IPC endpoint. The pipe's ACL allows only the current Windows user and denies network logons,
/// so websites, other users and remote machines cannot reach it. There is no TCP port.
/// </summary>
public sealed class PipeServer : IDisposable
{
    private readonly string _name;
    private readonly Func<byte[], Task<byte[]>> _handler;
    private readonly CancellationTokenSource _cts = new();
    private bool _first = true;

    public PipeServer(string name, Func<byte[], Task<byte[]>> handler)
    {
        _name = name;
        _handler = handler;
    }

    public bool Running { get; private set; }

    public static PipeSecurity CreateSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("No user SID.");
        var ps = new PipeSecurity();
        // Clients (PipeOptions.CurrentUserOnly) compare the pipe owner with the token's default owner, which is
        // the user normally but BUILTIN\Administrators in an elevated process. Use the same value.
        ps.SetOwner(identity.Owner ?? user);
        ps.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.Synchronize, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return ps;
    }

    private NamedPipeServerStream CreateInstance()
    {
        var options = PipeOptions.Asynchronous | PipeOptions.WriteThrough;
        if (_first) options |= PipeOptions.FirstPipeInstance; // fail if someone else already created this name
        var s = NamedPipeServerStreamAcl.Create(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, options, 64 * 1024, 64 * 1024, CreateSecurity());
        _first = false;
        return s;
    }

    public void Start()
    {
        NamedPipeServerStream first;
        try
        {
            first = CreateInstance();
        }
        catch (Exception ex)
        {
            Log.Error("IPC pipe could not be created (name in use?)", ex);
            return;
        }
        Running = true;
        _ = AcceptLoopAsync(first);
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream next)
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            var server = next;
            try
            {
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                break;
            }
            catch (Exception ex)
            {
                Log.Error("IPC accept failed", ex);
                server.Dispose();
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
                try { next = CreateInstance(); } catch { break; }
                continue;
            }
            try { next = CreateInstance(); }
            catch (Exception ex)
            {
                Log.Error("IPC instance failed", ex);
                _ = ServeAsync(server, token);
                break;
            }
            _ = ServeAsync(server, token);
        }
        Running = false;
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken token)
    {
        using (server)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                while (server.IsConnected)
                {
                    var request = await PipeFraming.ReadAsync(server, timeout.Token).ConfigureAwait(false);
                    if (request == null) break;
                    byte[] response;
                    try { response = await _handler(request).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        Log.Error("IPC handler failed", ex);
                        response = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"internal\"}");
                    }
                    await PipeFraming.WriteAsync(server, response, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or EndOfStreamException)
            {
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

public static class PipeClient
{
    /// <summary>Sends one request and waits for the answer. CurrentUserOnly makes .NET verify the pipe is owned by this user.</summary>
    public static async Task<byte[]?> SendAsync(string name, byte[] request, TimeSpan connectTimeout, CancellationToken ct = default)
    {
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync((int)connectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
        await PipeFraming.WriteAsync(client, request, ct).ConfigureAwait(false);
        return await PipeFraming.ReadAsync(client, ct).ConfigureAwait(false);
    }
}
