using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using MultiServerSync.Services;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

/// <summary>
/// Доска вкладок: то же, что лента ярлыков наверху главного окна, но кирпичами
/// и в отдельном окне. Когда вкладок несколько десятков, лента наверху перестаёт
/// помогать — здесь ряды набираются по ширине окна, окно тянется мышью.
/// Щелчок по кирпичу открывает вкладку в главном окне, доска остаётся на месте.
/// </summary>
public partial class TabBoardWindow : FluentWindow
{
    /// <summary>Доска одна: повторное нажатие кнопки поднимает уже открытую.</summary>
    private static TabBoardWindow? _open;

    private readonly ListCollectionView _view;

    private string _filter = string.Empty;

    private readonly System.Collections.ObjectModel.ObservableCollection<Workspace> _tabs;

    private TabBoardWindow()
    {
        InitializeComponent();

        var settings = AppState.Current.Settings;

        // размер прошлого раза, но в пределах экрана: доска переживает смену монитора
        Width = Clamp(settings.BoardWidth, MinWidth, SystemParameters.WorkArea.Width);
        Height = Clamp(settings.BoardHeight, MinHeight, SystemParameters.WorkArea.Height);

        // вкладки того раздела, где доску открыли: у «Команды» и «Копирования» они свои.
        // Свой вид коллекции, а не общий: фильтр доски не должен трогать ленту наверху
        _tabs = AppState.Current.Tabs;
        _view = new ListCollectionView(_tabs)
        {
            Filter = o => o is Workspace workspace && Match(workspace),
        };

        Board.ItemsSource = _view;

        ((System.Collections.Specialized.INotifyCollectionChanged)_view).CollectionChanged += (_, _) => UpdateCount();
        UpdateCount();

        InputBindings.Add(new KeyBinding(new Core.RelayCommand(_ => Close()), Key.Escape, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new Core.RelayCommand(_ => AddTab()), Key.T, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Core.RelayCommand(_ => FilterBox.Focus()), Key.F, ModifierKeys.Control));

        Closing += OnClosing;
    }

    /// <summary>Открывает доску или поднимает уже открытую.</summary>
    public static void Open(Window owner)
    {
        if (_open is not null)
        {
            if (_open.WindowState == WindowState.Minimized)
            {
                _open.WindowState = WindowState.Normal;
            }

            _open.Activate();
            AppLog.Info("Доска вкладок уже открыта — поднимаем её.");
            return;
        }

        _open = new TabBoardWindow { Owner = owner };
        _open.Closed += (_, _) => _open = null;
        _open.Show();

        AppLog.Info($"Доска вкладок открыта: {_open.Width:0}×{_open.Height:0}, вкладок {_open._tabs.Count}.");
    }

    private bool Match(Workspace workspace)
    {
        if (_filter.Length == 0)
        {
            return true;
        }

        if (workspace.Title.Contains(_filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // искать удобнее по серверу, а не по названию: «где вкладка, которая шлёт на web-03»
        return workspace.CopyTargets.Concat(workspace.CommandTargets)
            .Any(s => s.Display.Contains(_filter, StringComparison.OrdinalIgnoreCase));
    }

    private void OnFilterChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _filter = FilterBox.Text.Trim();
        _view.Refresh();
        UpdateCount();
    }

    private void UpdateCount()
    {
        var total = _tabs.Count;

        CountText.Text = _view.Count == total
            ? $"вкладок: {total}"
            : $"показано: {_view.Count} из {total}";
    }

    /// <summary>Щелчок по кирпичу: вкладка открывается в главном окне, доска не закрывается.</summary>
    private void OnBrickClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Workspace workspace })
        {
            AppState.Current.ActiveWorkspace = workspace;
        }
    }

    private void OnAddTab(object sender, RoutedEventArgs e) => AddTab();

    private void AddTab()
    {
        var workspace = AppState.Current.AddWorkspace();

        AppState.Current.ActiveWorkspace = workspace;

        // новая вкладка не должна прятаться за фильтром — иначе кнопка выглядит сломанной
        if (_filter.Length > 0)
        {
            FilterBox.Text = string.Empty;
        }
    }

    private void OnCloseTab(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is FrameworkElement { DataContext: Workspace workspace })
        {
            AppState.Current.RemoveWorkspace(workspace);
            UpdateCount();
        }
    }

    private void OnRenameTab(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: Workspace workspace })
        {
            return;
        }

        var dialog = new InputDialog("Название вкладки", "Как назвать эту вкладку:", workspace.Title)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Value))
        {
            workspace.Title = dialog.Value;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var settings = AppState.Current.Settings;

        // у развёрнутого окна Width — размер во весь экран: запоминаем тот, к которому оно вернётся
        var size = WindowState == WindowState.Normal
            ? new Size(Width, Height)
            : new Size(RestoreBounds.Width, RestoreBounds.Height);

        settings.BoardWidth = Clamp(size.Width, MinWidth, SystemParameters.WorkArea.Width);
        settings.BoardHeight = Clamp(size.Height, MinHeight, SystemParameters.WorkArea.Height);

        AppState.Current.SaveSettings();
    }

    private static double Clamp(double value, double min, double max) =>
        double.IsNaN(value) || value < min ? min : value > max ? max : value;
}
