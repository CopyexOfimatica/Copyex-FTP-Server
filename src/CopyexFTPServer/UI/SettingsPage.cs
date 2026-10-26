// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using CopyexFTPServer.Core;
using CopyexFTPServer.Models;

namespace CopyexFTPServer.UI;

/// <summary>Página "Configuración". Los cambios se aplican al pulsar Guardar.</summary>
internal sealed class SettingsPage : UserControl
{
    private readonly NumericUpDown _port = Num(1, 65535, 100);
    private readonly TextBox _root = new() { Font = Theme.Base, Dock = DockStyle.Fill, Margin = new Padding(0, 9, 8, 0) };
    private readonly ModernButton _btnBrowse = ModernButton.Create("Examinar…", ButtonKind.Secondary, Glyphs.FolderOpen);
    private readonly NumericUpDown _passiveMin = Num(1024, 65535, 90);
    private readonly NumericUpDown _passiveMax = Num(1024, 65535, 90);

    private readonly CheckBox _startWindows = Check("Arrancar Copyex FTP Server al iniciar sesión en Windows");
    private readonly CheckBox _autoStart = Check("Iniciar el servidor FTP automáticamente al abrir la aplicación");
    private readonly CheckBox _startMinimized = Check("Arrancar minimizado en la bandeja del sistema (junto al reloj)");

    private readonly CheckBox _onlyLocal = Check("Aceptar solo conexiones de la red local");
    private readonly NumericUpDown _maxConnections = Num(1, 1000, 90);
    private readonly NumericUpDown _maxPerIp = Num(1, 1000, 90);
    private readonly NumericUpDown _idle = Num(30, 7200, 90);

    private readonly CheckBox _sftpEnabled = Check("Activar SFTP (transferencia cifrada por SSH) además de FTP");
    private readonly NumericUpDown _sftpPort = Num(1, 65535, 100);
    private readonly TextBox _sftpFingerprint = new()
    {
        ReadOnly = true,
        Font = Theme.Mono,
        Dock = DockStyle.Fill,
        BackColor = Theme.SurfaceAlt,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 8, 0, 0)
    };

    private readonly CheckBox _notify = Check("Mostrar una notificación de Windows al recibir cada escaneo");
    private readonly CheckBox _detailedTrace = Check("Registro detallado (cada comando en el registro)");

    private readonly ModernButton _btnSave = ModernButton.Create("Guardar cambios", ButtonKind.Primary, Glyphs.Save);
    private readonly ModernButton _btnDiscard = ModernButton.Create("Descartar", ButtonKind.Secondary, Glyphs.Undo);
    private readonly Label _dirtyLabel = Ui.MakeLabel("", Theme.Small, Theme.TextMuted);

    private bool _loading;

    public event EventHandler? Changed;
    public event EventHandler? SaveClicked;
    public event EventHandler? DiscardClicked;

    public SettingsPage()
    {
        BackColor = Theme.Background;
        Font = Theme.Base;

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Background, Padding = new Padding(0, 0, 8, 0) };
        var body = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Background };

        Ui.StackTop(body, 14, BuildServerCard(), BuildSftpCard(), BuildStartupCard(), BuildSecurityCard(), BuildLogCard());
        scroll.Controls.Add(body);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            WrapContents = false,
            BackColor = Theme.Background,
            Padding = new Padding(0, 14, 0, 0)
        };
        _dirtyLabel.Margin = new Padding(6, 10, 0, 0);
        bottom.Controls.AddRange(new Control[] { _btnSave, _btnDiscard, _dirtyLabel });

        Controls.Add(scroll);
        Controls.Add(bottom);

        _btnBrowse.Click += (_, _) => BrowseRoot();
        _btnSave.Click += (_, _) => SaveClicked?.Invoke(this, EventArgs.Empty);
        _btnDiscard.Click += (_, _) => DiscardClicked?.Invoke(this, EventArgs.Empty);

        foreach (NumericUpDown n in new[] { _port, _passiveMin, _passiveMax, _maxConnections, _maxPerIp, _idle, _sftpPort })
            n.ValueChanged += (_, _) => RaiseChanged();
        foreach (CheckBox c in new[] { _startWindows, _autoStart, _startMinimized, _onlyLocal, _notify, _detailedTrace, _sftpEnabled })
            c.CheckedChanged += (_, _) => RaiseChanged();
        _sftpEnabled.CheckedChanged += (_, _) => _sftpPort.Enabled = _sftpEnabled.Checked;
        _root.TextChanged += (_, _) => RaiseChanged();

        SetDirty(false);
    }

    private Card BuildServerCard()
    {
        var card = new Card { Height = 222 };
        TableLayoutPanel grid = Ui.Grid(3, 210, -1, 0);
        Ui.AddRow(grid, 38, Ui.MakeLabel("Servidor FTP", Theme.CardTitle));
        Ui.AddRow(grid, 44, Ui.FieldLabel("Puerto FTP"), Wrap(_port), null);
        Ui.AddRow(grid, 44, Ui.FieldLabel("Carpeta raíz general"), _root, _btnBrowse);
        _btnBrowse.Anchor = AnchorStyles.Left;
        _btnBrowse.Height = 32;
        _btnBrowse.Margin = new Padding(0, 6, 0, 0);

        var passive = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 0) };
        Label dash = Ui.MakeLabel("–");
        dash.Margin = new Padding(6, 6, 6, 0);
        passive.Controls.AddRange(new Control[] { _passiveMin, dash, _passiveMax });
        Ui.AddRow(grid, 44, Ui.FieldLabel("Puertos de datos (pasivo)"), passive, null);
        Ui.AddRow(grid, 30, null, Ui.Hint("Ábrelos en el firewall solo si Windows bloquea el programa."));
        grid.SetColumnSpan(grid.GetControlFromPosition(1, grid.RowCount - 1)!, 2);
        card.Controls.Add(grid);
        return card;
    }

    private Card BuildSftpCard()
    {
        var card = new Card { Height = 222 };
        TableLayoutPanel grid = Ui.Grid(2, 210, -1);
        Ui.AddRow(grid, 38, Ui.MakeLabel("SFTP (opcional)", Theme.CardTitle));
        Ui.AddRow(grid, 32, _sftpEnabled);
        grid.SetColumnSpan(_sftpEnabled, 2);
        Ui.AddRow(grid, 44, Ui.FieldLabel("Puerto SFTP"), Wrap(_sftpPort));
        Ui.AddRow(grid, 40, Ui.FieldLabel("Huella de la clave"), _sftpFingerprint);
        Label hint = Ui.Hint("Usa los mismos usuarios y permisos. Si el escáner pide confirmar la huella, debe coincidir con esta.");
        Ui.AddRow(grid, 32, hint);
        grid.SetColumnSpan(hint, 2);
        card.Controls.Add(grid);
        return card;
    }

    private Card BuildStartupCard()
    {
        var card = new Card { Height = 154 };
        TableLayoutPanel grid = Ui.Grid(1, -1);
        Ui.AddRow(grid, 38, Ui.MakeLabel("Inicio", Theme.CardTitle));
        Ui.AddRow(grid, 32, _startWindows);
        Ui.AddRow(grid, 32, _autoStart);
        Ui.AddRow(grid, 32, _startMinimized);
        card.Controls.Add(grid);
        return card;
    }

    private Card BuildSecurityCard()
    {
        var card = new Card { Height = 230 };
        TableLayoutPanel grid = Ui.Grid(3, 330, 0, -1);
        Ui.AddRow(grid, 38, Ui.MakeLabel("Seguridad", Theme.CardTitle));
        Ui.AddRow(grid, 32, _onlyLocal);
        grid.SetColumnSpan(_onlyLocal, 3);
        Label localHint = Ui.Hint("Rechaza conexiones desde Internet. Se consideran locales las IPs privadas y las subredes de este equipo.");
        Ui.AddRow(grid, 26, localHint);
        grid.SetColumnSpan(localHint, 3);
        Ui.AddRow(grid, 38, Ui.FieldLabel("Conexiones simultáneas máximas"), Wrap(_maxConnections), null);
        Ui.AddRow(grid, 38, Ui.FieldLabel("Conexiones máximas desde una misma IP"), Wrap(_maxPerIp), null);
        Ui.AddRow(grid, 38, Ui.FieldLabel("Cerrar sesiones inactivas tras (segundos)"), Wrap(_idle), null);
        card.Controls.Add(grid);
        return card;
    }

    private Card BuildLogCard()
    {
        var card = new Card { Height = 160 };
        TableLayoutPanel grid = Ui.Grid(1, -1);
        Ui.AddRow(grid, 38, Ui.MakeLabel("Avisos y registro", Theme.CardTitle));
        Ui.AddRow(grid, 32, _notify);
        Ui.AddRow(grid, 32, _detailedTrace);
        Ui.AddRow(grid, 28, Ui.Hint("El registro se guarda solo en memoria y se borra al cerrar la aplicación."));
        card.Controls.Add(grid);
        return card;
    }

    // ------------------------------------------------------------------ API

    public void LoadFrom(AppConfig config)
    {
        _loading = true;
        try
        {
            _port.Value = Math.Clamp(config.Port, 1, 65535);
            _root.Text = config.RootFolder;
            _passiveMin.Value = Math.Clamp(config.PassivePortMin, 1024, 65535);
            _passiveMax.Value = Math.Clamp(config.PassivePortMax, 1024, 65535);
            _startWindows.Checked = config.StartWithWindows;
            _autoStart.Checked = config.AutoStartServer;
            _startMinimized.Checked = config.StartMinimized;
            _onlyLocal.Checked = config.AllowOnlyLocalNetworks;
            _maxConnections.Value = Math.Clamp(config.MaxConnections, 1, 1000);
            _maxPerIp.Value = Math.Clamp(config.MaxConnectionsPerIp, 1, 1000);
            _idle.Value = Math.Clamp(config.IdleTimeoutSeconds, 30, 7200);
            _notify.Checked = config.NotifyOnUpload;
            _detailedTrace.Checked = config.DetailedTrace;
            _sftpEnabled.Checked = config.SftpEnabled;
            _sftpPort.Value = Math.Clamp(config.SftpPort, 1, 65535);
            _sftpPort.Enabled = config.SftpEnabled;
        }
        finally
        {
            _loading = false;
        }
        SetDirty(false);
    }

    public bool IsDirty(AppConfig c) =>
        (int)_port.Value != c.Port ||
        !ConfigService.PathsEquivalent(_root.Text, c.RootFolder) ||
        (int)_passiveMin.Value != c.PassivePortMin ||
        (int)_passiveMax.Value != c.PassivePortMax ||
        _startWindows.Checked != c.StartWithWindows ||
        _autoStart.Checked != c.AutoStartServer ||
        _startMinimized.Checked != c.StartMinimized ||
        _onlyLocal.Checked != c.AllowOnlyLocalNetworks ||
        (int)_maxConnections.Value != c.MaxConnections ||
        (int)_maxPerIp.Value != c.MaxConnectionsPerIp ||
        (int)_idle.Value != c.IdleTimeoutSeconds ||
        _notify.Checked != c.NotifyOnUpload ||
        _detailedTrace.Checked != c.DetailedTrace ||
        _sftpEnabled.Checked != c.SftpEnabled ||
        (int)_sftpPort.Value != c.SftpPort;

    /// <summary>Valida y copia los valores en <paramref name="target"/>. Devuelve el error o null.</summary>
    public string? ApplyTo(AppConfig target)
    {
        string? rootError = RootFolderPolicy.Validate(_root.Text, out string root);
        if (rootError != null)
            return "Carpeta raíz general: " + rootError;

        int port = (int)_port.Value;
        int pMin = (int)_passiveMin.Value;
        int pMax = (int)_passiveMax.Value;
        if (pMax < pMin)
            return "El puerto pasivo final debe ser mayor o igual que el inicial.";
        if (port >= pMin && port <= pMax)
            return "El puerto FTP no puede estar dentro del rango de puertos de datos.";
        int sftpPort = (int)_sftpPort.Value;
        if (_sftpEnabled.Checked && sftpPort == port)
            return "El puerto SFTP no puede ser el mismo que el puerto FTP.";
        if (_sftpEnabled.Checked && sftpPort >= pMin && sftpPort <= pMax)
            return "El puerto SFTP no puede estar dentro del rango de puertos de datos.";

        target.Port = port;
        target.RootFolder = root;
        target.PassivePortMin = pMin;
        target.PassivePortMax = pMax;
        target.StartWithWindows = _startWindows.Checked;
        target.AutoStartServer = _autoStart.Checked;
        target.StartMinimized = _startMinimized.Checked;
        target.AllowOnlyLocalNetworks = _onlyLocal.Checked;
        target.MaxConnections = (int)_maxConnections.Value;
        target.MaxConnectionsPerIp = (int)_maxPerIp.Value;
        target.IdleTimeoutSeconds = (int)_idle.Value;
        target.NotifyOnUpload = _notify.Checked;
        target.DetailedTrace = _detailedTrace.Checked;
        target.SftpEnabled = _sftpEnabled.Checked;
        if (sftpPort != port)
            target.SftpPort = sftpPort;
        return null;
    }

    /// <summary>Actualiza solo el puerto FTP (cambiado desde el Panel) sin tocar otros cambios en curso.</summary>
    public void SyncPort(int port)
    {
        _loading = true;
        try { _port.Value = Math.Clamp(port, 1, 65535); }
        finally { _loading = false; }
    }

    public void SetSftpFingerprint(string? fingerprint) =>
        _sftpFingerprint.Text = fingerprint ?? "Se generará al activar SFTP y guardar";

    public void SetDirty(bool dirty)
    {
        _btnSave.Enabled = dirty;
        _btnDiscard.Enabled = dirty;
        _dirtyLabel.Text = dirty ? "Hay cambios sin guardar" : "Configuración guardada";
        _dirtyLabel.ForeColor = dirty ? Theme.Warning : Theme.TextMuted;
    }

    public void FocusRootFolder()
    {
        _root.Focus();
        _root.SelectAll();
    }

    private void RaiseChanged()
    {
        if (!_loading)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private void BrowseRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Selecciona la carpeta que será la raíz general del FTP",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(_root.Text) ? _root.Text : string.Empty
        };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            _root.Text = dialog.SelectedPath;
    }

    // ------------------------------------------------------------------ helpers

    private static NumericUpDown Num(int min, int max, int width) => new()
    {
        Minimum = min,
        Maximum = max,
        Width = width,
        Font = Theme.Base,
        TextAlign = HorizontalAlignment.Right,
        Margin = new Padding(0, 6, 0, 0)
    };

    private static CheckBox Check(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Theme.Base,
        ForeColor = Theme.Text,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 6, 0, 0),
        UseMnemonic = false
    };

    private static Control Wrap(Control c)
    {
        c.Anchor = AnchorStyles.Left;
        return c;
    }
}
