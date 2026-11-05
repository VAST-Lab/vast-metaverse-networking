using System;
using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using VastMetaverseTools.Networking;
using VastMetaverseTools.Player;

namespace VastMetaverseNetworking
{
    public struct SceneSyncEntryState : INetworkSerializable, IEquatable<SceneSyncEntryState>
    {
        public int Index;
        public ulong OwnerClientId;
        public Vector3 Position;
        public Quaternion Rotation;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Index);
            serializer.SerializeValue(ref OwnerClientId);
            serializer.SerializeValue(ref Position);
            serializer.SerializeValue(ref Rotation);
        }

        public bool Equals(SceneSyncEntryState other) =>
            Index == other.Index &&
            OwnerClientId == other.OwnerClientId &&
            Position.Equals(other.Position) &&
            Rotation.Equals(other.Rotation);
    }

    [RequireComponent(typeof(NetworkObject))]
    public class SceneObjectSyncManager : NetworkBehaviour
    {
        public static SceneObjectSyncManager Instance { get; private set; }

        [SerializeField] private float _syncRate = 20f;

        private readonly List<NetworkSyncedObject> _sceneObjects = new List<NetworkSyncedObject>();
        private readonly Dictionary<NetworkSyncedObject, int> _objectToIndex = new Dictionary<NetworkSyncedObject, int>();
        private readonly List<SceneSyncEntryState> _syncedStates = new List<SceneSyncEntryState>();
        private float _nextSyncTime;

        private void Awake()
        {
            Instance = this;
        }

        public override void OnNetworkSpawn()
        {
            CollectSceneObjects();
            _syncedStates.Clear();

            ulong defaultOwnerId = NetworkManager.Singleton.LocalClientId;
            for (int i = 0; i < _sceneObjects.Count; i++)
            {
                NetworkSyncedObject obj = _sceneObjects[i];
                bool isAuthority = HasAuthority || IsServer;
                _syncedStates.Add(new SceneSyncEntryState
                {
                    Index = i,
                    OwnerClientId = isAuthority ? defaultOwnerId : ulong.MaxValue,
                    Position = obj.transform.position,
                    Rotation = obj.transform.rotation
                });
                obj.SetNetworkState(true, isAuthority);
            }

            NetworkManager.Singleton.OnClientConnectedCallback += HandleClientConnected;
        }

        public override void OnNetworkDespawn()
        {
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientConnectedCallback -= HandleClientConnected;
            }

            foreach (NetworkSyncedObject obj in _sceneObjects)
            {
                if (obj != null)
                {
                    obj.OnOwnershipRequested -= HandleOwnershipRequested;
                    obj.SetNetworkState(false, true);
                }
            }
        }

        private void HandleClientConnected(ulong clientId)
        {
            if (clientId == NetworkManager.Singleton.LocalClientId) return;

            ulong localId = NetworkManager.Singleton.LocalClientId;
            for (int i = 0; i < _syncedStates.Count; i++)
            {
                SceneSyncEntryState state = _syncedStates[i];
                if (state.OwnerClientId == localId && i < _sceneObjects.Count && _sceneObjects[i] != null)
                {
                    state.Position = _sceneObjects[i].transform.position;
                    state.Rotation = _sceneObjects[i].transform.rotation;
                    _syncedStates[i] = state;
                    SyncEntryStateRpc(state.Index, state.OwnerClientId, state.Position, state.Rotation);
                }
            }
        }

        private void CollectSceneObjects()
        {
            _sceneObjects.Clear();
            _objectToIndex.Clear();
            NetworkSyncedObject[] allObjects = FindObjectsByType<NetworkSyncedObject>(FindObjectsInactive.Include);
            foreach (NetworkSyncedObject obj in allObjects)
            {
                if (obj.GetComponent<PlayerController>() != null) continue;
                if (obj.GetComponent<NetworkObject>() != null) continue;
                _sceneObjects.Add(obj);
            }
            _sceneObjects.Sort((a, b) => string.CompareOrdinal(GetHierarchyPath(a.transform), GetHierarchyPath(b.transform)));
            for (int i = 0; i < _sceneObjects.Count; i++)
            {
                _objectToIndex[_sceneObjects[i]] = i;
                _sceneObjects[i].OnOwnershipRequested -= HandleOwnershipRequested;
                _sceneObjects[i].OnOwnershipRequested += HandleOwnershipRequested;
            }
        }

        private static string GetHierarchyPath(Transform current)
        {
            StringBuilder sb = new StringBuilder(current.name);
            while (current.parent != null)
            {
                current = current.parent;
                sb.Insert(0, '/');
                sb.Insert(0, current.name);
            }
            return sb.ToString();
        }

        private void HandleOwnershipRequested(NetworkSyncedObject obj)
        {
            if (!IsSpawned || !_objectToIndex.TryGetValue(obj, out int index)) return;
            RequestOwnershipRpc(index, NetworkManager.Singleton.LocalClientId);
        }

        [Rpc(SendTo.Everyone)]
        private void RequestOwnershipRpc(int index, ulong requesterClientId)
        {
            if (index < 0 || index >= _syncedStates.Count || index >= _sceneObjects.Count) return;

            SceneSyncEntryState state = _syncedStates[index];
            state.OwnerClientId = requesterClientId;
            _syncedStates[index] = state;

            NetworkSyncedObject obj = _sceneObjects[index];
            if (obj != null) obj.SetNetworkState(true, requesterClientId == NetworkManager.Singleton.LocalClientId);
        }

        [Rpc(SendTo.Everyone)]
        private void SyncEntryStateRpc(int index, ulong ownerClientId, Vector3 position, Quaternion rotation)
        {
            if (index < 0 || index >= _syncedStates.Count || index >= _sceneObjects.Count) return;
            SceneSyncEntryState state = _syncedStates[index];
            state.OwnerClientId = ownerClientId;
            state.Position = position;
            state.Rotation = rotation;
            _syncedStates[index] = state;
            NetworkSyncedObject obj = _sceneObjects[index];

            if (obj != null)
            {
                bool isLocalOwner = ownerClientId == NetworkManager.Singleton.LocalClientId;
                obj.SetNetworkState(true, isLocalOwner);
                if (!isLocalOwner) obj.transform.SetPositionAndRotation(position, rotation);
            }
        }

        private void Update()
        {
            if (!IsSpawned || NetworkManager.Singleton == null) return;
            ulong localClientId = NetworkManager.Singleton.LocalClientId;

            if (Time.time >= _nextSyncTime)
            {
                _nextSyncTime = Time.time + (1f / _syncRate);
                for (int i = 0; i < _syncedStates.Count && i < _sceneObjects.Count; i++)
                {
                    NetworkSyncedObject obj = _sceneObjects[i];
                    if (obj == null || !obj.SyncTransform) continue;

                    SceneSyncEntryState state = _syncedStates[i];
                    if (state.OwnerClientId == localClientId)
                    {
                        if (Vector3.SqrMagnitude(obj.transform.position - state.Position) > 0.0001f ||
                            Quaternion.Angle(obj.transform.rotation, state.Rotation) > 0.1f)
                        {
                            state.Position = obj.transform.position;
                            state.Rotation = obj.transform.rotation;
                            _syncedStates[i] = state;
                            UpdateTransformServerRpc(i, state.Position, state.Rotation);
                        }
                    }
                }
            }
            for (int i = 0; i < _syncedStates.Count && i < _sceneObjects.Count; i++)
            {
                NetworkSyncedObject obj = _sceneObjects[i];
                if (obj == null || !obj.SyncTransform) continue;
                SceneSyncEntryState state = _syncedStates[i];
                if (state.OwnerClientId != localClientId && state.OwnerClientId != ulong.MaxValue)
                {
                    Vector3 pos = Vector3.Lerp(obj.transform.position, state.Position, Time.deltaTime * 15f);
                    Quaternion rot = Quaternion.Slerp(obj.transform.rotation, state.Rotation, Time.deltaTime * 15f);
                    obj.transform.SetPositionAndRotation(pos, rot);
                }
            }
        }

        [Rpc(SendTo.Server)]
        private void UpdateTransformServerRpc(int index, Vector3 position, Quaternion rotation)
        {
            UpdateTransformClientRpc(index, position, rotation);
        }

        [Rpc(SendTo.ClientsAndHost)]
        private void UpdateTransformClientRpc(int index, Vector3 position, Quaternion rotation)
        {
            if (index < 0 || index >= _syncedStates.Count) return;
            SceneSyncEntryState state = _syncedStates[index];
            if (state.OwnerClientId == NetworkManager.Singleton.LocalClientId) return;
            state.Position = position;
            state.Rotation = rotation;
            _syncedStates[index] = state;
        }
    }
}