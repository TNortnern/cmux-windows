using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Cmux.Core.IPC;

/// <summary>
/// Creates named pipe server instances that only the current Windows user can open.
/// </summary>
public static class PipeSecurityFactory
{
    private static readonly Lazy<SecurityIdentifier> s_user = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("Current user SID is unavailable.");
    });

    private static readonly Lazy<PipeSecurity> s_security = new(() =>
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(s_user.Value, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    });

    /// <summary>SID of the current user, as a string (e.g. "S-1-5-21-...").</summary>
    public static string CurrentUserSid => s_user.Value.Value;

    /// <summary>
    /// Creates one server instance whose DACL grants FullControl only to the current user.
    /// PipeOptions.CurrentUserOnly cannot be combined with an explicit PipeSecurity (the runtime
    /// rejects it), so the explicit ACL is used on the server; clients use CurrentUserOnly to verify
    /// the server is owned by the same user.
    /// </summary>
    public static NamedPipeServerStream CreateServer(string pipeName) =>
        NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: s_security.Value);
}
