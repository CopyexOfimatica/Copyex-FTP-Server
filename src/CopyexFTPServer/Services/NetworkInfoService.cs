// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using CopyexFTPServer.Models;

namespace CopyexFTPServer.Services;

/// <summary>Detecta el adaptador "principal" del equipo (el que el escáner debe usar).</summary>
public static class NetworkInfoService
{
    private static readonly string[] VirtualMarkers =
    {
        "virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "wireguard",
        "tailscale", "zerotier", "tunnel", "tap-", "tap ", "vpn", "npcap", "wsl", "loopback"
    };

    public static string GetHostName()
    {
        try { return Dns.GetHostName(); }
        catch { return Environment.MachineName; }
    }

    public static PreferredNetwork? GetPreferredNetwork()
    {
        var candidates = new List<PreferredNetwork>();

        NetworkInterface[] adapters;
        try { adapters = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return null; }

        foreach (NetworkInterface adapter in adapters)
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
                IsLikelyVirtual(adapter))
                continue;

            try
            {
                IPInterfaceProperties props = adapter.GetIPProperties();
                int index = props.GetIPv4Properties()?.Index ?? -1;
                if (index < 0)
                    continue;

                bool hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.None));

                foreach (UnicastIPAddressInformation unicast in props.UnicastAddresses)
                {
                    IPAddress address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(address) ||
                        address.GetAddressBytes()[0] == 169)
                        continue;

                    candidates.Add(new PreferredNetwork(address, adapter.Name, adapter.Id, index, adapter.NetworkInterfaceType, hasGateway));
                }
            }
            catch
            {
                // Adaptador transitorio: se ignora.
            }
        }

        if (candidates.Count == 0)
            return null;

        int bestIndex = GetWindowsBestInterfaceIndex();
        if (bestIndex >= 0)
        {
            PreferredNetwork? best = candidates
                .Where(n => n.InterfaceIndex == bestIndex)
                .OrderByDescending(n => Ftp.LocalNetworkFilter.IsPrivateIPv4(n.Address))
                .FirstOrDefault();
            if (best != null)
                return best;
        }

        return candidates
            .OrderByDescending(n => n.HasGateway)
            .ThenByDescending(n => Ftp.LocalNetworkFilter.IsPrivateIPv4(n.Address))
            .ThenByDescending(n => IsPhysicalLan(n.InterfaceType))
            .FirstOrDefault();
    }

    private static bool IsLikelyVirtual(NetworkInterface adapter)
    {
        string text = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
        return VirtualMarkers.Any(text.Contains);
    }

    private static bool IsPhysicalLan(NetworkInterfaceType type) => type is
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or
        NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT or
        NetworkInterfaceType.Wireless80211;

    /// <summary>Interfaz que Windows usaría para salir a Internet (no envía tráfico).</summary>
    private static int GetWindowsBestInterfaceIndex()
    {
        try
        {
            uint destination = BitConverter.ToUInt32(new byte[] { 8, 8, 8, 8 }, 0);
            return GetBestInterface(destination, out uint index) == 0 ? unchecked((int)index) : -1;
        }
        catch
        {
            return -1;
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);
}
