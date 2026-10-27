// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using DynamicIsland.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DynamicIsland;

public partial class App : Application
{
    const string MutexName = "DynamicIsland.SingleInstance.v1";
    const string RestartArg = "--restarted";
    const string AutoStartArg = "--autostart";
    const string TaskName = "DynamicIsland";
    static readonly object StartupLock = new();

    static Mutex? _mutex;
    Forms.NotifyIcon? _tray;
    MainWindow? _island;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is Exception err) Log.Error("Fatal", err);
        };

        // Inicio con Windows por el registro: si la tarea programada (que abre con permisos de administrador)
        // también existe, se le da ventaja para que Windows no muestre el aviso de permisos al iniciar sesión.
        if (e.Args.Contains(AutoStartArg) && Settings.Current.RunAsAdmin && !Native.IsElevated())
            Thread.Sleep(15000);

        // Una sola instancia. Tras un reinicio se espera a que la anterior termine.
        // Si la otra instancia corre como administrador, Windows niega el acceso al mutex: ya hay una isla abierta.
        try { _mutex = new Mutex(false, MutexName); }
        catch (UnauthorizedAccessException)
        {
            Shutdown();
            return;
        }
        bool owned;
        try { owned = _mutex.WaitOne(e.Args.Contains(RestartArg) ? 5000 : 0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            Shutdown();
            return;
        }

        // Elevación automática si el usuario la activó en Ajustes.
        if (Settings.Current.RunAsAdmin && !Native.IsElevated() && !e.Args.Contains(RestartArg))
        {
            if (StartSelf(elevated: true)) return;
        }

        _island = new MainWindow();
        _island.Show();
        SetupTray();

        // Se revisa el inicio con Windows en cada arranque (por si el .exe cambió de carpeta o la tarea se borró).
        System.Threading.Tasks.Task.Run(ApplyStartup);
    }

    void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();

        var toggle = new Forms.ToolStripMenuItem("Ocultar isla");
        toggle.Click += (_, _) =>
        {
            bool hidden = _island!.ToggleUserHidden();
            toggle.Text = hidden ? "Mostrar isla" : "Ocultar isla";
        };
        menu.Items.Add(toggle);

        var startup = new Forms.ToolStripMenuItem("Iniciar con Windows") { CheckOnClick = true, Checked = Settings.Current.StartWithWindows };
        startup.CheckedChanged += (_, _) =>
        {
            Settings.Current.StartWithWindows = startup.Checked;
            Settings.Current.Save();
            ApplyStartup();
        };
        menu.Items.Add(startup);

        if (!Native.IsElevated())
        {
            var admin = new Forms.ToolStripMenuItem("Reiniciar como administrador");
            admin.Click += (_, _) => RestartElevated();
            menu.Items.Add(admin);
        }

        menu.Items.Add(new Forms.ToolStripSeparator());
        var exit = new Forms.ToolStripMenuItem("Salir");
        exit.Click += (_, _) => Shutdown();
        menu.Items.Add(exit);

        _tray = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Dynamic Island",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => toggle.PerformClick();
    }

    static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var res = GetResourceStream(new Uri("pack://application:,,,/Assets/logo.ico", UriKind.Absolute));
            if (res != null)
            {
                using var stream = res.Stream;
                return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex) { Log.Error("Tray icon", ex); }
        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>Reinicia la app con permisos de administrador.</summary>
    public static void RestartElevated()
    {
        if (Current is App app) app.StartSelf(elevated: true);
    }

    bool StartSelf(bool elevated)
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return false;
        try
        {
            // Se libera la instancia única para que el nuevo proceso pueda tomarla.
            try { _mutex?.ReleaseMutex(); } catch { }
            Process.Start(new ProcessStartInfo(exe, RestartArg) { UseShellExecute = true, Verb = elevated ? "runas" : "" });
            Shutdown();
            return true;
        }
        catch (Win32Exception)
        {
            // El usuario canceló el aviso de Windows: se sigue sin elevar.
            try { _mutex?.WaitOne(0); } catch { }
            return false;
        }
    }

    /// <summary>
    /// Inicio con Windows. Siempre se usa la clave Run del usuario; en modo administrador además se crea
    /// una tarea programada que abre la isla con permisos sin preguntar. Si ambas existen, la de la
    /// clave Run espera y se cierra sola al ver que la tarea ya abrió la isla.
    /// </summary>
    public static void ApplyStartup()
    {
        lock (StartupLock)
        {
            var s = Settings.Current;
            string? exe = Environment.ProcessPath;
            if (exe == null) return;
            bool elevated = Native.IsElevated();
            bool wantTask = s.StartWithWindows && s.RunAsAdmin;

            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (s.StartWithWindows) k.SetValue("DynamicIsland", $"\"{exe}\" {AutoStartArg}");
                else k.DeleteValue("DynamicIsland", false);
            }
            catch (Exception ex) { Log.Error("Startup registry", ex); }

            // La tarea con permisos de administrador solo se puede crear o borrar desde la app elevada.
            if (!elevated) return;
            if (wantTask) CreateStartupTask(exe);
            else RunSchtasks($"/Delete /F /TN {TaskName}", logErrors: false);
        }
    }

    /// <summary>
    /// Tarea al iniciar sesión con permisos elevados. Se define en XML para quitar las condiciones que
    /// Windows pone por defecto: no iniciar con batería y detener la tarea a los 3 días.
    /// </summary>
    static void CreateStartupTask(string exe)
    {
        string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        string Esc(string v) => System.Security.SecurityElement.Escape(v) ?? v;
        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
            "  <RegistrationInfo><Description>Dynamic Island</Description></RegistrationInfo>\n" +
            "  <Triggers><LogonTrigger><Enabled>true</Enabled>" +
            $"<UserId>{Esc(user)}</UserId><Delay>PT3S</Delay></LogonTrigger></Triggers>\n" +
            "  <Principals><Principal id=\"Author\">" +
            $"<UserId>{Esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel>" +
            "</Principal></Principals>\n" +
            "  <Settings>" +
            "<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
            "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
            "<AllowHardTerminate>true</AllowHardTerminate>" +
            "<StartWhenAvailable>false</StartWhenAvailable>" +
            "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>" +
            "<IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>" +
            "<AllowStartOnDemand>true</AllowStartOnDemand>" +
            "<Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun>" +
            "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>4</Priority>" +
            "</Settings>\n" +
            "  <Actions Context=\"Author\"><Exec>" +
            $"<Command>{Esc(exe)}</Command><WorkingDirectory>{Esc(System.IO.Path.GetDirectoryName(exe) ?? "")}</WorkingDirectory>" +
            "</Exec></Actions>\n" +
            "</Task>\n";
        string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DynamicIsland-startup.xml");
        try
        {
            System.IO.File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            RunSchtasks($"/Create /F /TN {TaskName} /XML \"{file}\"", logErrors: true);
        }
        catch (Exception ex) { Log.Error("Startup task", ex); }
        finally
        {
            try { System.IO.File.Delete(file); } catch { }
        }
    }

    static void RunSchtasks(string args, bool logErrors)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return;
            var err = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(8000)) return;
            if (p.ExitCode != 0 && logErrors)
                Log.Error("Startup task", new InvalidOperationException($"schtasks {p.ExitCode}: {err.Result.Trim()}"));
        }
        catch (Exception ex) { if (logErrors) Log.Error("Startup task", ex); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _island?.StopModules();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
