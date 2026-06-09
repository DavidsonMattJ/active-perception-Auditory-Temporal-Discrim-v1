using System.Collections;
using UnityEngine;

public class MakeAuditoryStimulus : MonoBehaviour
{
    /// <summary>
    /// Duration discrimination task — ratio-based timing.
    ///
    /// Pre-walk: plays the standard tone standardRepetitions times so the
    /// participant learns the reference duration.
    ///
    /// During the walk: plays ONE comparison tone whose duration differs from the
    /// standard by a staircase-controlled RATIO (Weber fraction):
    ///   longer  = standard × (1 + ratio)
    ///   shorter = standard × (1 − ratio)
    ///
    /// Using a ratio rather than a fixed ms delta respects Weber's Law — the
    /// same proportional change is equally detectable regardless of the standard
    /// duration.  Two independent ratios are tracked (shorter / longer).
    ///
    /// Public API:
    ///   PlayStandardSequence()       → coroutine: plays reference tones before walk
    ///   RunComparisonResponse()      → coroutine: plays comparison + waits for 2AFC
    ///   StopAllAudio()               → interrupt any playing audio
    ///   SetRatio(ratio, isShorter)   → update the appropriate staircase ratio
    /// </summary>

    // ──────────────────────────────────────────────────────────────────
    //  Inspector parameters
    // ──────────────────────────────────────────────────────────────────

    [Header("Tone")]
    [SerializeField] private float toneFrequencyHz;
    public float standardDurationMs;           // reference tone duration (ms) — read by AdaptiveStaircase
    [Range(0f, 1f)]
    [SerializeField] private float toneAmplitude;
    [SerializeField] private float rampDurationMs;

    [Header("Standard Sequence (pre-walk)")]
    [SerializeField] private int standardRepetitions;
    [SerializeField] private float standardGapMs;   // silent gap between standard tones (ms)

    [Header("Comparison Tone — Ratio (Weber fraction)")]
    [Tooltip("Starting ratio, e.g. 0.25 = comparison is 25% longer/shorter than standard")]
    public float initialRatio;                 // read by AdaptiveStaircase
    [Tooltip("Minimum ratio the staircase can reach")]
    public float minRatio;                     // read by AdaptiveStaircase
    [Tooltip("Maximum ratio the staircase can reach (should be < 1 for shorter comparisons)")]
    public float maxRatio;                     // read by AdaptiveStaircase
    [SerializeField] private float detectionWindowSec;

    // ──────────────────────────────────────────────────────────────────
    //  Public trial state
    // ──────────────────────────────────────────────────────────────────

    public struct TrialState
    {
        public float toneFrequencyHz;
        public float standardDurationMs;
        public float currentRatio;            // staircase-controlled Weber ratio (shared for shorter and longer)
        public bool isShorter;                // this trial: comparison is shorter than standard?
        public float comparisonDurationMs;    // actual comparison duration played this trial
        public float ratio;                   // ratio used this trial (for logging)
        public float changeOnsetTime;         // trial-relative time when comparison tone was played
        public int changeCount;               // comparisons presented this trial (0 before, 1 after)
    }

    public TrialState trialState;

    // ──────────────────────────────────────────────────────────────────
    //  Private state
    // ──────────────────────────────────────────────────────────────────

    private AudioSource audioSource;
    private AudioClip standardClip;
    private const int sampleRate = 44100;

    [SerializeField] GameObject scriptHolder;
    experimentParameters experimentParameters;

    // ──────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ──────────────────────────────────────────────────────────────────

    void Awake()
    {
        // Ratio fields must be set in Awake so they are readable by AdaptiveStaircase.Start()
        // (script execution order is not guaranteed between Start() calls).
        initialRatio = 0.55f;   // 55% change to start
        minRatio     = 0.01f;   // 1% minimum (very fine discrimination)
        maxRatio     = 0.90f;   // 90% maximum (keeps shorter tone > 0 ms)
    }

    void Start()
    {
        experimentParameters = scriptHolder.GetComponent<experimentParameters>();

        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
            audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f;

        // Set defaults
        toneFrequencyHz    = 500f;
        standardDurationMs = 100f;
        toneAmplitude      = 0.8f;
        rampDurationMs     = 15f;
        standardRepetitions = 5;
        standardGapMs      = 300f;
        detectionWindowSec = experimentParameters.responseWindow;

        // Pre-generate standard clip (reused every trial)
        standardClip = GenerateToneClip(standardDurationMs / 1000f, toneFrequencyHz, toneAmplitude);

        // Initialise state
        trialState.toneFrequencyHz    = toneFrequencyHz;
        trialState.standardDurationMs = standardDurationMs;
        trialState.currentRatio       = initialRatio;
        trialState.changeCount        = 0;

        Debug.Log($"MakeAuditoryStimulus: standard={standardDurationMs}ms @ {toneFrequencyHz}Hz, " +
                  $"initialRatio={initialRatio:P0}, ramp={rampDurationMs}ms");
    }

    // ──────────────────────────────────────────────────────────────────
    //  Public methods
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plays the standard tone standardRepetitions times before the walk begins,
    /// so the participant can learn the reference duration.
    /// </summary>
    public IEnumerator PlayStandardSequence()
    {
        float toneSec = standardDurationMs / 1000f;
        float gapSec  = standardGapMs / 1000f;

        for (int i = 0; i < standardRepetitions; i++)
        {
            audioSource.PlayOneShot(standardClip);
            yield return new WaitForSecondsRealtime(toneSec + gapSec);
        }

        Debug.Log($"Standard sequence: {standardRepetitions} x {standardDurationMs}ms tones");
    }

    public float GetStandardSequenceDuration()
    {
        return ((standardDurationMs + standardGapMs) / 1000f) * standardRepetitions;
    }

    /// <summary>
    /// Plays one comparison tone and waits for the participant's 2AFC response.
    /// Onset timing is handled by targetAppearance; this coroutine only covers
    /// the play → response-window phase.
    /// Called by targetAppearance.trialProgress() at each pre-calculated onset.
    /// </summary>
    public IEnumerator RunComparisonResponse(runExperiment runner)
    {
        CollectPlayerInput playerInput = runner.GetComponent<CollectPlayerInput>();

        // ── Choose direction and compute comparison duration (ratio-based) ──
        trialState.isShorter = Random.Range(0f, 1f) < 0.5f;
        
        float ratio = Mathf.Clamp(trialState.currentRatio, minRatio, maxRatio);

        // Weber-fraction scaling:
        //   longer  = standard × (1 + ratio)
        //   shorter = standard × (1 − ratio)
        float compMs = trialState.isShorter
            ? standardDurationMs * (1f - ratio)
            : standardDurationMs * (1f + ratio);
        compMs = Mathf.Max(compMs, 5f);   // floor at 5 ms

        trialState.ratio                = ratio;
        trialState.comparisonDurationMs = compMs;
        trialState.changeOnsetTime      = runner.trialTime;
        trialState.changeCount++;

        var evt = new experimentParameters.StimulusEvent(
            toneFrequencyHz:    toneFrequencyHz,
            standardDurationMs: standardDurationMs,
            ratio:              trialState.ratio,
            comparisonDurationMs: trialState.comparisonDurationMs,
            isShorter:          trialState.isShorter,
            changeOnsetTime:    trialState.changeOnsetTime,
            changeIndex:        trialState.changeCount - 1
            
            
        );

        // ── Play comparison tone ─────────────────────────────────────
        AudioClip compClip = GenerateToneClip(compMs / 1000f, toneFrequencyHz, toneAmplitude);
        audioSource.PlayOneShot(compClip);

        Debug.Log($"[CompTrial] {compMs:F1}ms " +
                  $"({(trialState.isShorter ? "SHORTER" : "LONGER")} than {standardDurationMs}ms), " +
                  $"ratio={ratio:P1}");

        // ── Wait for 2AFC response ───────────────────────────────────
        float windowElapsed = 0f;
        bool  detected      = false;

        while (windowElapsed < detectionWindowSec)
        {
            if (playerInput.leftisPressed || playerInput.rightisPressed)
            {
                detected = true;
                bool respondedShorter = runner.DeriveShorterResponse(playerInput.leftisPressed);
                float rt = runner.trialTime - trialState.changeOnsetTime;
                Debug.Log($"[CompTrial] RT={rt:F3}s — {(respondedShorter ? "Shorter" : "Longer")}");
                runner.RecordChangeEvent(evt, respondedShorter);
                yield return new WaitUntil(() => !playerInput.leftisPressed && !playerInput.rightisPressed);
                break;
            }
            yield return null;
            windowElapsed += Time.deltaTime;
        }

        if (!detected)
        {
            Debug.Log($"[CompTrial] MISS — no response within {detectionWindowSec}s");
            runner.RecordNoResponse(evt);
        }
    }

    /// <summary>
    /// Stops any currently playing audio. Called by targetAppearance at trial end.
    /// </summary>
    public void StopAllAudio()
    {
        audioSource.Stop();
    }

    /// <summary>
    /// Updates the staircase-controlled Weber ratio for the given direction.
    /// Called by runExperiment after each response.
    /// </summary>
    public void SetRatio(float ratio)
    {
        trialState.currentRatio = Mathf.Clamp(ratio, minRatio, maxRatio);
        Debug.Log($"[CompTrial] Ratio → {trialState.currentRatio:P1}  " +
                  $"(shorter: {standardDurationMs * (1f - trialState.currentRatio):F1}ms, " +
                  $"longer: {standardDurationMs * (1f + trialState.currentRatio):F1}ms)");
    }

    // ──────────────────────────────────────────────────────────────────
    //  Tone generation
    // ──────────────────────────────────────────────────────────────────

    private AudioClip GenerateToneClip(float durationSec, float frequencyHz, float amplitude)
    {
        int sampleCount = Mathf.CeilToInt(durationSec * sampleRate);
        if (sampleCount < 1) sampleCount = 1;

        float[] samples     = new float[sampleCount];
        float   rampSec     = rampDurationMs / 1000f;
        int     rampSamples = Mathf.Min(Mathf.CeilToInt(rampSec * sampleRate), sampleCount / 2);

        for (int i = 0; i < sampleCount; i++)
        {
            float t      = (float)i / sampleRate;
            float sample = amplitude * Mathf.Sin(2f * Mathf.PI * frequencyHz * t);

            if (i < rampSamples)
            {
                float f  = (float)i / rampSamples;
                sample  *= 0.5f * (1f - Mathf.Cos(Mathf.PI * f));
            }
            else if (i >= sampleCount - rampSamples)
            {
                float f  = (float)(i - (sampleCount - rampSamples)) / rampSamples;
                sample  *= 0.5f * (1f + Mathf.Cos(Mathf.PI * f));
            }

            samples[i] = sample;
        }

        AudioClip clip = AudioClip.Create(
            $"tone_{durationSec * 1000f:F0}ms_{frequencyHz:F0}Hz",
            sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}
