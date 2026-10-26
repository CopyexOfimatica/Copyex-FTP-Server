// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CopyexFTPServer.Core;
using CopyexFTPServer.Models;

namespace CopyexFTPServer.Ftp;

public sealed record FtpFileReceivedInfo(string FilePath, string Username, long Bytes, DateTime ReceivedAt);

/// <summary>
/// Parte común de los servidores FTP y SFTP: escucha, filtro de red local, límites de
/// conexiones, usuarios en caliente y parada ordenada. Los eventos llegan desde hilos del
/// pool: la interfaz debe hacer Invoke.
/// </summary>
public abstract class TcpServerBase : IAsyncDisposable
{
    private readonly object _stateLock = new();
    private readonly object _countLock = new();
    private readonly ConcurrentDictionary<long, Task> _sessionTasks = new();
    private readonly Dictionary<IPAddress, int> _connectionsPerIp = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private long _nextSessionId;
    private int _activeConnections;
    private volatile UserDirectory _users = UserDirectory.Empty;

    /// <summary>"FTP" o "SFTP", para el registro.</summary>
    protected abstract string Protocol { get; }

    public bool IsRunning { get { lock (_stateLock) return _listener != null; } }
    public int Port { get; private set; }
    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    public event Action<bool>? RunningChanged;
    public event Action<FtpFileReceivedInfo>? FileReceived;
    public event Action<int>? ConnectionsChanged;

    internal UserDirectory Users => _users;

    public void Start(FtpServerOptions options, IEnumerable<FtpUser> users)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        lock (_stateLock)
        {
            if (_listener != null)
                return;

            string root = Path.GetFullPath(options.RootFolder);
            Directory.CreateDirectory(root);
            options = options with { RootFolder = root };
            UpdateUsers(users);

            var listener = new TcpListener(options.ListenAddress, options.Port);
            // Impide que otro proceso "robe" el puerto abriéndolo a la vez con SO_REUSEADDR.
            listener.Server.ExclusiveAddressUse = true;
            listener.Start(backlog: 64);

            _listener = listener;
            _cts = new CancellationTokenSource();
            Port = options.Port;
            CancellationToken token = _cts.Token;
            _acceptTask = Task.Run(() => AcceptLoopAsync(listener, options, token));
        }

        Logger.Success($"{Protocol} INICIADO - escuchando en {options.ListenAddress}:{options.Port} - raíz general: {options.RootFolder}");
        OnStarted(options);
        RaiseSafe(() => RunningChanged?.Invoke(true));
    }

    protected virtual void ValidateOptions(FtpServerOptions options) => options.Validate();

    protected virtual void OnStarted(FtpServerOptions options)
    {
    }

    public async Task StopAsync()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        Task? acceptTask;
        lock (_stateLock)
        {
            listener = _listener;
            if (listener == null)
                return;
            cts = _cts;
            acceptTask = _acceptTask;
            _listener = null;
            _cts = null;
            _acceptTask = null;
        }

        try { cts?.Cancel(); } catch { }
        try { listener.Stop(); } catch { }

        if (acceptTask != null)
        {
            try { await acceptTask.ConfigureAwait(false); } catch { }
        }

        // Las sesiones cierran sus sockets al cancelarse el token; se espera a que terminen.
        Task[] pending = _sessionTasks.Values.ToArray();
        if (pending.Length > 0)
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);

        cts?.Dispose();
        Port = 0;
        Logger.Info($"{Protocol} DETENIDO");
        RaiseSafe(() => RunningChanged?.Invoke(false));
    }

    /// <summary>Aplica en caliente los cambios de usuarios (altas, bajas, permisos, contraseñas).</summary>
    public void UpdateUsers(IEnumerable<FtpUser> users) => _users = new UserDirectory(users);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>Atiende una conexión ya aceptada hasta que termine.</summary>
    protected abstract Task RunSessionAsync(long id, TcpClient client, IPAddress remoteIp, FtpServerOptions options, CancellationToken serverToken);

    /// <summary>Respuesta de rechazo en el protocolo propio (o null para cerrar sin más).</summary>
    protected abstract string? RejectMessage(string reason);

    internal void NotifyFileReceived(FtpFileReceivedInfo info) => RaiseSafe(() => FileReceived?.Invoke(info));

    private async Task AcceptLoopAsync(TcpListener listener, FtpServerOptions options, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Logger.Error($"{Protocol} ERROR aceptando conexión: {ex.Message}");
                try { await Task.Delay(250, ct).ConfigureAwait(false); } catch { break; }
                continue;
            }

            try
            {
                HandleIncoming(client, options, ct);
            }
            catch (Exception ex)
            {
                Logger.Error($"{Protocol} ERROR preparando sesión: {ex.Message}");
                client.Dispose();
            }
        }
    }

    private void HandleIncoming(TcpClient client, FtpServerOptions options, CancellationToken ct)
    {
        if (client.Client.RemoteEndPoint is not IPEndPoint remote)
        {
            client.Dispose();
            return;
        }

        IPAddress ip = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;

        if (options.AllowOnlyLocalNetworks && !LocalNetworkFilter.IsLocal(ip))
        {
            Logger.Warning($"{Protocol} CONEXIÓN RECHAZADA {ip}: no pertenece a la red local (Configuración > Seguridad)");
            client.Dispose();
            return;
        }

        if (!TryReserveConnection(ip, options, out string? reason))
        {
            Logger.Warning($"{Protocol} CONEXIÓN RECHAZADA {ip}: {reason}");
            _ = RejectAsync(client, RejectMessage(reason!));
            return;
        }

        long id = Interlocked.Increment(ref _nextSessionId);
        client.NoDelay = true;
        RaiseSafe(() => ConnectionsChanged?.Invoke(ActiveConnections));

        Task task = Task.Run(async () =>
        {
            try
            {
                await RunSessionAsync(id, client, ip, options, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error($"{Protocol} SESIÓN ERROR {ip}: {ex.Message}");
            }
            finally
            {
                try { client.Dispose(); } catch { }
                ReleaseConnection(ip);
                _sessionTasks.TryRemove(id, out _);
                RaiseSafe(() => ConnectionsChanged?.Invoke(ActiveConnections));
            }
        }, CancellationToken.None);
        _sessionTasks[id] = task;
        if (task.IsCompleted)
            _sessionTasks.TryRemove(id, out _);
    }

    private bool TryReserveConnection(IPAddress ip, FtpServerOptions options, out string? reason)
    {
        lock (_countLock)
        {
            if (_activeConnections >= options.MaxConnections)
            {
                reason = $"límite total de {options.MaxConnections} conexiones alcanzado";
                return false;
            }
            _connectionsPerIp.TryGetValue(ip, out int count);
            if (count >= options.MaxConnectionsPerIp)
            {
                reason = $"límite de {options.MaxConnectionsPerIp} conexiones por IP alcanzado";
                return false;
            }
            _connectionsPerIp[ip] = count + 1;
            _activeConnections++;
            reason = null;
            return true;
        }
    }

    private void ReleaseConnection(IPAddress ip)
    {
        lock (_countLock)
        {
            if (_connectionsPerIp.TryGetValue(ip, out int count))
            {
                if (count <= 1) _connectionsPerIp.Remove(ip);
                else _connectionsPerIp[ip] = count - 1;
            }
            if (_activeConnections > 0)
                _activeConnections--;
        }
    }

    private static async Task RejectAsync(TcpClient client, string? reply)
    {
        try
        {
            if (reply != null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                byte[] bytes = Encoding.ASCII.GetBytes(reply + "\r\n");
                await client.GetStream().WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // El cliente ya no está: nada que hacer.
        }
        finally
        {
            client.Dispose();
        }
    }

    private static void RaiseSafe(Action action)
    {
        try { action(); }
        catch (Exception ex) { Logger.Error($"SERVIDOR ERROR en notificación: {ex.Message}"); }
    }
}
