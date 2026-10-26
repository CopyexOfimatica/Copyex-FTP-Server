// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CopyexFTPServer.Ftp;

/// <summary>
/// Decide si una IP pertenece a la red local: rangos privados (RFC 1918), enlace local,
/// loopback, o cualquier subred configurada en los adaptadores del propio equipo (cubre
/// empresas que usan IPs públicas dentro de su LAN).
/// </summary>
internal static class LocalNetworkFilter
{
    private static readonly object Sync = new();
    private static (uint Network, uint Mask)[] _subnets = Array.Empty<(uint, uint)>();
    private static DateTime _refreshedUtc = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;

        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        uint value = ToUInt32(address);
        if (IsPrivateIPv4(value))
            return true;

        foreach ((uint network, uint mask) in GetLocalSubnets())
        {
            if ((value & mask) == network)
                return true;
        }
        return false;
    }

    public static bool IsPrivateIPv4(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && IsPrivateIPv4(ToUInt32(address));

    private static bool IsPrivateIPv4(uint v) =>
        (v & 0xFF000000) == 0x0A000000 ||   // 10.0.0.0/8
        (v & 0xFFF00000) == 0xAC100000 ||   // 172.16.0.0/12
        (v & 0xFFFF0000) == 0xC0A80000 ||   // 192.168.0.0/16
        (v & 0xFFFF0000) == 0xA9FE0000 ||   // 169.254.0.0/16
        (v & 0xFF000000) == 0x7F000000;     // 127.0.0.0/8

    private static (uint Network, uint Mask)[] GetLocalSubnets()
    {
        lock (Sync)
        {
            if (DateTime.UtcNow - _refreshedUtc < CacheDuration)
                return _subnets;

            var list = new List<(uint, uint)>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                        continue;
                    try
                    {
                        foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
                        {
                            if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                                continue;
                            int prefix = unicast.PrefixLength;
                            if (prefix is < 8 or > 32)
                                continue;
                            uint mask = prefix == 32 ? uint.MaxValue : uint.MaxValue << (32 - prefix);
                            list.Add((ToUInt32(unicast.Address) & mask, mask));
                        }
                    }
                    catch
                    {
                        // Adaptador cambiando de estado: se ignora.
                    }
                }
            }
            catch
            {
                // Sin información de red: solo se aceptan rangos privados.
            }

            _subnets = list.ToArray();
            _refreshedUtc = DateTime.UtcNow;
            return _subnets;
        }
    }

    internal static uint ToUInt32(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
}
