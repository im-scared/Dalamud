using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

using Dalamud.Data;
using Dalamud.Game;
using Dalamud.Game.Chat.SeStringHandling;
using Dalamud.Game.ClientState;
using Dalamud.Game.Command;
using Dalamud.Game.Internal;
using Dalamud.Game.Network;
using Dalamud.Interface;
using Dalamud.Plugin;
using Serilog;
using Serilog.Core;

namespace Dalamud
{
    /// <summary>
    /// The main Dalamud class containing all subsystems.
    /// </summary>
    public sealed class Dalamud : IDisposable
    {
        #region Internals

        private readonly ManualResetEvent unloadSignal;

        private readonly ManualResetEvent finishUnloadSignal;

        private readonly string baseDirectory;

        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="Dalamud"/> class.
        /// </summary>
        /// <param name="info">DalamudStartInfo instance.</param>
        /// <param name="loggingLevelSwitch">LoggingLevelSwitch to control Serilog level.</param>
        /// <param name="finishSignal">Signal signalling shutdown.</param>
        public Dalamud(DalamudStartInfo info, LoggingLevelSwitch loggingLevelSwitch, ManualResetEvent finishSignal)
        {
            this.StartInfo = info;
            this.LogLevelSwitch = loggingLevelSwitch;

            this.baseDirectory = info.WorkingDirectory;

            this.unloadSignal = new ManualResetEvent(false);
            this.unloadSignal.Reset();

            this.finishUnloadSignal = finishSignal;
            this.unloadSignal.Reset();
        }

        #region Native Game Subsystems

        /// <summary>
        /// Gets game framework subsystem.
        /// </summary>
        internal Framework Framework { get; private set; }

        /// <summary>
        /// Gets Anti-Debug detection prevention subsystem.
        /// </summary>
        internal AntiDebug AntiDebug { get; private set; }

        /// <summary>
        /// Gets WinSock optimization subsystem.
        /// </summary>
        internal WinSockHandlers WinSock2 { get; private set; }

        /// <summary>
        /// Gets ImGui Interface subsystem.
        /// </summary>
        internal InterfaceManager InterfaceManager { get; private set; }

        /// <summary>
        /// Gets ClientState subsystem.
        /// </summary>
        internal ClientState ClientState { get; private set; }

        #endregion

        #region Dalamud Subsystems

        /// <summary>
        /// Gets Plugin Manager subsystem.
        /// </summary>
        internal PluginManager PluginManager { get; private set; }

        /// <summary>
        /// Gets Plugin Repository subsystem.
        /// </summary>
        internal PluginRepository PluginRepository { get; private set; }

        /// <summary>
        /// Gets Data provider subsystem.
        /// </summary>
        internal DataManager Data { get; private set; }

        /// <summary>
        /// Gets Command Manager subsystem.
        /// </summary>
        internal CommandManager CommandManager { get; private set; }

        /// <summary>
        /// Gets Localization subsystem facilitating localization for Dalamud and plugins.
        /// </summary>
        internal Localization LocalizationManager { get; private set; }

        #endregion

        #region Helpers

        /// <summary>
        /// Gets SeStringManager subsystem facilitating string parsing.
        /// </summary>
        internal SeStringManager SeStringManager { get; private set; }

        /// <summary>
        /// Gets copy-enabled SigScanner for target module.
        /// </summary>
        internal SigScanner SigScanner { get; private set; }

        /// <summary>
        /// Gets LoggingLevelSwitch for Dalamud and Plugin logs.
        /// </summary>
        internal LoggingLevelSwitch LogLevelSwitch { get; private set; }

        /// <summary>
        /// Gets StartInfo object passed from injector.
        /// </summary>
        internal DalamudStartInfo StartInfo { get; private set; }

        /// <summary>
        /// Gets Configuration object facilitating save and load of Dalamud configuration.
        /// </summary>
        internal DalamudConfiguration Configuration { get; private set; }

        #endregion

        #region Dalamud Core functionality

        /// <summary>
        /// Gets Dalamud base UI.
        /// </summary>
        internal DalamudInterface DalamudUi { get; private set; }

        /// <summary>
        /// Gets Dalamud chat commands.
        /// </summary>
        internal DalamudCommands DalamudCommands { get; private set; }

        /// <summary>
        /// Gets Dalamud chat-based features.
        /// </summary>
        internal ChatHandlers ChatHandlers { get; private set; }

        /// <summary>
        /// Gets Dalamud network-based features.
        /// </summary>
        internal NetworkHandlers NetworkHandlers { get; private set; }

        #endregion

        /// <summary>
        /// Gets Injected process module.
        /// </summary>
        internal ProcessModule TargetModule { get; private set; }

        /// <summary>
        /// Gets a value indicating whether Dalamud was successfully loaded.
        /// </summary>
        internal bool IsReady { get; private set; }

        /// <summary>
        /// Gets location of stored assets.
        /// </summary>
        internal DirectoryInfo AssetDirectory => new DirectoryInfo(this.StartInfo.AssetDirectory);

        /// <summary>
        /// Gets April Fools system.
        /// </summary>
        internal Fools2021 Fools { get; private set; }

        /// <summary>
        /// Start and initialize Dalamud subsystems.
        /// </summary>
        public void Start()
        {
            try
            {
                this.Configuration = DalamudConfiguration.Load(this.StartInfo.ConfigurationPath);

                // Initialize the process information.
                this.TargetModule = Process.GetCurrentProcess().MainModule;
                this.SigScanner = new SigScanner(this.TargetModule, true);

                Log.Verbose("[START] Scanner OK!");

                this.AntiDebug = new AntiDebug(this.SigScanner);
#if DEBUG
                AntiDebug.Enable();
#endif

                Log.Verbose("[START] AntiDebug OK!");

                // Initialize game subsystem
                this.Framework = new Framework(this.SigScanner, this);

                Log.Verbose("[START] Framework OK!");

                this.WinSock2 = new WinSockHandlers();

                Log.Verbose("[START] WinSock OK!");

                this.NetworkHandlers = new NetworkHandlers(this, this.StartInfo.OptOutMbCollection);

                Log.Verbose("[START] NH OK!");

                this.ClientState = new ClientState(this, this.StartInfo, this.SigScanner);

                Log.Verbose("[START] CS OK!");

                this.LocalizationManager = new Localization(this.AssetDirectory.FullName);
                if (!string.IsNullOrEmpty(this.Configuration.LanguageOverride))
                    this.LocalizationManager.SetupWithLangCode(this.Configuration.LanguageOverride);
                else
                    this.LocalizationManager.SetupWithUiCulture();

                Log.Verbose("[START] LOC OK!");

                this.PluginRepository =
                    new PluginRepository(this, this.StartInfo.PluginDirectory, this.StartInfo.GameVersion);

                Log.Verbose("[START] PREPO OK!");

                this.DalamudUi = new DalamudInterface(this);

                Log.Verbose("[START] DUI OK!");

                var isInterfaceLoaded = false;
                if (!bool.Parse(Environment.GetEnvironmentVariable("DALAMUD_NOT_HAVE_INTERFACE") ?? "false"))
                {
                    try
                    {
                        this.InterfaceManager = new InterfaceManager(this, this.SigScanner);
                        this.InterfaceManager.OnDraw += this.DalamudUi.Draw;

                        this.InterfaceManager.Enable();
                        isInterfaceLoaded = true;

                        Log.Verbose("[START] IM OK!");

                        this.InterfaceManager.WaitForFontRebuild();
                    }
                    catch (Exception e)
                    {
                        Log.Information(e, "Could not init interface.");
                    }
                }

                var time = DateTime.Now;
                if (time.Day == 1 && time.Month == 4 && time.Year == 2021)
                {
                    this.Fools = new Fools2021(this);
                    this.InterfaceManager.OnDraw += this.Fools.Draw;
                }

                this.Data = new DataManager(this.StartInfo.Language);
                try
                {
                    this.Data.Initialize(this.AssetDirectory.FullName);
                }
                catch (Exception e)
                {
                    Log.Error(e, "Could not initialize DataManager.");
                    this.Unload();
                    return;
                }

                Log.Verbose("[START] Data OK!");

                this.SeStringManager = new SeStringManager(this.Data);

                Log.Verbose("[START] SeString OK!");

                // Initialize managers. Basically handlers for the logic
                this.CommandManager = new CommandManager(this, this.StartInfo.Language);
                this.DalamudCommands = new DalamudCommands(this);
                this.DalamudCommands.SetupCommands();

                Log.Verbose("[START] CM OK!");

                this.ChatHandlers = new ChatHandlers(this);

                Log.Verbose("[START] CH OK!");

                if (!bool.Parse(Environment.GetEnvironmentVariable("DALAMUD_NOT_HAVE_PLUGINS") ?? "false"))
                {
                    try
                    {
                        this.PluginRepository.CleanupPlugins();

                        Log.Verbose("[START] PRC OK!");

                        this.PluginManager = new PluginManager(
                            this,
                            this.StartInfo.PluginDirectory,
                            this.StartInfo.DefaultPluginDirectory);
                        this.PluginManager.LoadPlugins();

                        Log.Verbose("[START] PM OK!");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Plugin load failed.");
                    }
                }

                this.Framework.Enable();
                Log.Verbose("[START] Framework ENABLE!");

                this.ClientState.Enable();
                Log.Verbose("[START] CS ENABLE!");

                this.IsReady = true;

                Troubleshooting.LogTroubleshooting(this, isInterfaceLoaded);

                Log.Information("Dalamud is ready.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Dalamud::Start() failed.");
                this.Unload();
            }
        }

        /// <summary>
        ///     Queue an unload of Dalamud when it gets the chance.
        /// </summary>
        public void Unload()
        {
            Log.Information("Trigger unload");
            this.unloadSignal.Set();
        }

        /// <summary>
        ///     Wait for an unload request to start.
        /// </summary>
        public void WaitForUnload()
        {
            this.unloadSignal.WaitOne();
        }

        /// <summary>
        ///     Wait for a queued unload to be finalized.
        /// </summary>
        public void WaitForUnloadFinish()
        {
            this.finishUnloadSignal.WaitOne();
        }

        /// <summary>
        ///     Dispose Dalamud subsystems.
        /// </summary>
        public void Dispose()
        {
            try
            {
                this.Fools?.Dispose();

                // this must be done before unloading plugins, or it can cause a race condition
                // due to rendering happening on another thread, where a plugin might receive
                // a render call after it has been disposed, which can crash if it attempts to
                // use any resources that it freed in its own Dispose method
                this.InterfaceManager?.Dispose();

                try
                {
                    this.PluginManager.UnloadPlugins();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Plugin unload failed.");
                }

                this.Framework.Dispose();
                this.ClientState.Dispose();

                this.unloadSignal.Dispose();

                this.WinSock2.Dispose();

                this.SigScanner.Dispose();

                this.Data.Dispose();

                this.AntiDebug.Dispose();

                Log.Debug("Dalamud::Dispose() OK!");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Dalamud::Dispose() failed.");
            }
        }

        /// <summary>
        ///     Replace the built-in exception handler with a debug one.
        /// </summary>
        internal void ReplaceExceptionHandler()
        {
            var releaseFilter = this.SigScanner.ScanText(
                "40 55 53 56 48 8D AC 24 ?? ?? ?? ?? B8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 2B E0 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 48 83 3D ?? ?? ?? ?? ??");
            Log.Debug($"SE debug filter at {releaseFilter.ToInt64():X}");

            var oldFilter = NativeFunctions.SetUnhandledExceptionFilter(releaseFilter);
            Log.Debug("Reset ExceptionFilter, old: {0}", oldFilter);
        }
    }
}
