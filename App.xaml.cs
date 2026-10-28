using System;
using Levity.Services;
using System.IO;
using System.Threading;
using System.Windows;

namespace Levity
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex;
        private static EventWaitHandle? _wakeupEvent;
        private const string MutexName = "Levity_SingleInstance_Mutex";
        private const string EventName = "Levity_Wakeup_Event";

        protected override void OnStartup(StartupEventArgs e)
        {
            // "Levity.exe --run-tool <request file>": the elevated copy that runs one approved Windows tool, then exits. No window, no tray.
            if (e.Args.Length == 2 && e.Args[0] == "--run-tool")
            {
                int code = 1;
                try { code = Levity.Services.WindowsTools.ElevatedEntry.Run(e.Args[1]); } catch { }
                Shutdown(code);
                return;
            }

            Levity.Services.DotEnv.Load(); // before anything reads configuration
            string logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
            try { RollingLog.AppendTo(logFile, $"[{DateTime.Now:HH:mm:ss}] OnStartup entered\n"); } catch { }

            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), ev.ExceptionObject.ToString()); } catch { }
            };
            DispatcherUnhandledException += (s, ev) =>
            {
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), ev.Exception.ToString()); } catch { }
            };

            _mutex = new Mutex(true, MutexName, out bool isNewInstance);
            try { RollingLog.AppendTo(logFile, $"[{DateTime.Now:HH:mm:ss}] isNewInstance={isNewInstance}\n"); } catch { }
            if (!isNewInstance)
            {
                try
                {
                    using var existingEvent = EventWaitHandle.OpenExisting(EventName);
                    existingEvent.Set();
                    try { RollingLog.AppendTo(logFile, $"[{DateTime.Now:HH:mm:ss}] Signaled existing instance wakeup event\n"); } catch { }
                }
                catch (Exception ex)
                {
                    try { RollingLog.AppendTo(logFile, $"[{DateTime.Now:HH:mm:ss}] Failed to signal event: {ex.Message}\n"); } catch { }
                    System.Windows.MessageBox.Show("Levity is already running in your Taskbar System Tray.\nPress Win+Alt+A to open it.",
                                    "Levity", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                Shutdown();
                return;
            }

            try
            {
                _wakeupEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            }
            catch { }

            base.OnStartup(e);

            try
            {
                var mainWindow = new MainWindow();
                try { RollingLog.AppendTo(logFile, $"[{DateTime.Now:HH:mm:ss}] MainWindow constructed\n"); } catch { }
                mainWindow.InitializeSidebar();
                try { RollingLog.AppendTo(logFile, $"[{DateTime.Now:HH:mm:ss}] InitializeSidebar finished\n"); } catch { }

                if (_wakeupEvent != null)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        while (_wakeupEvent != null)
                        {
                            try
                            {
                                if (_wakeupEvent.WaitOne())
                                {
                                    mainWindow.Dispatcher.Invoke(() => mainWindow.SlideIn());
                                }
                            }
                            catch { break; }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), ex.ToString()); } catch { }
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { RollingLog.Append("startup.log", $"[{DateTime.Now:HH:mm:ss}] App.OnExit called! ExitCode={e.ApplicationExitCode}\n"); } catch { }
            _wakeupEvent?.Dispose();
            _wakeupEvent = null;

            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            _mutex = null;

            base.OnExit(e);
        }
    }
}
