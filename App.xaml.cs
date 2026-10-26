using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace AntigravitySidebar
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex;
        private static EventWaitHandle? _wakeupEvent;
        private const string MutexName = "AntigravitySidebar_SingleInstance_Mutex";
        private const string EventName = "AntigravitySidebar_Wakeup_Event";

        protected override void OnStartup(StartupEventArgs e)
        {
            string logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
            try { File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] OnStartup entered\n"); } catch { }

            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), ev.ExceptionObject.ToString()); } catch { }
            };
            DispatcherUnhandledException += (s, ev) =>
            {
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), ev.Exception.ToString()); } catch { }
            };

            _mutex = new Mutex(true, MutexName, out bool isNewInstance);
            try { File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] isNewInstance={isNewInstance}\n"); } catch { }
            if (!isNewInstance)
            {
                try
                {
                    using var existingEvent = EventWaitHandle.OpenExisting(EventName);
                    existingEvent.Set();
                    try { File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] Signaled existing instance wakeup event\n"); } catch { }
                }
                catch (Exception ex)
                {
                    try { File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] Failed to signal event: {ex.Message}\n"); } catch { }
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
                try { File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] MainWindow constructed\n"); } catch { }
                mainWindow.InitializeSidebar();
                try { File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] InitializeSidebar finished\n"); } catch { }

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
            try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log"), $"[{DateTime.Now:HH:mm:ss}] App.OnExit called! ExitCode={e.ApplicationExitCode}\n"); } catch { }
            _wakeupEvent?.Dispose();
            _wakeupEvent = null;

            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            _mutex = null;

            base.OnExit(e);
        }
    }
}
