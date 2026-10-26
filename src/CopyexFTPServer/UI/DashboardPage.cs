// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using CopyexFTPServer.Ftp;
using CopyexFTPServer.Models;

namespace CopyexFTPServer.UI;

/// <summary>Página "Panel": estado del servidor, datos para configurar el escáner y red.</summary>
internal sealed class DashboardPage : UserControl
{
    private readonly ToolTip _toolTip = new();

    private readonly Banner _firstRunBanner = new();

    private readonly StatusDot _dot = new();
    private readonly Label _statusTitle = Ui.MakeLabel("Servidor detenido", Theme.Hero);
    private readonly Label _statusSubtitle = Ui.MakeLabel("Los escáneres no pueden enviar archivos", Theme.Base, Theme.TextMuted);
    private readonly ModernButton _btnStart = ModernButton.Create("Iniciar", ButtonKind.Success, Glyphs.Play, 118);
    private readonly ModernButton _btnStop = ModernButton.Create("Detener", ButtonKind.Danger, Glyphs.Stop, 118);
    private readonly ModernButton _btnRestart = ModernButton.Create("Reiniciar", ButtonKind.Warning, Glyphs.Refresh, 118);

    private readonly Label _ipValue = Ui.MakeLabel("—", Theme.Value);
    private readonly Label _ipHint = Ui.MakeLabel("Dirección del equipo en la red", Theme.Small, Theme.TextMuted);
    private readonly ModernButton _btnCopyIp = ModernButton.Create("", ButtonKind.Secondary, Glyphs.Copy, 36);
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 21, Width = 84, Font = Theme.Value, TextAlign = HorizontalAlignment.Center };
    private readonly ModernButton _btnApplyPort = ModernButton.Create("Aplicar", ButtonKind.Primary, null, 76);
    private readonly Label _portHint = Ui.MakeLabel("El que se pone en el escáner", Theme.Small, Theme.TextMuted);
    private readonly Label _hostValue = Ui.MakeLabel("—", Theme.Value);
    private readonly Label _hostHint = Ui.MakeLabel("Nombre de red del equipo", Theme.Small, Theme.TextMuted);
    private readonly ModernButton _btnCopyHost = ModernButton.Create("", ButtonKind.Secondary, Glyphs.Copy, 36);
    private readonly Label _receivedValue = Ui.MakeLabel("0", Theme.Value);
    private readonly Label _receivedHint = Ui.MakeLabel("Ninguno todavía", Theme.Small, Theme.TextMuted);

    private readonly Badge _profileBadge = new();
    private readonly Badge _ipModeBadge = new();
    private readonly ModernButton _btnCheck = ModernButton.Create("Comprobar", ButtonKind.Secondary, Glyphs.Refresh);
    private readonly ModernButton _btnMakePrivate = ModernButton.Create("Cambiar a privada", ButtonKind.Primary, Glyphs.Shield);
    private readonly ModernButton _btnConfigureIp = ModernButton.Create("Configurar IP", ButtonKind.Secondary, Glyphs.Network);
    private readonly Label _networkHint = Ui.Hint("");

    private readonly Label _folderPath = Ui.MakeLabel("", Theme.Bold, Theme.Text, autoSize: false);
    private readonly ModernButton _btnOpenFolder = ModernButton.Create("Abrir carpeta", ButtonKind.Secondary, Glyphs.FolderOpen);
    private readonly ModernButton _btnShortcut = ModernButton.Create("Acceso directo", ButtonKind.Secondary, Glyphs.Shortcut);

    private int _savedPort = 21;

    public event EventHandler? StartClicked;
    public event EventHandler? StopClicked;
    public event EventHandler? RestartAppClicked;
    public event EventHandler<int>? ApplyPortClicked;
    public event EventHandler? CheckNetworkClicked;
    public event EventHandler? MakePrivateClicked;
    public event EventHandler? ConfigureIpClicked;
    public event EventHandler? OpenFolderClicked;
    public event EventHandler? CreateShortcutClicked;
    public event EventHandler? GoToSettingsClicked;

    public DashboardPage()
    {
        BackColor = Theme.Background;
        AutoScroll = true;
        Font = Theme.Base;

        var body = new Panel { Dock = DockStyle.Top, BackColor = Theme.Background, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };

        _firstRunBanner.Set(BadgeKind.Info, Glyphs.Info,
            "Primera ejecución: revisa la carpeta raíz en Configuración y pulsa «Iniciar». El acceso directo ESCANER se creará entonces.",
            "Configuración");
        _firstRunBanner.Action.Click += (_, _) => GoToSettingsClicked?.Invoke(this, EventArgs.Empty);
        _firstRunBanner.Height = 60;

        Ui.StackTop(body, 14, _firstRunBanner, BuildHero(), BuildTiles(), BuildNetworkCard(), BuildFolderCard());
        Ui.SetStackedVisible(_firstRunBanner, false);
        Controls.Add(body);

        _btnStart.Click += (_, _) => StartClicked?.Invoke(this, EventArgs.Empty);
        _btnStop.Click += (_, _) => StopClicked?.Invoke(this, EventArgs.Empty);
        _btnRestart.Click += (_, _) => RestartAppClicked?.Invoke(this, EventArgs.Empty);
        _btnApplyPort.Click += (_, _) => ApplyPortClicked?.Invoke(this, (int)_port.Value);
        _port.ValueChanged += (_, _) => UpdatePortButton();
        _port.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && _btnApplyPort.Enabled)
            {
                ApplyPortClicked?.Invoke(this, (int)_port.Value);
                e.SuppressKeyPress = true;
            }
        };
        _btnCheck.Click += (_, _) => CheckNetworkClicked?.Invoke(this, EventArgs.Empty);
        _btnMakePrivate.Click += (_, _) => MakePrivateClicked?.Invoke(this, EventArgs.Empty);
        _btnConfigureIp.Click += (_, _) => ConfigureIpClicked?.Invoke(this, EventArgs.Empty);
        _btnOpenFolder.Click += (_, _) => OpenFolderClicked?.Invoke(this, EventArgs.Empty);
        _btnShortcut.Click += (_, _) => CreateShortcutClicked?.Invoke(this, EventArgs.Empty);
        _toolTip.SetToolTip(_btnShortcut, "Crear o actualizar el acceso directo «ESCANER» del escritorio a esta carpeta");
        _btnCopyIp.Click += (_, _) => CopyValue(_ipValue.Text, _btnCopyIp);
        _btnCopyHost.Click += (_, _) => CopyValue(_hostValue.Text, _btnCopyHost);

        _toolTip.SetToolTip(_btnStart, "Iniciar el servidor (START)");
        _toolTip.SetToolTip(_btnStop, "Detener el servidor (STOP)");
        _toolTip.SetToolTip(_btnRestart, "Cerrar y volver a abrir Copyex FTP Server");
        _toolTip.SetToolTip(_btnCopyIp, "Copiar IP");
        _toolTip.SetToolTip(_btnCopyHost, "Copiar nombre del equipo");
        _toolTip.SetToolTip(_btnApplyPort, "Guardar el nuevo puerto (reinicia el servidor si está en marcha)");
        _toolTip.SetToolTip(_btnCheck, "Volver a consultar IP, perfil de red y DHCP");
        _toolTip.SetToolTip(_btnMakePrivate, "Cambiar este adaptador de red de Pública a Privada (pide permisos)");
        _toolTip.SetToolTip(_btnConfigureIp, "Ver DHCP/IP fija, buscar IPs libres y configurar el adaptador");

        SetNetworkChecking(true);
        UpdatePortButton();
    }

    private Card BuildHero()
    {
        var card = new Card { Height = 100, Padding = new Padding(20, 16, 20, 16) };
        TableLayoutPanel grid = Ui.Grid(3, 40, -1, 0);
        grid.RowCount = 1;
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _dot.Anchor = AnchorStyles.Left;
        _dot.Size = new Size(26, 26);

        var texts = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Surface, Margin = Padding.Empty };
        texts.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        texts.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        foreach (Label l in new[] { _statusTitle, _statusSubtitle })
        {
            l.AutoSize = false;
            l.Dock = DockStyle.Fill;
            l.AutoEllipsis = true;
        }
        _statusTitle.TextAlign = ContentAlignment.BottomLeft;
        _statusSubtitle.TextAlign = ContentAlignment.TopLeft;
        texts.Controls.Add(_statusTitle, 0, 0);
        texts.Controls.Add(_statusSubtitle, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            BackColor = Theme.Surface,
            Margin = Padding.Empty
        };
        foreach (ModernButton b in new[] { _btnStart, _btnStop, _btnRestart })
        {
            b.Height = 44;
            b.Margin = new Padding(8, 0, 0, 0);
            buttons.Controls.Add(b);
        }

        grid.Controls.Add(_dot, 0, 0);
        grid.Controls.Add(texts, 1, 0);
        grid.Controls.Add(buttons, 2, 0);
        card.Controls.Add(grid);
        return card;
    }

    private Control BuildTiles()
    {
        var row = new TableLayoutPanel { Height = 112, ColumnCount = 4, RowCount = 1, BackColor = Theme.Background, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        row.Controls.Add(BuildTile("IP PARA EL ESCÁNER", _ipValue, _ipHint, _btnCopyIp, new Padding(0, 0, 6, 0)), 0, 0);
        row.Controls.Add(BuildPortTile(new Padding(6, 0, 6, 0)), 1, 0);
        row.Controls.Add(BuildTile("NOMBRE DEL EQUIPO", _hostValue, _hostHint, _btnCopyHost, new Padding(6, 0, 6, 0)), 2, 0);
        row.Controls.Add(BuildTile("ESCANEOS RECIBIDOS HOY", _receivedValue, _receivedHint, null, new Padding(6, 0, 0, 0)), 3, 0);
        return row;
    }

    private static Card BuildTile(string caption, Label value, Label hint, ModernButton? action, Padding margin)
    {
        var card = new Card { Dock = DockStyle.Fill, Margin = margin, Padding = new Padding(16, 14, 10, 12) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = Theme.Surface };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Label cap = Ui.MakeLabel(caption, Theme.Caption, Theme.TextMuted);
        value.AutoSize = false;
        value.Dock = DockStyle.Fill;
        value.AutoEllipsis = true;
        value.TextAlign = ContentAlignment.MiddleLeft;
        hint.AutoSize = false;
        hint.Dock = DockStyle.Fill;
        hint.AutoEllipsis = true;

        grid.Controls.Add(cap, 0, 0);
        grid.SetColumnSpan(cap, 2);
        grid.Controls.Add(value, 0, 1);
        grid.Controls.Add(hint, 0, 2);
        grid.SetColumnSpan(hint, 2);
        if (action != null)
        {
            action.Height = 32;
            action.Anchor = AnchorStyles.Right;
            action.Margin = Padding.Empty;
            grid.Controls.Add(action, 1, 1);
        }
        card.Controls.Add(grid);
        return card;
    }

    private Card BuildPortTile(Padding margin)
    {
        var card = new Card { Dock = DockStyle.Fill, Margin = margin, Padding = new Padding(16, 14, 10, 12) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = Theme.Surface };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Label cap = Ui.MakeLabel("PUERTO FTP", Theme.Caption, Theme.TextMuted);
        _port.Anchor = AnchorStyles.Left;
        _port.Margin = new Padding(0, 2, 8, 0);
        _btnApplyPort.Height = 32;
        _btnApplyPort.Anchor = AnchorStyles.Left;
        _btnApplyPort.Margin = Padding.Empty;
        _portHint.AutoSize = false;
        _portHint.Dock = DockStyle.Fill;
        _portHint.AutoEllipsis = true;

        grid.Controls.Add(cap, 0, 0);
        grid.SetColumnSpan(cap, 2);
        grid.Controls.Add(_port, 0, 1);
        grid.Controls.Add(_btnApplyPort, 1, 1);
        grid.Controls.Add(_portHint, 0, 2);
        grid.SetColumnSpan(_portHint, 2);
        card.Controls.Add(grid);
        return card;
    }

    private Card BuildNetworkCard()
    {
        var card = new Card { Height = 176 };
        TableLayoutPanel grid = Ui.Grid(4, 150, 0, 0, -1);

        Label title = Ui.MakeLabel("Red del equipo", Theme.CardTitle);
        title.Anchor = AnchorStyles.Left;
        _btnCheck.Anchor = AnchorStyles.Right;
        _btnCheck.Margin = Padding.Empty;
        Ui.AddRow(grid, 42, title, null, null, _btnCheck);
        grid.SetColumnSpan(title, 3);

        _profileBadge.Anchor = AnchorStyles.Left;
        _btnMakePrivate.Anchor = AnchorStyles.Left;
        _btnMakePrivate.Height = 32;
        Ui.AddRow(grid, 42, Ui.FieldLabel("Perfil de red"), _profileBadge, _btnMakePrivate, null);

        _ipModeBadge.Anchor = AnchorStyles.Left;
        _btnConfigureIp.Anchor = AnchorStyles.Left;
        _btnConfigureIp.Height = 32;
        Ui.AddRow(grid, 42, Ui.FieldLabel("Configuración IP"), _ipModeBadge, _btnConfigureIp, null);

        Ui.AddRow(grid, -1, _networkHint);
        grid.SetColumnSpan(_networkHint, 4);

        card.Controls.Add(grid);
        return card;
    }

    private Card BuildFolderCard()
    {
        var card = new Card { Height = 78 };
        TableLayoutPanel grid = Ui.Grid(3, 34, -1, 0);
        grid.RowCount = 1;
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Label icon = Ui.MakeLabel(Glyphs.FolderOpen, Theme.IconLarge, Theme.Accent);
        icon.Anchor = AnchorStyles.Left;

        var texts = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Surface };
        texts.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        texts.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        Label caption = Ui.MakeLabel("CARPETA RAÍZ GENERAL (ESCANEOS)", Theme.Caption, Theme.TextMuted);
        caption.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _folderPath.Dock = DockStyle.Fill;
        _folderPath.AutoEllipsis = true;
        _folderPath.TextAlign = ContentAlignment.TopLeft;
        texts.Controls.Add(caption, 0, 0);
        texts.Controls.Add(_folderPath, 0, 1);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            BackColor = Theme.Surface,
            Margin = Padding.Empty
        };
        _btnShortcut.Margin = new Padding(0, 0, 8, 0);
        _btnOpenFolder.Margin = Padding.Empty;
        actions.Controls.Add(_btnShortcut);
        actions.Controls.Add(_btnOpenFolder);

        grid.Controls.Add(icon, 0, 0);
        grid.Controls.Add(texts, 1, 0);
        grid.Controls.Add(actions, 2, 0);
        card.Controls.Add(grid);
        return card;
    }

    // ------------------------------------------------------------------ API pública

    public void SetServerState(bool running, int ftpPort, int? sftpPort, int connections, bool busy)
    {
        _dot.DotColor = running ? Theme.Success : Theme.Danger;
        _statusTitle.Text = running ? "Servidor en ejecución" : "Servidor detenido";
        string ports = sftpPort.HasValue ? $"FTP {ftpPort} · SFTP {sftpPort}" : $"Puerto FTP {ftpPort}";
        _statusSubtitle.Text = running
            ? $"{ports} · {connections} {(connections == 1 ? "conexión activa" : "conexiones activas")}"
            : "Los escáneres no pueden enviar archivos";
        _btnStart.Enabled = !running && !busy;
        _btnStop.Enabled = running && !busy;
        _btnRestart.Enabled = !busy;
    }

    public void ScrollToTop() => AutoScrollPosition = Point.Empty;

    // Evita que el panel salte hacia abajo al recibir el foco un botón.
    protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;

    public void SetAddress(string? ip, string hostname)
    {
        _ipValue.Text = string.IsNullOrEmpty(ip) ? "No disponible" : ip;
        _btnCopyIp.Enabled = !string.IsNullOrEmpty(ip);
        _hostValue.Text = hostname;
    }

    /// <summary>Puerto FTP guardado en la configuración (el editable vuelve a este valor).</summary>
    public void SetSavedPort(int port, int? sftpPort)
    {
        _savedPort = port;
        _port.Value = Math.Clamp(port, 1, 65535);
        _portHint.Text = sftpPort.HasValue ? $"SFTP: puerto {sftpPort}" : "El que se pone en el escáner";
        UpdatePortButton();
    }

    private void UpdatePortButton() => _btnApplyPort.Enabled = (int)_port.Value != _savedPort;

    public void SetReceived(int count, FtpFileReceivedInfo? last)
    {
        _receivedValue.Text = count.ToString();
        _receivedHint.Text = last == null
            ? "Ninguno todavía"
            : $"Último: {last.ReceivedAt:HH:mm:ss} · {Path.GetFileName(last.FilePath)}";
        if (last != null)
            _toolTip.SetToolTip(_receivedHint, last.FilePath);
    }

    public void SetRootFolder(string path)
    {
        _folderPath.Text = path;
        _toolTip.SetToolTip(_folderPath, path);
    }

    public void SetFirstRun(bool firstRun) => Ui.SetStackedVisible(_firstRunBanner, firstRun);

    public void SetNetworkChecking(bool checking)
    {
        _btnCheck.Enabled = !checking;
        if (checking)
        {
            _profileBadge.Set(BadgeKind.Neutral, "Comprobando…", null);
            _ipModeBadge.Set(BadgeKind.Neutral, "Comprobando…", null);
            _btnMakePrivate.Visible = false;
            _btnConfigureIp.Enabled = false;
            _networkHint.Text = "Consultando la configuración de red de Windows…";
            _networkHint.ForeColor = Theme.TextMuted;
        }
    }

    public void SetMakePrivateBusy(bool busy) => _btnMakePrivate.Enabled = !busy;

    public void SetNetwork(PreferredNetwork? network, NetworkCategory category, NetworkAdapterConfig? adapter)
    {
        if (network == null)
        {
            _profileBadge.Set(BadgeKind.Danger, "Sin red", Glyphs.Warning);
            _ipModeBadge.Set(BadgeKind.Neutral, "No disponible", null);
            _btnMakePrivate.Visible = false;
            _btnConfigureIp.Enabled = false;
            _networkHint.Text = "No se ha encontrado ningún adaptador de red IPv4 activo.";
            _networkHint.ForeColor = Theme.Danger;
            return;
        }

        switch (category)
        {
            case NetworkCategory.Public:
                _profileBadge.Set(BadgeKind.Danger, "Pública", Glyphs.Warning);
                _btnMakePrivate.Visible = true;
                _networkHint.Text = $"{network.InterfaceName}: la red pública puede bloquear el acceso desde el escáner.";
                _networkHint.ForeColor = Theme.Danger;
                break;
            case NetworkCategory.Private:
            case NetworkCategory.Domain:
                _profileBadge.Set(BadgeKind.Success, category == NetworkCategory.Private ? "Privada" : "Dominio", Glyphs.Check);
                _btnMakePrivate.Visible = false;
                _networkHint.Text = $"Adaptador: {network.InterfaceName}";
                _networkHint.ForeColor = Theme.TextMuted;
                break;
            default:
                _profileBadge.Set(BadgeKind.Warning, "No disponible", Glyphs.Warning);
                _btnMakePrivate.Visible = false;
                _networkHint.Text = $"{network.InterfaceName}: no se pudo determinar el perfil de red.";
                _networkHint.ForeColor = Theme.Warning;
                break;
        }

        if (adapter == null)
        {
            _ipModeBadge.Set(BadgeKind.Warning, "No disponible", Glyphs.Warning);
            _btnConfigureIp.Enabled = false;
            _btnConfigureIp.Text = "Configurar IP";
        }
        else if (adapter.DhcpEnabled)
        {
            _ipModeBadge.Set(BadgeKind.Warning, "DHCP", Glyphs.Warning);
            _btnConfigureIp.Enabled = true;
            _btnConfigureIp.Text = "Establecer IP fija";
            _toolTip.SetToolTip(_ipModeBadge, "Con DHCP la IP puede cambiar y el escáner dejaría de encontrar el equipo.");
        }
        else
        {
            _ipModeBadge.Set(BadgeKind.Success, "IP fija", Glyphs.Check);
            _btnConfigureIp.Enabled = true;
            _btnConfigureIp.Text = "Configurar IP";
            _toolTip.SetToolTip(_ipModeBadge, "La IP del equipo es fija.");
        }
    }

    private void CopyValue(string value, Control source)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "—" or "No disponible")
            return;
        bool ok = Ui.TryCopyToClipboard(value);
        _toolTip.Show(ok ? "Copiado" : "No se pudo copiar", source, 0, -28, 1500);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _toolTip.Dispose();
        base.Dispose(disposing);
    }
}
