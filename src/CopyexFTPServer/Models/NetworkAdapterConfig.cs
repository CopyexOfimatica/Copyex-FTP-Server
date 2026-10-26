// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Net;
using System.Net.NetworkInformation;

namespace CopyexFTPServer.Models;

public sealed record NetworkAdapterConfig(
    string InterfaceName,
    string InterfaceDescription,
    string InterfaceId,
    int InterfaceIndex,
    IPAddress Address,
    int PrefixLength,
    IPAddress? Gateway,
    IReadOnlyList<IPAddress> DnsServers,
    bool DhcpEnabled)
{
    public string ModeText => DhcpEnabled ? "DHCP" : "IP fija";
}

/// <summary>Adaptador que se considera "la red del equipo" para mostrar al técnico.</summary>
public sealed record PreferredNetwork(
    IPAddress Address,
    string InterfaceName,
    string InterfaceId,
    int InterfaceIndex,
    NetworkInterfaceType InterfaceType,
    bool HasGateway);

public enum NetworkCategory
{
    Unknown,
    Public,
    Private,
    Domain
}
