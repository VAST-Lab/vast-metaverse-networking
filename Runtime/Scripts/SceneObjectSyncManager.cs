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

        public bool Equals(SceneSyncEntryState other) => Index == other.Index && OwnerClientId == other.OwnerClientId &&
                                                         Position.Equals(other.Position) && Rotation.Equals(other.Rotation);
    }

    [RequireComponent(typeof(NetworkObject))]
    public class SceneObjectSyncManager : NetworkBehaviour
    {
        public static SceneObjectSyncManager Instance { get; private set; }

        [SerializeField] private float _syncRate = 20f;

        private readonly List<NetworkSyncedObject> _sceneObjects = new List<NetworkSyncedObject>();
        private readonly Dictionary<NetworkSyncedObject, int> _objectToIndex = new Dictionary<NetworkSyncedObject, int>();
        private NetworkList<SceneSyncEntryState> _syncedStates;
        private float _nextSyncTime;

        private void Awake()
        {
            Instance = this;
            _syncedStates = new NetworkList<SceneSyncEntryState>(
                default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        }

        public override void OnNetworkSpawn()
        {
            CollectSceneObjects();

            if (IsServer)
            {
                for (int i = 0; i < _sceneObjects.Count; i++)
                {
                    var obj = _sceneObjects[i];
                    _syncedStates.Add(new SceneSyncEntryState
                    {
                        Index = i,
                        OwnerClientId = NetworkManager.ServerClientId,
                        Position = obj.transform.position,
                        Rotation = obj.transform.rotation
                    });
                    obj.SetNetworkState(true, true);
                }
            }
            else
            {
                for (int i = 0; i < _syncedStates.Count && i < _sceneObjects.Count; i++)
                {
                    var state = _syncedStates[i];
                    var obj = _sceneObjects[state.Index];
                    bool isLocalOwner = state.OwnerClientId == NetworkManager.Singleton.LocalClientId;
                    obj.SetNetworkState(true, isLocalOwner);
                    if (obj.SyncTransform && !isLocalOwner)
                    {
                        obj.transform.SetPositionAndRotation(state.Position, state.Rotation);
                    }
                }
            }

            _syncedStates.OnListChanged += HandleSyncedStateChanged;
        }

        public override void OnNetworkDespawn()
        {
            _syncedStates.OnListChanged -= HandleSyncedStateChanged;
            foreach (var obj in _sceneObjects)
            {
                if (obj != null)
                {
                    obj.OnOwnershipRequested -= HandleOwnershipRequested;
                    obj.SetNetworkState(false, true);
                }
            }
        }

        private void CollectSceneObjects()
        {
            _sceneObjects.Clear();
            _objectToIndex.Clear();

            var allObjects = FindObjectsByType<NetworkSyncedObject>(FindObjectsInactive.Include);
            foreach (var obj in allObjects)
            {
                if (obj.GetComponent<PlayerController>() != null) continue;
                if (obj.GetComponent<NetworkObject>() != null) continue;
                _sceneObjects.Add(obj);
            }

            _sceneObjects.Sort((a, b) => string.CompareOrdinal(GetHierarchyPath(a.transform), GetHierarchyPath(b.transform)));

            for (int i = 0; i < _sceneObjects.Count; i++)
            {
                _objectToIndex[_sceneObjects[i]] = i;
                _sceneObjects[i].OnOwnershipRequested += HandleOwnershipRequested;
            }
        }

        private static string GetHierarchyPath(Transform current)
        {
            var sb = new StringBuilder(current.name);
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
            if (!_objectToIndex.TryGetValue(obj, out int index)) return;
            RequestOwnershipRpc(index, NetworkManager.Singleton.LocalClientId);
        }

        [Rpc(SendTo.Server)]
        private void RequestOwnershipRpc(int index, ulong requesterClientId)
        {
            if (index < 0 || index >= _syncedStates.Count) return;

            var state = _syncedStates[index];
            state.OwnerClientId = requesterClientId;
            _syncedStates[index] = state;

            for (int i = 0; i < _sceneObjects.Count; i++)
            {
                if (i == index && _sceneObjects[i] != null)
                {
                    _sceneObjects[i].SetNetworkState(true, requesterClientId == NetworkManager.Singleton.LocalClientId);
                }
            }
        }

        private void HandleSyncedStateChanged(NetworkListEvent<SceneSyncEntryState> changeEvent)
        {
            int idx = changeEvent.Value.Index;
            if (idx < 0 || idx >= _sceneObjects.Count) return;

            var obj = _sceneObjects[idx];
            if (obj == null) return;

            bool isOwner = changeEvent.Value.OwnerClientId == NetworkManager.Singleton.LocalClientId;
            obj.SetNetworkState(true, isOwner);
        }

        private void Update()
        {
            if (!IsSpawned) return;

            ulong localClientId = NetworkManager.Singleton.LocalClientId;

            if (Time.time >= _nextSyncTime)
            {
                _nextSyncTime = Time.time + (1f / _syncRate);
                for (int i = 0; i < _syncedStates.Count && i < _sceneObjects.Count; i++)
                {
                    var obj = _sceneObjects[i];
                    if (obj == null || !obj.SyncTransform) continue;

                    var state = _syncedStates[i];
                    if (state.OwnerClientId == localClientId)
                    {
                        if (Vector3.SqrMagnitude(obj.transform.position - state.Position) > 0.0001f ||
                            Quaternion.Angle(obj.transform.rotation, state.Rotation) > 0.1f)
                        {
                            UpdateTransformRpc(i, obj.transform.position, obj.transform.rotation);
                        }
                    }
                }
            }

            for (int i = 0; i < _syncedStates.Count && i < _sceneObjects.Count; i++)
            {
                var obj = _sceneObjects[i];
                if (obj == null || !obj.SyncTransform) continue;

                var state = _syncedStates[i];
                if (state.OwnerClientId != localClientId)
                {
                    var pos = Vector3.Lerp(obj.transform.position, state.Position, Time.deltaTime * 15f);
                    var rot = Quaternion.Slerp(obj.transform.rotation, state.Rotation, Time.deltaTime * 15f);
                    obj.transform.SetPositionAndRotation(pos, rot);
                }
            }
        }

        [Rpc(SendTo.Server)]
        private void UpdateTransformRpc(int index, Vector3 position, Quaternion rotation)
        {
            if (index < 0 || index >= _syncedStates.Count) return;

            var state = _syncedStates[index];
            state.Position = position;
            state.Rotation = rotation;
            _syncedStates[index] = state;
        }
    }
}