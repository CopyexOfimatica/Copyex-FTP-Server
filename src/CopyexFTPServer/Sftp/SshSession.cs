// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CopyexFTPServer.Core;
using CopyexFTPServer.Ftp;
using CopyexFTPServer.Models;

namespace CopyexFTPServer.Sftp;

/// <summary>
/// Una conexión SSH: transporte, autenticación por contraseña y canales. Solo se permite el
/// subsistema "sftp": no hay consola, ni ejecución de comandos, ni reenvío de puertos.
/// </summary>
internal sealed class SshSession : IDisposable
{
    private const uint InitialWindow = 2 * 1024 * 1024;
    private const uint LocalMaxPacket = 32 * 1024;
    private const int MaxChannels = 4;
    private const int MaxAuthRequests = 20;
    private const int MaxPendingOutput = 1024 * 1024;
    private const int MaxBufferedInput = 512 * 1024;
    private static readonly TimeSpan LoginGraceTime = TimeSpan.FromSeconds(30);

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly FtpServerOptions _options;
    private readonly SftpServer _server;
    private readonly SshTransport _transport;
    private readonly CancellationTokenSource _cts;
    private readonly CancellationTokenRegistration _closeOnCancel;
    private readonly Dictionary<uint, SshChannel> _channels = new();
    private uint _nextChannelId;
    private int _authFailures;
    private int _authRequests;
    private int _disposed;

    public SshSession(long id, TcpClient client, IPAddress remoteIp, FtpServerOptions options, SftpServer server, SshHostKey hostKey, CancellationToken serverToken)
    {
        _client = client;
        _stream = client.GetStream();
        int port = (client.Client.RemoteEndPoint as IPEndPoint)?.Port ?? 0;
        Remote = remoteIp + ":" + port.ToString(CultureInfo.InvariantCulture);
        _options = options;
        _server = server;
        _transport = new SshTransport(_stream, hostKey);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        _closeOnCancel = _cts.Token.Register(static s => { try { ((TcpClient)s!).Dispose(); } catch { } }, client);
        Id = id;
    }

    public long Id { get; }
    public string Remote { get; }
    public FtpUser? User { get; private set; }
    public FtpFileSystem? FileSystem { get; private set; }
    public bool Trace => _options.DetailedTrace;
    internal SftpServer Server => _server;
    internal CancellationToken Token => _cts.Token;

    public async Task RunAsync()
    {
        Logger.Info($"SFTP CONEXIÓN {Remote} (sesión #{Id})");
        DateTime loginDeadline = DateTime.UtcNow + LoginGraceTime;
        string reason = "fin de sesión";
        try
        {
            using (var grace = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                grace.CancelAfter(LoginGraceTime);
                await _transport.ExchangeVersionsAsync(grace.Token).ConfigureAwait(false);
                await _transport.SendKexInitAsync(grace.Token).ConfigureAwait(false);
            }

            while (!_cts.IsCancellationRequested)
            {
                byte[] payload;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    TimeSpan limit = User == null ? loginDeadline - DateTime.UtcNow : _options.IdleTimeout;
                    if (limit <= TimeSpan.Zero)
                        throw new TimeoutException();
                    timeout.CancelAfter(limit);
                    try
                    {
                        payload = await _transport.ReadPacketAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                    {
                        throw new TimeoutException();
                    }
                }

                if (!await DispatchAsync(payload).ConfigureAwait(false))
                    break;
            }
        }
        catch (TimeoutException)
        {
            reason = User == null ? "tiempo de inicio de sesión agotado" : $"inactividad ({_options.IdleTimeout.TotalSeconds:0} s)";
            await TryDisconnectAsync(11, "Tiempo agotado").ConfigureAwait(false);
        }
        catch (SshProtocolException ex)
        {
            reason = "error de protocolo: " + ex.Message;
            Logger.Warning($"SFTP {Remote}: {ex.Message}");
            await TryDisconnectAsync(2, "Error de protocolo").ConfigureAwait(false);
        }
        catch (SshDisconnectedException)
        {
            reason = "el cliente cerró la conexión";
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            reason = "conexión cerrada";
        }
        finally
        {
            foreach (SshChannel ch in _channels.Values)
                ch.Sftp?.Dispose();
            _channels.Clear();
        }

        Logger.Info($"SFTP DESCONEXIÓN {Remote} usuario={User?.Username ?? "-"} ({reason})");
    }

    // ------------------------------------------------------------------ despacho

    private async Task<bool> DispatchAsync(byte[] payload)
    {
        byte type = payload[0];

        if (_transport.SessionId == null && type != SshMsg.KexInit)
            throw new SshProtocolException("Mensaje recibido antes del intercambio de claves");

        switch (type)
        {
            case SshMsg.Disconnect:
                return false;
            case SshMsg.Ignore:
            case SshMsg.Debug:
            case SshMsg.Unimplemented:
                return true;
            case SshMsg.KexInit:
                bool first = _transport.SessionId == null;
                await _transport.RunKeyExchangeAsync(payload, _cts.Token).ConfigureAwait(false);
                if (first && Trace)
                    Logger.Write(LogKind.ServerReply, $"SFTP {Remote} cifrado acordado: {_transport.NegotiatedSummary} ({_transport.ClientVersion})");
                return true;
            case SshMsg.ServiceRequest:
                {
                    string service = new SshReader(payload.AsMemory(1)).String(64);
                    if (service != "ssh-userauth")
                        throw new SshProtocolException("Servicio no disponible: " + service);
                    await SendAsync(new SshWriter().Byte(SshMsg.ServiceAccept).String(service)).ConfigureAwait(false);
                    return true;
                }
            case SshMsg.UserauthRequest:
                return await HandleAuthAsync(new SshReader(payload.AsMemory(1))).ConfigureAwait(false);
            case SshMsg.GlobalRequest:
                {
                    var r = new SshReader(payload.AsMemory(1));
                    r.String(256);
                    if (r.Bool())
                        await SendAsync(new SshWriter().Byte(SshMsg.RequestFailure)).ConfigureAwait(false);
                    return true;
                }
        }

        if (type is >= SshMsg.ChannelOpen and <= SshMsg.ChannelFailure)
        {
            if (User == null)
                throw new SshProtocolException("Canal solicitado sin autenticación");
            return await HandleChannelAsync(type, new SshReader(payload.AsMemory(1))).ConfigureAwait(false);
        }

        await SendAsync(new SshWriter().Byte(SshMsg.Unimplemented).UInt32(_transport.LastReceivedSequence)).ConfigureAwait(false);
        return true;
    }

    // ------------------------------------------------------------------ autenticación

    private async Task<bool> HandleAuthAsync(SshReader r)
    {
        string username = r.String(256);
        string service = r.String(64);
        string method = r.String(64);

        if (User != null)
            return true; // RFC 4252: tras el éxito se ignoran nuevas peticiones
        if (service != "ssh-connection")
            throw new SshProtocolException("Servicio de autenticación no válido");
        if (++_authRequests > MaxAuthRequests)
        {
            await TryDisconnectAsync(14, "Demasiados intentos de autenticación").ConfigureAwait(false);
            return false;
        }

        if (method != "password")
        {
            await SendAuthFailureAsync().ConfigureAwait(false);
            return true;
        }

        bool changePassword = r.Bool();
        string password = r.String(FtpUser.MaxPasswordLength + 1);
        FtpUser? user = changePassword ? null : _server.Users.Authenticate(username, password);

        if (user == null)
        {
            _authFailures++;
            Logger.Warning($"SFTP LOGIN FALLIDO {Remote} usuario={username}");
            await Task.Delay(_options.FailedLoginDelay, _cts.Token).ConfigureAwait(false);
            if (_authFailures >= _options.MaxFailedLoginsPerSession)
            {
                await TryDisconnectAsync(14, "Demasiados intentos fallidos").ConfigureAwait(false);
                return false;
            }
            await SendAuthFailureAsync().ConfigureAwait(false);
            return true;
        }

        try
        {
            string root = RootFor(user);
            Directory.CreateDirectory(root);
            FileSystem = new FtpFileSystem(root);
        }
        catch (Exception ex)
        {
            Logger.Error($"SFTP LOGIN ERROR {Remote} usuario={user.Username}: la carpeta raíz no está disponible ({ex.Message})");
            await SendAuthFailureAsync().ConfigureAwait(false);
            return true;
        }

        User = user;
        Logger.Success($"SFTP LOGIN OK {Remote} usuario={user.Username} raíz={FileSystem.Root}");
        await SendAsync(new SshWriter().Byte(SshMsg.UserauthSuccess)).ConfigureAwait(false);
        return true;
    }

    private Task SendAuthFailureAsync() =>
        SendAsync(new SshWriter().Byte(SshMsg.UserauthFailure).NameList("password").Bool(false));

    private string RootFor(FtpUser user) =>
        Path.GetFullPath(user.UsesDefaultRoot ? _options.RootFolder : user.RootFolder);

    /// <summary>
    /// Igual que en FTP: si el usuario se deshabilita, se elimina o cambia de contraseña, la
    /// sesión se cierra; si cambian permisos o raíz se aplican a la siguiente operación.
    /// </summary>
    internal bool RefreshUser()
    {
        FtpUser current = User!;
        FtpUser? latest = _server.Users.Find(current.Username);
        if (latest == null || !latest.Enabled || !string.Equals(latest.PasswordHash, current.PasswordHash, StringComparison.Ordinal))
        {
            Logger.Warning($"SFTP SESIÓN CERRADA {Remote}: el usuario '{current.Username}' se ha deshabilitado, eliminado o cambiado de contraseña");
            return false;
        }
        if (!ReferenceEquals(latest, current))
        {
            string newRoot = RootFor(latest);
            if (!ConfigService.PathsEquivalent(newRoot, FileSystem!.Root))
            {
                Directory.CreateDirectory(newRoot);
                FileSystem = new FtpFileSystem(newRoot);
            }
            User = latest;
        }
        return true;
    }

    // ------------------------------------------------------------------ canales

    private async Task<bool> HandleChannelAsync(byte type, SshReader r)
    {
        if (type == SshMsg.ChannelOpen)
        {
            string channelType = r.String(64);
            uint sender = r.UInt32();
            uint window = r.UInt32();
            uint maxPacket = r.UInt32();
            if (channelType != "session" || _channels.Count >= MaxChannels)
            {
                Logger.Warning($"SFTP {Remote}: canal '{channelType}' rechazado");
                await SendAsync(new SshWriter().Byte(SshMsg.ChannelOpenFailure).UInt32(sender)
                    .UInt32(1).String("Solo se permite SFTP").String("es")).ConfigureAwait(false);
                return true;
            }

            var ch = new SshChannel
            {
                LocalId = _nextChannelId++,
                RemoteId = sender,
                RemoteWindow = window,
                RemoteMaxPacket = Math.Clamp(maxPacket, 1024u, 32768u),
                LocalWindow = InitialWindow
            };
            _channels[ch.LocalId] = ch;
            await SendAsync(new SshWriter().Byte(SshMsg.ChannelOpenConfirmation).UInt32(sender).UInt32(ch.LocalId)
                .UInt32(InitialWindow).UInt32(LocalMaxPacket)).ConfigureAwait(false);
            return true;
        }

        uint recipient = r.UInt32();
        if (!_channels.TryGetValue(recipient, out SshChannel? channel))
            throw new SshProtocolException("Canal desconocido");

        switch (type)
        {
            case SshMsg.ChannelRequest:
                {
                    string request = r.String(64);
                    bool wantReply = r.Bool();
                    bool ok = false;
                    if (request == "subsystem")
                    {
                        string name = r.String(64);
                        if (name == "sftp" && channel.Sftp == null)
                        {
                            channel.Sftp = new SftpHandler(this, channel);
                            ok = true;
                        }
                    }
                    else if (request is "exec" or "shell" or "pty-req" or "x11-req")
                    {
                        Logger.Warning($"SFTP SEGURIDAD {Remote}: petición '{request}' rechazada (solo se permite SFTP)");
                    }
                    if (wantReply)
                        await SendAsync(new SshWriter().Byte(ok ? SshMsg.ChannelSuccess : SshMsg.ChannelFailure).UInt32(channel.RemoteId)).ConfigureAwait(false);
                    return true;
                }

            case SshMsg.ChannelData:
            case SshMsg.ChannelExtendedData:
                {
                    if (type == SshMsg.ChannelExtendedData)
                        r.UInt32();
                    ReadOnlyMemory<byte> data = r.StringBytes((int)LocalMaxPacket + 1024);
                    if ((uint)data.Length > channel.LocalWindow)
                        throw new SshProtocolException("El cliente superó la ventana del canal");
                    channel.LocalWindow -= (uint)data.Length;
                    if (type == SshMsg.ChannelData && channel.Sftp != null)
                    {
                        if (!await channel.Sftp.FeedAsync(data).ConfigureAwait(false))
                            return false;
                    }
                    await MaybeAdjustWindowAsync(channel).ConfigureAwait(false);
                    return true;
                }

            case SshMsg.ChannelWindowAdjust:
                {
                    uint bytes = r.UInt32();
                    channel.RemoteWindow = (uint)Math.Min((ulong)channel.RemoteWindow + bytes, uint.MaxValue);
                    await FlushChannelAsync(channel).ConfigureAwait(false);
                    if (channel.Sftp != null && channel.Sftp.HasBufferedInput && CanSend(channel))
                    {
                        if (!await channel.Sftp.ProcessAsync().ConfigureAwait(false))
                            return false;
                    }
                    await MaybeAdjustWindowAsync(channel).ConfigureAwait(false);
                    return true;
                }

            case SshMsg.ChannelEof:
                // El cliente terminó (p. ej. "bye"): como OpenSSH, se informa del fin del
                // subsistema y se cierra el canal para que el cliente pueda salir.
                if (channel.Sftp != null && !channel.CloseSent)
                {
                    channel.Sftp.Dispose();
                    await FlushChannelAsync(channel).ConfigureAwait(false);
                    await SendAsync(new SshWriter().Byte(SshMsg.ChannelRequest).UInt32(channel.RemoteId)
                        .String("exit-status").Bool(false).UInt32(0)).ConfigureAwait(false);
                    await SendAsync(new SshWriter().Byte(SshMsg.ChannelEof).UInt32(channel.RemoteId)).ConfigureAwait(false);
                    await SendAsync(new SshWriter().Byte(SshMsg.ChannelClose).UInt32(channel.RemoteId)).ConfigureAwait(false);
                    channel.CloseSent = true;
                }
                return true;

            case SshMsg.ChannelClose:
                if (!channel.CloseSent)
                {
                    channel.CloseSent = true;
                    await SendAsync(new SshWriter().Byte(SshMsg.ChannelClose).UInt32(channel.RemoteId)).ConfigureAwait(false);
                }
                channel.Sftp?.Dispose();
                _channels.Remove(channel.LocalId);
                return true;

            default:
                return true; // CHANNEL_SUCCESS / FAILURE / OPEN_* no esperados: se ignoran
        }
    }

    private async Task MaybeAdjustWindowAsync(SshChannel channel)
    {
        // La ventana solo se amplía si lo pendiente de procesar es poco (normalmente un trozo de
        // la siguiente petición): así un cliente rápido no puede llenar la memoria del servidor.
        if (channel.LocalWindow < InitialWindow / 2 && (channel.Sftp?.BufferedBytes ?? 0) < MaxBufferedInput)
        {
            uint grant = InitialWindow - channel.LocalWindow;
            channel.LocalWindow = InitialWindow;
            await SendAsync(new SshWriter().Byte(SshMsg.ChannelWindowAdjust).UInt32(channel.RemoteId).UInt32(grant)).ConfigureAwait(false);
        }
    }

    internal bool CanSend(SshChannel channel) => channel.PendingBytes < MaxPendingOutput;

    /// <summary>Envía datos por el canal respetando la ventana del cliente; lo que no cabe queda en cola.</summary>
    internal async Task SendChannelDataAsync(SshChannel channel, byte[] data)
    {
        channel.Pending.Enqueue(data);
        channel.PendingBytes += data.Length;
        await FlushChannelAsync(channel).ConfigureAwait(false);
    }

    private async Task FlushChannelAsync(SshChannel channel)
    {
        while (channel.Pending.Count > 0 && channel.RemoteWindow > 0)
        {
            byte[] head = channel.Pending.Peek();
            int available = head.Length - channel.PendingOffset;
            int chunk = (int)Math.Min((uint)available, Math.Min(channel.RemoteWindow, channel.RemoteMaxPacket));

            var w = new SshWriter(chunk + 16).Byte(SshMsg.ChannelData).UInt32(channel.RemoteId)
                .String(head.AsSpan(channel.PendingOffset, chunk));
            await SendAsync(w).ConfigureAwait(false);

            channel.RemoteWindow -= (uint)chunk;
            channel.PendingOffset += chunk;
            channel.PendingBytes -= chunk;
            if (channel.PendingOffset >= head.Length)
            {
                channel.Pending.Dequeue();
                channel.PendingOffset = 0;
            }
        }
    }

    // ------------------------------------------------------------------ envío

    private Task SendAsync(SshWriter message) => _transport.WriteAsync(message, _cts.Token);

    private async Task TryDisconnectAsync(uint reasonCode, string description)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _transport.WriteAsync(new SshWriter().Byte(SshMsg.Disconnect).UInt32(reasonCode).String(description).String("es"), timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // El cliente ya no está.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _closeOnCancel.Dispose();
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
        _transport.Dispose();
        try { _stream.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
    }
}

internal sealed class SshChannel
{
    public uint LocalId;
    public uint RemoteId;
    public uint RemoteWindow;
    public uint RemoteMaxPacket;
    public uint LocalWindow;
    public bool CloseSent;
    public SftpHandler? Sftp;
    public readonly Queue<byte[]> Pending = new();
    public int PendingOffset;
    public long PendingBytes;
}
