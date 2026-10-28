// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Windows;
using System.Windows.Threading;

namespace DynamicIsland.Core;

public enum IslandModuleCategory
{
    Media,
    Calls,
    Timers,
    System,
    Other,
}

/// <summary>
/// Actividad de la isla. Se muestra la activa de mayor prioridad; la siguiente
/// no transitoria aparece como una gota separada.
/// </summary>
public abstract class IslandModule
{
    /// <summary>Tamaño del área "Ahora" del panel expandido.</summary>
    public static readonly Size NowArea = new(520, 215);

    public abstract string Name { get; }
    public virtual string Glyph => UI.GHome;
    public abstract int Priority { get; }
    public virtual IslandModuleCategory Category => IslandModuleCategory.Other;

    /// <summary>Avisos cortos que se ocultan solos.</summary>
    public virtual bool IsTransient => false;

    public bool IsActive { get; private set; }

    public abstract Size CompactSize { get; }
    public abstract FrameworkElement CompactView { get; }
    public virtual FrameworkElement? BubbleView => null;
    public virtual FrameworkElement? ExpandedView => null;

    /// <summary>true mientras necesite actualizarse en cada fotograma.</summary>
    public virtual bool WantsFrames => false;
    public virtual void OnFrame(double dt, double time) { }

    /// <summary>Se dispara en el hilo de la UI cuando cambia el estado o el contenido.</summary>
    public event Action<IslandModule>? StateChanged;

    public virtual void Start() { }
    public virtual void Stop() { }

    protected void SetActive(bool active)
    {
        IsActive = active;
        Raise();
    }

    protected void Raise() => StateChanged?.Invoke(this);

    protected static void Ui(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        d.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            try { action(); } catch (Exception ex) { Log.Error("UI", ex); }
        }));
    }
}

/// <summary>Módulo de aviso corto que se oculta solo.</summary>
public abstract class TransientModule : IslandModule
{
    readonly DispatcherTimer _hide = new();

    protected TransientModule()
    {
        _hide.Tick += (_, _) => { _hide.Stop(); SetActive(false); };
    }

    public override bool IsTransient => true;

    protected void Flash(double seconds = 1.8)
    {
        _hide.Stop();
        _hide.Interval = TimeSpan.FromSeconds(seconds);
        _hide.Start();
        SetActive(true);
    }
}

/// <summary>Permite a las páginas pedir el teclado a la ventana o cerrar el panel.</summary>
public static class IslandHost
{
    public static Action? RequestKeyboard;
    public static Action? Collapse;
    /// <summary>true mientras se arrastra un archivo desde la isla hacia otra app.</summary>
    public static bool DraggingOut;
    /// <summary>true mientras el panel está abierto (los avisos esperan para ocultarse).</summary>
    public static bool IsExpanded;
    /// <summary>Abre el traductor con un texto.</summary>
    public static Action<string>? OpenTranslate;
}
