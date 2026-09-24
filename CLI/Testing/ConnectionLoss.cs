using System.IO;
using System.Net.Sockets;

namespace valheim_cli.Testing;

/// <summary>
/// A connection that closes before its command answers. The server does
/// this when it stops, and when a live reload replaces valheimCLI (the new
/// instance unloads the one holding the connection). The command is never
/// resent: it may have run. A caller reconnects (valheim-cli wait --for
/// plugin-server) and asks what is running now (cli_build).
/// </summary>
public static class ConnectionLoss
{
    public const string ErrorCode = "connection_closed";

    public static string Line(string command)
    {
        return $"ERROR: code={ErrorCode} message=the connection closed before '{command}' answered (the server stopped, or a live reload replaced valheimCLI); the command was not resent; reconnect with valheim-cli wait --for plugin-server";
    }

    /// <summary>A read that hit the socket's receive timeout, as opposed to a reset or closed connection.</summary>
    public static bool IsReadTimeout(IOException ex)
    {
        return ex.InnerException is SocketException socket &&
               (socket.SocketErrorCode == SocketError.TimedOut || socket.SocketErrorCode == SocketError.WouldBlock);
    }
}
