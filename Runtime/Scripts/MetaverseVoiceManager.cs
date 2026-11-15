using System;
using System.Threading.Tasks;
using UnityEngine;
using VastMetaverseTools.Managers;
#if !UNITY_WEBGL || UNITY_EDITOR
using Unity.Services.Vivox;
#endif

namespace VastMetaverseNetworking
{
    public class MetaverseVoiceManager : MonoBehaviour
    {
        [SerializeField] private int _audibleDistance = 32;
        [SerializeField] private int _conversationalDistance = 2;
        [SerializeField] private float _audioFadeIntensity = 1.0f;
        [SerializeField] private float _positionUpdateInterval = 0.2f;

        private string _activeChannelName;
        private bool _isChannelJoined;
        private bool _isVivoxInitialized;
        private float _nextPositionUpdate;

        public async Task InitializeAndJoinVoiceAsync(string channelName)
        {
            await LeaveVoiceAsync();
            _activeChannelName = channelName;
#if !UNITY_WEBGL || UNITY_EDITOR
            try
            {
                if (!_isVivoxInitialized)
                {
                    await VivoxService.Instance.InitializeAsync();
                    _isVivoxInitialized = true;
                }
                if (!VivoxService.Instance.IsLoggedIn)
                {
                    await VivoxService.Instance.LoginAsync();
                }

                Channel3DProperties props = new Channel3DProperties(
                    _audibleDistance,
                    _conversationalDistance,
                    _audioFadeIntensity,
                    AudioFadeModel.InverseByDistance);

                await VivoxService.Instance.JoinPositionalChannelAsync(
                    _activeChannelName,
                    ChatCapability.AudioOnly,
                    props);

                _isChannelJoined = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MetaverseVoiceManager] Vivox setup failed: {e.Message}");
            }
#else
			await Task.CompletedTask;
#endif
        }

        // Leaves the voice channel (mic off for others) but stays logged in, so rejoining is quick.
        public async Task LeaveVoiceAsync()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            if (!_isChannelJoined) return;
            _isChannelJoined = false;
            try
            {
                await VivoxService.Instance.LeaveAllChannelsAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MetaverseVoiceManager] Leaving voice failed: {e.Message}");
            }
#else
			await Task.CompletedTask;
#endif
        }

        private void Update()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            if (!_isChannelJoined || MetaverseManager.LocalPlayer == null) return;

            if (Time.time >= _nextPositionUpdate)
            {
                _nextPositionUpdate = Time.time + _positionUpdateInterval;
                VivoxService.Instance.Set3DPosition(
                    MetaverseManager.LocalPlayer.gameObject,
                    _activeChannelName);
            }
#endif
        }

        private async void OnDestroy()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            // Log out even after LeaveVoiceAsync, which leaves the channel but stays logged in.
            if (_isVivoxInitialized && VivoxService.Instance != null)
            {
                try
                {
                    if (_isChannelJoined) await VivoxService.Instance.LeaveAllChannelsAsync();
                    if (VivoxService.Instance.IsLoggedIn) await VivoxService.Instance.LogoutAsync();
                }
                catch
                {
                }
                _isChannelJoined = false;
            }
#else
			await Task.CompletedTask;
#endif
        }
    }
}