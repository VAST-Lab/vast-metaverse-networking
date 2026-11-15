using System;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
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
        [Tooltip("Press this key to mute or unmute your mic while in voice chat.")]
        [SerializeField] private Key _muteKey = Key.M;

        private string _activeChannelName;
        private bool _isChannelJoined;
        private bool _isVivoxInitialized;
        private float _nextPositionUpdate;

        public bool IsInVoice => _isChannelJoined;
        public bool IsMuted { get; private set; }
        public event Action<bool> OnMuteChanged;

        public void ToggleMute() => SetMuted(!IsMuted);

        // Mutes only your mic; you still hear everyone else.
        public void SetMuted(bool muted)
        {
            IsMuted = muted;
            ApplyMute();
            OnMuteChanged?.Invoke(muted);
        }

        private void ApplyMute()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            if (!_isVivoxInitialized) return;
            if (IsMuted) VivoxService.Instance.MuteInputDevice();
            else VivoxService.Instance.UnmuteInputDevice();
#endif
        }

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
                ApplyMute(); // Stay muted across rejoins.
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
            if (!_isChannelJoined) return;

            if (Keyboard.current != null && Keyboard.current[_muteKey].wasPressedThisFrame && !IsTyping())
            {
                ToggleMute();
            }

            if (MetaverseManager.LocalPlayer != null && Time.time >= _nextPositionUpdate)
            {
                _nextPositionUpdate = Time.time + _positionUpdateInterval;
                Vector3 playerPosition = MetaverseManager.LocalPlayer.transform.position;
                Camera cam = Camera.main;
                if (cam != null)
                {
                    // Hear from where your character stands, but facing where the camera looks,
                    // so voices pan left and right as you turn the camera.
                    VivoxService.Instance.Set3DPosition(
                        playerPosition,
                        playerPosition,
                        cam.transform.forward,
                        cam.transform.up,
                        _activeChannelName);
                }
                else
                {
                    VivoxService.Instance.Set3DPosition(
                        MetaverseManager.LocalPlayer.gameObject,
                        _activeChannelName);
                }
            }
#endif
        }

        // So typing an "m" in a text box doesn't toggle the mic.
        private static bool IsTyping()
        {
            GameObject selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return selected != null && (selected.GetComponent<TMP_InputField>() != null || selected.GetComponent<InputField>() != null);
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