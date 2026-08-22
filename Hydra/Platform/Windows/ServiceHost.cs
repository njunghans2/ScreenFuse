using System.Runtime.Versioning;
using Cathedral.Logging;
using Cathedral.Utils;
using Hydra.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;

namespace Hydra.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class ServiceHost
{
    internal static void Run(string[] args)
    {
        HydraConfigFile configFile;
        List<HydraConfig> profiles;
        string configPath;
        try
        {
            var installedPath = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\ScreenFuse", "ConfigPath", null) as string;
            if (!string.IsNullOrWhiteSpace(installedPath) && File.Exists(installedPath))
            {
                configPath = Path.GetFullPath(installedPath);
                configFile = HydraConfigFile.Parse(File.ReadAllText(configPath), configPath);
            }
            else
            {
                (configFile, configPath) = HydraConfigFile.LoadAll(Env.Config);
            }
            profiles = configFile.Profiles;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return;
        }

        var config = HydraConfig.HasConditions(profiles) ? null : profiles[0];
        var profile = new HydraProfile(configFile, config);

        var builder = Host.CreateApplicationBuilder(args).DisableEventLog();
        var services = builder.Services;

        services.AddWindowsService(options => options.ServiceName = "ScreenFuse");
        services.AddSereneConsoleLogging(c => c.MinLogLevel = profile.LogLevel);

        if (configFile.LogFile is { } logFileSetting)
        {
            var logPath = Path.IsPathRooted(logFileSetting)
                ? logFileSetting
                : Path.GetFullPath(logFileSetting, Path.GetDirectoryName(configPath)!);
            services.AddSereneFileLogging(logPath, c => c.MinLogLevel = profile.LogLevel);
        }
        services.AddSingleton<IHydraProfile>(profile);
        services.AddSingleton<SessionWatchdog>();
        services.AddHostedService(sp => sp.GetRequiredService<SessionWatchdog>());
        services.AddHostedService<SasService>();
        var app = builder.Build();

        app.Run();
    }
}
