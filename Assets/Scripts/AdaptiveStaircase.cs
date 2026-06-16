using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting.FullSerializer;

public class AdaptiveStaircase : MonoBehaviour
{
    /// <summary>
    /// A single adaptive staircase component that manages independent staircase
    /// instances for any number of experimental conditions (e.g., slow/natural walking).
    ///
    /// Usage:
    ///   float nextDelta = staircase.ProcessResponse("slow", wasCorrect);
    ///   float nextDelta = staircase.ProcessResponse("natural", wasCorrect);
    ///
    /// Each condition string creates its own independent staircase on first use.
    /// All instances share the same configuration (step sizes, rules, etc.)
    /// but track their own state (trial count, reversals, threshold).
    /// </summary>

    [System.Serializable]
    public enum StaircaseType
    {
        SimpleUpDown,           // 1-up, 1-down (50% threshold)
        TwoUpOneDown,          // 2-up, 1-down (70.7% threshold)
        ThreeUpOneDown,        // 3-up, 1-down (79.4% threshold)
        OneUpTwoDown,          // 1-up, 2-down (70.7% threshold)
        OneUpThreeDown         // 1-up, 3-down (79.4% threshold)
    }

    // ──────────────────────────────────────────────────────────────────
    //  Inspector-readable parameters (shared across all instances)
    // ──────────────────────────────────────────────────────────────────

    [Header("Staircase Configuration")]
    public StaircaseType staircaseType = StaircaseType.TwoUpOneDown;

    public float initialRatio;

    public float minRatio;

    public float maxRatio;

    [Header("Step Sizes (Ratio Change)")]
    public float initialStepSize;   // as pcnt, e.g. 0.50 = 50%
    public float finalStepSize;      // as pcnt, e.g. 0.95 = 95% (must be below 1 to play tone)
    public int reversalsToReduceStep; // After how many reversals to halve step size

    public float currentRatio; // display current.

    // ──────────────────────────────────────────────────────────────────
    //  Per-condition staircase state
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Internal state for a single staircase instance.
    /// </summary>
    private class StaircaseState
    {
        public string conditionLabel;

        // public float currentIntensity; // amp. now using currentRatio instead.
        public float currentRatio;
        public float currentStepSize;
        public int trialCount;
        public int reversalCount;
        public int consecutiveCorrect;
        public int consecutiveIncorrect;
        public bool isComplete;

        // History
        public List<float> intensityHistory = new List<float>();
        public List<bool> responseHistory = new List<bool>();
        public List<float> reversalIntensities = new List<float>();
        public List<int> reversalTrials = new List<int>();
        public bool lastDirectionWasUp;
        public bool hasEstablishedDirection; // true after the first step is taken (not on first reversal)
    }

    // Dictionary of independent staircases, keyed by condition label
    private Dictionary<string, StaircaseState> staircases = new Dictionary<string, StaircaseState>();

    public GameObject Screen;
    MakeAuditoryStimulus makeAuditoryStimulus;
    // establish defauly parameters from other scripts.

    void Start()
    {
        makeAuditoryStimulus = Screen.GetComponent<MakeAuditoryStimulus>();

        initialRatio = makeAuditoryStimulus.initialRatio;  // starting Weber ratio (e.g. 0.25)
        minRatio = makeAuditoryStimulus.minRatio;
        maxRatio = makeAuditoryStimulus.maxRatio;
        currentRatio = makeAuditoryStimulus.initialRatio; // to be adapted.
        initialStepSize = .10f;//  makeAuditoryStimulus.initialStepSize; // e.g. 0.50 (50% change)
        finalStepSize = .01f;
        reversalsToReduceStep = 2;//makeAuditoryStimulus.reversalsToReduceStep; // e.g. 2   
    }
    // ──────────────────────────────────────────────────────────────────
    //  Public API
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Process a response for a given condition. Creates the staircase instance
    /// on first use for that condition.
    /// </summary>
    /// <param name="condition">Condition label (e.g., "slow", "natural")</param>
    /// <param name="correct">Whether the participant's response was correct</param>
    /// <returns>The new intensity (duration delta in seconds) for the next trial</returns>
    public float ProcessResponse(string condition, bool correct)
    {
        StaircaseState s = GetOrCreateStaircase(condition);

        if (s.isComplete)
        {
            Debug.LogWarning($"Staircase [{condition}] is already complete!");
            return s.currentRatio;
        }

        // Record the response
        s.trialCount++;
        s.responseHistory.Add(correct);
        s.intensityHistory.Add(s.currentRatio);

        Debug.Log($"[Staircase:{condition}] Trial {s.trialCount}: correct={correct}, ratio={s.currentRatio:F3}");

        // Update consecutive counters
        if (correct)
        {
            s.consecutiveCorrect++;
            s.consecutiveIncorrect = 0;
        }
        else
        {
            s.consecutiveIncorrect++;
            s.consecutiveCorrect = 0;
        }

        // Determine direction
        bool shouldGoUp = ShouldIncreaseIntensity(s);
        bool shouldGoDown = ShouldDecreaseIntensity(s);

        // Check for reversal before updating intensity
        bool isReversal = CheckForReversal(s, shouldGoUp, shouldGoDown);

        // Update intensity
        if (shouldGoUp)
        {
            s.currentRatio += s.currentStepSize;
            s.lastDirectionWasUp = true;
            s.hasEstablishedDirection = true;
            s.consecutiveCorrect = 0;
            s.consecutiveIncorrect = 0;
        }
        else if (shouldGoDown)
        {
            s.currentRatio -= s.currentStepSize;
            s.lastDirectionWasUp = false;
            s.hasEstablishedDirection = true;
            s.consecutiveCorrect = 0;
            s.consecutiveIncorrect = 0;
        }

        // Record reversal
        if (isReversal)
        {
            s.reversalCount++;
            s.reversalIntensities.Add(s.currentRatio);
            s.reversalTrials.Add(s.trialCount);

            Debug.Log($"[Staircase:{condition}] Reversal #{s.reversalCount} at trial {s.trialCount}, intensity {s.currentRatio:F3}");

            // Reduce step size after every N reversals
            if (s.reversalCount > 0 && s.reversalCount % reversalsToReduceStep == 0)
            {
                float oldStep = s.currentStepSize;
                s.currentStepSize = Mathf.Max(finalStepSize, s.currentStepSize * 0.75f); // reduce by half, but not below finalStepSize
                Debug.Log($"[Staircase:{condition}] Step size reduced: {oldStep:F3} -> {s.currentStepSize:F3}");
            }
        }

        // Clamp
        s.currentRatio = Mathf.Clamp(s.currentRatio, minRatio, maxRatio);

        Debug.Log($"[Staircase:{condition}] New intensity: {s.currentRatio:F3}, reversals: {s.reversalCount}");

        // display in inspector for debugging;
        currentRatio = s.currentRatio;

        return s.currentRatio;
    }

    /// <summary>
    /// Get the current intensity for a condition without processing a response.
    /// </summary>
    public float GetCurrentIntensity(string condition)
    {
        StaircaseState s = GetOrCreateStaircase(condition);
        return s.currentRatio;
    }

    /// <summary>
    /// Get the estimated threshold for a condition (mean of last 6 reversals).
    /// </summary>
    public float GetEstimatedThreshold(string condition)
    {
        StaircaseState s = GetOrCreateStaircase(condition);
        return CalculateThreshold(s);
    }

    /// <summary>
    /// Get the number of trials completed for a condition.
    /// </summary>
    public int GetTrialCount(string condition)
    {
        if (staircases.TryGetValue(condition, out StaircaseState s))
            return s.trialCount;
        return 0;
    }

    /// <summary>
    /// Get the number of reversals for a condition.
    /// </summary>
    public int GetReversalCount(string condition)
    {
        if (staircases.TryGetValue(condition, out StaircaseState s))
            return s.reversalCount;
        return 0;
    }

    /// <summary>
    /// Get the intensity history for a condition (for plotting/analysis).
    /// </summary>
    public List<float> GetIntensityHistory(string condition)
    {
        if (staircases.TryGetValue(condition, out StaircaseState s))
            return new List<float>(s.intensityHistory);
        return new List<float>();
    }

    /// <summary>
    /// Get the response history for a condition.
    /// </summary>
    public List<bool> GetResponseHistory(string condition)
    {
        if (staircases.TryGetValue(condition, out StaircaseState s))
            return new List<bool>(s.responseHistory);
        return new List<bool>();
    }

    /// <summary>
    /// Reset a single condition's staircase.
    /// </summary>
    public void ResetCondition(string condition)
    {
        if (staircases.ContainsKey(condition))
        {
            staircases.Remove(condition);
            Debug.Log($"[Staircase:{condition}] Reset.");
        }
    }

    /// <summary>
    /// Reset all staircases.
    /// </summary>
    public void ResetAll()
    {
        staircases.Clear();
        Debug.Log("All staircases reset.");
    }

    /// <summary>
    /// Print summary statistics for all active conditions.
    /// </summary>
    public void PrintSummary()
    {
        Debug.Log("=== STAIRCASE SUMMARY ===");
        foreach (var kvp in staircases)
        {
            var s = kvp.Value;
            float accuracy = s.responseHistory.Count > 0
                ? s.responseHistory.Count(r => r) / (float)s.responseHistory.Count * 100f
                : 0f;

            Debug.Log($"[{kvp.Key}] Trials: {s.trialCount}, Reversals: {s.reversalCount}, " +
                      $"Final: {s.currentRatio:F3}, Threshold: {CalculateThreshold(s):F3}, " +
                      $"Accuracy: {accuracy:F1}%");
            Debug.Log($"  Reversal ratios: {string.Join(", ", s.reversalIntensities.Select(r => r.ToString("F3")))}");
        }
    }

    /// <summary>
    /// Returns a list of all active condition labels.
    /// </summary>
    public List<string> GetActiveConditions()
    {
        return new List<string>(staircases.Keys);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Private helpers
    // ──────────────────────────────────────────────────────────────────

    private StaircaseState GetOrCreateStaircase(string condition)
    {
        if (!staircases.TryGetValue(condition, out StaircaseState s))
        {
            s = new StaircaseState
            {
                conditionLabel = condition,
                currentRatio = initialRatio,
                currentStepSize = initialStepSize,
                trialCount = 0,
                reversalCount = 0,
                consecutiveCorrect = 0,
                consecutiveIncorrect = 0,
                isComplete = false,
                lastDirectionWasUp = false,
                hasEstablishedDirection = false
            };
            staircases[condition] = s;
            Debug.Log($"[Staircase:{condition}] Created new instance (initial={initialRatio:F3}, step={initialStepSize:F3})");
        }
        return s;
    }

    private bool ShouldIncreaseIntensity(StaircaseState s)
    {
        switch (staircaseType)
        {
            case StaircaseType.SimpleUpDown:
                return s.consecutiveIncorrect >= 1;
            case StaircaseType.TwoUpOneDown:
                return s.consecutiveIncorrect >= 1;
            case StaircaseType.ThreeUpOneDown:
                return s.consecutiveIncorrect >= 1;
            case StaircaseType.OneUpTwoDown:
                return s.consecutiveIncorrect >= 2;
            case StaircaseType.OneUpThreeDown:
                return s.consecutiveIncorrect >= 3;
            default:
                return false;
        }
    }

    private bool ShouldDecreaseIntensity(StaircaseState s)
    {
        switch (staircaseType)
        {
            case StaircaseType.SimpleUpDown:
                return s.consecutiveCorrect >= 1;
            case StaircaseType.TwoUpOneDown:
                return s.consecutiveCorrect >= 2;
            case StaircaseType.ThreeUpOneDown:
                return s.consecutiveCorrect >= 3;
            case StaircaseType.OneUpTwoDown:
                return s.consecutiveCorrect >= 1;
            case StaircaseType.OneUpThreeDown:
                return s.consecutiveCorrect >= 1;
            default:
                return false;
        }
    }

    private bool CheckForReversal(StaircaseState s, bool shouldGoUp, bool shouldGoDown)
    {
        if (!s.hasEstablishedDirection)
            return false; // no direction yet — first step can't be a reversal

        return (shouldGoUp && !s.lastDirectionWasUp) || (shouldGoDown && s.lastDirectionWasUp);
    }

    private float CalculateThreshold(StaircaseState s)
    {
        if (s.reversalIntensities.Count < 4)
        {
            return s.currentRatio; // Not enough data
        }

        // Use last 6 reversals or all if fewer than 6
        int reversalsToUse = Mathf.Min(6, s.reversalIntensities.Count);
        int startIndex = s.reversalIntensities.Count - reversalsToUse;

        float sum = 0f;
        for (int i = startIndex; i < s.reversalIntensities.Count; i++)
        {
            sum += s.reversalIntensities[i];
        }

        return sum / reversalsToUse;
    }
}
