// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Net.Sockets;
using CopyexFTPServer.Core;
using CopyexFTPServer.Ftp;
using CopyexFTPServer.Models;
using CopyexFTPServer.Services;
using CopyexFTPServer.Sftp;

namespace CopyexFTPServer.UI;

internal sealed class MainForm : Form
{
    private enum Page { Dashboard, Users, Settings, Log }

    private AppConfig _config;
    private readonly FtpServer _ftp = new();
    private readonly SftpServer _sftp = new();
    private readonly bool _startHidden;
    private bool _hiddenAtStartup;
    private bool _initialized;
    private bool _allowClose;
    private bool _shutdownDone;
    private bool _firstRunPending;
    private bool _trayHintShown;
    private bool _serverBusy;

    // Red
    private PreferredNetwork? _network;
    private NetworkAdapterConfig? _adapter;
    private NetworkCategory _category = NetworkCategory.Unknown;
    private bool _networkBusy;
    private string _lastNetworkLogKey = string.Empty;

    // Archivos recibidos
    private int _receivedToday;
    private DateTime _receivedDay = DateTime.Today;
    private readonly List<FtpFileReceivedInfo> _pendingNotifications = new();
    private readonly System.Windows.Forms.Timer _notifyTimer = new() { Interval = 1500 };
    private string? _balloonTarget;

    private readonly EventWaitHandle _showSignal;
    private RegisteredWaitHandle? _showSignalRegistration;

    // UI
    private readonly Panel _sidebar = new();
    private readonly Panel _content = new();
    private readonly Label _pageTitle = Ui.MakeLabel("", Theme.PageTitle);
    private readonly Label _pageSubtitle = Ui.MakeLabel("", Theme.Base, Theme.TextMuted);
    private readonly StatusDot _sideDot = new() { Size = new Size(14, 14) };
    private readonly Label _sideStatus = Ui.MakeLabel("Detenido", Theme.Small, Theme.SidebarText);
    private readonly Dictionary<Page, NavItem> _nav = new();
    private readonly Dictionary<Page, Control> _pages = new();
    private readonly DashboardPage _dashboard = new();
    private readonly UsersPage _usersPage = new();
    private readonly SettingsPage _settingsPage = new();
    private readonly LogPage _logPage = new();
    private Page _currentPage = Page.Dashboard;

    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly ToolStripMenuItem _trayShow = new("Abrir Copyex FTP Server");
    private readonly ToolStripMenuItem _trayToggle = new("Iniciar servidor");
    private readonly ToolStripMenuItem _trayOpenFolder = new("Abrir carpeta de escaneos");
    private readonly ToolStripMenuItem _trayExit = new("Salir");

    public MainForm(bool startedByWindows, bool forceShow, EventWaitHandle showSignal)
    {
        _showSignal = showSignal;
        _config = ConfigService.Load();
        _firstRunPending = _config.InitialSetupCompleted != true;

        // Primera ejecución: siempre visible para terminar la configuración.
        _startHidden = !_firstRunPending && !forceShow && (startedByWindows || _config.StartMinimized);

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = AppInfo.Name;
        Icon = AppResources.AppIcon;
        Font = Theme.Base;
        BackColor = Theme.Background;
        ClientSize = new Size(1080, 800);
        MinimumSize = new Size(940, 660);
        StartPosition = FormStartPosition.CenterScreen;
        BuildLayout();
        BuildTray();
        ResumeLayout(true);

        WireEvents();
        ShowPage(Page.Dashboard);
    }

    private bool IsRunning => _ftp.IsRunning;

    // ================================================================== construcción

    private void BuildLayout()
    {
        _sidebar.Dock = DockStyle.Left;
        _sidebar.Width = 230;
        _sidebar.BackColor = Theme.Sidebar;
        _sidebar.Padding = new Padding(0, 0, 0, 12);

        var brand = new Panel { Dock = DockStyle.Top, Height = 86, BackColor = Theme.Sidebar };
        var logo = new PictureBox
        {
            Image = AppResources.Logo,
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(44, 44),
            Location = new Point(18, 22),
            BackColor = Theme.Sidebar
        };
        var brandName = new Label
        {
            Text = AppInfo.Name,
            Font = new Font("Segoe UI Semibold", 11.5f),
            ForeColor = Color.White,
            BackColor = Theme.Sidebar,
            AutoSize = false,
            Location = new Point(70, 22),
            Size = new Size(156, 44),
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false
        };
        brand.Controls.AddRange(new Control[] { logo, brandName });

        var navPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Sidebar, Padding = new Padding(0, 10, 0, 0) };
        var items = new (Page Page, string Text, string Glyph)[]
        {
            (Page.Dashboard, "Panel", Glyphs.Home),
            (Page.Users, "Usuarios", Glyphs.People),
            (Page.Settings, "Configuración", Glyphs.Settings),
            (Page.Log, "Registro", Glyphs.Document)
        };
        for (int i = items.Length - 1; i >= 0; i--)
        {
            var item = new NavItem(items[i].Text, items[i].Glyph) { Dock = DockStyle.Top };
            Page page = items[i].Page;
            item.Click += (_, _) => ShowPage(page);
            _nav[page] = item;
            navPanel.Controls.Add(item);
        }

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 70, BackColor = Theme.Sidebar };
        _sideDot.Location = new Point(24, 15);
        _sideDot.BackColor = Theme.Sidebar;
        _sideStatus.Location = new Point(46, 13);
        var aboutLink = new LinkLabel
        {
            Text = "Acerca de · Licencia",
            AutoSize = true,
            Font = Theme.Small,
            LinkColor = Theme.SidebarMuted,
            ActiveLinkColor = Theme.SidebarText,
            VisitedLinkColor = Theme.SidebarMuted,
            LinkBehavior = LinkBehavior.HoverUnderline,
            BackColor = Theme.Sidebar,
            Location = new Point(22, 42)
        };
        aboutLink.LinkClicked += (_, _) => AboutInfo.Show(this);
        footer.Controls.AddRange(new Control[] { _sideDot, _sideStatus, aboutLink });

        _sidebar.Controls.Add(navPanel);
        _sidebar.Controls.Add(footer);
        _sidebar.Controls.Add(brand);

        _content.Dock = DockStyle.Fill;
        _content.BackColor = Theme.Background;
        _content.Padding = new Padding(28, 20, 28, 20);

        var header = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Theme.Background };
        _pageTitle.Location = new Point(0, 6);
        _pageSubtitle.Location = new Point(2, 44);
        // Logotipo corporativo, siempre visible arriba a la derecha.
        var brandLogo = new PictureBox
        {
            Image = AppResources.Brand,
            SizeMode = PictureBoxSizeMode.Zoom,
            Dock = DockStyle.Right,
            Width = 236,
            BackColor = Theme.Background,
            Padding = new Padding(0, 0, 0, 8)
        };
        header.Controls.AddRange(new Control[] { brandLogo, _pageTitle, _pageSubtitle });

        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };
        foreach ((Page page, Control control) in new (Page, Control)[]
                 {
                     (Page.Dashboard, _dashboard), (Page.Users, _usersPage), (Page.Settings, _settingsPage), (Page.Log, _logPage)
                 })
        {
            control.Dock = DockStyle.Fill;
            control.Visible = false;
            _pages[page] = control;
            host.Controls.Add(control);
        }

        _content.Controls.Add(host);
        _content.Controls.Add(header);

        Controls.Add(_content);
        Controls.Add(_sidebar);
    }

    private void BuildTray()
    {
        _trayShow.Font = new Font(_trayMenu.Font, FontStyle.Bold);
        var trayAbout = new ToolStripMenuItem("Acerca de...");
        trayAbout.Click += (_, _) => AboutInfo.Show(Visible ? this : null);
        _trayMenu.Items.AddRange(new ToolStripItem[] { _trayShow, _trayToggle, _trayOpenFolder, new ToolStripSeparator(), trayAbout, _trayExit });
        _tray.ContextMenuStrip = _trayMenu;
        _tray.Icon = AppResources.TrayIcon;
        _tray.Text = AppInfo.Name;
        _tray.Visible = true;
    }

    private void WireEvents()
    {
        foreach (TcpServerBase server in new TcpServerBase[] { _ftp, _sftp })
        {
            server.RunningChanged += _ => PostToUi(UpdateServerUi);
            server.ConnectionsChanged += _ => PostToUi(UpdateServerUi);
            server.FileReceived += info => PostToUi(() => OnFileReceived(info));
        }

        _dashboard.StartClicked += async (_, _) => await StartServerAsync(interactive: true);
        _dashboard.StopClicked += async (_, _) => await StopServerAsync();
        _dashboard.RestartAppClicked += (_, _) => RestartApplication();
        _dashboard.ApplyPortClicked += async (_, port) => await ApplyPortAsync(port);
        _dashboard.CheckNetworkClicked += async (_, _) => await RefreshNetworkAsync(forceLog: true);
        _dashboard.MakePrivateClicked += async (_, _) => await MakeNetworkPrivateAsync();
        _dashboard.ConfigureIpClicked += async (_, _) => await ConfigureIpAsync();
        _dashboard.OpenFolderClicked += (_, _) => OpenRootFolder();
        _dashboard.CreateShortcutClicked += (_, _) => CreateShortcutInteractive();
        _dashboard.GoToSettingsClicked += (_, _) => { ShowPage(Page.Settings); _settingsPage.FocusRootFolder(); };

        _usersPage.AddClicked += (_, _) => AddUser();
        _usersPage.EditClicked += (_, index) => EditUser(index);
        _usersPage.DeleteClicked += (_, index) => DeleteUser(index);

        _settingsPage.Changed += (_, _) => _settingsPage.SetDirty(_settingsPage.IsDirty(_config));
        _settingsPage.SaveClicked += async (_, _) => await SaveSettingsAsync();
        _settingsPage.DiscardClicked += (_, _) => _settingsPage.LoadFrom(_config);

        _trayShow.Click += (_, _) => ShowFromTray();
        _trayToggle.Click += async (_, _) =>
        {
            if (IsRunning) await StopServerAsync();
            else await StartServerAsync(interactive: true);
        };
        _trayOpenFolder.Click += (_, _) => OpenRootFolder();
        _trayExit.Click += (_, _) => ExitApplication();
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_balloonTarget != null) Ui.RevealFile(_balloonTarget);
            else ShowFromTray();
        };

        _notifyTimer.Tick += (_, _) => FlushNotifications();
    }

    // ================================================================== ciclo de vida

    protected override void SetVisibleCore(bool value)
    {
        // Arranque en bandeja sin parpadeo: se crea la ventana pero no se muestra.
        if (value && _startHidden && !_hiddenAtStartup && !IsHandleCreated)
        {
            _hiddenAtStartup = true;
            CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_initialized)
            return;
        _initialized = true;
        BeginInvoke(new Action(async () =>
        {
            try { await InitializeAsync(); }
            catch (Exception ex) { Logger.Error($"ERROR inicializando: {ex}"); }
        }));
    }

    private async Task InitializeAsync()
    {
        _showSignalRegistration = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => PostToUi(ShowFromTray), null, Timeout.Infinite, executeOnlyOnce: false);

        ActiveControl = _nav[Page.Dashboard];
        _settingsPage.LoadFrom(_config);
        _settingsPage.SetSftpFingerprint(SshHostKey.TryGetFingerprint(AppPaths.SftpHostKeyFile));
        RefreshUsers();
        _dashboard.SetRootFolder(_config.RootFolder);
        _dashboard.SetFirstRun(_firstRunPending);
        _dashboard.SetReceived(0, null);
        _dashboard.SetSavedPort(_config.Port, _config.SftpEnabled ? _config.SftpPort : null);
        _dashboard.SetAddress(null, NetworkInfoService.GetHostName());
        UpdateServerUi();

        if (!_firstRunPending)
            EnsureShortcut(_config.RootFolder);

        try { StartupService.Apply(_config.StartWithWindows); }
        catch (Exception ex) { Logger.Error($"INICIO ERROR: no se pudo configurar el arranque con Windows: {ex.Message}"); }

        Logger.Info($"DATOS: configuración en {AppPaths.DataDirectory}");

        if (_firstRunPending)
            Logger.Info("CONFIG: primera ejecución; el servidor queda detenido hasta pulsar «Iniciar»");
        else if (_config.AutoStartServer)
            await StartServerAsync(interactive: false);

#if UI_PREVIEW
        if (int.TryParse(Environment.GetEnvironmentVariable("CX_PAGE"), out int previewPage))
            ShowPage((Page)previewPage);
#endif

        // Única comprobación de red al abrir: sin temporizadores ni sondeos.
        await RefreshNetworkAsync(forceLog: true);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _logPage.SetActive(Visible && _currentPage == Page.Log);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && Visible)
            HideToTray();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // La X oculta en la bandeja; el apagado de Windows o «Salir» sí cierran.
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                ShowBalloon("Sigue en ejecución", "Copyex FTP Server continúa junto al reloj. Para cerrarlo usa «Salir» en su menú.", ToolTipIcon.Info, null);
            }
            return;
        }

        ShutdownNow();
        base.OnFormClosing(e);
    }

    private void ShutdownNow()
    {
        if (_shutdownDone)
            return;
        _shutdownDone = true;

        try { _showSignalRegistration?.Unregister(null); } catch { }
        _notifyTimer.Stop();
        _logPage.SetActive(false);

        try
        {
            // StopAsync no necesita el hilo de la ventana: se puede esperar sin bloqueo mutuo.
            Task stop = Task.WhenAll(_ftp.StopAsync(), _sftp.StopAsync());
            if (!stop.Wait(TimeSpan.FromSeconds(6)))
                Logger.Warning("SERVIDOR: la detención tardó demasiado; se cierra igualmente");
        }
        catch (Exception ex)
        {
            Logger.Error($"SERVIDOR ERROR al detener: {ex.Message}");
        }

        _sftp.HostKey?.Dispose();
        _tray.Visible = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _trayMenu.Dispose();
            _notifyTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        _tray.Visible = true;
    }

    private void ShowFromTray()
    {
        if (IsDisposed || _shutdownDone)
            return;
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void ExitApplication()
    {
        _allowClose = true;
        Close();
    }

    private void RestartApplication()
    {
        if (_settingsPage.IsDirty(_config))
        {
            DialogResult choice = MessageBox.Show(this,
                "Hay cambios de configuración sin guardar.\r\n\r\n¿Guardarlos antes de reiniciar?",
                "Reiniciar " + AppInfo.Name, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (choice == DialogResult.Cancel)
                return;
            if (choice == DialogResult.Yes && !SaveSettingsCore(out _))
                return;
        }

        Logger.Info("APLICACIÓN: reinicio solicitado por el usuario");
        Program.RequestRestart();
        ExitApplication();
    }

    private void PostToUi(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
            return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    // ================================================================== navegación

    private void ShowPage(Page page)
    {
        _currentPage = page;
        foreach ((Page p, NavItem item) in _nav)
            item.Selected = p == page;

        (string title, string subtitle) = page switch
        {
            Page.Users => ("Usuarios", "Cuentas que pueden conectarse al servidor y sus permisos"),
            Page.Settings => ("Configuración", "Puertos, carpeta raíz, SFTP, arranque y seguridad"),
            Page.Log => ("Registro", "Actividad del servidor en tiempo real (Server Trace)"),
            _ => ("Panel", "Estado del servidor y datos para configurar el escáner")
        };
        _pageTitle.Text = title;
        _pageSubtitle.Text = subtitle;

        foreach ((Page p, Control control) in _pages)
            control.Visible = p == page;
        _pages[page].BringToFront();

        _logPage.SetActive(Visible && page == Page.Log);
    }

    // ================================================================== servidor

    private async Task StartServerAsync(bool interactive)
    {
        if (IsRunning || _serverBusy)
            return;

        if (interactive && _settingsPage.IsDirty(_config))
        {
            DialogResult choice = MessageBox.Show(this,
                "Hay cambios de configuración sin guardar.\r\n\r\n¿Guardarlos antes de iniciar el servidor?",
                AppInfo.Name, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (choice == DialogResult.Cancel)
                return;
            if (choice == DialogResult.Yes && !SaveSettingsCore(out _))
                return;
        }

        if (interactive && !_config.Users.Any(u => u.Enabled))
        {
            if (!Ui.Confirm(this, "No hay usuarios habilitados. El servidor arrancará, pero nadie podrá iniciar sesión.\r\n\r\n¿Continuar?",
                    null, MessageBoxIcon.Warning))
                return;
        }

        SetBusy(true);
        try
        {
            FtpServerOptions options = FtpServerOptions.FromConfig(_config);
            try
            {
                await Task.Run(() => _ftp.Start(options, _config.Users));
            }
            catch (SocketException ex)
            {
                ReportStartError("FTP", _config.Port, ex, interactive);
                return;
            }

            if (_config.SftpEnabled)
            {
                try
                {
                    await Task.Run(() =>
                    {
                        _sftp.HostKey ??= SshHostKey.LoadOrCreate(AppPaths.SftpHostKeyFile);
                        _sftp.Start(options with { Port = _config.SftpPort }, _config.Users);
                    });
                    _settingsPage.SetSftpFingerprint(_sftp.HostKey?.Fingerprint);
                }
                catch (SocketException ex)
                {
                    ReportStartError("SFTP", _config.SftpPort, ex, interactive);
                }
            }

            if (_firstRunPending)
            {
                _firstRunPending = false;
                _config.InitialSetupCompleted = true;
                TrySaveConfig();
                EnsureShortcut(_config.RootFolder);
                _dashboard.SetFirstRun(false);
                Logger.Success("CONFIG: configuración inicial completada; el inicio automático se aplicará en próximos arranques");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"SERVIDOR ERROR al iniciar: {ex.Message}");
            ReportError($"No se pudo iniciar el servidor:\r\n{ex.Message}", interactive);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ReportStartError(string protocol, int port, SocketException ex, bool interactive)
    {
        string reason = ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied
            ? $"El puerto {port} está ocupado por otro programa (otro servidor {protocol}, IIS, OpenSSH o una versión anterior de Copyex FTP Server)."
            : ex.Message;
        Logger.Error($"{protocol} ERROR al iniciar: {reason}");
        ReportError($"No se pudo abrir el puerto {protocol} {port}.\r\n\r\n{reason}", interactive);
    }

    private void ReportError(string message, bool interactive)
    {
        if (interactive || Visible)
            Ui.Error(this, message);
        else
            ShowBalloon("Servidor detenido", message, ToolTipIcon.Error, null);
    }

    private async Task StopServerAsync()
    {
        if (_serverBusy)
            return;
        SetBusy(true);
        try
        {
            await Task.WhenAll(_ftp.StopAsync(), _sftp.StopAsync());
        }
        catch (Exception ex)
        {
            Logger.Error($"SERVIDOR ERROR al detener: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RestartServerAsync()
    {
        await StopServerAsync();
        await StartServerAsync(interactive: true);
    }

    private void SetBusy(bool busy)
    {
        _serverBusy = busy;
        UpdateServerUi();
    }

    private void UpdateServerUi()
    {
        if (IsDisposed)
            return;
        bool running = IsRunning;
        int port = running ? _ftp.Port : _config.Port;
        int? sftpPort = _sftp.IsRunning ? _sftp.Port : null;
        int connections = _ftp.ActiveConnections + _sftp.ActiveConnections;

        _dashboard.SetServerState(running, port, sftpPort, connections, _serverBusy);
        _dashboard.SetAddress(_network?.Address.ToString(), NetworkInfoService.GetHostName());
        _sideDot.DotColor = running ? Theme.Success : Theme.Danger;
        _sideStatus.Text = running ? $"En ejecución · puerto {port}" : "Servidor detenido";
        _trayToggle.Text = running ? "Detener servidor" : "Iniciar servidor";

        string trayText = running ? $"{AppInfo.Name} - activo (puerto {port})" : $"{AppInfo.Name} - detenido";
        _tray.Text = trayText.Length > 63 ? trayText[..63] : trayText;
    }

    /// <summary>Cambio rápido del puerto FTP desde el Panel.</summary>
    private async Task ApplyPortAsync(int port)
    {
        if (port == _config.Port)
            return;
        if (port >= _config.PassivePortMin && port <= _config.PassivePortMax)
        {
            Ui.Warn(this, $"El puerto {port} está dentro del rango de puertos de datos ({_config.PassivePortMin}-{_config.PassivePortMax}). Elige otro.");
            return;
        }
        if (_config.SftpEnabled && port == _config.SftpPort)
        {
            Ui.Warn(this, $"El puerto {port} ya lo usa el SFTP. Elige otro.");
            return;
        }
        int active = _ftp.ActiveConnections + _sftp.ActiveConnections;
        if (IsRunning && active > 0 &&
            !Ui.Confirm(this, $"Hay {active} conexiones activas. Para cambiar el puerto hay que reiniciar el servidor y se cortarán.\r\n\r\n¿Continuar?"))
        {
            _dashboard.SetSavedPort(_config.Port, _config.SftpEnabled ? _config.SftpPort : null);
            return;
        }

        int old = _config.Port;
        _config.Port = port;
        try
        {
            ConfigService.Save(_config);
        }
        catch (Exception ex)
        {
            _config.Port = old;
            Ui.Error(this, $"No se pudo guardar la configuración:\r\n{ex.Message}");
            return;
        }

        Logger.Success($"CONFIG: puerto FTP cambiado de {old} a {port}");
        _settingsPage.SyncPort(port);
        _settingsPage.SetDirty(_settingsPage.IsDirty(_config));
        _dashboard.SetSavedPort(port, _config.SftpEnabled ? _config.SftpPort : null);
        if (IsRunning)
            await RestartServerAsync();
        UpdateServerUi();
    }

    // ================================================================== archivos recibidos

    private void OnFileReceived(FtpFileReceivedInfo info)
    {
        if (_receivedDay != DateTime.Today)
        {
            _receivedDay = DateTime.Today;
            _receivedToday = 0;
        }
        _receivedToday++;
        _dashboard.SetReceived(_receivedToday, info);

        if (!_config.NotifyOnUpload)
            return;

        // Se agrupan las notificaciones: un escaneo de 30 páginas no genera 30 avisos.
        _pendingNotifications.Add(info);
        if (_pendingNotifications.Count > 200)
            _pendingNotifications.RemoveRange(0, _pendingNotifications.Count - 200);
        if (!_notifyTimer.Enabled)
            _notifyTimer.Start();
    }

    private void FlushNotifications()
    {
        _notifyTimer.Stop();
        if (_pendingNotifications.Count == 0)
            return;

        FtpFileReceivedInfo last = _pendingNotifications[^1];
        int count = _pendingNotifications.Count;
        _pendingNotifications.Clear();

        string text = count == 1
            ? $"{last.ReceivedAt:HH:mm:ss} - {Path.GetFileName(last.FilePath)}"
            : $"{count} archivos recibidos. Último: {last.ReceivedAt:HH:mm:ss} - {Path.GetFileName(last.FilePath)}";
        ShowBalloon(count == 1 ? "Escaneo recibido" : "Escaneos recibidos", text, ToolTipIcon.Info, last.FilePath);
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon, string? target)
    {
        if (_shutdownDone)
            return;
        _balloonTarget = target;
        _tray.Visible = true;
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text.Length > 250 ? text[..250] : text;
        _tray.BalloonTipIcon = icon;
        _tray.ShowBalloonTip(5000);
    }

    // ================================================================== usuarios

    private void RefreshUsers() => _usersPage.SetUsers(_config.Users, _config.RootFolder);

    private void AddUser()
    {
        var names = _config.Users.Select(u => u.Username).ToList();
        using var form = new UserForm(_config.RootFolder, names);
        if (form.ShowDialog(this) != DialogResult.OK)
            return;

        _config.Users.Add(form.ResultUser);
        if (!PersistUsers($"USUARIO: creado '{form.ResultUser.Username}'"))
            _config.Users.Remove(form.ResultUser);
        RefreshUsers();
        _usersPage.SelectRow(_config.Users.Count - 1);
    }

    private void EditUser(int index)
    {
        if (index < 0 || index >= _config.Users.Count)
            return;
        FtpUser original = _config.Users[index];
        var others = _config.Users.Where((_, i) => i != index).Select(u => u.Username).ToList();
        using var form = new UserForm(_config.RootFolder, others, original);
        if (form.ShowDialog(this) != DialogResult.OK)
            return;

        _config.Users[index] = form.ResultUser;
        if (!PersistUsers($"USUARIO: modificado '{form.ResultUser.Username}'"))
            _config.Users[index] = original;
        RefreshUsers();
        _usersPage.SelectRow(index);
    }

    private void DeleteUser(int index)
    {
        if (index < 0 || index >= _config.Users.Count)
            return;
        FtpUser user = _config.Users[index];
        string extra = _config.Users.Count(u => u.Enabled) == 1 && user.Enabled
            ? "\r\n\r\nEs el único usuario habilitado: nadie podrá conectarse."
            : string.Empty;
        if (!Ui.Confirm(this, $"¿Eliminar el usuario «{user.Username}»?{extra}\r\n\r\nLas sesiones abiertas con este usuario se cerrarán."))
            return;

        _config.Users.RemoveAt(index);
        if (!PersistUsers($"USUARIO: eliminado '{user.Username}'"))
            _config.Users.Insert(index, user);
        RefreshUsers();
    }

    /// <summary>Guarda usuarios en disco y los aplica en caliente a FTP y SFTP.</summary>
    private bool PersistUsers(string logMessage)
    {
        try
        {
            ConfigService.Save(_config);
        }
        catch (Exception ex)
        {
            Logger.Error($"CONFIG ERROR: {ex.Message}");
            Ui.Error(this, $"No se pudo guardar la configuración:\r\n{ex.Message}");
            return false;
        }
        _ftp.UpdateUsers(_config.Users);
        _sftp.UpdateUsers(_config.Users);
        Logger.Success(logMessage);
        return true;
    }

    // ================================================================== configuración

    private async Task SaveSettingsAsync()
    {
        if (!SaveSettingsCore(out bool serverAffected))
            return;

        if (serverAffected && IsRunning)
        {
            if (Ui.Confirm(this, "Algunos cambios afectan al servidor en marcha.\r\n\r\n¿Reiniciar el servidor ahora para aplicarlos? (Se cortarán las transferencias en curso.)"))
                await RestartServerAsync();
            else
                Logger.Info("CONFIG: los cambios del servidor se aplicarán la próxima vez que se inicie");
        }
    }

    private bool SaveSettingsCore(out bool serverAffected)
    {
        serverAffected = false;
        AppConfig candidate = _config.CloneSettings();
        string? error = _settingsPage.ApplyTo(candidate);
        if (error != null)
        {
            Ui.Warn(this, error);
            ShowPage(Page.Settings);
            return false;
        }

        try
        {
            Directory.CreateDirectory(candidate.RootFolder);
        }
        catch (Exception ex)
        {
            Ui.Error(this, $"No se puede crear o usar la carpeta raíz:\r\n{ex.Message}");
            return false;
        }

        if (candidate.SftpEnabled)
        {
            try
            {
                UseWaitCursor = true;
                using SshHostKey key = SshHostKey.LoadOrCreate(AppPaths.SftpHostKeyFile);
                _settingsPage.SetSftpFingerprint(key.Fingerprint);
            }
            catch (Exception ex)
            {
                Ui.Error(this, $"No se pudo crear la clave del servidor SFTP:\r\n{ex.Message}");
                return false;
            }
            finally
            {
                UseWaitCursor = false;
            }
        }

        serverAffected =
            candidate.Port != _config.Port ||
            !ConfigService.PathsEquivalent(candidate.RootFolder, _config.RootFolder) ||
            candidate.PassivePortMin != _config.PassivePortMin ||
            candidate.PassivePortMax != _config.PassivePortMax ||
            candidate.AllowOnlyLocalNetworks != _config.AllowOnlyLocalNetworks ||
            candidate.MaxConnections != _config.MaxConnections ||
            candidate.MaxConnectionsPerIp != _config.MaxConnectionsPerIp ||
            candidate.IdleTimeoutSeconds != _config.IdleTimeoutSeconds ||
            candidate.DetailedTrace != _config.DetailedTrace ||
            candidate.SftpEnabled != _config.SftpEnabled ||
            (candidate.SftpEnabled && candidate.SftpPort != _config.SftpPort);

        try
        {
            ConfigService.Save(candidate);
        }
        catch (Exception ex)
        {
            Logger.Error($"CONFIG ERROR: {ex.Message}");
            Ui.Error(this, $"No se pudo guardar la configuración:\r\n{ex.Message}");
            return false;
        }

        // Los usuarios se editan aparte: se conserva la lista actual.
        candidate.Users = _config.Users;
        _config = candidate;

        try { StartupService.Apply(_config.StartWithWindows); }
        catch (Exception ex) { Logger.Error($"INICIO ERROR: {ex.Message}"); }

        EnsureShortcut(_config.RootFolder);

        _settingsPage.LoadFrom(_config);
        _dashboard.SetRootFolder(_config.RootFolder);
        _dashboard.SetSavedPort(_config.Port, _config.SftpEnabled ? _config.SftpPort : null);
        RefreshUsers();
        UpdateServerUi();
        Logger.Success("CONFIG: configuración guardada");
        return true;
    }

    private void TrySaveConfig()
    {
        try { ConfigService.Save(_config); }
        catch (Exception ex) { Logger.Error($"CONFIG ERROR: {ex.Message}"); }
    }

    private static bool EnsureShortcut(string root)
    {
        try
        {
            ShortcutService.EnsureDesktopShortcut(root);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"CARPETA ERROR: no se pudo crear/actualizar el acceso directo ESCANER: {ex.Message}");
            return false;
        }
    }

    private void CreateShortcutInteractive()
    {
        if (EnsureShortcut(_config.RootFolder))
        {
            Logger.Success($"CARPETA: acceso directo ESCANER del escritorio -> {_config.RootFolder}");
            Ui.Info(this, $"Acceso directo «ESCANER» creado en el escritorio.\r\n\r\nAbre la carpeta:\r\n{_config.RootFolder}");
        }
        else
        {
            Ui.Error(this, "No se pudo crear el acceso directo. Revisa el Registro.");
        }
    }

    private void OpenRootFolder()
    {
        try
        {
            Directory.CreateDirectory(_config.RootFolder);
            Ui.OpenFolder(_config.RootFolder);
        }
        catch (Exception ex)
        {
            Ui.Error(this, ex.Message);
        }
    }

    // ================================================================== red

    private async Task RefreshNetworkAsync(bool forceLog)
    {
        if (_networkBusy)
            return;
        _networkBusy = true;
        _dashboard.SetNetworkChecking(true);
        try
        {
            PreferredNetwork? network = await Task.Run(NetworkInfoService.GetPreferredNetwork);
            NetworkAdapterConfig? adapter = null;
            NetworkCategory category = NetworkCategory.Unknown;
            if (network != null)
            {
                adapter = await Task.Run(() => NetworkIpService.GetConfiguration(network.InterfaceIndex));
                category = await NetworkProfileService.GetCategoryAsync(network.InterfaceIndex);
            }
            if (IsDisposed)
                return;

            _network = network;
            _adapter = adapter;
            _category = category;
            _dashboard.SetNetwork(network, category, adapter);
            UpdateServerUi();
            LogNetworkState(forceLog);
        }
        catch (Exception ex)
        {
            Logger.Error($"RED ERROR: {ex.Message}");
            if (!IsDisposed)
                _dashboard.SetNetwork(null, NetworkCategory.Unknown, null);
        }
        finally
        {
            _networkBusy = false;
            if (!IsDisposed)
                _dashboard.SetNetworkChecking(false);
        }
    }

    private void LogNetworkState(bool force)
    {
        if (_network == null)
        {
            if (force || _lastNetworkLogKey != "none")
                Logger.Error("RED ERROR: no se encontró un adaptador de red IPv4 activo");
            _lastNetworkLogKey = "none";
            return;
        }

        string key = $"{_network.InterfaceIndex}|{_network.Address}|{_category}|{_adapter?.DhcpEnabled}";
        if (!force && key == _lastNetworkLogKey)
            return;
        _lastNetworkLogKey = key;

        string mode = _adapter == null ? "modo IP desconocido" : _adapter.DhcpEnabled ? "DHCP" : "IP fija";
        string message = $"RED: {_network.InterfaceName} ({_network.Address}) perfil {NetworkProfileService.ToDisplayName(_category)}, {mode}";
        if (_category == NetworkCategory.Public)
            Logger.Warning(message + " - la red pública puede bloquear el acceso");
        else if (_category == NetworkCategory.Unknown)
            Logger.Warning(message);
        else
            Logger.Success(message);
    }

    private async Task MakeNetworkPrivateAsync()
    {
        if (_network == null)
        {
            Ui.Warn(this, "No se ha encontrado un adaptador de red IPv4 activo.");
            return;
        }

        _dashboard.SetMakePrivateBusy(true);
        try
        {
            bool ok = await NetworkProfileService.SetPrivateAsync(_network.InterfaceIndex);
            await RefreshNetworkAsync(forceLog: true);
            if (!ok && _category == NetworkCategory.Public)
                Ui.Warn(this, "No se pudo cambiar la red a Privada. Comprueba el aviso de Control de cuentas de usuario (UAC).");
        }
        finally
        {
            if (!IsDisposed)
                _dashboard.SetMakePrivateBusy(false);
        }
    }

    private async Task ConfigureIpAsync()
    {
        if (_network == null)
        {
            Ui.Warn(this, "No se ha encontrado un adaptador IPv4 activo.");
            return;
        }
        NetworkAdapterConfig? adapter = NetworkIpService.GetConfiguration(_network.InterfaceIndex);
        if (adapter == null)
        {
            Ui.Warn(this, "No se ha podido leer la configuración IPv4 del adaptador activo.");
            return;
        }

        using (var form = new StaticIpForm(adapter))
            form.ShowDialog(this);

        await RefreshNetworkAsync(forceLog: true);
    }
}
