// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CopyexFTPServer.Tests;

/// <summary>Cliente FTP mínimo para pruebas: permite enviar comandos "malintencionados" a mano.</summary>
internal sealed class RawFtp : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private RawFtp(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        _reader = new StreamReader(_stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
    }

    public static async Task<(RawFtp Ftp, int Code, string Text)> ConnectAsync(int port, IPAddress? localAddress = null)
    {
        var client = localAddress == null ? new TcpClient() : new TcpClient(new IPEndPoint(localAddress, 0));
        await client.ConnectAsync(IPAddress.Loopback, port);
        var ftp = new RawFtp(client);
        (int code, string text) = await ftp.ReadReplyAsync();
        return (ftp, code, text);
    }

    public async Task<(int Code, string Text)> ReadReplyAsync()
    {
        using var cts = new CancellationTokenSource(Timeout);
        string? line;
        try
        {
            line = await _reader.ReadLineAsync(cts.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return (-1, ex.GetType().Name);
        }
        if (line == null || line.Length < 3)
            return (-1, line ?? "<cerrado>");

        int code = int.Parse(line[..3], CultureInfo.InvariantCulture);
        var text = new StringBuilder(line);
        if (line.Length > 3 && line[3] == '-')
        {
            string end = line[..3] + " ";
            while (true)
            {
                string? next = await _reader.ReadLineAsync(cts.Token);
                if (next == null) break;
                text.Append('\n').Append(next);
                if (next.StartsWith(end, StringComparison.Ordinal)) break;
            }
        }
        return (code, text.ToString());
    }

    public async Task SendAsync(string command)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(command + "\r\n");
        await _stream.WriteAsync(bytes);
    }

    public async Task SendRawAsync(byte[] bytes) => await _stream.WriteAsync(bytes);

    public async Task<(int Code, string Text)> CmdAsync(string command)
    {
        await SendAsync(command);
        return await ReadReplyAsync();
    }

    public async Task<int> LoginAsync(string user, string password)
    {
        (int c1, _) = await CmdAsync("USER " + user);
        if (c1 != 331) return c1;
        (int c2, _) = await CmdAsync("PASS " + password);
        return c2;
    }

    public async Task<IPEndPoint> PasvAsync()
    {
        (int code, string text) = await CmdAsync("PASV");
        if (code != 227) throw new InvalidOperationException("PASV falló: " + text);
        int open = text.IndexOf('(');
        int close = text.IndexOf(')', open);
        int[] n = text[(open + 1)..close].Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        return new IPEndPoint(new IPAddress(new[] { (byte)n[0], (byte)n[1], (byte)n[2], (byte)n[3] }), n[4] * 256 + n[5]);
    }

    public async Task<int> EpsvAsync()
    {
        (int code, string text) = await CmdAsync("EPSV");
        if (code != 229) throw new InvalidOperationException("EPSV falló: " + text);
        int a = text.IndexOf("|||", StringComparison.Ordinal) + 3;
        int b = text.IndexOf('|', a);
        return int.Parse(text[a..b], CultureInfo.InvariantCulture);
    }

    public async Task<(int Code, string Text, byte[] Data)> RetrAsync(string path, long? rest = null)
    {
        IPEndPoint ep = await PasvAsync();
        if (rest.HasValue)
        {
            (int rc, string rt) = await CmdAsync("REST " + rest.Value.ToString(CultureInfo.InvariantCulture));
            if (rc != 350) return (rc, rt, Array.Empty<byte>());
        }
        using var data = new TcpClient();
        await data.ConnectAsync(ep);
        (int c1, string t1) = await CmdAsync("RETR " + path);
        if (c1 != 150) return (c1, t1, Array.Empty<byte>());
        using var ms = new MemoryStream();
        await data.GetStream().CopyToAsync(ms);
        (int c2, string t2) = await ReadReplyAsync();
        return (c2, t2, ms.ToArray());
    }

    public async Task<(int Code, string Text)> StorAsync(string path, byte[] content, long? rest = null, string verb = "STOR")
    {
        IPEndPoint ep = await PasvAsync();
        if (rest.HasValue)
        {
            (int rc, string rt) = await CmdAsync("REST " + rest.Value.ToString(CultureInfo.InvariantCulture));
            if (rc != 350) return (rc, rt);
        }
        using var data = new TcpClient();
        await data.ConnectAsync(ep);
        (int c1, string t1) = await CmdAsync(verb + " " + path);
        if (c1 != 150) return (c1, t1);
        await data.GetStream().WriteAsync(content);
        data.Client.Shutdown(SocketShutdown.Send);
        data.Close();
        return await ReadReplyAsync();
    }

    public async Task<(int Code, string Text, string Listing)> ListAsync(string command)
    {
        IPEndPoint ep = await PasvAsync();
        using var data = new TcpClient();
        await data.ConnectAsync(ep);
        (int c1, string t1) = await CmdAsync(command);
        if (c1 != 150) return (c1, t1, string.Empty);
        using var reader = new StreamReader(data.GetStream(), Encoding.UTF8);
        string listing = await reader.ReadToEndAsync();
        (int c2, string t2) = await ReadReplyAsync();
        return (c2, t2, listing);
    }

    public bool IsClosed()
    {
        try
        {
            return _client.Client.Poll(0, SelectMode.SelectRead) && _client.Client.Available == 0;
        }
        catch
        {
            return true;
        }
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
        _client.Dispose();
    }
}
