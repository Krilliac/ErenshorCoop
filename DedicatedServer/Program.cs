using System;
using System.Threading;
using ErenshorDedicatedServer.Core;
using ErenshorDedicatedServer.Console;
using ErenshorDedicatedServer.Configuration;

namespace ErenshorDedicatedServer
{
    internal static class Program
    {
        private static volatile bool _running = true;
        private static ServerCore _server;
        private static ConsoleInterface _console;

        private static void Main(string[] args)
        {
            System.Console.Title = "Erenshor Dedicated Server";
            System.Console.CancelKeyPress += OnCancelKeyPress;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            ServerLogger.Initialize();
            ServerLogger.Info("==============================================");
            ServerLogger.Info("   Erenshor Dedicated Server v1.0.0");
            ServerLogger.Info("   Standalone Authoritative Game Server");
            ServerLogger.Info("==============================================");
            ServerLogger.Info("");

            try
            {
                var config = ServerConfig.Load();
                if (config == null)
                {
                    ServerLogger.Fatal("Failed to load server configuration. Exiting.");
                    return;
                }

                ServerLogger.Info($"Server Name: {config.ServerName}");
                ServerLogger.Info($"Max Players: {config.MaxPlayers}");
                ServerLogger.Info($"Port: {config.Port}");
                ServerLogger.Info($"Tick Rate: {config.TickRate} Hz");
                ServerLogger.Info("");

                _server = new ServerCore(config);
                _console = new ConsoleInterface(_server);

                if (!_server.Start())
                {
                    ServerLogger.Fatal("Failed to start server. Exiting.");
                    return;
                }

                ServerLogger.Info("Server started successfully. Type 'help' for commands.");
                ServerLogger.Info("");

                // Main server loop
                var tickInterval = 1000.0 / config.TickRate;
                var lastTick = DateTime.UtcNow;

                while (_running)
                {
                    var now = DateTime.UtcNow;
                    var elapsed = (now - lastTick).TotalMilliseconds;

                    if (elapsed >= tickInterval)
                    {
                        var deltaTime = (float)(elapsed / 1000.0);
                        lastTick = now;

                        try
                        {
                            _server.Tick(deltaTime);
                        }
                        catch (Exception ex)
                        {
                            ServerLogger.Error($"Exception in server tick: {ex.Message}");
                            ServerLogger.Debug($"Stack trace: {ex.StackTrace}");
                        }
                    }

                    // Process console input (non-blocking)
                    _console.ProcessInput();

                    // Don't burn CPU
                    var sleepTime = Math.Max(1, (int)(tickInterval - (DateTime.UtcNow - lastTick).TotalMilliseconds));
                    Thread.Sleep(Math.Min(sleepTime, 16));
                }
            }
            catch (Exception ex)
            {
                ServerLogger.Fatal($"Unhandled exception: {ex.Message}");
                ServerLogger.Fatal($"Stack trace: {ex.StackTrace}");
            }
            finally
            {
                Shutdown();
            }
        }

        private static void OnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            ServerLogger.Info("Shutdown signal received...");
            _running = false;
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            ServerLogger.Fatal($"FATAL: Unhandled exception: {ex?.Message}");
            ServerLogger.Fatal($"Stack trace: {ex?.StackTrace}");

            if (e.IsTerminating)
            {
                Shutdown();
            }
        }

        public static void RequestShutdown()
        {
            _running = false;
        }

        private static void Shutdown()
        {
            ServerLogger.Info("Shutting down server...");

            try
            {
                _server?.Stop();
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Error during shutdown: {ex.Message}");
            }

            ServerLogger.Info("Server stopped.");
            ServerLogger.Shutdown();
        }
    }
}
