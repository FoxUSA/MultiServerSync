using System.Windows;
using System.Windows.Threading;
using MultiServerSync.Core;
using MultiServerSync.Services;
using MultiServerSync.Views;
using MultiServerSync.Views.Dialogs;

namespace MultiServerSync;

public partial class App : Application
{
    /// <summary>
    /// Программа выходит по-настоящему. Пока это не так, крестик главного окна
    /// прячет его в трей, а не закрывает.
    /// </summary>
    public static bool Quitting { get; private set; }

    /// <summary>Выход из программы: кнопка «Выход», меню в трее, установка обновления.</summary>
    public static void Quit()
    {
        Quitting = true;
        Current.Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // выход из Windows: прятаться в трей некуда
        Quitting = true;
        base.OnSessionEnding(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // замер запуска по этапам — в отладочный журнал, чтобы видеть, на что уходят секунды
        var startup = StartupTiming.Begin();

        // повторный запуск не открывает второе окно: работающая копия добавляет вкладку
        if (!SingleInstance.TryBecomePrimary())
        {
            if (!SingleInstance.SignalNewTab())
            {
                MessageBox.Show(
                    "Программа уже запущена, но не отвечает на запрос новой вкладки.\n"
                    + "Закройте её через диспетчер задач и запустите заново.",
                    AppVersion.ProductName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Необработанная ошибка: " + (args.ExceptionObject as Exception)?.Message);

        base.OnStartup(e);

        // колесо мыши во всех окнах листает то, что под курсором
        WheelScroll.Register();

        // щелчок по пустому месту снимает курсор с поля ввода
        FocusDrop.Register();

        startup.Mark("подготовка");
        AppState.Current.Load();
        startup.Mark("загрузка серверов и вкладок");

        // пароли серверов зашифрованы; с мастер-паролем — спросить его до главного окна
        PasswordVault.Init();

        if (PasswordVault.IsMaster)
        {
            // окно ввода пока единственное: его закрытие не должно само завершать программу
            var mode = ShutdownMode;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var opened = MasterPasswordDialog.Unlock();
            ShutdownMode = mode;

            if (!opened)
            {
                // нерасшифрованные пароли при выходе сохраняются как были
                Quit();
                return;
            }
        }

        PasswordVault.OpenAll();

        // память отмены начинается с открытых паролей: их расшифровка — не действие
        UndoJournal.Start();

        // терминалы вкладок и журнал — какими их оставили
        TerminalStore.Load();

        AppLog.Debug($"Запуск. Версия {AppVersion.Details}.");
        AppLog.Debug($"Программа: {AppPaths.DataDir}");
        AppLog.Debug($"Настройки пользователя: {AppPaths.UserDataDir}");

        // иконки папок чинятся при каждом запуске: архиваторы и переносы между ПК их съедают
        Task.Run(FolderIcons.Restore);

        var window = new MainWindow();
        startup.Mark("создание окна");
        MainWindow = window;
        window.ContentRendered += (_, _) => startup.Finish("показ окна");
        window.Show();

        SingleInstance.StartListener(() =>
            window.Dispatcher.BeginInvoke(new Action(window.OpenWorkspaceFromNewInstance)));

        // при каждом запуске спрашиваем byfox.dev о новой версии: если есть — кнопка в шапке
        _ = UpdateService.CheckOnStartupAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (AppState.Current.Workspaces.Count > 0)
            {
                AppState.Current.SaveAll();
                TerminalStore.Save();
                AppLog.Debug("Выход.");
            }
        }
        catch
        {
            // выходим в любом случае
        }

        AtomicFile.Flush();

        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Ошибка интерфейса: " + e.Exception.Message);
        MessageBox.Show(e.Exception.ToString(), "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
