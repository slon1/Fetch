using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
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
        private FcmPushService _fcmPushService;
        private CancellationTokenSource _appCts;
        private CrashCoordinator _crashCoordinator;
        private FatalErrorView _fatalErrorView;
        private bool _runtimeDisposed;
        private bool _fatalState;
        private bool _pushSyncInProgress;
        private bool _pushSyncRequested;

        private void Awake()
        {
            _fatalErrorView = new FatalErrorView();
            _crashCoordinator = new CrashCoordinator(new LocalCrashReportSink());
            _crashCoordinator.RegisterGlobalHandlers();
            Debug.Log("[Bootstrap] Awake complete.");
        }

        private void Start()
        {
            string startupStage = "bootstrap";

            try
            {
                Debug.Log("[Bootstrap] Start begin.");
                Screen.sleepTimeout = SleepTimeout.NeverSleep;
                UnityEngine.Application.runInBackground = true;

                startupStage = "preflight";
                Debug.Log("[Bootstrap] Running startup preflight.");
                StartupCheckResult preflight = StartupPreflightValidator.Validate(
                    config,
                    remoteAudioSource,
                    callerScreen,
                    videoScreen,
                    chatScreen,
                    infoScreen);
                if (!preflight.Success)
                {
                    Debug.LogError($"[Bootstrap] Preflight failed: {preflight.ErrorCode} | {preflight.Message} | {preflight.Detail}");
                    EnterFatalState(_crashCoordinator.CreatePreflightFailureReport(preflight));
                    return;
                }

                startupStage = "webrtc-update";
                Debug.Log("[Bootstrap] Starting WebRTC.Update coroutine.");
                StartCoroutine(WebRTC.Update());

                startupStage = "build-services";
                Debug.Log("[Bootstrap] Building runtime services.");
                _appCts = new CancellationTokenSource();
                BuildServices();

                startupStage = "build-ui";
                Debug.Log("[Bootstrap] Building UI coordinator.");
                BuildUiCoordinator();

                startupStage = "initialize-ui";
                Debug.Log("[Bootstrap] Initializing UI.");
                _uiCoordinator.Initialize();
                InitializePushAsync().Forget();
                Debug.Log("[Bootstrap] Start complete.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Bootstrap] Fatal during stage '{startupStage}': {e}");
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
            string step = "identity";
            try
            {
                Debug.Log("[Bootstrap] BuildServices: loading local identity.");
                var identity = LocalClientIdentity.Load(config.room.maxDisplayNameLength);

                step = "diagnostics";
                Debug.Log("[Bootstrap] BuildServices: creating diagnostics.");
                _diagnostics = new ConnectionDiagnostics(localPeerId: identity.ClientId);

                step = "visibility";
                Debug.Log("[Bootstrap] BuildServices: creating visibility tracker.");
                _visibilityTracker = new AppVisibilityTracker();

                step = "notifications";
                Debug.Log("[Bootstrap] BuildServices: creating local notification service.");
                _notificationService = new AndroidLocalNotificationService(_visibilityTracker, _diagnostics);
                _notificationService.Warmup();

                step = "worker-client";
                Debug.Log("[Bootstrap] BuildServices: creating worker client.");
                _workerClient = new WorkerClient(config);

                step = "secrets-provider";
                Debug.Log("[Bootstrap] BuildServices: creating secrets provider.");
                _secretsProvider = new DevSecretsProvider();

                step = "media-capture";
                Debug.Log("[Bootstrap] BuildServices: creating media capture service.");
                _mediaCapture = new MediaCaptureService(transform, _diagnostics);

                step = "booth-socket";
                Debug.Log("[Bootstrap] BuildServices: creating booth socket service.");
                _boothSocketService = new BoothSocketService(config, _diagnostics);

                step = "fcm-push";
                Debug.Log("[Bootstrap] BuildServices: creating FCM push service.");
                _fcmPushService = new FcmPushService(_diagnostics);

                step = "booth-flow";
                Debug.Log("[Bootstrap] BuildServices: creating booth flow.");
                _boothFlow = new BoothFlowCoordinator(
                    _workerClient,
                    _boothSocketService,
                    _diagnostics,
                    identity);

                step = "connection-flow";
                Debug.Log("[Bootstrap] BuildServices: creating connection flow.");
                _connectionFlow = new ConnectionFlowCoordinator(
                    _workerClient,
                    config,
                    _secretsProvider,
                    _diagnostics,
                    _mediaCapture,
                    _boothSocketService,
                    identity.ClientId);

                step = "event-wiring";
                Debug.Log("[Bootstrap] BuildServices: wiring FCM/booth events.");
                _boothFlow.OnSnapshotChanged += HandleBoothSnapshotChanged;
                _fcmPushService.OnTokenReceived += HandleFcmTokenReceived;
                _fcmPushService.OnIncomingCallPushReceived += HandleIncomingCallPushReceived;
                Debug.Log("[Bootstrap] BuildServices complete.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Bootstrap] BuildServices failed at step '{step}': {e}");
                throw;
            }
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

        private async UniTaskVoid InitializePushAsync()
        {
            if (_fcmPushService == null)
                return;

            try
            {
                Debug.Log("[Bootstrap] Initializing FCM push service.");
                await _fcmPushService.InitializeAsync(_appCts.Token);
                QueuePushTokenSync();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Debug.LogError($"[Bootstrap] InitializePush failed: {e}");
                _diagnostics?.LogWarning("FCM", $"Push init failed: {e.Message}");
            }
        }

        private void HandleBoothSnapshotChanged(BoothSnapshot _)
        {
            QueuePushTokenSync();
        }

        private void HandleFcmTokenReceived(string token)
        {
            Debug.Log($"[Bootstrap] FCM token received. Length={(token?.Length ?? 0)}");
            QueuePushTokenSync();
        }

        private void HandleIncomingCallPushReceived(FcmIncomingCallPush push)
        {
            if (_runtimeDisposed || push == null)
                return;

            Debug.Log($"[Bootstrap] Incoming FCM call push: callId={push.CallId} caller={push.CallerNumber} hasDisplayNotification={push.HasDisplayNotification}");
            if (!push.HasDisplayNotification)
                _notificationService?.NotifyIncomingCall(push.CallId, push.CallerNumber);
        }

        private void QueuePushTokenSync()
        {
            if (_runtimeDisposed || _appCts == null || _fcmPushService == null)
                return;

            _pushSyncRequested = true;
            if (_pushSyncInProgress)
                return;

            SyncPushTokenAsync().Forget();
        }

        private async UniTaskVoid SyncPushTokenAsync()
        {
            if (_pushSyncInProgress)
                return;

            _pushSyncInProgress = true;
            try
            {
                while (_pushSyncRequested && !_runtimeDisposed)
                {
                    _pushSyncRequested = false;

                    string boothNumber = _boothFlow?.BoothNumber;
                    string clientId = _boothFlow?.LocalClientId;
                    string token = _fcmPushService?.CurrentToken;
                    if (string.IsNullOrWhiteSpace(boothNumber) ||
                        string.IsNullOrWhiteSpace(clientId) ||
                        string.IsNullOrWhiteSpace(token) ||
                        !_fcmPushService.NeedsServerRegistration(boothNumber, clientId))
                    {
                        continue;
                    }

                    Debug.Log($"[Bootstrap] Registering FCM token for booth {boothNumber}.");
                    bool ok = await _workerClient.RegisterBoothPushTokenAsync(
                        boothNumber,
                        clientId,
                        platform: "android",
                        token,
                        _appCts.Token);

                    if (ok)
                    {
                        _fcmPushService.MarkRegistered(boothNumber, clientId, token);
                        Debug.Log($"[Bootstrap] FCM token registered for booth {boothNumber}.");
                        _diagnostics?.LogInfo("FCM", $"Push token registered for booth {boothNumber}.");
                    }
                    else
                    {
                        Debug.LogWarning($"[Bootstrap] FCM token registration failed for booth {boothNumber}.");
                        _diagnostics?.LogWarning("FCM", $"Push token registration failed for booth {boothNumber}.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Debug.LogError($"[Bootstrap] SyncPushToken failed: {e}");
                _diagnostics?.LogWarning("FCM", $"Push token sync failed: {e.Message}");
            }
            finally
            {
                _pushSyncInProgress = false;
                if (_pushSyncRequested && !_runtimeDisposed)
                    SyncPushTokenAsync().Forget();
            }
        }

        private void EnterFatalState(CrashReportModel report)
        {
            if (_fatalState) return;
            _fatalState = true;

            Debug.LogError(BuildFatalConsoleMessage(report));
            if (!string.IsNullOrWhiteSpace(report?.stackTrace))
                Debug.LogError($"[BootstrapFatal] StackTrace: {report.stackTrace}");
            if (!string.IsNullOrWhiteSpace(report?.unityLogTail))
                Debug.LogError($"[BootstrapFatal] UnityLogTail:\n{report.unityLogTail}");

            DisposeRuntimeServices();
            _fatalErrorView.Show(report);
        }

        private void DisposeRuntimeServices()
        {
            if (_runtimeDisposed) return;
            _runtimeDisposed = true;

            SafeRun(() => _appCts?.Cancel());
            SafeRun(() =>
            {
                if (_boothFlow != null)
                    _boothFlow.OnSnapshotChanged -= HandleBoothSnapshotChanged;
            });
            SafeRun(() =>
            {
                if (_fcmPushService != null)
                {
                    _fcmPushService.OnTokenReceived -= HandleFcmTokenReceived;
                    _fcmPushService.OnIncomingCallPushReceived -= HandleIncomingCallPushReceived;
                }
            });
            SafeRun(() => _uiCoordinator?.Dispose());
            SafeRun(() => (_connectionFlow as IDisposable)?.Dispose());
            SafeRun(() => (_boothFlow as IDisposable)?.Dispose());
            SafeRun(() => _boothSocketService?.Dispose());
            SafeRun(() => _fcmPushService?.Dispose());
            SafeRun(() => _notificationService?.Dispose());
            SafeRun(() => _mediaCapture?.Dispose());
            SafeRun(() => _appCts?.Dispose());

            _uiCoordinator = null;
            _connectionFlow = null;
            _boothFlow = null;
            _boothSocketService = null;
            _fcmPushService = null;
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

        private static string BuildFatalConsoleMessage(CrashReportModel report)
        {
            if (report == null)
                return "[BootstrapFatal] Unknown fatal report.";

            var builder = new StringBuilder();
            builder.Append("[BootstrapFatal] ");
            builder.Append(report.errorCode);
            builder.Append(" | stage=");
            builder.Append(report.startupStage);
            builder.Append(" | message=");
            builder.Append(report.message);
            if (!string.IsNullOrWhiteSpace(report.exceptionType))
            {
                builder.Append(" | exception=");
                builder.Append(report.exceptionType);
            }
            if (!string.IsNullOrWhiteSpace(report.deviceModel))
            {
                builder.Append(" | device=");
                builder.Append(report.deviceModel);
            }
            if (!string.IsNullOrWhiteSpace(report.platform))
            {
                builder.Append(" | platform=");
                builder.Append(report.platform);
            }
            if (!string.IsNullOrWhiteSpace(report.apiLevel))
            {
                builder.Append(" | api=");
                builder.Append(report.apiLevel);
            }
            return builder.ToString();
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
