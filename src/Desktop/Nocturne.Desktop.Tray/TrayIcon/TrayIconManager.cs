using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;
using Nocturne.Desktop.Tray.Helpers;
using Nocturne.Desktop.Tray.Models;
using Nocturne.Widget.Contracts;

namespace Nocturne.Desktop.Tray.TrayIcon;

/// <summary>
/// Manages the system tray icon lifecycle, including icon updates,
/// tooltip text, and context menu.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly TaskbarIcon _taskbarIcon;
    private readonly IconRenderer _iconRenderer;
    private bool _disposed;

    public event EventHandler? FlyoutRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public TrayIconManager(XamlRoot xamlRoot)
    {
        _iconRenderer = new IconRenderer();
        _taskbarIcon = new TaskbarIcon();

        _taskbarIcon.ToolTipText = "Nocturne - Connecting...";
        _taskbarIcon.LeftClickCommand = new RelayCommand(() => FlyoutRequested?.Invoke(this, EventArgs.Empty));

        _taskbarIcon.ContextMenuMode = ContextMenuMode.SecondWindow;

        var contextMenu = new Microsoft.UI.Xaml.Controls.MenuFlyout();

        var settingsItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "Settings" };
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        var separatorItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator();

        var exitItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "Exit" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(separatorItem);
        contextMenu.Items.Add(exitItem);

        _taskbarIcon.ContextFlyout = contextMenu;
    }

    public async Task UpdateAsync(GlucoseReading? reading, TraySettings settings)
    {
        var iconBytes = await _iconRenderer.RenderIconAsync(reading, settings);

        using var stream = new MemoryStream(iconBytes);
        _taskbarIcon.UpdateIcon(stream);

        _taskbarIcon.ToolTipText = reading is not null
            ? FormatTooltip(reading, settings)
            : "Nocturne - No data";
    }

    public async Task SetConnectingAsync(TraySettings settings)
    {
        var iconBytes = await _iconRenderer.RenderIconAsync(null, settings);
        using var stream = new MemoryStream(iconBytes);
        _taskbarIcon.UpdateIcon(stream);
        _taskbarIcon.ToolTipText = "Nocturne - Connecting...";
    }

    public void ForceCreate()
    {
        _taskbarIcon.ForceCreate();
    }

    private static string FormatTooltip(GlucoseReading reading, TraySettings settings)
    {
        var value = GlucoseRangeHelper.FormatValue(reading.Mgdl, settings.Unit);
        var unit = settings.Unit == GlucoseUnit.MmolL ? "mmol/L" : "mg/dL";
        var arrow = TrendHelper.GetArrowText(reading.Direction);
        var delta = GlucoseRangeHelper.FormatDelta(reading.Delta, settings.Unit);
        var age = TimeAgoHelper.Format(reading.Timestamp);

        return $"Nocturne - {value} {unit} {arrow} {delta} ({age})";
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _taskbarIcon.Dispose();
            _iconRenderer.Dispose();
            _disposed = true;
        }
    }
}
