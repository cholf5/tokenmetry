using System;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;

namespace CCUsage.App.Tray;

public class TrayService
{
    private readonly Window _mainWindow;
    private TrayIcon? _trayIcon;

    public TrayService(Window mainWindow)
    {
        _mainWindow = mainWindow;
    }

    public void Initialize()
    {
        _trayIcon = new TrayIcon
        {
            ToolTipText = "AI Coding Cost Monitor",
            Menu = new NativeMenu
            {
                Items =
                {
                    new NativeMenuItem { Header = "Open", Command = new RelayCommand(ShowWindow) },
                    new NativeMenuItem { Header = "Exit", Command = new RelayCommand(ExitApp) }
                }
            }
        };

        _trayIcon.Clicked += (_, _) => ShowWindow();
    }

    public void UpdateToolTip(string tooltip)
    {
        if (_trayIcon != null)
        {
            _trayIcon.ToolTipText = tooltip;
        }
    }

    private void ShowWindow()
    {
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private void ExitApp()
    {
        _mainWindow.Close();
        Environment.Exit(0);
    }
}
