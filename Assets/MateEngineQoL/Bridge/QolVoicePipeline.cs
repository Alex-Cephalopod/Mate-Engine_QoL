using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MateEngineQoL.AI;
using MateEngineQoL.Settings;
using MateEngineQoL.Voice;
using UnityEngine;
using Debug = UnityEngine.Debug;
using LipSyncAnalyzer = uLipSync.uLipSync;

namespace MateEngineQoL.Bridge
{
    /// <summary>
    /// Speaks chat replies. Reply text from <see cref="QolChatRouter"/> is cut into sentences, filtered
    /// (<see cref="SpeechFilter"/>), sent to the TTS provider one sentence at a time, resampled to the output rate and
    /// queued in a ring buffer. This object's AudioSource plays a silent loop; <see cref="OnAudioFilterRead"/> replaces it
    /// with the buffered speech, and uLipSync (the next component) analyses the result for <see cref="QolLipSync"/>.
    /// Keeps the avatar's isTalking on until the buffer drains. A new message, a character switch or grabbing the
    /// avatar stops speech at once.
    /// </summary>
    public sealed class QolVoicePipeline : MonoBehaviour
    {
        const float BufferSeconds = 30f;
        /// <summary>Speech counts as over this long after the buffer drains (covers the audio driver's own buffer).</summary>
        const float TailSeconds = 0.25f;

        static readonly int IsTalkingHash = Animator.StringToHash("isTalking");

        public static QolVoicePipeline Instance { get; private set; }

        /// <summary>True when a voice provider is configured and ready.</summary>
        public static bool IsEnabled => Instance != null && Instance.Provider != null;

        /// <summary>True while speech is queued, being fetched or playing.</summary>
        public static bool IsSpeaking => Instance != null && Instance._speaking;

        /// <summary>The analyser on the voice output; <see cref="QolLipSync"/> reads its result.</summary>
        public LipSyncAnalyzer LipSync { get; private set; }

        /// <summary>The last speech error (shown on the settings page), or null.</summary>
        public string LastError { get; private set; }

        /// <summary>Milliseconds of silence between a sentence being ready and its first audio (latest sentence that had to wait).</summary>
        public long LastLatencyMs { get; private set; } = -1;

        /// <summary>Counts sentences that started playing and speech errors, so the settings test can wait for either.</summary>
        public int SentencesStarted { get; private set; }
        public int ErrorCount { get; private set; }

        public event Action StatusChanged;

        ITtsProvider Provider
        {
            get
            {
                QolServices.EnsureInitialized();
                return QolServices.Registry.Tts;
            }
        }

        AudioSource _source;
        PcmRingBuffer _buffer;
        int _outputRate;
        volatile float _volume = 1f;

        SentenceSplitter _splitter = new SentenceSplitter();
        SpeechFilter _filter = new SpeechFilter(true);
        bool _inReply;
        readonly Queue<(string Text, long ReadyAt)> _queue = new Queue<(string, long)>();
        bool _working;
        CancellationTokenSource _cts;
        readonly Stopwatch _clock = Stopwatch.StartNew();

        bool _speaking;
        float _quietSince = -1f;
        long _drainedAt;
        float _nextAvatarSearch;
        Animator _talkAnimator;
        AvatarAnimatorController _avatar;
        bool _wasDragging, _wasPlaying;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            try
            {
                var go = new GameObject("QoL Voice");
                go.SetActive(false); // add everything before Awake/OnEnable run
                DontDestroyOnLoad(go);
                go.AddComponent<AudioSource>();
                go.AddComponent<QolVoicePipeline>();
                // After the pipeline, so its OnAudioFilterRead sees the speech the pipeline writes.
                var lipSync = go.AddComponent<LipSyncAnalyzer>();
                lipSync.profile = Resources.Load<uLipSync.Profile>(QolLipSync.ProfileResource);
                if (lipSync.profile == null) Debug.LogWarning("[QoL Voice] Lip sync profile missing; the mouth won't move.");
                go.SetActive(true);

                QolEvents.AvatarLoaded += QolLipSync.Attach;
                foreach (AvatarAnimatorController a in FindObjectsByType<AvatarAnimatorController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    QolLipSync.Attach(a.gameObject);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[QoL Voice] Not installed: " + e.Message);
            }
        }

        void Awake()
        {
            Instance = this;
            LipSync = GetComponent<LipSyncAnalyzer>();
            _source = GetComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = true;
            _source.spatialBlend = 0f;
            _source.bypassListenerEffects = true;
            _source.bypassReverbZones = true;
            SetupOutput();
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;

            QolChatRouter.ReplyDelta += OnReplyDelta;
            QolChatRouter.ReplyCompleted += OnReplyCompleted;
            QolChatRouter.Cancelled += Stop;
            QolEvents.AvatarLoaded += OnAvatarLoaded;
            QolServices.EnsureInitialized();
            QolServices.Registry.Changed += OnSettingsChanged;
            OnSettingsChanged();
        }

        void OnDestroy()
        {
            Stop();
            AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;
            QolChatRouter.ReplyDelta -= OnReplyDelta;
            QolChatRouter.ReplyCompleted -= OnReplyCompleted;
            QolChatRouter.Cancelled -= Stop;
            QolEvents.AvatarLoaded -= OnAvatarLoaded;
            if (QolServices.Registry != null) QolServices.Registry.Changed -= OnSettingsChanged;
            if (Instance == this) Instance = null;
        }

        /// <summary>A silent clip keeps the AudioSource running; speech is written over it in OnAudioFilterRead.</summary>
        void SetupOutput()
        {
            _outputRate = AudioSettings.outputSampleRate;
            _buffer = new PcmRingBuffer(Mathf.CeilToInt(_outputRate * BufferSeconds));
            AudioClip carrier = AudioClip.Create("QoL Voice Carrier", _outputRate, 1, _outputRate, false);
            if (_source.clip != null) Destroy(_source.clip);
            _source.clip = carrier;
            if (Provider != null) _source.Play();
        }

        void OnAudioConfigurationChanged(bool deviceWasChanged)
        {
            Stop();
            SetupOutput();
        }

        void OnSettingsChanged()
        {
            TtsSettings tts = QolServices.Settings.Tts;
            _volume = Mathf.Clamp01(tts.Volume);
            if (Provider == null)
            {
                Stop();
                if (_source.isPlaying) _source.Stop();
            }
            else if (!_source.isPlaying)
            {
                _source.Play();
            }
            StatusChanged?.Invoke();
        }

        // ---- input -------------------------------------------------------------------

        void OnReplyDelta(string delta)
        {
            if (Provider == null) return;
            if (!_inReply)
            {
                _inReply = true;
                _splitter.Reset();
                _filter = new SpeechFilter(QolServices.Settings.Tts.SkipActions);
            }
            foreach (string sentence in _splitter.Push(delta)) Enqueue(sentence);
        }

        void OnReplyCompleted(string reply)
        {
            if (!_inReply) return;
            _inReply = false;
            string rest = _splitter.Flush();
            if (rest != null) Enqueue(rest);
        }

        /// <summary>Speaks text outside a chat reply (the settings page's test). Stops anything already playing.</summary>
        public void Speak(string text)
        {
            Stop();
            if (Provider == null) return;
            var splitter = new SentenceSplitter();
            _filter = new SpeechFilter(QolServices.Settings.Tts.SkipActions);
            foreach (string sentence in splitter.Push(text)) Enqueue(sentence);
            string rest = splitter.Flush();
            if (rest != null) Enqueue(rest);
        }

        /// <summary>Drops queued and playing speech and cancels the TTS request in flight.</summary>
        public void Stop()
        {
            // ChatBot owns isTalking while a reply runs (a new one is starting, or the cancelled one's done callback
            // clears it). Otherwise, e.g. the settings test line, nobody else would turn it off.
            if (_speaking && !QolChatRouter.IsReplying && _talkAnimator != null) _talkAnimator.SetBool(IsTalkingHash, false);
            _inReply = false;
            _splitter.Reset();
            _queue.Clear();
            // Not disposed: the worker may still be inside a request linked to this token.
            _cts?.Cancel();
            _cts = null;
            _buffer?.Clear();
            _speaking = false;
            _quietSince = -1f;
        }

        void Enqueue(string sentence)
        {
            string text = _filter.Filter(sentence);
            if (text == null) return;
            _queue.Enqueue((text, _clock.ElapsedMilliseconds));
            _speaking = true;
            if (!_working) _ = RunQueueAsync();
        }

        // ---- TTS worker (main thread; awaits resume on Unity's SynchronizationContext) -------------

        async Task RunQueueAsync()
        {
            _working = true;
            if (_cts == null) _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;
            try
            {
                while (_queue.Count > 0 && !ct.IsCancellationRequested)
                {
                    (string text, long readyAt) = _queue.Dequeue();
                    ITtsProvider tts = Provider;
                    if (tts == null) break;
                    if (!await SpeakSentenceAsync(tts, text, readyAt, ct)) break;
                }
            }
            finally
            {
                _working = false;
                // Stop() then new text while this worker was still winding down: start a fresh one.
                if (ct.IsCancellationRequested && _queue.Count > 0) _ = RunQueueAsync();
            }
        }

        /// <returns>False when speech should stop (cancelled or failed).</returns>
        async Task<bool> SpeakSentenceAsync(ITtsProvider tts, string text, long readyAt, CancellationToken ct)
        {
            TtsSettings settings = QolServices.Settings.Tts;
            var voice = new VoiceSettings { Model = settings.Model, Voice = settings.Voice, Speed = settings.Speed };
            var resampler = new Resampler(tts.SampleRate, _outputRate);
            bool first = true;
            try
            {
                await foreach (float[] chunk in tts.StreamSpeechAsync(text, voice, ct))
                {
                    float[] samples = resampler.Process(chunk);
                    if (first && samples.Length > 0)
                    {
                        first = false;
                        // Only a wait the listener hears: a sentence fetched while the previous one plays costs nothing.
                        if (_buffer.Count == 0) LastLatencyMs = _clock.ElapsedMilliseconds - Math.Max(readyAt, _drainedAt);
                        SentencesStarted++;
                        if (LastError != null) { LastError = null; StatusChanged?.Invoke(); }
                    }
                    await WriteAsync(samples, ct);
                }
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                Fail("The voice server stopped responding.");
                return false;
            }
            catch (ProviderException e)
            {
                Fail(e.Message);
                return false;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Fail("Unexpected error, see Player.log.");
                return false;
            }
        }

        void Fail(string message)
        {
            Debug.LogWarning("[QoL Voice] " + message);
            LastError = message;
            ErrorCount++;
            _queue.Clear(); // a down server would otherwise time out once per sentence
            StatusChanged?.Invoke();
        }

        async Task WriteAsync(float[] samples, CancellationToken ct)
        {
            int offset = 0;
            while (offset < samples.Length)
            {
                ct.ThrowIfCancellationRequested();
                int n = _buffer.Write(samples, offset, samples.Length - offset);
                offset += n;
                if (n == 0) await Task.Delay(50, ct);
            }
        }

        // ---- output (audio thread) ---------------------------------------------------

        float[] _mono = new float[0];

        void OnAudioFilterRead(float[] data, int channels)
        {
            PcmRingBuffer buffer = _buffer;
            int frames = data.Length / channels;
            if (_mono.Length < frames) _mono = new float[frames];
            int n = buffer != null ? buffer.Read(_mono, 0, frames) : 0;
            float volume = _volume;
            int i = 0;
            for (int f = 0; f < frames; f++)
            {
                float s = f < n ? _mono[f] * volume : 0f;
                for (int c = 0; c < channels; c++) data[i++] = s;
            }
        }

        // ---- talking state and interrupts -----------------------------------------------

        void OnAvatarLoaded(GameObject model)
        {
            _avatar = null;
            _talkAnimator = null;
            _nextAvatarSearch = 0f;
        }

        void Update()
        {
            _volume = Mathf.Clamp01(QolServices.Settings.Tts.Volume); // the settings slider changes it live

            if ((_avatar == null || !_avatar.isActiveAndEnabled) && Time.unscaledTime >= _nextAvatarSearch)
            {
                _nextAvatarSearch = Time.unscaledTime + 1f;
                _avatar = FindFirstObjectByType<AvatarAnimatorController>();
                _talkAnimator = _avatar != null ? _avatar.animator : null;
            }

            bool busy = _queue.Count > 0 || _working || (_buffer != null && _buffer.Count > 0);
            if (_buffer != null && _buffer.Count == 0 && _wasPlaying) _drainedAt = _clock.ElapsedMilliseconds;
            _wasPlaying = _buffer != null && _buffer.Count > 0;
            if (busy) _quietSince = -1f;
            else if (_speaking && _quietSince < 0f) _quietSince = Time.unscaledTime;

            if (_speaking && !busy && Time.unscaledTime - _quietSince > TailSeconds)
            {
                _speaking = false;
                _quietSince = -1f;
                // The reply finished before the speech did, so ChatBot left isTalking on for us.
                if (!QolChatRouter.IsReplying && _talkAnimator != null) _talkAnimator.SetBool(IsTalkingHash, false);
            }
            else if (_speaking && _talkAnimator != null)
            {
                _talkAnimator.SetBool(IsTalkingHash, true);
            }

            // Grabbing the avatar while it speaks interrupts it, like sending a new message.
            bool dragging = _avatar != null && _avatar.isDragging;
            if (dragging && !_wasDragging && _speaking) Interrupt();
            _wasDragging = dragging;
        }

        void Interrupt()
        {
            if (CharacterManager.Instance != null) CharacterManager.Instance.CancelReply();
            else QolChatRouter.Cancel();
            Stop();
            if (_talkAnimator != null) _talkAnimator.SetBool(IsTalkingHash, false);
        }
    }
}
