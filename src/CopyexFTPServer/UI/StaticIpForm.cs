// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Net;
using System.Net.Sockets;
using CopyexFTPServer.Core;
using CopyexFTPServer.Models;
using CopyexFTPServer.Services;

namespace CopyexFTPServer.UI;

/// <summary>Ventana para ver DHCP/IP fija, buscar IPs libres y aplicar el cambio.</summary>
internal sealed class StaticIpForm : Form
{
    private NetworkAdapterConfig _config;

    private readonly Label _adapter = Ui.MakeLabel("", Theme.CardTitle, Theme.Text, autoSize: false);
    private readonly Label _adapterDesc = Ui.Hint("");
    private readonly TextBox _currentIp = ReadOnlyField();
    private readonly TextBox _mask = ReadOnlyField();
    private readonly TextBox _gateway = ReadOnlyField();
    private readonly TextBox _dns = ReadOnlyField();
    private readonly Badge _mode = new();
    private readonly TextBox _newIp = new() { Font = Theme.Mono, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
    private readonly ListBox _list = new();
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Bottom, Height = 4, Style = ProgressBarStyle.Continuous, Visible = false };
    private readonly Label _scanInfo = Ui.Hint("");
    private readonly ModernButton _btnScan = ModernButton.Create("Buscar IPs libres", ButtonKind.Secondary, Glyphs.Search);
    private readonly ModernButton _btnApply = ModernButton.Create("Establecer IP fija", ButtonKind.Primary, Glyphs.Check);
    private readonly ModernButton _btnDhcp = ModernButton.Create("Volver a DHCP", ButtonKind.Secondary, Glyphs.Undo);
    private readonly ModernButton _btnClose = ModernButton.Create("Cerrar", ButtonKind.Secondary);

    private CancellationTokenSource? _scanCts;
    private bool _busy;

    public StaticIpForm(NetworkAdapterConfig config)
    {
        _config = config;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Configuración IP del equipo";
        Icon = AppResources.AppIcon;
        Font = Theme.Base;
        BackColor = Theme.Background;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, 660);
        MinimumSize = new Size(640, 600);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Padding = new Padding(18);
        BuildUi();
        ResumeLayout(true);

        LoadConfig();
        Shown += async (_, _) => await ScanAsync();
        FormClosing += (_, e) =>
        {
            if (_busy)
            {
                e.Cancel = true; // no se cierra a mitad de un cambio de red
                return;
            }
            _scanCts?.Cancel();
        };
        FormClosed += (_, _) => _scanCts?.Dispose();
    }

    private void BuildUi()
    {
        var current = new Card { Height = 270 };
        TableLayoutPanel g1 = Ui.Grid(2, 150, -1);
        _adapter.Dock = DockStyle.Fill;
        _adapter.AutoEllipsis = true;
        Ui.AddRow(g1, 30, _adapter);
        g1.SetColumnSpan(_adapter, 2);
        Ui.AddRow(g1, 24, _adapterDesc);
        g1.SetColumnSpan(_adapterDesc, 2);
        Ui.AddRow(g1, 36, Ui.FieldLabel("IP actual"), _currentIp);
        Ui.AddRow(g1, 36, Ui.FieldLabel("Máscara"), _mask);
        Ui.AddRow(g1, 36, Ui.FieldLabel("Puerta de enlace"), _gateway);
        Ui.AddRow(g1, 36, Ui.FieldLabel("DNS actuales"), _dns);
        _mode.Anchor = AnchorStyles.Left;
        Ui.AddRow(g1, 38, Ui.FieldLabel("Modo"), _mode);
        current.Controls.Add(g1);

        var picker = new Card { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 14) };
        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 84, ColumnCount = 3, BackColor = Color.Transparent };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        Label pickTitle = Ui.MakeLabel("Nueva IP fija", Theme.CardTitle);
        pickTitle.Anchor = AnchorStyles.Left;
        _btnScan.Anchor = AnchorStyles.Right;
        _btnScan.Height = 32;
        _btnScan.Margin = Padding.Empty;
        top.Controls.Add(pickTitle, 0, 0);
        top.SetColumnSpan(pickTitle, 2);
        top.Controls.Add(_btnScan, 2, 0);
        top.Controls.Add(Ui.FieldLabel("IP a establecer"), 0, 1);
        top.Controls.Add(_newIp, 1, 1);

        _scanInfo.Dock = DockStyle.Bottom;
        _scanInfo.Height = 40;
        _scanInfo.Text = "Las IPs se comprueban con ping y la tabla ARP. Un equipo apagado o una IP del rango DHCP del router podría no detectarse: revisa las reservas del router si existen.";

        _list.Dock = DockStyle.Fill;
        _list.Font = Theme.Mono;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.IntegralHeight = false;

        picker.Controls.Add(_list);
        picker.Controls.Add(_progress);
        picker.Controls.Add(_scanInfo);
        picker.Controls.Add(top);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 12, 0, 0)
        };
        _btnClose.Margin = new Padding(40, 0, 0, 0);
        buttons.Controls.AddRange(new Control[] { _btnApply, _btnDhcp, _btnClose });

        var spacer = new Panel { Dock = DockStyle.Top, Height = 12, BackColor = Color.Transparent };
        current.Dock = DockStyle.Top;

        Controls.Add(picker);
        Controls.Add(spacer);
        Controls.Add(current);
        Controls.Add(buttons);

        _btnScan.Click += async (_, _) => await ScanAsync();
        _btnApply.Click += async (_, _) => await ApplyAsync();
        _btnDhcp.Click += async (_, _) => await ReturnToDhcpAsync();
        _btnClose.Click += (_, _) => Close();
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (_list.SelectedItem is string value && IPAddress.TryParse(value, out _))
                _newIp.Text = value;
        };
        _list.DoubleClick += async (_, _) =>
        {
            if (_list.SelectedItem is string value && IPAddress.TryParse(value, out _))
                await ApplyAsync();
        };
        _newIp.TextChanged += (_, _) => UpdateButtons();
        CancelButton = _btnClose;
    }

    private void LoadConfig()
    {
        _adapter.Text = "Adaptador: " + _config.InterfaceName;
        _adapterDesc.Text = _config.InterfaceDescription;
        _currentIp.Text = $"{_config.Address}/{_config.PrefixLength}";
        _mask.Text = NetworkIpService.PrefixToMask(_config.PrefixLength);
        _gateway.Text = _config.Gateway?.ToString() ?? "Sin puerta de enlace";
        _dns.Text = _config.DnsServers.Count == 0 ? "No disponibles" : string.Join("  ·  ", _config.DnsServers);
        if (_config.DhcpEnabled)
            _mode.Set(BadgeKind.Warning, "DHCP (automática)", Glyphs.Warning);
        else
            _mode.Set(BadgeKind.Success, "IP fija", Glyphs.Check);
        _btnApply.Text = _config.DhcpEnabled ? "Establecer IP fija" : "Cambiar IP fija";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _btnScan.Enabled = !_busy;
        _btnApply.Enabled = !_busy && IPAddress.TryParse(_newIp.Text.Trim(), out IPAddress? ip) && ip.AddressFamily == AddressFamily.InterNetwork;
        _btnDhcp.Enabled = !_busy && !_config.DhcpEnabled;
        _btnClose.Enabled = !_busy;
    }

    private async Task ScanAsync()
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        CancellationToken token = _scanCts.Token;

        _btnScan.Enabled = false;
        _list.Items.Clear();
        _list.Items.Add("Buscando IPs libres…");
        _progress.Value = 0;
        _progress.Visible = true;
        var progress = new Progress<int>(p => { if (!IsDisposed) _progress.Value = Math.Clamp(p, 0, 100); });

        try
        {
            IpScanResult result = await NetworkIpService.FindAvailableIpsAsync(_config, progress, token);
            if (IsDisposed) return;

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (IPAddress ip in result.FreeAddresses)
                _list.Items.Add(ip.ToString());
            if (_list.Items.Count == 0)
                _list.Items.Add("No se encontraron IPs libres en el rango comprobado.");
            _list.EndUpdate();

            if (result.FreeAddresses.Count > 0)
                _list.SelectedIndex = 0;
            if (result.Note != null)
                _scanInfo.Text = result.Note + " Las IPs se comprueban con ping y la tabla ARP.";
        }
        catch (OperationCanceledException)
        {
            // Ventana cerrada o nueva búsqueda.
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            _list.Items.Clear();
            _list.Items.Add("Error buscando IPs libres.");
            Logger.Error($"RED ERROR: búsqueda de IPs libres: {ex.Message}");
        }
        finally
        {
            if (!IsDisposed)
            {
                _progress.Visible = false;
                UpdateButtons();
            }
        }
    }

    private async Task ApplyAsync()
    {
        if (_busy) return;
        if (!IPAddress.TryParse(_newIp.Text.Trim(), out IPAddress? ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            Ui.Info(this, "Indica una dirección IPv4 válida o selecciona una de la lista.");
            return;
        }
        if (!NetworkIpService.IsInSameSubnet(_config, ip) || NetworkIpService.IsNetworkOrBroadcast(_config, ip))
        {
            Ui.Warn(this, "La IP indicada no es una dirección de equipo válida en la subred actual.");
            return;
        }
        if (_config.Gateway != null && ip.Equals(_config.Gateway))
        {
            Ui.Warn(this, "No puedes usar como IP del equipo la dirección de la puerta de enlace.");
            return;
        }

        SetBusy(true);
        try
        {
            if (!ip.Equals(_config.Address))
            {
                bool free = await NetworkIpService.IsAddressApparentlyAvailableAsync(ip);
                if (!free)
                {
                    Ui.Warn(this, "La IP ha respondido en la comprobación final y puede estar en uso. Elige otra dirección.");
                    SetBusy(false);
                    await ScanAsync();
                    return;
                }
            }

            IReadOnlyList<IPAddress> dns = NetworkIpService.GetDnsServersForStatic(_config);
            string message =
                "Se configurará el adaptador con:\r\n\r\n" +
                $"IP: {ip}\r\nMáscara: {NetworkIpService.PrefixToMask(_config.PrefixLength)}\r\n" +
                $"Puerta de enlace: {_config.Gateway?.ToString() ?? "(ninguna)"}\r\n" +
                $"DNS: {string.Join(", ", dns)}\r\n\r\n" +
                "Windows pedirá permisos de administrador. Si algo falla se restaura la configuración anterior.\r\n\r\n¿Continuar?";
            if (!Ui.Confirm(this, message, "Establecer IP fija", MessageBoxIcon.Warning))
                return;

            bool ok = await NetworkIpService.ApplyStaticAsync(_config, ip);
            NetworkAdapterConfig? updated = NetworkIpService.GetConfiguration(_config.InterfaceIndex);
            if (updated != null) _config = updated;
            LoadConfig();

            if (ok)
            {
                Ui.Info(this, "IP fija configurada. Recuerda poner esta IP en el escáner.");
                _busy = false;
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                Ui.Error(this, "No se pudo establecer la IP fija. Revisa el Registro y el aviso de permisos (UAC).");
            }
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private async Task ReturnToDhcpAsync()
    {
        if (_busy) return;
        if (!Ui.Confirm(this,
                "Se restaurarán la IP y las DNS automáticas (DHCP).\r\n\r\n" +
                "El adaptador de red se desactivará y activará unos segundos para renovar la conexión. " +
                "La IP del equipo cambiará y habrá que actualizarla en el escáner.\r\n\r\n¿Continuar?",
                "Volver a DHCP"))
            return;

        SetBusy(true);
        try
        {
            bool ok = await NetworkIpService.ReturnToDhcpAsync(_config);
            if (ok)
            {
                Ui.Info(this, "Adaptador restaurado a DHCP. Windows obtendrá IP, puerta de enlace y DNS automáticamente.");
                _busy = false;
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                Ui.Error(this, "No se pudo restaurar DHCP. Revisa el Registro y el aviso de permisos (UAC).");
            }
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        UpdateButtons();
    }

    private static TextBox ReadOnlyField() => new()
    {
        ReadOnly = true,
        Font = Theme.Base,
        Dock = DockStyle.Fill,
        BackColor = Theme.Surface,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 5, 0, 0)
    };
}
