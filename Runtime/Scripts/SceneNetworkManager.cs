using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.SceneManagement;
using VastMetaverseTools.Managers;
using VastMetaverseTools.Player;

namespace VastMetaverseNetworking
{
    public class SceneNetworkManager : MonoBehaviour
    {
        public static SceneNetworkManager Instance { get; private set; }

        [SerializeField] private bool _startNetworked = false;
        [SerializeField] private string _urlMultiplayerParam = "multiplayer";
        [SerializeField] private string _sessionName = "GlobalLobby";
        [SerializeField] private int _maxPlayers = 16;
        [SerializeField] private GameObject _joinButton;

        [Header("Prefabs")]
        [SerializeField] private NetworkObject _networkedPlayerPrefab;
        [SerializeField] private SceneObjectSyncManager _sceneSyncManagerPrefab;
        [SerializeField] private MetaverseVoiceManager _voiceManager;

        private string _lobbyId;
        private float _heartbeatTimer;
        private bool _isConnecting;
        private bool _servicesInitialized;
        private Vector3 _pendingSpawnPos;
        private Quaternion _pendingSpawnRot;
        private bool _hasPendingTransform;

        public bool IsConnected => !string.IsNullOrEmpty(_lobbyId) && NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            EnsureNetworkManagerExists();
            if (_startNetworked)
            {
                MetaverseManager.DeferInitialPlayerSpawn = true;
            }
        }

        private async void Start()
        {
            if (_joinButton != null)
            {
                _joinButton.SetActive(!_startNetworked);
            }
            if (_startNetworked)
            {
                await JoinOrCreateLobbyAsync();
            }
        }

        private void Update()
        {
            if (string.IsNullOrEmpty(_lobbyId) || NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;

            _heartbeatTimer += Time.deltaTime;
            if (_heartbeatTimer >= 15f)
            {
                _heartbeatTimer = 0f;
                _ = LobbyService.Instance.SendHeartbeatPingAsync(_lobbyId);
            }
        }

        private void EnsureNetworkManagerExists()
        {
            if (NetworkManager.Singleton != null)
            {
                if (!NetworkManager.Singleton.TryGetComponent(out UnityTransport existingTransport))
                {
                    existingTransport = NetworkManager.Singleton.gameObject.AddComponent<UnityTransport>();
                }
                if (NetworkManager.Singleton.NetworkConfig == null)
                {
                    NetworkManager.Singleton.NetworkConfig = new NetworkConfig();
                }
                NetworkManager.Singleton.NetworkConfig.NetworkTransport = existingTransport;
                RegisterNetworkPrefabs(NetworkManager.Singleton);
                return;
            }

            GameObject netManagerObj = new GameObject("NetworkManager");
            netManagerObj.SetActive(false);
            DontDestroyOnLoad(netManagerObj);

            UnityTransport transport = netManagerObj.AddComponent<UnityTransport>();
            NetworkManager netManager = netManagerObj.AddComponent<NetworkManager>();
            netManager.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = transport,
                PlayerPrefab = _networkedPlayerPrefab != null ? _networkedPlayerPrefab.gameObject : null,
                EnableSceneManagement = false
            };

            RegisterNetworkPrefabs(netManager);
            netManagerObj.SetActive(true);

            if (NetworkManager.Singleton == null)
            {
                netManager.SetSingleton();
            }
        }

        private void RegisterNetworkPrefabs(NetworkManager netManager)
        {
            if (netManager == null || netManager.NetworkConfig == null || netManager.IsListening) return;

            if (_networkedPlayerPrefab != null)
            {
                netManager.NetworkConfig.PlayerPrefab = _networkedPlayerPrefab.gameObject;
                TryAddPrefabToConfig(netManager, _networkedPlayerPrefab.gameObject);
            }

            if (_sceneSyncManagerPrefab != null)
            {
                TryAddPrefabToConfig(netManager, _sceneSyncManagerPrefab.gameObject);
            }
        }

        private static void TryAddPrefabToConfig(NetworkManager netManager, GameObject prefab)
        {
            if (prefab == null || netManager.NetworkConfig.Prefabs == null) return;
            if (!netManager.NetworkConfig.Prefabs.Contains(prefab))
            {
                netManager.NetworkConfig.Prefabs.Add(new NetworkPrefab { Prefab = prefab });
            }
        }

        public async void JoinLobbyNow()
        {
            if (_isConnecting || IsConnected) return;
            if (_joinButton != null)
            {
                _joinButton.SetActive(false);
            }
            await JoinOrCreateLobbyAsync();
        }

        public async Task JoinOrCreateLobbyAsync()
        {
            if (_isConnecting || IsConnected) return;
            _isConnecting = true;
            if (_joinButton != null)
            {
                _joinButton.SetActive(false);
            }

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

                RegisterNetworkPrefabs(NetworkManager.Singleton);
                await InitializeServicesAndAuthAsync();
                CacheLocalPlayerSpawnTransform();

                NetworkManager.Singleton.OnServerStarted -= HandleServerStarted;
                NetworkManager.Singleton.OnServerStarted += HandleServerStarted;
                NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnected;
                NetworkManager.Singleton.OnClientDisconnectCallback += HandleClientDisconnected;
                NetworkManager.Singleton.OnClientStopped -= HandleClientStopped;
                NetworkManager.Singleton.OnClientStopped += HandleClientStopped;

                string baseRoomKey = SanitizeSessionKey($"{_sessionName}_{SceneManager.GetActiveScene().name}");
                await ExecuteMatchmakingAsync(baseRoomKey);

                if (_voiceManager == null)
                {
                    _voiceManager = FindAnyObjectByType<MetaverseVoiceManager>();
                    if (_voiceManager == null)
                    {
                        _voiceManager = gameObject.AddComponent<MetaverseVoiceManager>();
                    }
                }
                await _voiceManager.InitializeAndJoinVoiceAsync(baseRoomKey);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SceneNetworkManager] Failed to join lobby: {e}");
                await DisconnectAsync();
                if (MetaverseManager.LocalPlayer == null && MetaverseManager.Instance != null)
                {
                    MetaverseManager.Instance.SpawnSingleplayer();
                }
                if (_joinButton != null)
                {
                    _joinButton.SetActive(true);
                }
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
                InitializationOptions options = new InitializationOptions();
                options.SetProfile(GetUniqueProfileName());
                await UnityServices.InitializeAsync(options);
            }
            while (UnityServices.State == ServicesInitializationState.Initializing)
            {
                await Task.Yield();
            }
            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }
            _servicesInitialized = true;
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

        private async Task ExecuteMatchmakingAsync(string roomKey)
        {
            QueryLobbiesOptions queryOptions = new QueryLobbiesOptions
            {
                Filters = new List<QueryFilter>
                {
                    new QueryFilter(QueryFilter.FieldOptions.AvailableSlots, "0", QueryFilter.OpOptions.GT),
                    new QueryFilter(QueryFilter.FieldOptions.S1, roomKey, QueryFilter.OpOptions.EQ)
                }
            };

            QueryResponse queryResponse = await LobbyService.Instance.QueryLobbiesAsync(queryOptions);
            foreach (Lobby lobby in queryResponse.Results)
            {
                if (lobby.Players != null && lobby.Players.Count > 0)
                {
                    try
                    {
                        await JoinMatchAsync(lobby);
                        return;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[SceneNetworkManager] Failed to join lobby {lobby.Id} ({e.Message}). Trying next...");
                    }
                }
            }

            await HostMatchAsync(roomKey);
        }

        private async Task HostMatchAsync(string roomKey)
        {
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(Mathf.Max(1, _maxPlayers - 1));
            string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

            CreateLobbyOptions lobbyOptions = new CreateLobbyOptions
            {
                IsPrivate = false,
                Data = new Dictionary<string, DataObject>
                {
                    { "JoinCode", new DataObject(DataObject.VisibilityOptions.Member, joinCode) },
                    { "RoomKey", new DataObject(DataObject.VisibilityOptions.Public, roomKey, DataObject.IndexOptions.S1) }
                }
            };

            Lobby lobby = await LobbyService.Instance.CreateLobbyAsync(roomKey, _maxPlayers, lobbyOptions);
            _lobbyId = lobby.Id;
            _heartbeatTimer = 0f;

            UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
#if UNITY_WEBGL && !UNITY_EDITOR
			transport.UseWebSockets = true;
			transport.SetRelayServerData(allocation.ToRelayServerData("wss"));
#else
            transport.UseWebSockets = false;
            transport.SetRelayServerData(allocation.ToRelayServerData("dtls"));
#endif
            NetworkManager.Singleton.NetworkConfig.NetworkTransport = transport;

            if (!NetworkManager.Singleton.StartHost())
            {
                throw new Exception("NetworkManager.StartHost() failed.");
            }
        }

        private async Task JoinMatchAsync(Lobby lobby)
        {
            Lobby joinedLobby = await LobbyService.Instance.JoinLobbyByIdAsync(lobby.Id);
            _lobbyId = joinedLobby.Id;

            if (joinedLobby.Data == null || !joinedLobby.Data.TryGetValue("JoinCode", out DataObject joinCodeObj) || string.IsNullOrEmpty(joinCodeObj.Value))
            {
                await LeaveLobbySilentlyAsync();
                throw new Exception("Lobby is missing a valid Relay JoinCode.");
            }

            JoinAllocation joinAllocation;
            try
            {
                joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCodeObj.Value);
            }
            catch
            {
                await LeaveLobbySilentlyAsync();
                throw;
            }

            UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
#if UNITY_WEBGL && !UNITY_EDITOR
			transport.UseWebSockets = true;
			transport.SetRelayServerData(joinAllocation.ToRelayServerData("wss"));
#else
            transport.UseWebSockets = false;
            transport.SetRelayServerData(joinAllocation.ToRelayServerData("dtls"));
#endif
            NetworkManager.Singleton.NetworkConfig.NetworkTransport = transport;

            if (!NetworkManager.Singleton.StartClient())
            {
                await LeaveLobbySilentlyAsync();
                throw new Exception("NetworkManager.StartClient() failed.");
            }
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

        private void HandleServerStarted()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (_sceneSyncManagerPrefab != null && SceneObjectSyncManager.Instance == null)
            {
                SceneObjectSyncManager syncMgr = Instantiate(_sceneSyncManagerPrefab);
                syncMgr.NetworkObject.Spawn();
            }
        }

        private void HandleClientDisconnected(ulong clientId)
        {
            if (NetworkManager.Singleton != null && clientId == NetworkManager.Singleton.LocalClientId)
            {
                _ = DisconnectAsync();
            }
        }

        private void HandleClientStopped(bool _)
        {
            if (!_isConnecting && _joinButton != null)
            {
                _joinButton.SetActive(true);
            }
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
                foreach (Collider col in oldCols)
                {
                    col.enabled = false;
                }
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

        private async Task LeaveLobbySilentlyAsync()
        {
            if (!string.IsNullOrEmpty(_lobbyId) && _servicesInitialized && AuthenticationService.Instance.IsSignedIn)
            {
                string idToLeave = _lobbyId;
                _lobbyId = null;
                try
                {
                    await LobbyService.Instance.RemovePlayerAsync(idToLeave, AuthenticationService.Instance.PlayerId);
                }
                catch
                {
                }
            }
        }

        private async Task DisconnectAsync()
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            {
                NetworkManager.Singleton.Shutdown();
            }
            await LeaveLobbySilentlyAsync();
            if (_joinButton != null)
            {
                _joinButton.SetActive(true);
            }
        }

        private void OnApplicationQuit()
        {
            if (_servicesInitialized)
            {
                _ = DisconnectAsync();
            }
        }

        private async void OnDestroy()
        {
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnServerStarted -= HandleServerStarted;
                NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnected;
                NetworkManager.Singleton.OnClientStopped -= HandleClientStopped;
            }
            await DisconnectAsync();
        }
    }
}