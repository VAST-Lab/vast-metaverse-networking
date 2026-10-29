using System;
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
using VastMetaverseTools.UI;

namespace VastMetaverseNetworking
{
    public enum NetworkStartMode
    {
        SingleplayerWithLobbyOption,
        StartNetworked
    }

    public class SceneNetworkManager : MonoBehaviour
    {
        public static SceneNetworkManager Instance { get; private set; }

        [Header("Startup Mode")]
        [SerializeField] private NetworkStartMode _startMode = NetworkStartMode.SingleplayerWithLobbyOption;
        [SerializeField] private string _urlMultiplayerParam = "multiplayer";
        [SerializeField] private string _sessionName = "GlobalLobby";
        [SerializeField] private int _maxPlayers = 32;
        [SerializeField] private bool _useDistributedAuthority = true;

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

            EvaluateWebGLLaunchMode();
            EnsureNetworkManagerExists();

            if (_startMode == NetworkStartMode.StartNetworked)
            {
                MetaverseManager.DeferInitialPlayerSpawn = true;
            }
        }

        private async void Start()
        {
            if (_startMode == NetworkStartMode.StartNetworked)
            {
                if (UIManager.Instance != null)
                {
                    UIManager.Instance.SetJoinLobbyButton(false, null);
                }
                await JoinOrCreateLobbyAsync();
            }
            else
            {
                if (UIManager.Instance != null)
                {
                    UIManager.Instance.SetJoinLobbyButton(true, OnJoinLobbyButtonClicked);
                }
            }
        }

        private void EvaluateWebGLLaunchMode()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
			if (ExternalTools.ParseURLHash(_urlMultiplayerParam, out string val))
			{
				if (val.Equals("true", StringComparison.OrdinalIgnoreCase) || val == "1")
				{
					_startMode = NetworkStartMode.StartNetworked;
				}
				else if (val.Equals("false", StringComparison.OrdinalIgnoreCase) || val == "0")
				{
					_startMode = NetworkStartMode.SingleplayerWithLobbyOption;
				}
			}
#endif
        }

        private void EnsureNetworkManagerExists()
        {
            if (NetworkManager.Singleton != null) return;

            GameObject netManagerObj = new GameObject("NetworkManager");
            DontDestroyOnLoad(netManagerObj);

            UnityTransport transport = netManagerObj.AddComponent<UnityTransport>();
            transport.UseWebSockets = true;

            NetworkManager netManager = netManagerObj.AddComponent<NetworkManager>();
            netManager.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = transport,
                PlayerPrefab = _networkedPlayerPrefab != null ? _networkedPlayerPrefab.gameObject : null,
                EnableSceneManagement = false
            };
        }

        public async void OnJoinLobbyButtonClicked()
        {
            if (_isConnecting || IsConnected) return;
            if (UIManager.Instance != null)
            {
                UIManager.Instance.SetJoinLobbyButton(false, null);
            }
            await JoinOrCreateLobbyAsync();
        }

        public async Task JoinOrCreateLobbyAsync()
        {
            if (_isConnecting || IsConnected) return;
            _isConnecting = true;

            try
            {
                if (UnityServices.State == ServicesInitializationState.Uninitialized)
                {
                    await UnityServices.InitializeAsync();
                }

                if (!AuthenticationService.Instance.IsSignedIn)
                {
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                }

                if (MetaverseManager.LocalPlayer != null)
                {
                    _pendingSpawnPos = MetaverseManager.LocalPlayer.transform.position;
                    _pendingSpawnRot = MetaverseManager.LocalPlayer.transform.rotation;
                    _hasPendingTransform = true;
                }
                else if (MetaverseManager.Instance != null)
                {
                    MetaverseManager.Instance.GetSpawnTransform(out _pendingSpawnPos, out _pendingSpawnRot);
                    _hasPendingTransform = true;
                }

                NetworkManager.Singleton.OnClientConnectedCallback += HandleClientConnected;
                NetworkManager.Singleton.OnServerStarted += HandleServerStarted;

                string activeSessionName = $"{_sessionName}_{SceneManager.GetActiveScene().name}";
                var options = new SessionOptions
                {
                    Name = activeSessionName,
                    MaxPlayers = _maxPlayers,
                    IsPrivate = false,
                    IsLocked = false
                };

                if (_useDistributedAuthority)
                {
                    options.WithDistributedAuthorityNetwork();
                }
                else
                {
                    options.WithRelayNetwork();
                }

                _currentSession = await MultiplayerService.Instance.CreateOrJoinSessionAsync(activeSessionName, options);

                if (_voiceManager == null)
                {
                    _voiceManager = FindAnyObjectByType<MetaverseVoiceManager>();
                    if (_voiceManager == null)
                    {
                        _voiceManager = gameObject.AddComponent<MetaverseVoiceManager>();
                    }
                }
                await _voiceManager.InitializeAndJoinVoiceAsync(activeSessionName);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SceneNetworkManager] Failed to join session: {e.Message}");
                if (MetaverseManager.LocalPlayer == null && MetaverseManager.Instance != null)
                {
                    MetaverseManager.Instance.SpawnSingleplayer();
                }
                if (UIManager.Instance != null && _startMode == NetworkStartMode.SingleplayerWithLobbyOption)
                {
                    UIManager.Instance.SetJoinLobbyButton(true, OnJoinLobbyButtonClicked);
                }
            }
            finally
            {
                _isConnecting = false;
            }
        }

        private void HandleServerStarted()
        {
            if (!NetworkManager.Singleton.IsServer) return;
            if (_sceneSyncManagerPrefab != null && SceneObjectSyncManager.Instance == null)
            {
                var syncMgr = Instantiate(_sceneSyncManagerPrefab);
                syncMgr.NetworkObject.Spawn();
            }
        }

        private void HandleClientConnected(ulong clientId)
        {
            if (clientId != NetworkManager.Singleton.LocalClientId) return;

            if (NetworkManager.Singleton.LocalClient?.PlayerObject != null)
            {
                var netPlayerController = NetworkManager.Singleton.LocalClient.PlayerObject.GetComponent<PlayerController>();
                if (netPlayerController != null)
                {
                    ConfigureSpawnedLocalPlayer(netPlayerController);
                }
            }
        }

        public void ConfigureSpawnedLocalPlayer(PlayerController netPlayerController)
        {
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

        private async void OnDestroy()
        {
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientConnectedCallback -= HandleClientConnected;
                NetworkManager.Singleton.OnServerStarted -= HandleServerStarted;
            }
            if (_currentSession != null)
            {
                try
                {
                    await _currentSession.LeaveAsync();
                }
                catch
                {
                }
                _currentSession = null;
            }
        }
    }
}