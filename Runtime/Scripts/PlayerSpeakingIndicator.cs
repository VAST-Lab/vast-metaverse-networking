using System;
using System.Text;
using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Unity.Services.Vivox;

namespace VastMetaverseNetworking
{
    // Floating tag over a player's head: their name in a white pill, with a mic bubble beside it
    // while they're in voice chat (gray when quiet, green while talking, red and crossed out when muted).
    // Each owner shares its own name and Vivox speech state, so every client sees the same tag.
    [RequireComponent(typeof(NetworkObject))]
    public class PlayerSpeakingIndicator : NetworkBehaviour
    {
        public const string PlayerNamePrefKey = "PlayerName";
        private const int MaxNameLength = 20;
        private const int MaxNameBytes = 29; // What fits in a FixedString32Bytes.

        [Tooltip("Optional. Leave empty to use the built-in white bubble.")]
        [SerializeField] private Sprite _bubbleSprite;
        [Tooltip("Optional. Leave empty to use the built-in mic icon.")]
        [SerializeField] private Sprite _micSprite;
        [Tooltip("Scales the whole tag (name and mic) at once.")]
        [SerializeField] private float _tagScale = 0.65f;
        [SerializeField] private float _iconSize = 0.35f;
        [Tooltip("How far above the avatar's head bone the tag floats.")]
        [SerializeField] private float _headOffset = 0.45f;
        [Tooltip("Used when the avatar has no head bone.")]
        [SerializeField] private float _fallbackHeight = 2.1f;
        [SerializeField] private bool _showWhenSilent = true;
        [SerializeField] private Color _silentMicColor = new Color(0.55f, 0.55f, 0.55f);
        [SerializeField] private Color _speakingMicColor = new Color(0.2f, 0.75f, 0.35f);
        [SerializeField] private Color _mutedMicColor = new Color(0.85f, 0.2f, 0.2f);
        [SerializeField] private float _pollInterval = 0.1f;

        [Header("Name")]
        [Tooltip("Optional. Leave empty to use TextMeshPro's default font.")]
        [SerializeField] private TMP_FontAsset _nameFont;
        [SerializeField] private float _nameFontSize = 2f;
        [SerializeField] private float _nameTagHeight = 0.32f;
        [SerializeField] private float _nameTagPadding = 0.2f;
        [SerializeField] private float _micGap = 0.05f;
        [SerializeField] private Color _nameTagColor = new Color(1f, 1f, 1f, 0.9f);
        [SerializeField] private Color _nameTextColor = new Color(0.15f, 0.15f, 0.15f);

        private readonly NetworkVariable<bool> _netInVoice = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<bool> _netSpeaking = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<bool> _netMuted = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<FixedString32Bytes> _netName = new NetworkVariable<FixedString32Bytes>(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private static Sprite _defaultBubble;
        private static Sprite _defaultMic;
        private static Sprite _defaultMutedMic;
        private static Sprite _defaultPill;

        private Transform _tag;
        private Transform _micBubble;
        private SpriteRenderer _micRenderer;
        private Sprite _micIcon;
        private SpriteRenderer _nameBackground;
        private TextMeshPro _nameText;
        private Transform _head;
        private string _localName;
        private float _nextPoll;
        private float _nextHeadSearch;

        private MetaverseVoiceManager _voiceManager;

        public override void OnNetworkSpawn()
        {
            CreateTag();
            _netName.OnValueChanged += OnNameChanged;
            if (IsOwner) ShareLocalName();
            ShowName(_netName.Value.ToString());
        }

        public override void OnNetworkDespawn()
        {
            _netName.OnValueChanged -= OnNameChanged;
            if (_tag != null) Destroy(_tag.gameObject);
        }

        private void Update()
        {
            if (!IsSpawned || !IsOwner || Time.time < _nextPoll) return;
            _nextPoll = Time.time + _pollInterval;

            ReadOwnVoiceState(out bool inVoice, out bool speaking, out bool muted);
            if (_netInVoice.Value != inVoice) _netInVoice.Value = inVoice;
            if (_netSpeaking.Value != speaking) _netSpeaking.Value = speaking;
            if (_netMuted.Value != muted) _netMuted.Value = muted;

            // Picks up a name saved after joining, e.g. from a sign-in prompt.
            if (PlayerPrefs.GetString(PlayerNamePrefKey, _localName) != _localName) ShareLocalName();
        }

        private void LateUpdate()
        {
            if (_tag == null) return;

            _tag.position = GetHeadPosition();
            Camera cam = Camera.main;
            if (cam != null) _tag.rotation = cam.transform.rotation;
            _tag.localScale = Vector3.one * _tagScale;

            bool speaking = _netSpeaking.Value;
            bool micVisible = speaking || (_showWhenSilent && _netInVoice.Value);
            if (_micBubble.gameObject.activeSelf != micVisible) _micBubble.gameObject.SetActive(micVisible);
            if (!micVisible) return;

            bool muted = _netMuted.Value;
            _micRenderer.sprite = muted ? GetDefaultMutedMic() : _micIcon;
            if (muted) speaking = false;

            float pulse = speaking ? 1f + 0.1f * Mathf.Sin(Time.time * 12f) : 1f;
            _micBubble.localScale = Vector3.one * (_iconSize * pulse);
            _micRenderer.color = muted ? _mutedMicColor : speaking ? _speakingMicColor : _silentMicColor;
        }

        private void ShareLocalName()
        {
            _localName = GetLocalPlayerName();
            _netName.Value = new FixedString32Bytes(_localName);
        }

        private static string GetLocalPlayerName()
        {
            string playerName = PlayerPrefs.GetString(PlayerNamePrefKey, string.Empty).Trim();
            if (string.IsNullOrEmpty(playerName))
            {
                playerName = "Guest " + UnityEngine.Random.Range(1000, 10000);
                PlayerPrefs.SetString(PlayerNamePrefKey, playerName);
                PlayerPrefs.Save();
            }

            if (playerName.Length > MaxNameLength) playerName = playerName.Substring(0, MaxNameLength);
            while (Encoding.UTF8.GetByteCount(playerName) > MaxNameBytes) playerName = playerName.Substring(0, playerName.Length - 1);
            return playerName;
        }

        private void OnNameChanged(FixedString32Bytes previous, FixedString32Bytes current)
        {
            ShowName(current.ToString());
        }

        // Sizes the white pill to the name and keeps the mic bubble just to its left.
        private void ShowName(string playerName)
        {
            if (_nameText == null) return;

            bool hasName = !string.IsNullOrEmpty(playerName);
            _nameText.gameObject.SetActive(hasName);
            _nameBackground.gameObject.SetActive(hasName);
            if (!hasName)
            {
                _micBubble.localPosition = Vector3.zero;
                return;
            }

            _nameText.text = playerName;
            float tagWidth = _nameText.GetPreferredValues(playerName).x + _nameTagPadding;
            _nameBackground.size = new Vector2(tagWidth, _nameTagHeight);
            _micBubble.localPosition = new Vector3(-(tagWidth * 0.5f + _micGap + _iconSize * 0.5f), 0f, 0f);
        }

        private static void ReadOwnVoiceState(out bool inVoice, out bool speaking, out bool muted)
        {
            inVoice = false;
            speaking = false;
            muted = false;

            if (MetaverseVoiceManager.Instance != null)
            {
                inVoice = MetaverseVoiceManager.Instance.IsInVoice;
                muted = MetaverseVoiceManager.Instance.IsMuted;
            }

            try
            {
                if (VivoxService.Instance != null && VivoxService.Instance.IsLoggedIn)
                {
                    muted = VivoxService.Instance.IsInputDeviceMuted;

#if !UNITY_WEBGL || UNITY_EDITOR
                    if (VivoxService.Instance.ActiveChannels != null && VivoxService.Instance.ActiveChannels.Count > 0)
                    {
                        inVoice = true;
                        foreach (var channel in VivoxService.Instance.ActiveChannels.Values)
                        {
                            foreach (var participant in channel)
                            {
                                if (participant.IsSelf)
                                {
                                    speaking = participant.SpeechDetected;
                                    break;
                                }
                            }
                        }
                    }
#endif
                }
            }
            catch
            {
            }
        }

        private void CreateTag()
        {
            if (_tag != null) return;

            _tag = new GameObject("PlayerTag").transform;
            _tag.SetParent(transform, false);

            _micBubble = new GameObject("MicBubble").transform;
            _micBubble.SetParent(_tag, false);
            _micBubble.localScale = Vector3.one * _iconSize;
            SpriteRenderer bubble = AddSprite(_micBubble.gameObject, _bubbleSprite != null ? _bubbleSprite : GetDefaultBubble(), 0);

            var mic = new GameObject("Mic");
            mic.transform.SetParent(_micBubble, false);
            _micIcon = _micSprite != null ? _micSprite : GetDefaultMic();
            _micRenderer = AddSprite(mic, _micIcon, bubble.sortingOrder + 1);
            _micBubble.gameObject.SetActive(false);

            var background = new GameObject("NameBackground");
            background.transform.SetParent(_tag, false);
            _nameBackground = AddSprite(background, GetDefaultPill(), 0);
            _nameBackground.drawMode = SpriteDrawMode.Sliced;
            _nameBackground.color = _nameTagColor;

            var text = new GameObject("NameText");
            text.transform.SetParent(_tag, false);
            text.transform.localPosition = new Vector3(0f, 0f, -0.01f); // Just in front of the pill.
            _nameText = text.AddComponent<TextMeshPro>();
            if (_nameFont != null) _nameText.font = _nameFont;
            _nameText.fontSize = _nameFontSize;
            _nameText.color = _nameTextColor;
            _nameText.alignment = TextAlignmentOptions.Center;
            _nameText.richText = false; // Show names exactly as typed, so tags like <size=99> can't restyle them.
            _nameText.rectTransform.sizeDelta = new Vector2(4f, _nameTagHeight);
            _nameText.sortingOrder = _nameBackground.sortingOrder + 1;
        }

        private static SpriteRenderer AddSprite(GameObject go, Sprite sprite, int sortingOrder)
        {
            SpriteRenderer spriteRenderer = go.AddComponent<SpriteRenderer>();
            spriteRenderer.sprite = sprite;
            spriteRenderer.sortingOrder = sortingOrder;
            spriteRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return spriteRenderer;
        }

        // Avatars can be swapped at runtime, so look the head bone up again whenever it disappears.
        private Vector3 GetHeadPosition()
        {
            if (_head == null && Time.time >= _nextHeadSearch)
            {
                _nextHeadSearch = Time.time + 0.5f;
                foreach (Animator animator in GetComponentsInChildren<Animator>())
                {
                    if (!animator.isHuman || animator.avatar == null) continue;
                    _head = animator.GetBoneTransform(HumanBodyBones.Head);
                    if (_head != null) break;
                }
            }

            return _head != null
                ? _head.position + Vector3.up * _headOffset
                : transform.position + Vector3.up * _fallbackHeight;
        }

        private static Sprite GetDefaultBubble()
        {
            if (_defaultBubble == null) _defaultBubble = CreateSprite(BubbleDistance, 128, 128f, Vector4.zero);
            return _defaultBubble;
        }

        private static Sprite GetDefaultMic()
        {
            if (_defaultMic == null) _defaultMic = CreateSprite(MicDistance, 128, 128f, Vector4.zero);
            return _defaultMic;
        }

        private static Sprite GetDefaultMutedMic()
        {
            if (_defaultMutedMic == null) _defaultMutedMic = CreateSprite(MutedMicDistance, 128, 128f, Vector4.zero);
            return _defaultMutedMic;
        }

        // A 64px pill whose round ends become the 9-slice borders, so it stretches to any name length.
        private static Sprite GetDefaultPill()
        {
            if (_defaultPill == null) _defaultPill = CreateSprite(PillDistance, 64, 200f, new Vector4(31f, 31f, 31f, 31f));
            return _defaultPill;
        }

        // Draws a white shape from a signed distance function (negative = inside), with smooth edges.
        private static Sprite CreateSprite(Func<Vector2, float> distance, int size, float pixelsPerUnit, Vector4 border)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2((x + 0.5f) / size, (y + 0.5f) / size);
                    float alpha = Mathf.Clamp01(0.5f - distance(p) * size);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), pixelsPerUnit,
                0, SpriteMeshType.FullRect, border);
        }

        private static float BubbleDistance(Vector2 p)
        {
            return (p - new Vector2(0.5f, 0.5f)).magnitude - 0.46f;
        }

        private static float PillDistance(Vector2 p)
        {
            return (p - new Vector2(0.5f, 0.5f)).magnitude - 0.49f;
        }

        private static float MicDistance(Vector2 p)
        {
            float head = SegmentDistance(p, new Vector2(0.5f, 0.55f), new Vector2(0.5f, 0.68f)) - 0.1f;

            // Lower half of a ring around the mic head.
            float ring = Mathf.Abs((p - new Vector2(0.5f, 0.56f)).magnitude - 0.17f) - 0.028f;
            ring = Mathf.Max(ring, p.y - 0.56f);

            float stem = BoxDistance(p, new Vector2(0.5f, 0.33f), new Vector2(0.028f, 0.06f));
            float foot = BoxDistance(p, new Vector2(0.5f, 0.27f), new Vector2(0.1f, 0.028f));
            return Mathf.Min(Mathf.Min(head, ring), Mathf.Min(stem, foot));
        }

        // The mic with a diagonal slash through it, plus a small gap around the slash so it reads clearly.
        private static float MutedMicDistance(Vector2 p)
        {
            float slash = SegmentDistance(p, new Vector2(0.27f, 0.77f), new Vector2(0.73f, 0.23f));
            float micWithGap = Mathf.Max(MicDistance(p), 0.065f - slash);
            return Mathf.Min(micWithGap, slash - 0.032f);
        }

        private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return (p - (a + ab * t)).magnitude;
        }

        private static float BoxDistance(Vector2 p, Vector2 center, Vector2 halfSize)
        {
            Vector2 d = new Vector2(Mathf.Abs(p.x - center.x), Mathf.Abs(p.y - center.y)) - halfSize;
            Vector2 outside = new Vector2(Mathf.Max(d.x, 0f), Mathf.Max(d.y, 0f));
            return outside.magnitude + Mathf.Min(Mathf.Max(d.x, d.y), 0f);
        }
    }
}
