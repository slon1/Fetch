using System;
using System.Threading;
using Unity.WebRTC;
using UnityEngine;
using WebRtcV2.Application.Connection;
using WebRtcV2.Application.Room;
using WebRtcV2.Config;
using WebRtcV2.CrashHandling;
using CrashReportModel = WebRtcV2.CrashHandling.CrashReport;
using WebRtcV2.Presentation;
using WebRtcV2.Shared;
using WebRtcV2.Transport;

namespace WebRtcV2.Bootstrap
{
    /// <summary>
    /// Composition root for Game.unity.
    /// Builds services, owns app lifetime, and delegates scene UI orchestration
    /// to <see cref="AppUiCoordinator"/>.
    /// </summary>
    public class AppBootstrap : MonoBehaviour
    {
        [Header("Config")]
        [SerializeField] private AppConfig config;

        [Header("Audio")]
        [Tooltip("AudioSource on which remote audio will be played.")]
        [SerializeField] private AudioSource remoteAudioSource;

        [Header("Views")]
        [SerializeField] private LobbyScreenView lobbyView;
        [SerializeField] private CallScreenView callView;
        [SerializeField] private ConnectionStatusView statusView;

        private WorkerClient _workerClient;
        private ISecretsProvider _secretsProvider;
        private ConnectionDiagnostics _diagnostics;
        private MediaCaptureService _mediaCapture;
        private IRoomFlow _roomFlow;
        private RoomHeartbeatService _heartbeatService;
        private RoomControlSocketService _roomControlSocketService;
        private IConnectionFlow _connectionFlow;
        private AppUiCoordinator _uiCoordinator;
        private AppVisibilityTracker _visibilityTracker;
        private AndroidLocalNotificationService _notificationService;
        private CancellationTokenSource _appCts;
        private CrashCoordinator _crashCoordinator;
        private FatalErrorView _fatalErrorView;
        private bool _runtimeDisposed;
        private bool _fatalState;

        private void Awake()
        {
            _fatalErrorView = new FatalErrorView();
            _crashCoordinator = new CrashCoordinator(new LocalCrashReportSink());
            _crashCoordinator.RegisterGlobalHandlers();
        }

        private void Start()
        {
            string startupStage = "bootstrap";

            try
            {
                Screen.sleepTimeout = SleepTimeout.NeverSleep;
                UnityEngine.Application.runInBackground = true;

                startupStage = "preflight";
                StartupCheckResult preflight = StartupPreflightValidator.Validate(
                    config,
                    remoteAudioSource,
                    lobbyView,
                    callView,
                    statusView);
                if (!preflight.Success)
                {
                    EnterFatalState(_crashCoordinator.CreatePreflightFailureReport(preflight));
                    return;
                }

                startupStage = "webrtc-update";
                StartCoroutine(WebRTC.Update());

                startupStage = "build-services";
                _appCts = new CancellationTokenSource();
                BuildServices();

                startupStage = "build-ui";
                BuildUiCoordinator();

                startupStage = "initialize-ui";
                _uiCoordinator.Initialize();
            }
            catch (Exception e)
            {
                CrashReportModel report = _crashCoordinator.ReportFatal(
                    MapStartupStageToErrorCode(startupStage),
                    startupStage,
                    BuildFatalMessage(startupStage),
                    e);
                EnterFatalState(report);
            }
        }

        private void Update()
        {
            _roomControlSocketService?.DispatchMessageQueue();

            if (_crashCoordinator != null && _crashCoordinator.TryConsumePendingFatal(out CrashReportModel report))
                EnterFatalState(report);
        }

        private void OnDestroy()
        {
            DisposeRuntimeServices();
            _crashCoordinator?.Dispose();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            _visibilityTracker?.SetFocused(hasFocus);
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            _visibilityTracker?.SetPaused(pauseStatus);
        }

        private void BuildServices()
        {
            var identity = LocalClientIdentity.Load(config.room.maxDisplayNameLength);

            _diagnostics = new ConnectionDiagnostics(localPeerId: identity.ClientId);
            _visibilityTracker = new AppVisibilityTracker();
            _notificationService = new AndroidLocalNotificationService(_visibilityTracker, _diagnostics);
            _notificationService.Warmup();

            _workerClient = new WorkerClient(config);
            _secretsProvider = new DevSecretsProvider();
            _mediaCapture = new MediaCaptureService(transform, _diagnostics);

            _roomFlow = new RoomFlowCoordinator(
                _workerClient,
                config,
                _diagnostics,
                identity.ClientId,
                identity.DisplayName);

            _heartbeatService = new RoomHeartbeatService(_roomFlow, config, _diagnostics);
            _roomControlSocketService = new RoomControlSocketService(config, _diagnostics);

            _connectionFlow = new ConnectionFlowCoordinator(
                _workerClient,
                config,
                _secretsProvider,
                _diagnostics,
                _mediaCapture,
                _roomControlSocketService,
                identity.ClientId);
        }

        private void BuildUiCoordinator()
        {
            _uiCoordinator = new AppUiCoordinator(
                config,
                lobbyView,
                callView,
                statusView,
                remoteAudioSource,
                _mediaCapture,
                _roomFlow,
                _connectionFlow,
                _heartbeatService,
                _roomControlSocketService,
                _notificationService,
                _appCts.Token);
        }

        private void EnterFatalState(CrashReportModel report)
        {
            if (_fatalState) return;
            _fatalState = true;

            DisposeRuntimeServices();
            _fatalErrorView.Show(report);
        }

        private void DisposeRuntimeServices()
        {
            if (_runtimeDisposed) return;
            _runtimeDisposed = true;

            SafeRun(() => _appCts?.Cancel());
            SafeRun(() => _uiCoordinator?.Dispose());
            SafeRun(() => (_connectionFlow as IDisposable)?.Dispose());
            SafeRun(() => _heartbeatService?.Dispose());
            SafeRun(() => _roomControlSocketService?.Dispose());
            SafeRun(() => _notificationService?.Dispose());
            SafeRun(() => _mediaCapture?.Dispose());
            SafeRun(() => _appCts?.Dispose());

            _uiCoordinator = null;
            _connectionFlow = null;
            _heartbeatService = null;
            _roomControlSocketService = null;
            _notificationService = null;
            _mediaCapture = null;
            _appCts = null;
        }

        private static void SafeRun(Action action)
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bootstrap] Cleanup error: {e.Message}");
            }
        }

        private static string MapStartupStageToErrorCode(string stage)
        {
            return stage switch
            {
                "webrtc-update" => "BOOT-WEBRTC",
                "build-services" => "BOOT-SERVICES",
                "build-ui" => "BOOT-UI",
                "initialize-ui" => "BOOT-INIT",
                _ => "BOOT-START"
            };
        }

        private static string BuildFatalMessage(string stage)
        {
            return stage switch
            {
                "webrtc-update" => "WebRTC runtime failed to start.",
                "build-services" => "Application services failed to initialize.",
                "build-ui" => "UI coordinator failed to build.",
                "initialize-ui" => "UI failed to initialize.",
                _ => "Application failed to start."
            };
        }
    }
}
