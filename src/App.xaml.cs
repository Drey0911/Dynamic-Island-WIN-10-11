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

        // Una sola instancia. Tras un reinicio se espera a que la anterior termine.
        _mutex = new Mutex(false, MutexName);
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
    /// Inicio con Windows. Con permisos de administrador se usa el Programador de tareas
    /// (el registro Run no puede elevar); si no, la clave Run del usuario.
    /// </summary>
    public static void ApplyStartup()
    {
        var s = Settings.Current;
        string? exe = Environment.ProcessPath;
        if (exe == null) return;
        bool useTask = s.StartWithWindows && s.RunAsAdmin && Native.IsElevated();
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (s.StartWithWindows && !useTask) k.SetValue("DynamicIsland", $"\"{exe}\"");
            else k.DeleteValue("DynamicIsland", false);
        }
        catch (Exception ex) { Log.Error("Startup registry", ex); }

        if (!Native.IsElevated()) return;
        string args = useTask
            ? $"/Create /F /TN DynamicIsland /SC ONLOGON /RL HIGHEST /TR \"\\\"{exe}\\\"\""
            : "/Delete /F /TN DynamicIsland";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(5000);
        }
        catch (Exception ex) { Log.Error("Startup task", ex); }
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
