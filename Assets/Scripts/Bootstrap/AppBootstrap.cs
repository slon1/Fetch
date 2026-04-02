using System;
using System.Threading;
using Unity.WebRTC;
using UnityEngine;
using WebRtcV2.Application.Booth;
using WebRtcV2.Application.Connection;
using WebRtcV2.Config;
using WebRtcV2.CrashHandling;
using CrashReportModel = WebRtcV2.CrashHandling.CrashReport;
using WebRtcV2.Presentation;
using WebRtcV2.Shared;
using WebRtcV2.Transport;

namespace WebRtcV2.Bootstrap
{
    public class AppBootstrap : MonoBehaviour
    {
        [Header("Config")]
        [SerializeField] private AppConfig config;

        [Header("Audio")]
        [Tooltip("AudioSource on which remote audio will be played.")]
        [SerializeField] private AudioSource remoteAudioSource;

        [Header("Screens")]
        [SerializeField] private CallerScr callerScreen;
        [SerializeField] private VideoScr videoScreen;
        [SerializeField] private ChatScr chatScreen;
        [SerializeField] private InfoScr infoScreen;

        private WorkerClient _workerClient;
        private ISecretsProvider _secretsProvider;
        private ConnectionDiagnostics _diagnostics;
        private MediaCaptureService _mediaCapture;
        private IBoothFlow _boothFlow;
        private BoothSocketService _boothSocketService;
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
                    callerScreen,
                    videoScreen,
                    chatScreen,
                    infoScreen);
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
            _boothSocketService?.DispatchMessageQueue();

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
            _uiCoordinator?.SetAppVisibility();
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            _visibilityTracker?.SetPaused(pauseStatus);
            _uiCoordinator?.SetAppVisibility();
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
            _boothSocketService = new BoothSocketService(config, _diagnostics);

            _boothFlow = new BoothFlowCoordinator(
                _workerClient,
                _boothSocketService,
                _diagnostics,
                identity);

            _connectionFlow = new ConnectionFlowCoordinator(
                _workerClient,
                config,
                _secretsProvider,
                _diagnostics,
                _mediaCapture,
                _boothSocketService,
                identity.ClientId);
        }

        private void BuildUiCoordinator()
        {
            _uiCoordinator = new AppUiCoordinator(
                callerScreen,
                videoScreen,
                chatScreen,
                infoScreen,
                remoteAudioSource,
                _mediaCapture,
                _boothFlow,
                _connectionFlow,
                _notificationService,
                _visibilityTracker,
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
            SafeRun(() => (_boothFlow as IDisposable)?.Dispose());
            SafeRun(() => _boothSocketService?.Dispose());
            SafeRun(() => _notificationService?.Dispose());
            SafeRun(() => _mediaCapture?.Dispose());
            SafeRun(() => _appCts?.Dispose());

            _uiCoordinator = null;
            _connectionFlow = null;
            _boothFlow = null;
            _boothSocketService = null;
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
