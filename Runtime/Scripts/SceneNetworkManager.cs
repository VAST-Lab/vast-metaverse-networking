using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;
using VastMetaverseTools.Managers;
using VastMetaverseTools.Player;

namespace VastMetaverseNetworking
{
    [DefaultExecutionOrder(-1000)]
    public class WebSocketTransportEnforcer : MonoBehaviour
    {
        private UnityTransport _transport;

        public void Initialize(UnityTransport transport)
        {
            _transport = transport;
            if (_transport != null) _transport.UseWebSockets = true;
        }

        private void Update()
        {
            if (_transport != null && !_transport.UseWebSockets)
            {
                _transport.UseWebSockets = true;
            }
        }
    }

    public class SceneNetworkManager : MonoBehaviour
    {
        public static SceneNetworkManager Instance { get; private set; }

        [SerializeField] private bool _startNetworked = false;
        [SerializeField] private string _urlMultiplayerParam = "multiplayer";
        [SerializeField] private string _sessionName = "GlobalLobby";
        [SerializeField] private int _maxPlayers = 16;
        [SerializeField] private bool _useDistributedAuthority = true;
        [SerializeField] private GameObject _joinButton;

        [Header("Prefabs")]
        [SerializeField] private NetworkObject _networkedPlayerPrefab;
        [SerializeField] private SceneObjectSyncManager _sceneSyncManagerPrefab;
        [SerializeField] private MetaverseVoiceManager _voiceManager;

        private ISession _currentSession;
        private bool _isConnecting;
        private Vector3 _pendingSpawnPos;
        private Quaternion _pendingSpawnRot;
        private bool _hasPendingTransform;

        public bool IsConnected => _currentSession != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            EnsureNetworkManagerExists();
            if (_startNetworked) MetaverseManager.DeferInitialPlayerSpawn = true;
        }

        private async void Start()
        {
            if (_joinButton != null) _joinButton.SetActive(!_startNetworked);
            if (_startNetworked) await JoinOrCreateLobbyAsync();
        }

        private void EnsureNetworkManagerExists()
        {
            if (NetworkManager.Singleton != null)
            {
                UnityTransport existingTransport = NetworkManager.Singleton.GetComponent<UnityTransport>();
                if (existingTransport != null)
                {
                    existingTransport.UseWebSockets = true;
                    EnsureWebSocketEnforcer(existingTransport);
                }
                if (_networkedPlayerPrefab != null && NetworkManager.Singleton.NetworkConfig.PlayerPrefab == null)
                {
                    NetworkManager.Singleton.NetworkConfig.PlayerPrefab = _networkedPlayerPrefab.gameObject;
                }
                return;
            }

            GameObject netManagerObj = new GameObject("NetworkManager");
            DontDestroyOnLoad(netManagerObj);

            UnityTransport transport = netManagerObj.AddComponent<UnityTransport>();
            transport.UseWebSockets = true;
            EnsureWebSocketEnforcer(transport);

            NetworkManager netManager = netManagerObj.AddComponent<NetworkManager>();
            netManager.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = transport,
                PlayerPrefab = _networkedPlayerPrefab != null ? _networkedPlayerPrefab.gameObject : null,
                EnableSceneManagement = false
            };
        }

        private static void EnsureWebSocketEnforcer(UnityTransport transport)
        {
            if (transport == null) return;
            WebSocketTransportEnforcer enforcer = transport.GetComponent<WebSocketTransportEnforcer>();
            if (enforcer == null) enforcer = transport.gameObject.AddComponent<WebSocketTransportEnforcer>();
            enforcer.Initialize(transport);
        }

        public async void JoinLobbyNow()
        {
            if (_isConnecting || IsConnected) return;
            if (_joinButton != null) _joinButton.SetActive(false);
            await JoinOrCreateLobbyAsync();
        }

        public async Task JoinOrCreateLobbyAsync()
        {
            if (_isConnecting || IsConnected) return;
            _isConnecting = true;
            if (_joinButton != null) _joinButton.SetActive(false);

            try
            {
                EnsureNetworkManagerExists();

                if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
                {
                    NetworkManager.Singleton.Shutdown();
                    while (NetworkManager.Singleton.ShutdownInProgress)
                    {
                        await Task.Yield();
                    }
                }

                await InitializeServicesAndAuthAsync();
                CacheLocalPlayerSpawnTransform();

                NetworkManager.Singleton.OnServerStarted -= HandleServerStarted;
                NetworkManager.Singleton.OnServerStarted += HandleServerStarted;
                NetworkManager.Singleton.OnClientStopped -= HandleClientStopped;
                NetworkManager.Singleton.OnClientStopped += HandleClientStopped;

                string baseRoomKey = SanitizeSessionKey($"{_sessionName}_{SceneManager.GetActiveScene().name}");
                await CleanupStaleSessionsAsync();

                _currentSession = await ConnectWithRecycleFallbackAsync(baseRoomKey);
                _currentSession.RemovedFromSession += HandleRemovedFromSession;
                _currentSession.Deleted += HandleSessionDeleted;

                if (_voiceManager == null)
                {
                    _voiceManager = FindAnyObjectByType<MetaverseVoiceManager>();
                    if (_voiceManager == null) _voiceManager = gameObject.AddComponent<MetaverseVoiceManager>();
                }
                await _voiceManager.InitializeAndJoinVoiceAsync(baseRoomKey);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SceneNetworkManager] Failed to join session: {e}");
                await ForceCleanupFailedConnectionAsync();

                if (MetaverseManager.LocalPlayer == null && MetaverseManager.Instance != null)
                {
                    MetaverseManager.Instance.SpawnSingleplayer();
                }

                if (_joinButton != null) _joinButton.SetActive(true);
            }
            finally
            {
                _isConnecting = false;
            }
        }

        private async Task InitializeServicesAndAuthAsync()
        {
            if (UnityServices.State == ServicesInitializationState.Uninitialized)
            {
                InitializationOptions initOptions = new InitializationOptions();
                string profileName = GetUniqueProfileName();
                initOptions.SetProfile(profileName);
                await UnityServices.InitializeAsync(initOptions);
            }

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }
        }

        private string GetUniqueProfileName()
        {
            const string prefKey = "VastMetaverse_AuthProfile";
            string savedProfile = PlayerPrefs.GetString(prefKey, "");
            if (string.IsNullOrEmpty(savedProfile))
            {
                savedProfile = $"Player_{Guid.NewGuid():N}".Substring(0, 20);
                PlayerPrefs.SetString(prefKey, savedProfile);
                PlayerPrefs.Save();
            }
#if UNITY_EDITOR
            return $"{savedProfile}_Editor";
#else
			return savedProfile;
#endif
        }

        private static string SanitizeSessionKey(string rawKey)
        {
            char[] arr = rawKey.ToCharArray();
            for (int i = 0; i < arr.Length; i++)
            {
                if (!char.IsLetterOrDigit(arr[i]) && arr[i] != '_' && arr[i] != '-')
                {
                    arr[i] = '_';
                }
            }
            return new string(arr);
        }

        private void CacheLocalPlayerSpawnTransform()
        {
            if (MetaverseManager.LocalPlayer != null)
            {
                _pendingSpawnPos = GetSafeGroundPosition(MetaverseManager.LocalPlayer.transform.position);
                _pendingSpawnRot = MetaverseManager.LocalPlayer.transform.rotation;
                _hasPendingTransform = true;
            }
            else if (MetaverseManager.Instance != null)
            {
                MetaverseManager.Instance.GetSpawnTransform(out Vector3 rawPos, out _pendingSpawnRot);
                _pendingSpawnPos = GetSafeGroundPosition(rawPos);
                _hasPendingTransform = true;
            }
        }

        private Vector3 GetSafeGroundPosition(Vector3 targetPos)
        {
            Vector3 rayStart = targetPos + Vector3.up * 1.5f;
            if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 5f, ~0, QueryTriggerInteraction.Ignore))
            {
                return hit.point + Vector3.up * 0.08f;
            }
            return targetPos + Vector3.up * 0.08f;
        }

        private async Task CleanupStaleSessionsAsync()
        {
            try
            {
                IList<string> joinedSessions = await MultiplayerService.Instance.GetJoinedSessionIdsAsync();
                if (joinedSessions == null || joinedSessions.Count == 0) return;

                foreach (string staleSessionId in joinedSessions)
                {
                    try
                    {
                        ISession staleSession = await MultiplayerService.Instance.ReconnectToSessionAsync(staleSessionId);
                        await staleSession.LeaveAsync();
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SceneNetworkManager] CleanupStaleSessions warning: {e.Message}");
            }
        }

        private SessionOptions BuildSessionOptions(string sessionName, string roomTag)
        {
            SessionOptions options = new SessionOptions
            {
                Name = sessionName,
                MaxPlayers = _maxPlayers,
                IsPrivate = false,
                IsLocked = false,
                SessionProperties = new Dictionary<string, SessionProperty>
                {
                    { "RoomTag", new SessionProperty(roomTag, VisibilityPropertyOptions.Public, PropertyIndex.String1) }
                }
            };

            if (_useDistributedAuthority) options.WithDistributedAuthorityNetwork();
            else options.WithRelayNetwork();

            ForceWebSocketsInNetworkOptions(options);
            return options;
        }

        private static void ForceWebSocketsInNetworkOptions(object optionsObj)
        {
            if (NetworkManager.Singleton != null)
            {
                UnityTransport utp = NetworkManager.Singleton.GetComponent<UnityTransport>();
                if (utp != null) utp.UseWebSockets = true;
            }

            if (optionsObj == null) return;
            try
            {
                Type t = optionsObj.GetType();
                PropertyInfo netOptsProp = t.GetProperty("NetworkOptions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object netOpts = netOptsProp?.GetValue(optionsObj);
                if (netOpts != null)
                {
                    PropertyInfo useWsProp = netOpts.GetType().GetProperty("UseWebSockets", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (useWsProp != null && useWsProp.CanWrite)
                    {
                        useWsProp.SetValue(netOpts, true);
                    }
                    FieldInfo useWsField = netOpts.GetType().GetField("<UseWebSockets>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                                        ?? netOpts.GetType().GetField("m_UseWebSockets", BindingFlags.Instance | BindingFlags.NonPublic)
                                        ?? netOpts.GetType().GetField("UseWebSockets", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (useWsField != null)
                    {
                        useWsField.SetValue(netOpts, true);
                    }
                }
            }
            catch
            {
            }
        }

        private async Task<ISession> ConnectWithRecycleFallbackAsync(string baseRoomKey)
        {
            try
            {
                QuerySessionsOptions queryOptions = new QuerySessionsOptions
                {
                    FilterOptions = new List<FilterOption>
                    {
                        new FilterOption(FilterField.StringIndex1, baseRoomKey, FilterOperation.Equal),
                        new FilterOption(FilterField.AvailableSlots, "1", FilterOperation.GreaterOrEqual)
                    }
                };

                QuerySessionsResults results = await MultiplayerService.Instance.QuerySessionsAsync(queryOptions);
                if (results != null && results.Sessions != null)
                {
                    foreach (ISessionInfo sessionInfo in results.Sessions)
                    {
                        if (sessionInfo.MaxPlayers - sessionInfo.AvailableSlots <= 0) continue;

                        try
                        {
                            JoinSessionOptions joinOpts = new JoinSessionOptions();
                            ForceWebSocketsInNetworkOptions(joinOpts);
                            return await MultiplayerService.Instance.JoinSessionByIdAsync(sessionInfo.Id, joinOpts);
                        }
                        catch (Exception joinByIdEx)
                        {
                            Debug.LogWarning($"[SceneNetworkManager] Active session {sessionInfo.Id} was unreachable ({joinByIdEx.Message}). Trying fallback...");
                        }
                    }
                }
            }
            catch (Exception queryEx)
            {
                Debug.LogWarning($"[SceneNetworkManager] QuerySessions warning: {queryEx.Message}");
            }

            Exception lastException = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                string candidateSessionId = attempt == 0 ? baseRoomKey : $"{baseRoomKey}_{attempt}";
                SessionOptions options = BuildSessionOptions(candidateSessionId, baseRoomKey);

                try
                {
                    return await MultiplayerService.Instance.CreateOrJoinSessionAsync(candidateSessionId, options);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    Debug.LogWarning($"[SceneNetworkManager] Slot '{candidateSessionId}' unavailable or recycling ({ex.Message}). Trying next slot...");
                    await CleanupStaleSessionsAsync();
                    await Task.Delay(300);
                }
            }

            SessionOptions fallbackOptions = BuildSessionOptions(baseRoomKey, baseRoomKey);
            try
            {
                return await MultiplayerService.Instance.CreateSessionAsync(fallbackOptions);
            }
            catch
            {
                throw lastException ?? new Exception("Unable to create or join any session slot.");
            }
        }

        private void HandleServerStarted()
        {
            if (NetworkManager.Singleton == null) return;
            bool canSpawnSceneSync = NetworkManager.Singleton.IsServer ||
                                     (_useDistributedAuthority && _currentSession != null && _currentSession.IsHost);

            if (canSpawnSceneSync && _sceneSyncManagerPrefab != null && SceneObjectSyncManager.Instance == null)
            {
                SceneObjectSyncManager syncMgr = Instantiate(_sceneSyncManagerPrefab);
                syncMgr.NetworkObject.Spawn();
            }
        }

        private void HandleClientStopped(bool _)
        {
            if (!_isConnecting && _joinButton != null)
            {
                _joinButton.SetActive(true);
            }
        }

        private void HandleRemovedFromSession()
        {
            _currentSession = null;
            if (_joinButton != null) _joinButton.SetActive(true);
        }

        private void HandleSessionDeleted()
        {
            _currentSession = null;
            if (_joinButton != null) _joinButton.SetActive(true);
        }

        public void ConfigureSpawnedLocalPlayer(PlayerController netPlayerController)
        {
            if (MetaverseManager.LocalPlayer != null && MetaverseManager.LocalPlayer != netPlayerController)
            {
                if (!_hasPendingTransform)
                {
                    _pendingSpawnPos = GetSafeGroundPosition(MetaverseManager.LocalPlayer.transform.position);
                    _pendingSpawnRot = MetaverseManager.LocalPlayer.transform.rotation;
                    _hasPendingTransform = true;
                }
                Collider[] oldCols = MetaverseManager.LocalPlayer.GetComponentsInChildren<Collider>(true);
                foreach (Collider col in oldCols) col.enabled = false;
                MetaverseManager.LocalPlayer.gameObject.SetActive(false);
            }

            if (_hasPendingTransform)
            {
                netPlayerController.TeleportTo(_pendingSpawnPos, _pendingSpawnRot);
                _hasPendingTransform = false;
            }
            if (MetaverseManager.Instance != null)
            {
                MetaverseManager.Instance.RegisterLocalPlayer(netPlayerController);
            }
        }

        private async Task ForceCleanupFailedConnectionAsync()
        {
            if (_currentSession != null)
            {
                try
                {
                    await _currentSession.LeaveAsync();
                }
                catch { }
                _currentSession = null;
            }

            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            {
                NetworkManager.Singleton.Shutdown();
            }
        }

        private void OnApplicationQuit()
        {
            if (_currentSession != null)
            {
                _ = _currentSession.LeaveAsync();
                _currentSession = null;
            }
        }

        private async void OnDestroy()
        {
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnServerStarted -= HandleServerStarted;
                NetworkManager.Singleton.OnClientStopped -= HandleClientStopped;
            }
            if (_currentSession != null)
            {
                _currentSession.RemovedFromSession -= HandleRemovedFromSession;
                _currentSession.Deleted -= HandleSessionDeleted;
                try
                {
                    await _currentSession.LeaveAsync();
                }
                catch { }
                _currentSession = null;
            }
        }
    }
}