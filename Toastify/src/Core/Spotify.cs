using log4net;
using SpotifyAPI;
using SpotifyAPI.Local;
using SpotifyAPI.Local.Models;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Toastify.Common;
using Toastify.Core.Broadcaster;
using Toastify.Events;
using Toastify.Helpers;
using Toastify.Model;
using Toastify.Services;
using ToastifyAPI.Events;
using ToastifyAPI.Helpers;
using ToastifyAPI.Native;
using ToastifyAPI.Native.Enums;
using ToastifyAPI.Native.Structs;
using SpotifyTrackChangedEventArgs = Toastify.Events.SpotifyTrackChangedEventArgs;

namespace Toastify.Core
{
    internal class Spotify : IDisposable
    {
        private static readonly ILog logger = LogManager.GetLogger(typeof(Spotify));

        #region Singleton

        private static Spotify _instance;

        public static Spotify Instance
        {
            get { return _instance ?? (_instance = new Spotify()); }
        }

        #endregion Singleton

        #region Private fields

        #region Spotify Launcher

        private BackgroundWorker spotifyLauncher;

        private PausableTimer spotifyLauncherTimeoutTimer;

        private readonly EventWaitHandle spotifyLauncherWaitHandle = new EventWaitHandle(false, EventResetMode.ManualReset, "Toastify_SpotifyLauncherWaitHandle");

        #endregion Spotify Launcher

        private ToastifyBroadcaster broadcaster;

        private SpotifyWindow spotifyWindow;

        private SpotifyLocalAPI localAPI;

        private SpotifyLocalAPIConfig localAPIConfig;

        private readonly string spotifyPath;

        private Process spotifyProcess;

        private Song _currentSong;

        private bool? _isPlaying;

        #endregion Private fields

        #region Public Properties

        public bool IsRunning
        {
            get { return this.spotifyWindow?.IsValid ?? false; }
        }

        public StatusResponse Status { get { return this.localAPI?.GetStatus(); } }

        public Song CurrentSong
        {
            get { return this._currentSong ?? (this._currentSong = this.Status?.Track); }
            private set { this._currentSong = value; }
        }

        public bool IsPlaying
        {
            get { return this._isPlaying ?? (this._isPlaying = this.Status?.Playing ?? false).Value; }
            private set { this._isPlaying = value; }
        }

        #endregion Public properties

        #region Events

        public event EventHandler Exited;

        public event EventHandler<SpotifyStateEventArgs> Connected;

        public event EventHandler<SpotifyTrackChangedEventArgs> SongChanged;

        public event EventHandler<SpotifyPlayStateChangedEventArgs> PlayStateChanged;

        public event EventHandler<SpotifyTrackTimeChangedEventArgs> TrackTimeChanged;

        public event EventHandler<SpotifyVolumeChangedEventArgs> VolumeChanged;

        #endregion Events

        protected Spotify()
        {
            this.spotifyPath = GetSpotifyPath();
            this.InitLocalAPI();

            // TODO: Init ToastifyBroadcaster later in the initialization flow (after Spotify?)
            this.broadcaster = new ToastifyBroadcaster(41348);
            this.broadcaster.StartAsync().Wait();
        }

        public void InitLocalAPI()
        {
            this.DisposeLocalAPI();

            this.localAPIConfig = new SpotifyLocalAPIConfig
            {
                TimerInterval = 500,
                ProxyConfig = App.ProxyConfig.ProxyConfig
            };
            this.localAPI = new SpotifyLocalAPI(this.localAPIConfig);

            //this.localAPI.OnTrackChange += this.SpotifyLocalAPI_OnTrackChange;
            //this.localAPI.OnPlayStateChange += this.SpotifyLocalAPI_OnPlayStateChange;
            //this.localAPI.OnTrackTimeChange += this.SpotifyLocalAPI_OnTrackTimeChange;
            //this.localAPI.OnVolumeChange += this.SpotifyLocalAPI_OnVolumeChange;
            //this.localAPI.ListenForEvents = true;
        }

        public void StartSpotify()
        {
            this.spotifyLauncher = new BackgroundWorker { WorkerSupportsCancellation = true };
            this.spotifyLauncher.DoWork += this.StartSpotify_WorkerTask;
            this.spotifyLauncher.RunWorkerCompleted += this.StartSpotify_WorkerTaskCompleted;

            if (Settings.Current.StartupWaitTimeout < 60000)
                Settings.Current.StartupWaitTimeout = 60000;
            this.spotifyLauncherTimeoutTimer = new PausableTimer(Settings.Current.StartupWaitTimeout) { AutoReset = false };
            this.spotifyLauncherTimeoutTimer.Elapsed += this.SpotifyLauncherTimeoutTimer_Elapsed;

            this.spotifyLauncher.RunWorkerAsync();
            this.spotifyLauncherTimeoutTimer.Start();
        }

        #region Spotify Launcher background worker

        private void SpotifyLauncherTimeoutTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            this.spotifyLauncher.CancelAsync();
        }

        private void StartSpotify_WorkerTask(object sender, DoWorkEventArgs e)
        {
            var process = ToastifyAPI.Spotify.FindSpotifyProcess();
            this.spotifyProcess = !this.IsRunning && process == null ? this.LaunchSpotifyAndWaitForInputIdle(e) : process;

            if (e.Cancel)
                return;
            if (this.spotifyProcess == null)
                throw new ApplicationStartupException(Properties.Resources.ERROR_STARTUP_PROCESS);

            this.spotifyProcess.EnableRaisingEvents = true;
            this.spotifyProcess.Exited += this.Spotify_Exited;

            this.spotifyWindow = new SpotifyWindow(this.spotifyProcess);
            this.spotifyWindow.InitializationFinished += this.SpotifyWindow_InitializationFinished;

            //this.ConnectWithSpotify(e);
        }

        private void StartSpotify_WorkerTaskCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Error != null || e.Cancelled)
            {
                if (e.Error != null)
                {
                    if (e.Error is ApplicationStartupException applicationStartupException)
                    {
                        logger.Error("Error while starting Spotify.", applicationStartupException);

                        string errorMsg = Properties.Resources.ERROR_STARTUP_SPOTIFY;
                        MessageBox.Show($"{errorMsg}{Environment.NewLine}{applicationStartupException.Message}", "Toastify", MessageBoxButton.OK, MessageBoxImage.Error);

                        Analytics.TrackException(applicationStartupException, true);
                    }
                    else if (e.Error is WebException webException)
                    {
                        logger.Error("Web exception while starting Spotify.", webException);

                        string errorMsg = Properties.Resources.ERROR_STARTUP_RESTART;
                        string status = $"{webException.Status}";
                        if (webException.Status == WebExceptionStatus.ProtocolError)
                            status += $" ({(webException.Response as HttpWebResponse)?.StatusCode}, \"{(webException.Response as HttpWebResponse)?.StatusDescription}\")";
                        string techDetails = $"Technical details: {webException.Message}{Environment.NewLine}{webException.HResult}, {status}";
                        MessageBox.Show($"{errorMsg}{Environment.NewLine}{techDetails}", "Toastify", MessageBoxButton.OK, MessageBoxImage.Error);

                        Analytics.TrackException(webException, true);
                    }
                    else
                    {
                        logger.Error("Unknown error while starting Spotify.", e.Error);

                        string errorMsg = Properties.Resources.ERROR_UNKNOWN;
                        string techDetails = $"Technical Details: {e.Error.Message}{Environment.NewLine}{e.Error.StackTrace}";
                        MessageBox.Show($"{errorMsg}{Environment.NewLine}{techDetails}", "Toastify", MessageBoxButton.OK, MessageBoxImage.Error);

                        Analytics.TrackException(e.Error, true);
                    }
                }
                else // e.Cancelled
                {
                    logger.Error($"Toastify was not able to find or connect to Spotify within the timeout interval ({Settings.Current.StartupWaitTimeout / 1000} seconds).");

                    string errorMsg = Properties.Resources.ERROR_STARTUP_SPOTIFY_TIMEOUT;
                    MessageBoxResult choice = MessageBoxResult.No;
                    App.CallInSTAThread(() =>
                    {
                        choice = CustomMessageBox.ShowYesNo(
                            $"{errorMsg}{Environment.NewLine}Do you need to set up or change your proxy details?{Environment.NewLine}{Environment.NewLine}Toastify will terminate regardless of your choice.",
                            "Toastify",
                            "Yes",  // Yes
                            "No",   // No
                            MessageBoxImage.Error);
                    }, true);

                    if (choice == MessageBoxResult.Yes)
                        this.ChangeProxySettings();
                }

                // Terminate Toastify
                App.Terminate();
            }
            //else
            //    this.Spotify_Connected(this, new SpotifyStateEventArgs(this.Status));

            this.DisposeSpotifyLauncher();
            this.DisposeSpotifyLauncherTimeoutTimer();

            this.spotifyLauncherWaitHandle.Close();
        }

        /// <summary>
        /// Starts the Spotify process and waits for it to enter an idle state.
        /// </summary>
        /// <returns> The started process. </returns>
        private Process LaunchSpotifyAndWaitForInputIdle(DoWorkEventArgs e)
        {
            logger.Info("Launching Spotify...");

            if (string.IsNullOrWhiteSpace(this.spotifyPath))
                throw new ApplicationStartupException(Properties.Resources.ERROR_STARTUP_SPOTIFY_NOT_FOUND);

            // Launch Spotify.
            this.spotifyProcess = Process.Start(this.spotifyPath, App.SpotifyParameters);

            // If it is an UWP app, then Process.Start should return null: we need to look for the process.
            bool signaled = false;
            while (this.spotifyProcess == null && !signaled)
            {
                this.spotifyProcess = ToastifyAPI.Spotify.FindSpotifyProcess();
                signaled = this.spotifyLauncherWaitHandle.WaitOne(1000);
                if (this.spotifyLauncher.CheckCancellation(e))
                    return this.spotifyProcess;
            }

            // ReSharper disable once RedundantToStringCall
            if (this.spotifyProcess != null)
                logger.Info($"Spotify process started with ID {this.spotifyProcess.Id}{(!string.IsNullOrWhiteSpace(App.SpotifyParameters) ? $" and arguments \"{App.SpotifyParameters}\"" : string.Empty)}");

            // We need to let Spotify start-up before interacting with it.
            this.spotifyProcess?.WaitForInputIdle();

            if (Settings.Current.MinimizeSpotifyOnStartup)
                this.Minimize(1000);

            return this.spotifyProcess;
        }

        /// <summary>
        /// Connect with Spotify.
        /// </summary>
        /// <exception cref="ApplicationStartupException">
        ///   if Toastify was not able to connect with Spotify or
        ///   if Spotify returns a null status after connection.
        /// </exception>
        private void ConnectWithSpotify(DoWorkEventArgs e)
        {
            // Sometimes (specially with a lot of active processes), the WaitForInputIdle method (used in LaunchSpotifyAndWaitForInputIdle)
            // does not seem to wait long enough to let us connect to Spotify successfully on the first try; so we wait and re-try.

            logger.Info("Connecting to Spotify's local APIs endpoint...");

            this.spotifyLauncherWaitHandle.Reset();
            bool signaled;

            bool connected = false;
            bool proxySettingsChangedAlready = false;

            #region try { this.localAPI.Connect(); }

            do
            {
                signaled = this.spotifyLauncherWaitHandle.WaitOne(500);
                if (this.spotifyLauncher.CheckCancellation(e))
                    return;

                try
                {
                    if(logger.IsDebugEnabled)
                        logger.Debug($"Trying to connect to Spotify with {(!Settings.Current.UseProxy ? "no proxy" : $"proxy server \"{App.ProxyConfig}\"")}.");
                    connected = this.localAPI.Connect();
                }
                catch (WebException ex)
                {
                    this.HandleConnectWithSpotifyException(ex, ref proxySettingsChangedAlready);
                }
            } while (!connected && !signaled);

            #endregion try { this.localAPI.Connect(); }

            if (!connected)
                throw new ApplicationStartupException(Properties.Resources.ERROR_STARTUP_SPOTIFY_API_CONNECT);
            if (logger.IsDebugEnabled)
                logger.Debug($"Connected with Spotify on \"{this.localAPIConfig.HostUrl}:{this.localAPIConfig.Port}/\".");

            // Try to get a status report from Spotify
            var status = this.localAPI.GetStatus();
            if (status == null)
                throw new ApplicationStartupException(Properties.Resources.ERROR_STARTUP_SPOTIFY_API_STATUS_NULL);
        }

        private void HandleConnectWithSpotifyException(WebException ex, ref bool proxySettingsChangedAlready)
        {
            /*
             * Multiple known things might lead to a WebException:
             * 1) No internet connection available: this will cause a NameResolutionFailure
             * 2) Cannot enstablish connection to "open.spotify.com"
             *    - If "open.spotify.com" is blocked or redirected, a few things can happen:
             *      (a) SocketException; either ConnectionRefused or AccessDenied
             *      (b) NameResolutionFailure
             *    - Also (c), if the user needs to use a proxy and the server does not return UseProxy or ProxyAuthenticationRequired,
             *      it could fail with a ConnectionRefused error or AccessDenied (or possibly something else)
             * 3) A proxy is required
             *    (a) ProtocolError; either 305 (UseProxy) or 407 (ProxyAuthenticationRequired)
             *    (b) SocketException
             * 4) The proxy is not needed anymore?
             */

            bool handled = false;
            bool openSpotifyBlocked = false;
            string openSpotifyBlockedMessage = "";

            // ReSharper disable once MergeCastWithTypeCheck
            if (ex.InnerException is SocketException)
            {
                SocketException socketException = (SocketException)ex.InnerException;

                if (socketException.SocketErrorCode == SocketError.ConnectionRefused || socketException.SocketErrorCode == SocketError.AccessDenied)
                {
                    //// 2a) "open.spotify.com" redirected to 127.0.0.1 or blocked by the firewall

                    bool hostRedirected = Regex.IsMatch(socketException.Message, @"(127\.0\.0\.1|localhost|0\.0\.0\.0):80(?![0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

                    // Double check
                    HttpClientHandler clientHandler = Net.CreateHttpClientHandler(App.ProxyConfig);
                    using (HttpClient http = new HttpClient(clientHandler))
                    {
                        using (HttpRequestMessage request = new HttpRequestMessage())
                        {
                            request.Method = HttpMethod.Head;
                            request.RequestUri = new Uri("http://open.spotify.com");
                            request.Headers.Add("User-Agent", "Spotify (1.0.50.41368.gbd68dbef)");

                            try
                            {
                                using (http.SendAsync(request).Result) { /* do nothing */ }
                            }
                            catch
                            {
                                //// 2c) The user might need to enter or reset their proxy details (#62)
                                if (!proxySettingsChangedAlready || (Settings.Current.UseProxy && !App.ProxyConfig.IsValid()))
                                {
                                    this.AskTheUserToChangeOrDisableProxy("Toastify is having difficulties in connecting to Spotify. Are you behind a proxy?");
                                    proxySettingsChangedAlready = true;
                                }
                                else
                                {
                                    openSpotifyBlocked = true;
                                    openSpotifyBlockedMessage = hostRedirected
                                        ? Properties.Resources.ERROR_STARTUP_SPOTIFY_API_CONNECTION_BLOCKED_HOSTS
                                        : Properties.Resources.ERROR_STARTUP_SPOTIFY_API_CONNECTION_BLOCKED;
                                }

                                handled = true;
                            }
                        }
                    }
                }
            }
            else if (ex.Status == WebExceptionStatus.NameResolutionFailure && ex.Message.Contains("open.spotify.com"))
            {
                //// 2b) "open.spotify.com" redirected to 0.0.0.0
                openSpotifyBlocked = true;
                openSpotifyBlockedMessage = Properties.Resources.ERROR_STARTUP_SPOTIFY_API_CONNECTION_BLOCKED_HOSTS;
                handled = true;
            }

            if (openSpotifyBlocked)
            {
                logger.Error("Couldn't enstablish a connection to \"http://open.spotify.com\": the client blocked the connection to the host.");
                throw new ApplicationStartupException(openSpotifyBlockedMessage);
            }

            if (ex.Status == WebExceptionStatus.ProtocolError)
            {
                var httpResponse = (HttpWebResponse)ex.Response;

                //// 3a) HttpStatusCode is UseProxy or ProxyAuthenticationRequired
                if (httpResponse?.StatusCode == HttpStatusCode.UseProxy)
                {
                    string location = httpResponse.Headers[HttpResponseHeader.Location];
                    Uri uri = new Uri(location);
                    string[] userInfo = uri.UserInfo.Split(':');
                    ProxyConfig proxy = new ProxyConfig
                    {
                        Host = uri.Host,
                        Port = uri.Port,
                        Username = userInfo.Length > 0 && !string.IsNullOrEmpty(userInfo[0]) ? userInfo[0] : string.Empty,
                        Password = userInfo.Length > 1 && !string.IsNullOrEmpty(userInfo[1]) ? userInfo[1] : string.Empty,
                        BypassProxyOnLocal = true
                    };

                    App.ProxyConfig.Set(proxy);
                    handled = true;

                    if (logger.IsDebugEnabled)
                        logger.Debug($"The server requested the use of a proxy. Using \"{(!string.IsNullOrEmpty(proxy.Username) ? $"{proxy.Username}@" : "")}{proxy.Host}:{proxy.Port}\" as requested.");
                }
                else if (httpResponse?.StatusCode == HttpStatusCode.ProxyAuthenticationRequired && !(proxySettingsChangedAlready || App.ProxyConfig.IsValid()))
                {
                    if (logger.IsDebugEnabled)
                        logger.Debug("The requested proxy server requires authentication. Prompting the user for proxy details...", ex);

                    this.ChangeProxySettings();

                    handled = true;
                    proxySettingsChangedAlready = true;

                    if (logger.IsDebugEnabled)
                    {
                        var proxy = App.ProxyConfig;
                        logger.Debug($"Proxy has been set to \"{(!string.IsNullOrEmpty(proxy.Username) ? $"{proxy.Username}@" : "")}{proxy.Host}:{proxy.Port}\"");
                    }
                }
            }

            // All unhandled socket errors or HTTP status codes should be handled here
            if (!handled)
            {
                string errorCode;

                // ReSharper disable once MergeCastWithTypeCheck
                if (ex.InnerException is SocketException)
                {
                    SocketException socketException = (SocketException)ex.InnerException;
                    errorCode = socketException.SocketErrorCode.ToString();
                    logger.Warn($"Unhandled SocketException while connecting to Spotify: {socketException.SocketErrorCode}.", socketException);
                }
                else
                {
                    var httpResponse = (HttpWebResponse)ex.Response;
                    errorCode = httpResponse?.StatusCode.ToString();
                    logger.Warn("Unhandled WebException while connecting to Spotify.", ex);
                }

                // If we failed to handle the exception, ask the user if they need to configure a proxy
                if (!(proxySettingsChangedAlready || App.ProxyConfig.IsValid()))
                {
                    this.AskTheUserToChangeOrDisableProxy("Toastify is having difficulties in connecting to Spotify. Are you behind a proxy?");
                    proxySettingsChangedAlready = true;
                }
                else
                {
                    logger.Warn($"Proxy settings had no effect! {(errorCode != null ? $"Returned error code: {errorCode}" : "")}", ex);

                    if (logger.IsDebugEnabled)
                        logger.Debug("Asking the user if they want to retry, change proxy settings or exit...");

                    MessageBoxResult choice = MessageBoxResult.Cancel;
                    App.CallInSTAThread(() =>
                    {
                        choice = CustomMessageBox.ShowYesNoCancel(
                            $"Invalid proxy settings. {(errorCode != null ? $"Returned error code: {errorCode}" : "")}{Environment.NewLine}Do you want to retry?",
                            "Toastify",
                            "Retry",           // Yes
                            "Change settings", // No
                            "Exit",            // Cancel
                            MessageBoxImage.Exclamation);
                    }, true);

                    if (logger.IsDebugEnabled)
                    {
                        string hrChoice = choice == MessageBoxResult.Yes ? "Retry" : choice == MessageBoxResult.No ? "Change settings" : "Exit";
                        logger.Debug($"Choice = {hrChoice}");
                    }

                    if (choice == MessageBoxResult.No)
                        this.ChangeProxySettings();
                    else if (choice == MessageBoxResult.Cancel)
                        App.Terminate();
                }
            }
        }

        /// <summary>
        /// Prompt the user with the specified message. If they answer Yes, then they are requested to enter their proxy details;
        /// if they answer No, then the proxy is disabled.
        /// </summary>
        /// <param name="message"> The message </param>
        /// <returns> True if the user answered Yes; false otherwise. </returns>
        private bool AskTheUserToChangeOrDisableProxy(string message)
        {
            if (logger.IsDebugEnabled)
                logger.Debug("Asking the user if they are behind a proxy...");

            if (MessageBox.Show(message, "Toastify", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                this.ChangeProxySettings();
                return true;
            }
            else
            {
                // Disable the proxy
                App.ProxyConfig.Set(null);
                Settings.Current.UseProxy = false;
                Settings.Current.Save();

                if (logger.IsDebugEnabled)
                    logger.Debug("Use of proxy has been disabled.");

                return false;
            }
        }

        private void ChangeProxySettings()
        {
            if (this.spotifyLauncherTimeoutTimer?.IntervalRemaining > 0.0d)
                this.spotifyLauncherTimeoutTimer.Pause();
            App.ShowConfigProxyDialog();
            if (this.spotifyLauncherTimeoutTimer?.IntervalRemaining > 0.0d)
                this.spotifyLauncherTimeoutTimer.Resume();

            if (logger.IsDebugEnabled)
            {
                var proxy = App.ProxyConfig;
                logger.Debug($"Proxy has been set to \"{(!string.IsNullOrEmpty(proxy.Username) ? $"{proxy.Username}@" : "")}{proxy.Host}:{proxy.Port}\"");
            }
        }

        #endregion Spotify Launcher background worker

        #region SpotifyWindow wrapper methods/properties

        public bool IsMinimized
        {
            get { return this.spotifyWindow?.IsMinimized ?? false; }
        }

        public bool IsMinimizedToTray
        {
            get { return this.spotifyWindow?.IsMinimizedToTray ?? false; }
        }

        public Task Minimize(int delay = 0)
        {
            return this.spotifyWindow?.Minimize(delay);
        }

        public void ShowSpotify()
        {
            this.spotifyWindow?.Show();
        }

        private IntPtr GetMainWindowHandle()
        {
            return this.spotifyWindow?.Handle ?? IntPtr.Zero;
        }

        #endregion

        public void Kill()
        {
            //Can't kindly close Spotify this way anymore since Spotify version 1.0.75.483.g7ff4a0dc due to issue #31
            //this.SendShortcut(ToastifyActionEnum.Exit);

            try
            {
                if (this.spotifyProcess?.Handle != IntPtr.Zero)
                {
                    this.spotifyProcess?.CloseMainWindow();
                    if (this.spotifyProcess?.HasExited == false)
                        this.spotifyProcess?.Kill();
                }
                this.spotifyProcess?.Close();
                this.localAPI.Dispose();
            }
            catch { /* ignore */ }
        }

        public void SendAction(ToastifyActionEnum action)
        {
            if (!this.IsRunning)
                return;

            bool sendAppCommandMessage = false;
            bool sendMediaKey = false;

            switch (action)
            {
#if DEBUG
                case ToastifyActionEnum.ShowDebugView:
#endif
                case ToastifyActionEnum.None:
                case ToastifyActionEnum.CopyTrackInfo:
                case ToastifyActionEnum.PasteTrackInfo:
                case ToastifyActionEnum.ThumbsUp:
                case ToastifyActionEnum.ThumbsDown:
                case ToastifyActionEnum.ShowToast:
                case ToastifyActionEnum.SettingsSaved:
                case ToastifyActionEnum.Exit:
                    break;

                case ToastifyActionEnum.ShowSpotify:
                    Analytics.TrackEvent(Analytics.ToastifyEventCategory.Action, Analytics.ToastifyEvent.Action.ShowSpotify);
                    if (this.IsMinimized)
                        this.ShowSpotify();
                    else
                        this.Minimize();
                    break;

                case ToastifyActionEnum.VolumeUp:
                    Analytics.TrackEvent(Analytics.ToastifyEventCategory.Action, Analytics.ToastifyEvent.Action.VolumeUp);
                    switch ((ToastifyVolumeControlMode)Settings.Current.VolumeControlMode)
                    {
                        // The Spotify volume control mode has been dropped since Spotify version 1.0.75.483.g7ff4a0dc due to issue #31
                        //case ToastifyVolumeControlMode.Spotify:
                        //    this.SendShortcut(action);
                        //    break;

                        case ToastifyVolumeControlMode.SystemSpotifyOnly:
                            this.localAPI.IncrementVolume();
                            break;

                        case ToastifyVolumeControlMode.SystemGlobal:
                            sendMediaKey = true;
                            break;

                        default:
                            sendMediaKey = true;
                            break;
                    }
                    break;

                case ToastifyActionEnum.VolumeDown:
                    Analytics.TrackEvent(Analytics.ToastifyEventCategory.Action, Analytics.ToastifyEvent.Action.VolumeDown);
                    switch ((ToastifyVolumeControlMode)Settings.Current.VolumeControlMode)
                    {
                        // The Spotify volume control mode has been dropped since Spotify version 1.0.75.483.g7ff4a0dc due to issue #31
                        //case ToastifyVolumeControlMode.Spotify:
                        //    this.SendShortcut(action);
                        //    break;

                        case ToastifyVolumeControlMode.SystemSpotifyOnly:
                            this.localAPI.DecrementVolume();
                            break;

                        case ToastifyVolumeControlMode.SystemGlobal:
                        default:
                            sendMediaKey = true;
                            break;
                    }
                    break;

                case ToastifyActionEnum.Mute:
                    Analytics.TrackEvent(Analytics.ToastifyEventCategory.Action, Analytics.ToastifyEvent.Action.Mute);
                    switch ((ToastifyVolumeControlMode)Settings.Current.VolumeControlMode)
                    {
                        case ToastifyVolumeControlMode.SystemSpotifyOnly:
                            this.localAPI.ToggleMute();
                            break;

                        // The Spotify volume control mode has been dropped since Spotify version 1.0.75.483.g7ff4a0dc due to issue #31
                        //case ToastifyVolumeControlMode.Spotify:
                        case ToastifyVolumeControlMode.SystemGlobal:
                        default:
                            sendMediaKey = true;
                            break;
                    }
                    break;

                case ToastifyActionEnum.FastForward:
                case ToastifyActionEnum.Rewind:
                case ToastifyActionEnum.Stop:
                case ToastifyActionEnum.PlayPause:
                case ToastifyActionEnum.PreviousTrack:
                case ToastifyActionEnum.NextTrack:
                    goto default;

                default:
                    Analytics.TrackEvent(Analytics.ToastifyEventCategory.Action, $"{Analytics.ToastifyEvent.Action.Default}{action}");
                    sendAppCommandMessage = true;
                    break;
            }

            if (sendAppCommandMessage)
                Windows.SendAppCommandMessage(this.GetMainWindowHandle(), (IntPtr)action, true);
            if (sendMediaKey)
                Win32API.SendMediaKey(action);
        }

        public void VolumeUp()
        {
            this.localAPI?.IncrementVolume();
        }

        public void VolumeDown()
        {
            this.localAPI?.DecrementVolume();
        }

        public void ToggleMute()
        {
            this.localAPI?.ToggleMute();
        }

        private static string GetSpotifyPath()
        {
            string path = null;
            try
            {
                path = ToastifyAPI.Spotify.GetSpotifyPath();
                logger.Info($"Spotify executable found: \"{path}\"");
            }
            catch (Exception e)
            {
                logger.Error("Error while getting Spotify executable path.", e);
            }
            return path;
        }

        #region Dispose

        public static void DisposeInstance()
        {
            if (_instance != null)
            {
                _instance.Dispose();
                _instance = null;
            }
        }

        public void Dispose()
        {
            this.DisposeLocalAPI();
            this.DisposeSpotifyLauncher();
            this.DisposeSpotifyLauncherTimeoutTimer();
        }

        private void DisposeLocalAPI()
        {
            if (this.localAPI != null)
            {
                this.localAPI.ListenForEvents = false;
                this.localAPI.OnTrackChange -= this.SpotifyLocalAPI_OnTrackChange;
                this.localAPI.OnPlayStateChange -= this.SpotifyLocalAPI_OnPlayStateChange;
                this.localAPI.OnTrackTimeChange -= this.SpotifyLocalAPI_OnTrackTimeChange;
                this.localAPI.OnVolumeChange -= this.SpotifyLocalAPI_OnVolumeChange;

                this.localAPI.Dispose();
                this.localAPI = null;
            }

            this.localAPIConfig = null;
        }

        private void DisposeSpotifyLauncher()
        {
            if (this.spotifyLauncher != null)
            {
                this.spotifyLauncher.DoWork -= this.StartSpotify_WorkerTask;
                this.spotifyLauncher.RunWorkerCompleted -= this.StartSpotify_WorkerTaskCompleted;

                this.spotifyLauncher.Dispose();
                this.spotifyLauncher = null;
            }
        }

        private void DisposeSpotifyLauncherTimeoutTimer()
        {
            if (this.spotifyLauncherTimeoutTimer != null)
            {
                this.spotifyLauncherTimeoutTimer.Enabled = false;
                this.spotifyLauncherTimeoutTimer.Elapsed -= this.SpotifyLauncherTimeoutTimer_Elapsed;

                this.spotifyLauncherTimeoutTimer.Close();
                this.spotifyLauncherTimeoutTimer = null;
            }
        }

        #endregion Dispose

        #region Event handlers

        private void Spotify_Exited(object sender, EventArgs e)
        {
            this.Exited?.Invoke(sender, e);
        }

        private void Spotify_Connected(object sender, SpotifyStateEventArgs e)
        {
            this.Connected?.Invoke(sender, e);
        }

        private void SpotifyWindow_InitializationFinished(object sender, EventArgs e)
        {
            this.spotifyWindow.InitializationFinished -= this.SpotifyWindow_InitializationFinished;

            if (this.spotifyWindow.IsValid)
            {
                this.spotifyWindow.TitleWatcher.TitleChanged += this.SpotifyWindowTitleWatcher_TitleChanged;

                // Fake the Connected event
                string currentTitle = this.spotifyWindow.Title;
                SpotifyStateEventArgs spotifyStateEventArgs = null;

                if (string.Equals(currentTitle, SpotifyWindow.PAUSED_TITLE, StringComparison.InvariantCulture))
                    spotifyStateEventArgs = new SpotifyStateEventArgs(null, false, 1.0, 1.0);
                else
                {
                    string[] titleElements = currentTitle.Split('-');
                    if (titleElements.Length != 2)
                    {
                        // TODO: Handle unexpected title format
                    }
                    else
                    {
                        this.CurrentSong = new Song(titleElements[0].Trim(), titleElements[1].Trim(), 1, SpotifyTrackType.NORMAL, "Unknown Album");
                        spotifyStateEventArgs = new SpotifyStateEventArgs(this.CurrentSong, true, 1.0, 1.0);
                    }
                }

                if (spotifyStateEventArgs != null)
                    this.Connected?.Invoke(this, spotifyStateEventArgs);
            }
            else
            {
                string logError = this.spotifyProcess.HasExited ? "process has been terminated" : "null handle";
                logger.Error($"Couldn't find Spotify's window: {logError}");

                string errorMsg = Properties.Resources.ERROR_STARTUP_SPOTIFY_WINDOW_NOT_FOUND;
                MessageBox.Show($"{errorMsg}", "Toastify", MessageBoxButton.OK, MessageBoxImage.Error);

                App.Terminate();
            }
        }

        private void SpotifyWindowTitleWatcher_TitleChanged(object sender, WindowTitleChangedEventArgs e)
        {
            bool updateSong = false;
            if (string.Equals(e.NewTitle, SpotifyWindow.PAUSED_TITLE, StringComparison.InvariantCulture))
                this.SpotifyLocalAPI_OnPlayStateChange(this, new PlayStateEventArgs { Playing = false });
            else if (string.Equals(e.OldTitle, SpotifyWindow.PAUSED_TITLE, StringComparison.InvariantCulture))
            {
                this.SpotifyLocalAPI_OnPlayStateChange(this, new PlayStateEventArgs { Playing = true });
                updateSong = this.CurrentSong == null;
            }
            else
                updateSong = true;

            if (updateSong)
            {
                string[] newTitleElements = e.NewTitle.Split('-');
                if (newTitleElements.Length != 2)
                {
                    // TODO: Handle unexpected title format
                }
                else
                {
                    Song oldSong = this.CurrentSong;
                    this.CurrentSong = new Song(newTitleElements[0].Trim(), newTitleElements[1].Trim(), 1, SpotifyTrackType.NORMAL, "Unknown Album");
                    this.OnSongChanged(oldSong);
                }
            }
        }

        private void SpotifyLocalAPI_OnTrackChange(object sender, TrackChangeEventArgs e)
        {
            this.CurrentSong = e.NewTrack;
            this.OnSongChanged(e.OldTrack);
        }

        private void SpotifyLocalAPI_OnPlayStateChange(object sender, PlayStateEventArgs e)
        {
            this.IsPlaying = e.Playing;
            this.OnPlayStateChanged(e.Playing);
        }

        private void SpotifyLocalAPI_OnTrackTimeChange(object sender, TrackTimeChangeEventArgs e)
        {
            this.TrackTimeChanged?.Invoke(this, new SpotifyTrackTimeChangedEventArgs(e.TrackTime));
        }

        private void SpotifyLocalAPI_OnVolumeChange(object sender, VolumeChangeEventArgs e)
        {
            this.VolumeChanged?.Invoke(this, new SpotifyVolumeChangedEventArgs(e.OldVolume, e.NewVolume));
        }

        #endregion Event handlers

        private void OnSongChanged(Song previousSong)
        {
            this.SongChanged?.Invoke(this, new SpotifyTrackChangedEventArgs(previousSong, this.CurrentSong));
            this.broadcaster?.BroadcastCurrentSong(this.CurrentSong);
        }

        private void OnPlayStateChanged(bool playing)
        {
            this.PlayStateChanged?.Invoke(this, new SpotifyPlayStateChangedEventArgs(playing));
            this.broadcaster?.BroadcastPlayState(playing);
        }
    }
}