using System.Collections;
using UnityEngine;

public class targetAppearance : MonoBehaviour
{
    /// <summary>
    /// Controls comparison tone timing during each walking trial.
    ///
    /// Mirrors the VIS2AFC_v2 pattern:
    ///   - startSequence() fetches pre-calculated onset times from CalculateStimTimes
    ///     and records the walk-phase start time so onsets stay walk-relative.
    ///   - trialProgress() loops through every onset, delegates play + response to
    ///     MakeAuditoryStimulus, then waits for the trial clock to expire.
    ///   - The outer while(trialinProgress) means the coroutine exits cleanly the
    ///     moment trialPackDown() fires — no stale coroutines calling StopAllAudio.
    /// </summary>

    private float[] trialOnsets;
    private float   walkPhaseStartTime; // trialTime value when walk phase begins (after standard sequence)
    private Coroutine trialCoroutine;

    runExperiment        runExperiment;
    MakeAuditoryStimulus makeAuditoryStimulus;
    experimentParameters experimentParameters;
    CalculateStimTimes   calcStimTimes;

    [SerializeField] GameObject scriptHolder;

    private void Start()
    {
        runExperiment        = scriptHolder.GetComponent<runExperiment>();
        experimentParameters = scriptHolder.GetComponent<experimentParameters>();
        calcStimTimes        = scriptHolder.GetComponent<CalculateStimTimes>();
        makeAuditoryStimulus = GetComponent<MakeAuditoryStimulus>();
    }

    public void startSequence()
    {
        // Grab this trial's pre-calculated onset schedule
        trialOnsets = calcStimTimes.allOnsets[runExperiment.trialCount];

        // Record trialTime at walk-phase start so onset times stay walk-relative
        walkPhaseStartTime = runExperiment.trialTime;

        if (trialCoroutine != null)
            StopCoroutine(trialCoroutine);
        trialCoroutine = StartCoroutine(trialProgress());
    }

    IEnumerator trialProgress()
    {
        while (runExperiment.trialinProgress)
        {
            for (int i = 0; i < trialOnsets.Length; i++)
            {
                // Mirror VIS2AFC timing:
                //   first onset  → wait directly (elapsed starts at 0 from walk phase)
                //   subsequent   → compensate for time already elapsed in the walk phase
                float waitTime;
                if (i == 0)
                {
                    waitTime = trialOnsets[0];
                }
                else
                {
                    float absoluteOnset = walkPhaseStartTime + trialOnsets[i];
                    waitTime = absoluteOnset - runExperiment.trialTime;
                }

                if (waitTime > 0f)
                    yield return new WaitForSecondsRealtime(waitTime);

                if (!runExperiment.trialinProgress) break;

                // Play comparison tone and wait for 2AFC response
                yield return StartCoroutine(
                    makeAuditoryStimulus.RunComparisonResponse(runExperiment));
            }

            // Wait for the trial clock to expire
            while (runExperiment.trialTime < runExperiment.thisTrialDuration
                   && runExperiment.trialinProgress)
            {
                yield return null;
            }

            break; // trial complete — exit the outer while
        }

        makeAuditoryStimulus.StopAllAudio();
        trialCoroutine = null;
    }
}
