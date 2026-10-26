// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using CopyexFTPServer.Core;
using CopyexFTPServer.Models;

namespace CopyexFTPServer.Services;

public sealed record IpScanResult(IReadOnlyList<IPAddress> FreeAddresses, string? Note);

/// <summary>Lectura y cambio de la configuración IPv4 (DHCP / IP fija) del adaptador.</summary>
public static class NetworkIpService
{
    private const int MaxHostsToScan = 1022;      // una /22 como máximo
    private const int PingConcurrency = 48;
    private const int PingTimeoutMs = 600;

    public static NetworkAdapterConfig? GetConfiguration(int interfaceIndex)
    {
        NetworkInterface[] adapters;
        try { adapters = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return null; }

        foreach (NetworkInterface nic in adapters)
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            try
            {
                IPInterfaceProperties props = nic.GetIPProperties();
                IPv4InterfaceProperties? ipv4 = props.GetIPv4Properties();
                if (ipv4 == null || ipv4.Index != interfaceIndex)
                    continue;

                UnicastIPAddressInformation? unicast = props.UnicastAddresses.FirstOrDefault(u =>
                    u.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(u.Address) &&
                    u.Address.GetAddressBytes()[0] != 169);
                if (unicast == null)
                    return null;

                IPAddress? gateway = props.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork &&
                                         !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.None));

                var dns = props.DnsAddresses
                    .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any))
                    .Where(a => !(a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6SiteLocal))
                    .Distinct()
                    .ToList();

                return new NetworkAdapterConfig(nic.Name, nic.Description, nic.Id, interfaceIndex,
                    unicast.Address, unicast.PrefixLength, gateway, dns, ipv4.IsDhcpEnabled);
            }
            catch
            {
                // El adaptador puede cambiar mientras Windows actualiza la red.
            }
        }
        return null;
    }

    /// <summary>
    /// Busca IPs que parecen libres en la subred (de mayor a menor). Primero lanza pings en
    /// paralelo y después consulta la tabla ARP de Windows: un equipo que bloquea ICMP igualmente
    /// responde a ARP. En redes grandes solo se revisa la /24 del equipo.
    /// </summary>
    public static async Task<IpScanResult> FindAvailableIpsAsync(NetworkAdapterConfig config, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        if (config.PrefixLength is < 8 or > 30)
            return new IpScanResult(Array.Empty<IPAddress>(), "La máscara de red no permite buscar IPs libres.");

        uint ip = ToUInt32(config.Address);
        uint mask = uint.MaxValue << (32 - config.PrefixLength);
        uint network = ip & mask;
        uint broadcast = network | ~mask;
        string? note = null;

        uint first = network + 1;
        uint last = broadcast - 1;
        if (last - first + 1 > MaxHostsToScan)
        {
            uint net24 = ip & 0xFFFFFF00;
            first = Math.Max(first, net24 + 1);
            last = Math.Min(last, net24 + 254);
            note = $"La red /{config.PrefixLength} es muy grande: solo se ha revisado {FromUInt32(net24)}/24.";
        }

        var candidates = new List<IPAddress>();
        for (uint cursor = last; cursor >= first && cursor != 0; cursor--)
        {
            IPAddress candidate = FromUInt32(cursor);
            if (candidate.Equals(config.Address) || (config.Gateway != null && candidate.Equals(config.Gateway)))
                continue;
            candidates.Add(candidate);
        }

        var responded = new bool[candidates.Count];
        int done = 0;
        using (var gate = new SemaphoreSlim(PingConcurrency))
        {
            var tasks = new List<Task>(candidates.Count);
            try
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    int index = i;
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            responded[index] = await PingAsync(candidates[index], PingTimeoutMs).ConfigureAwait(false);
                        }
                        finally
                        {
                            gate.Release();
                            int n = Interlocked.Increment(ref done);
                            progress?.Report(n * 100 / candidates.Count);
                        }
                    }, CancellationToken.None));
                }
            }
            finally
            {
                // Siempre se espera a los pings en curso antes de liberar el semáforo.
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        HashSet<uint> arp = ReadArpTable();

        var free = new List<IPAddress>();
        for (int i = 0; i < candidates.Count; i++)
        {
            if (!responded[i] && !arp.Contains(ToUInt32(candidates[i])))
                free.Add(candidates[i]);
        }
        return new IpScanResult(free, note);
    }

    /// <summary>Comprobación final antes de asignar una IP: ping y petición ARP directa.</summary>
    public static async Task<bool> IsAddressApparentlyAvailableAsync(IPAddress address)
    {
        if (await PingAsync(address, 1000).ConfigureAwait(false))
            return false;
        bool arp = await Task.Run(() => RespondsToArp(address)).ConfigureAwait(false);
        return !arp;
    }

    public static IReadOnlyList<IPAddress> GetDnsServersForStatic(NetworkAdapterConfig current)
    {
        var dns = current.DnsServers
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
            .Distinct()
            .ToList();

        // Igual que 2.x: se conservan las DNS actuales; si solo había una se añade 8.8.8.8
        // como secundaria y si no había ninguna se usa 8.8.8.8 para no dejar el equipo sin DNS.
        var google = IPAddress.Parse("8.8.8.8");
        if (dns.Count == 0)
            dns.Add(google);
        else if (dns.Count == 1 && !dns[0].Equals(google))
            dns.Add(google);
        return dns;
    }

    public static bool IsInSameSubnet(NetworkAdapterConfig config, IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || config.PrefixLength is < 1 or > 32)
            return false;
        uint mask = uint.MaxValue << (32 - config.PrefixLength);
        return (ToUInt32(config.Address) & mask) == (ToUInt32(address) & mask);
    }

    public static bool IsNetworkOrBroadcast(NetworkAdapterConfig config, IPAddress address)
    {
        if (config.PrefixLength is < 1 or > 30)
            return false;
        uint mask = uint.MaxValue << (32 - config.PrefixLength);
        uint value = ToUInt32(address);
        return (value & ~mask) == 0 || (value | mask) == uint.MaxValue;
    }

    public static string PrefixToMask(int prefixLength)
    {
        if (prefixLength is < 0 or > 32) return "-";
        uint mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
        return FromUInt32(mask).ToString();
    }

    /// <summary>
    /// Establece IP fija conservando máscara, puerta de enlace y DNS. Si algo falla a mitad,
    /// el script restaura la configuración anterior (en 2.x el adaptador podía quedarse sin IP).
    /// </summary>
    public static async Task<bool> ApplyStaticAsync(NetworkAdapterConfig current, IPAddress newAddress)
    {
        IReadOnlyList<IPAddress> dns = GetDnsServersForStatic(current);
        string idx = current.InterfaceIndex.ToString(CultureInfo.InvariantCulture);
        string prefix = current.PrefixLength.ToString(CultureInfo.InvariantCulture);
        string gatewayArg = current.Gateway == null ? string.Empty : " -DefaultGateway " + Q(current.Gateway);
        string dnsArray = "@(" + string.Join(",", dns.Select(Q)) + ")";

        var oldDns = current.DnsServers.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
        string rollback = current.DhcpEnabled
            ? "Set-NetIPInterface -InterfaceIndex $i -AddressFamily IPv4 -Dhcp Enabled -ErrorAction SilentlyContinue; " +
              "Set-DnsClientServerAddress -InterfaceIndex $i -ResetServerAddresses -ErrorAction SilentlyContinue; "
            : "Get-NetIPAddress -InterfaceIndex $i -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue; " +
              "Get-NetRoute -InterfaceIndex $i -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue; " +
              $"New-NetIPAddress -InterfaceIndex $i -IPAddress {Q(current.Address)} -PrefixLength {prefix}{gatewayArg} -ErrorAction SilentlyContinue | Out-Null; " +
              (oldDns.Count > 0
                  ? $"Set-DnsClientServerAddress -InterfaceIndex $i -ServerAddresses @({string.Join(",", oldDns.Select(Q))}) -ErrorAction SilentlyContinue; "
                  : string.Empty);

        var script = new StringBuilder()
            .Append("$ErrorActionPreference='Stop'; $i=").Append(idx).Append("; ")
            .Append("try { ")
            .Append("Set-NetIPInterface -InterfaceIndex $i -AddressFamily IPv4 -Dhcp Disabled; ")
            .Append("Get-NetIPAddress -InterfaceIndex $i -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue; ")
            .Append("Get-NetRoute -InterfaceIndex $i -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue; ")
            .Append("New-NetIPAddress -InterfaceIndex $i -IPAddress ").Append(Q(newAddress)).Append(" -PrefixLength ").Append(prefix).Append(gatewayArg).Append(" | Out-Null; ")
            .Append("Set-DnsClientServerAddress -InterfaceIndex $i -ServerAddresses ").Append(dnsArray).Append("; ")
            .Append("exit 0 ")
            .Append("} catch { ")
            .Append(rollback)
            .Append("exit 2 }")
            .ToString();

        Logger.Info($"RED: estableciendo IP fija {newAddress}/{current.PrefixLength} en {current.InterfaceName}");
        int exit = await PowerShellRunner.RunElevatedAsync(script, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        if (exit == 2)
        {
            Logger.Error("RED ERROR: Windows rechazó la IP fija; se ha restaurado la configuración anterior");
            return false;
        }
        if (exit != 0)
            return false;

        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(200).ConfigureAwait(false);
            NetworkAdapterConfig? now = GetConfiguration(current.InterfaceIndex);
            if (now != null && !now.DhcpEnabled && now.Address.Equals(newAddress))
            {
                Logger.Success($"RED OK: {current.InterfaceName} configurado con IP fija {newAddress}/{current.PrefixLength}");
                return true;
            }
        }

        Logger.Warning("RED: Windows aceptó el cambio pero todavía no se ha podido confirmar la nueva IP");
        return true;
    }

    public static async Task<bool> ReturnToDhcpAsync(NetworkAdapterConfig current)
    {
        string idx = current.InterfaceIndex.ToString(CultureInfo.InvariantCulture);
        string script =
            "$ErrorActionPreference='Stop'; $i=" + idx + "; " +
            "Get-NetIPAddress -InterfaceIndex $i -AddressFamily IPv4 -PrefixOrigin Manual -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue; " +
            "Get-NetRoute -InterfaceIndex $i -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -Protocol NetMgmt -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue; " +
            "Set-NetIPInterface -InterfaceIndex $i -AddressFamily IPv4 -Dhcp Enabled; " +
            "Set-DnsClientServerAddress -InterfaceIndex $i -ResetServerAddresses; " +
            "$adapter = Get-NetAdapter -InterfaceIndex $i; " +
            "$adapter | Disable-NetAdapter -Confirm:$false; " +
            "Start-Sleep -Milliseconds 1200; " +
            "Get-NetAdapter -Name $adapter.Name | Enable-NetAdapter -Confirm:$false; " +
            "Start-Sleep -Milliseconds 1500; " +
            "& \"$env:SystemRoot\\System32\\ipconfig.exe\" /renew $adapter.Name | Out-Null; " +
            "exit 0";

        Logger.Info($"RED: restaurando DHCP en {current.InterfaceName}");
        int exit = await PowerShellRunner.RunElevatedAsync(script, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        if (exit != 0)
        {
            if (exit != PowerShellRunner.UacCancelledExitCode)
                Logger.Error($"RED ERROR: no se pudo restaurar DHCP (código {exit})");
            return false;
        }

        // Tras reiniciar el adaptador Windows puede tardar unos segundos en obtener la IP.
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(500).ConfigureAwait(false);
            NetworkAdapterConfig? now = GetConfiguration(current.InterfaceIndex);
            if (now?.DhcpEnabled == true)
            {
                Logger.Success($"RED OK: {current.InterfaceName} restaurado a DHCP ({now.Address})");
                return true;
            }
        }

        Logger.Warning("RED: Windows aceptó DHCP pero todavía no se ha podido confirmar la nueva dirección");
        return true;
    }

    // ------------------------------------------------------------------ utilidades

    private static string Q(IPAddress address) => PowerShellRunner.Quote(address.ToString());

    private static async Task<bool> PingAsync(IPAddress address, int timeoutMs)
    {
        try
        {
            using var ping = new Ping();
            PingReply reply = await ping.SendPingAsync(address, timeoutMs).ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private static bool RespondsToArp(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;
        uint destination = BitConverter.ToUInt32(bytes, 0);
        byte[] mac = new byte[6];
        int length = mac.Length;
        return SendARP(destination, 0, mac, ref length) == 0 && length > 0;
    }

    /// <summary>Direcciones IPv4 con entrada válida en la caché ARP de Windows.</summary>
    private static HashSet<uint> ReadArpTable()
    {
        var result = new HashSet<uint>();
        int size = 0;
        int rc = GetIpNetTable(IntPtr.Zero, ref size, false);
        if (rc != 122 /* ERROR_INSUFFICIENT_BUFFER */ || size <= 0)
            return result;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, false) != 0)
                return result;

            int count = Marshal.ReadInt32(buffer);
            const int rowSize = 24; // MIB_IPNETROW
            for (int i = 0; i < count; i++)
            {
                IntPtr row = buffer + 4 + i * rowSize;
                int macLength = Marshal.ReadInt32(row, 4);
                int type = Marshal.ReadInt32(row, 20);
                if (type is not (3 or 4) || macLength <= 0)   // 3 = dinámica, 4 = estática
                    continue;

                bool allZero = true, allFf = true;
                for (int b = 0; b < Math.Min(macLength, 8); b++)
                {
                    byte value = Marshal.ReadByte(row, 8 + b);
                    if (value != 0) allZero = false;
                    if (value != 0xFF) allFf = false;
                }
                if (allZero || allFf)
                    continue;

                byte[] addr = BitConverter.GetBytes(Marshal.ReadInt32(row, 16));
                result.Add(((uint)addr[0] << 24) | ((uint)addr[1] << 16) | ((uint)addr[2] << 8) | addr[3]);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    internal static uint ToUInt32(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    internal static IPAddress FromUInt32(uint value) =>
        new(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref int physicalAddrLen);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetIpNetTable(IntPtr pIpNetTable, ref int pdwSize, bool bOrder);
}
