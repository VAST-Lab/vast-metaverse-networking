using System;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using VastMetaverseTools.Managers;
using Unity.Services.Vivox;

using System.Runtime.InteropServices; // new

namespace VastMetaverseNetworking
{
    public class MetaverseVoiceManager : MonoBehaviour
    {
        public static MetaverseVoiceManager Instance { get; private set; }

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
        private void OnVivoxLoggedIn()
        {
            Debug.Log("[Voice TEST] LOGGED IN EVENT FIRED");
        }

        private void OnVivoxLoggedOut()
        {
            Debug.LogWarning("[Voice TEST] LOGGED OUT EVENT FIRED");
        }

        private float _loginStartTime = -1f; // new
        private bool _loginWarned; // new

#if UNITY_WEBGL && !UNITY_EDITOR
[DllImport("__Internal")]
private static extern void RequestMicrophone(string objName, string methodName);

private TaskCompletionSource<string> _micTcs;

// Called from JavaScript via SendMessage.
public void OnMicResult(string result)
{
    _micTcs?.TrySetResult(result);
}

private Task<string> RequestMicAsync()
{
    _micTcs = new TaskCompletionSource<string>();
    RequestMicrophone(gameObject.name, nameof(OnMicResult));
    return _micTcs.Task;
}
#endif

        public void ToggleMute() => SetMuted(!IsMuted);

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        // Mutes only your mic; you still hear everyone else.
        public void SetMuted(bool muted)
        {
            IsMuted = muted;
            ApplyMute();
            OnMuteChanged?.Invoke(muted);
        }

        private void ApplyMute()
        {
            if (!_isVivoxInitialized) return;
            if (IsMuted) VivoxService.Instance.MuteInputDevice();
            else VivoxService.Instance.UnmuteInputDevice();
        }

        // IMPORTANT FOR WEBGL: Call this method directly from a UI Button OnClick event
        public async Task InitializeAndJoinVoiceAsync(string channelName)
        {
            await LeaveVoiceAsync();
            // Keep the channel dynamic, but make it WebGL-compatible.
#if UNITY_WEBGL && !UNITY_EDITOR
_activeChannelName = System.Text.RegularExpressions.Regex.Replace(
    channelName,
    @"[^a-zA-Z0-9-]",
    "-"
);
#else
_activeChannelName = channelName;
#endif

            Debug.Log("[Voice] Dynamic channel received: " + channelName);
            Debug.Log("[Voice] Channel being used: " + _activeChannelName);

            try
            {

#if UNITY_WEBGL && !UNITY_EDITOR
string micResult = await RequestMicAsync();
Debug.Log("[Voice] 0 mic permission result: " + micResult);
if (micResult != "granted") throw new Exception("Microphone not available: " + micResult);
#endif
                Debug.Log("[Voice] 1 initializing Vivox");

                if (!_isVivoxInitialized)
                {
                    await VivoxService.Instance.InitializeAsync();
                    _isVivoxInitialized = true;

                    VivoxService.Instance.LoggedIn += OnVivoxLoggedIn;
                    VivoxService.Instance.LoggedOut += OnVivoxLoggedOut;
                }
                Debug.Log("[Voice] 2 logging in");

                Debug.Log("[Voice TEST] IsLoggedIn before login: " +
                          VivoxService.Instance.IsLoggedIn);

                if (!VivoxService.Instance.IsLoggedIn)
                {
                    _loginStartTime = Time.realtimeSinceStartup;
                    _loginWarned = false;

#if UNITY_WEBGL && !UNITY_EDITOR

    // WebGL workaround: wait for the LoggedIn event instead
    // of waiting indefinitely for LoginAsync().

    var loginCompletion = new TaskCompletionSource<bool>();

    Action loginHandler = () =>
    {
        Debug.Log("[Voice] WebGL login event received!");
        loginCompletion.TrySetResult(true);
    };

    VivoxService.Instance.LoggedIn += loginHandler;

    try
    {
        Task loginTask = VivoxService.Instance.LoginAsync();

        // Handle the unlikely possibility that login completed
        // before we started waiting.
        if (VivoxService.Instance.IsLoggedIn)
            loginCompletion.TrySetResult(true);

        Task completed = await Task.WhenAny(
            loginTask,
            loginCompletion.Task
        );

        if (completed == loginTask)
        {
            await loginTask;
        }
        else
        {
            Debug.Log("[Voice] WebGL login confirmed by event.");

            // Observe any eventual exception from the original task.
            _ = loginTask.ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Debug.LogException(t.Exception);
            }, TaskScheduler.Default);
        }

        if (!VivoxService.Instance.IsLoggedIn)
            throw new Exception("Login event received but Vivox is not logged in.");
    }
    finally
    {
        VivoxService.Instance.LoggedIn -= loginHandler;
        _loginStartTime = -1f;
    }

#else

                    try
                    {
                        await VivoxService.Instance.LoginAsync();
                    }
                    finally
                    {
                        _loginStartTime = -1f;
                    }

#endif
                }

                Debug.Log("[Voice] 2b login finished");

                Debug.Log("[Voice TEST] IsLoggedIn after login: " +
                          VivoxService.Instance.IsLoggedIn);

                Debug.Log("[Voice TEST] IsLoggedIn after login: " +
                          VivoxService.Instance.IsLoggedIn);
                Debug.Log("[Voice] 3 joining channel " + _activeChannelName);

#if UNITY_WEBGL && !UNITY_EDITOR
    await VivoxService.Instance.JoinGroupChannelAsync(_activeChannelName, ChatCapability.AudioOnly);
#else
                Channel3DProperties props = new Channel3DProperties(
                    _audibleDistance, _conversationalDistance, _audioFadeIntensity, AudioFadeModel.InverseByDistance);
                await VivoxService.Instance.JoinPositionalChannelAsync(_activeChannelName, ChatCapability.AudioOnly, props);
#endif

                Debug.Log("[Voice] 4 joined");
                _isChannelJoined = true;
                ApplyMute();
                Debug.Log("[Voice] 5 done");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MetaverseVoiceManager] Vivox setup failed: {e}");
            }
        }

        // Leaves the voice channel (mic off for others) but stays logged in, so rejoining is quick.
        public async Task LeaveVoiceAsync()
        {
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
        }

        private void Update()
        {
            if (_loginStartTime > 0f && !_loginWarned && Time.realtimeSinceStartup - _loginStartTime > 15f)
            {
                _loginWarned = true;
                Debug.LogWarning("[Voice] Login has been pending for 15s");
            }

            if (!_isChannelJoined) return;

            if (Keyboard.current != null && Keyboard.current[_muteKey].wasPressedThisFrame && !IsTyping())
            {
                ToggleMute();
            }

#if !UNITY_WEBGL || UNITY_EDITOR
            // 3D positional updates are disabled on WebGL because it uses a 2D channel
            if (MetaverseManager.LocalPlayer != null && Time.time >= _nextPositionUpdate)
            {
                _nextPositionUpdate = Time.time + _positionUpdateInterval;
                Vector3 playerPosition = MetaverseManager.LocalPlayer.transform.position;
                Camera cam = Camera.main;
                if (cam != null)
                {
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
        }
    }
}
