using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MultiServerSync.Services;
using MultiServerSync.Views.Dialogs;

namespace MultiServerSync.Views.Pages;

/// <summary>
/// Раздел «Мониторинг» в главном окне: у каждой вкладки свой сервер (отмеченный в панели слева, только один)
/// и свой период, оба помнятся между запусками. Переключили вкладку — на экране её сервер и её период, опрос
/// прежнего сервера идёт дальше, только если его показывает кто-то ещё. Кнопка справа от периодов выносит сервер
/// в отдельное окно; главное окно при этом продолжает показывать сервер вкладки.
/// </summary>
public partial class MonitorPage : Page
{
    private Services.Workspace? _tab;

    public MonitorPage()
    {
        InitializeComponent();

        View.RangeChanged += range => AppState.Current.MonitorRange = range;
        View.DetachRequested += () =>
        {
            if (View.Server is { } server)
            {
                MonitorWindow.Open(server, View.Range, Window.GetWindow(this));
            }
        };

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                AppState.Current.Section = AppSection.Monitor;
                Follow();
            }
        };

        // другая вкладка раздела — её сервер и период
        AppState.Current.ActiveWorkspaceChanged += _ => Follow();
        AppState.Current.Servers.CollectionChanged += (_, _) => Follow();
        Follow();
    }

    /// <summary>Вид раздела — по нему главное окно считает свой наименьший размер.</summary>
    public Controls.MonitorView Monitor => View;

    /// <summary>Сервер и период — открытой вкладки раздела; отметка слева сменилась — вид идёт следом.</summary>
    private void Follow()
    {
        var state = AppState.Current;
        var tab = state.TabsLoaded ? state.ActiveMonitorTab : null;

        if (!ReferenceEquals(tab, _tab))
        {
            if (_tab is not null)
            {
                _tab.PropertyChanged -= OnTabChanged;
            }

            _tab = tab;

            if (tab is not null)
            {
                tab.PropertyChanged += OnTabChanged;
            }
        }

        View.Show(tab?.MonitorServer, tab?.MonitorRange ?? MonitorRange.Live);
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Services.Workspace.SelectionInfo) or nameof(Services.Workspace.Servers)
            or nameof(Services.Workspace.PaneSet) or nameof(Services.Workspace.MonitorRange))
        {
            Follow();
        }
    }
}
