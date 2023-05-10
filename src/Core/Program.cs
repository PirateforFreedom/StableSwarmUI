using FreneticUtilities.FreneticDataSyntax;
using FreneticUtilities.FreneticExtensions;
using FreneticUtilities.FreneticToolkit;
using StableUI.Accounts;
using StableUI.Backends;
using StableUI.Utils;
using StableUI.WebAPI;
using System.Net.Sockets;
using System.Runtime.Loader;

namespace StableUI.Core;

/// <summary>Class that handles the core entry-point access to the program, and initialization of program layers.</summary>
public class Program
{
    /// <summary>Central store of available backends.</summary>
    public static BackendHandler Backends; // TODO: better location for central values

    /// <summary>Central store of web sessions.</summary>
    public static SessionHandler Sessions;

    /// <summary>Holder of server admin settings.</summary>
    public static Settings ServerSettings = new();

    private static readonly CancellationTokenSource GlobalCancelSource = new();

    /// <summary>If this is signalled, the program is cancelled.</summary>
    public static CancellationToken GlobalProgramCancel = GlobalCancelSource.Token;

    /// <summary>If enabled, settings will be locked to prevent user editing.</summary>
    public static bool LockSettings = false;

    /// <summary>Path to the settings file, as set by command line.</summary>
    public static string SettingsFilePath;

    /// <summary>Primary execution entry point.</summary>
    public static void Main(string[] args)
    {
        SpecialTools.Internationalize(); // Fix for MS's broken localization
        Logs.Init("=== StableUI Starting ===");
        AssemblyLoadContext.Default.Unloading += (_) => Shutdown();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        try
        {
            Logs.Init("Parsing command line...");
            ParseCommandLineArgs(args);
            Logs.Init("Loading settings file...");
            SettingsFilePath = CommandLineFlags.GetValueOrDefault("settings_file", "Data/Settings.fds");
            LoadSettingsFile();
            if (!LockSettings)
            {
                Logs.Init("Re-saving settings file...");
                SaveSettingsFile();
            }
            Logs.Init("Applying command line settings...");
            ApplyCommandLineSettings();
        }
        catch (InvalidDataException ex)
        {
            Logs.Error($"Command line arguments given are invalid: {ex.Message}");
            return;
        }
        Logs.Init("Loading backends...");
        Backends = new();
        Backends.Load();
        Logs.Init("Loading session handler...");
        Sessions = new();
        Logs.Init("Prepping API...");
        BasicAPIFeatures.Register();
        foreach (string str in CommandLineFlags.Keys.Where(k => !CommandLineFlagsRead.Contains(k)))
        {
            Logs.Warning($"Unused command line flag '{str}'");
        }
        Logs.Init("Launching server...");
        WebServer.Launch();
    }

    private volatile static bool HasShutdown = false;

    /// <summary>Main shutdown handler. Tells everything to stop.</summary>
    public static void Shutdown()
    {
        if (HasShutdown)
        {
            return;
        }
        HasShutdown = true;
        Logs.Info("Shutting down...");
        GlobalCancelSource.Cancel();
        Backends.Shutdown();
        Sessions.Shutdown();
    }

    #region settings
    /// <summary>Load the settings file.</summary>
    public static void LoadSettingsFile()
    {
        FDSSection section;
        try
        {
            section = FDSUtility.ReadFile(SettingsFilePath);
        }
        catch (FileNotFoundException)
        {
            Logs.Init("No settings file found.");
            return;
        }
        catch (Exception ex)
        {
            Logs.Error($"Error loading settings file: {ex}");
            return;
        }
        ServerSettings.Load(section);
    }

    /// <summary>Save the settings file.</summary>
    public static void SaveSettingsFile()
    {
        if (LockSettings)
        {
            return;
        }
        try
        {
            FDSUtility.SaveToFile(ServerSettings.Save(true), SettingsFilePath);
        }
        catch (Exception ex)
        {
            Logs.Error($"Error saving settings file: {ex}");
            return;
        }
    }
    #endregion

    #region command-line pre-apply
    /// <summary>Pre-applies settings choices from command line.</summary>
    public static void ApplyCommandLineSettings()
    {
        string environment = GetCommandLineFlag("environment", "production").ToLowerFast() switch
        {
            "dev" or "development" => "Development",
            "prod" or "production" => "Production",
            var mode => throw new InvalidDataException($"aspweb_mode value of '{mode}' is not valid")
        };
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", environment);
        string host = GetCommandLineFlag("host", ServerSettings.Host);
        string port = GetCommandLineFlag("port", $"{ServerSettings.Port}");
        WebServer.HostURL = $"http://{host}:{port}";
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", WebServer.HostURL);
        string logLevel = GetCommandLineFlag("asp_loglevel", environment == "Development" ? "debug" : "warning");
        WebServer.LogLevel = Enum.Parse<LogLevel>(logLevel, true);
        SessionHandler.LocalUserID = GetCommandLineFlag("user_id", SessionHandler.LocalUserID);
        LockSettings = GetCommandLineFlagAsBool("lock_settings", false);
    }
    #endregion

    #region command line
    /// <summary>Parses command line argument inputs and splits them into <see cref="CommandLineFlags"/> and <see cref="CommandLineValueFlags"/>.</summary>
    public static void ParseCommandLineArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--"))
            {
                throw new InvalidDataException($"Error: Unknown command line argument '{arg}'");
            }
            string key = arg[2..].ToLower();
            string value = "true";
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                value = args[++i];
            }
            if (CommandLineFlags.ContainsKey(key))
            {
                throw new InvalidDataException($"Error: Duplicate command line flag '{key}'");
            }
            CommandLineFlags[key] = value;
        }
    }

    /// <summary>Command line value-flags are contained here. Flags without value contain string 'true'. Don't read this directly, use <see cref="GetCommandLineFlag(string, string)"/>.</summary>
    public static Dictionary<string, string> CommandLineFlags = new();

    /// <summary>Helper to identify when command line flags go unused.</summary>
    public static HashSet<string> CommandLineFlagsRead = new();

    /// <summary>Get the command line flag for a given name, and default value.</summary>
    public static string GetCommandLineFlag(string key, string def)
    {
        CommandLineFlagsRead.Add(key);
        return CommandLineFlags.GetValueOrDefault(key, def);
    }

    /// <summary>Gets the command line flag for the given key as a boolean.</summary>
    public static bool GetCommandLineFlagAsBool(string key, bool def)
    {
        return GetCommandLineFlag(key, def.ToString()).ToLowerFast() switch
        {
            "true" or "yes" or "1" => true,
            "false" or "no" or "0" => false,
            var mode => throw new InvalidDataException($"Command line flag '{key}' value of '{mode}' is not valid")
        };
    }
    #endregion
}