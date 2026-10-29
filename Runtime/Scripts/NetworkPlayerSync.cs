using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using VastMetaverseTools.Avatars;
using VastMetaverseTools.Managers;
using VastMetaverseTools.Networking;
using VastMetaverseTools.Player;

namespace VastMetaverseNetworking
{
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(NetworkTransform))]
    [RequireComponent(typeof(PlayerController))]
    public class NetworkPlayerSync : NetworkBehaviour
    {
        [SerializeField] private PlayerController _playerController;
        [SerializeField] private NetworkTransform _networkTransform;
        [SerializeField] private NetworkSyncedObject _syncedObject;
        [SerializeField] private AvatarLoader _avatarLoader;
        [SerializeField] private Rigidbody _rb;

        private readonly NetworkVariable<float> _netMoveSpeed = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<bool> _netSeated = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<int> _netAvatarIndex = new NetworkVariable<int>(
            -1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private void Awake()
        {
            if (_playerController == null) _playerController = GetComponent<PlayerController>();
            if (_networkTransform == null) _networkTransform = GetComponent<NetworkTransform>();
            if (_syncedObject == null) _syncedObject = GetComponent<NetworkSyncedObject>();
            if (_avatarLoader == null) _avatarLoader = GetComponentInChildren<AvatarLoader>();
            if (_rb == null) _rb = GetComponent<Rigidbody>();

            _networkTransform.AuthorityMode = NetworkTransform.AuthorityModes.Owner;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_playerController == null) _playerController = GetComponent<PlayerController>();
            if (_networkTransform == null) _networkTransform = GetComponent<NetworkTransform>();
            if (_syncedObject == null) _syncedObject = GetComponent<NetworkSyncedObject>();
            if (_avatarLoader == null) _avatarLoader = GetComponentInChildren<AvatarLoader>();
            if (_rb == null) _rb = GetComponent<Rigidbody>();
        }
#endif

        public override void OnNetworkSpawn()
        {
            _syncedObject.SetNetworkState(true, IsOwner);

            _netSeated.OnValueChanged += OnSeatedChanged;
            _netAvatarIndex.OnValueChanged += OnAvatarIndexChanged;

            if (IsOwner)
            {
                _playerController.OnTeleported += HandleLocalTeleported;

                if (SceneNetworkManager.Instance != null)
                {
                    SceneNetworkManager.Instance.ConfigureSpawnedLocalPlayer(_playerController);
                }

                if (_playerController.AnimController != null)
                {
                    _playerController.AnimController.OnJumpTriggered += HandleLocalJump;
                }

                CharacterCustomizer.OnLocalAvatarSelected += HandleLocalAvatarSelected;
                if (CharacterCustomizer.SelectedAvatarIndex >= 0)
                {
                    _netAvatarIndex.Value = CharacterCustomizer.SelectedAvatarIndex;
                    ApplyAvatar(_netAvatarIndex.Value);
                }
            }
            else
            {
                if (_rb != null) _rb.isKinematic = true;
                if (_playerController.InputReader != null) _playerController.InputReader.SetInputEnabled(false);
                if (MetaverseManager.Instance != null && _playerController.AnimController != null)
                {
                    _playerController.AnimController.SetAnimationController(MetaverseManager.Instance.PlayerAnimController);
                }
                if (_netAvatarIndex.Value >= 0)
                {
                    ApplyAvatar(_netAvatarIndex.Value);
                }
                if (_playerController.AnimController != null)
                {
                    _playerController.AnimController.SetSeated(_netSeated.Value);
                }
            }
        }

        public override void OnNetworkDespawn()
        {
            _netSeated.OnValueChanged -= OnSeatedChanged;
            _netAvatarIndex.OnValueChanged -= OnAvatarIndexChanged;

            if (IsOwner)
            {
                _playerController.OnTeleported -= HandleLocalTeleported;
                if (_playerController.AnimController != null)
                {
                    _playerController.AnimController.OnJumpTriggered -= HandleLocalJump;
                }
                CharacterCustomizer.OnLocalAvatarSelected -= HandleLocalAvatarSelected;
            }
        }

        private void Update()
        {
            if (!IsSpawned || _playerController.AnimController == null) return;

            if (IsOwner)
            {
                float currentSpeed = _playerController.AnimController.CurrentMoveSpeed;
                if (Mathf.Abs(_netMoveSpeed.Value - currentSpeed) > 0.01f)
                {
                    _netMoveSpeed.Value = currentSpeed;
                }

                if (_netSeated.Value != _playerController.AnimController.IsSeated)
                {
                    _netSeated.Value = _playerController.AnimController.IsSeated;
                }
            }
            else
            {
                _playerController.AnimController.SetMoveSpeed(_netMoveSpeed.Value);
            }
        }

        private void HandleLocalTeleported(Vector3 position, Quaternion rotation)
        {
            if (IsSpawned && IsOwner)
            {
                _networkTransform.Teleport(position, rotation, transform.localScale);
            }
        }

        private void HandleLocalJump()
        {
            TriggerJumpRpc();
        }

        [Rpc(SendTo.NotMe)]
        private void TriggerJumpRpc()
        {
            if (_playerController.AnimController != null)
            {
                _playerController.AnimController.Jump();
            }
        }

        private void OnSeatedChanged(bool previousValue, bool newValue)
        {
            if (!IsOwner && _playerController.AnimController != null)
            {
                _playerController.AnimController.SetSeated(newValue);
            }
        }

        private void HandleLocalAvatarSelected(int index)
        {
            if (IsOwner)
            {
                _netAvatarIndex.Value = index;
            }
        }

        private void OnAvatarIndexChanged(int previousValue, int newValue)
        {
            if (!IsOwner && newValue >= 0)
            {
                ApplyAvatar(newValue);
            }
        }

        private void ApplyAvatar(int index)
        {
            if (_avatarLoader == null) return;
            var customizer = FindAnyObjectByType<CharacterCustomizer>();
            if (customizer != null && index >= 0 && index < customizer.AvatarCount)
            {
                _avatarLoader.LoadAvatarReference(customizer.GetAvatar(index));
                if (!IsOwner && _playerController.AnimController != null)
                {
                    _playerController.AnimController.SetSeated(_netSeated.Value);
                    _playerController.AnimController.SetMoveSpeed(_netMoveSpeed.Value);
                }
            }
        }
    }
}