using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;


public class experimentParameters : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created

    /// <summary>
    /// This script contains the high-level experiment structure (nblocks etc), and builds the arrays necessary for trial indexing, and data storage.
    /// - at this stage, we predefine trial conditions (pseudo-randomly).
    /// </summary>


    [Header("Experiment Mode")]
    [Tooltip("True = stationary vs natural walking (blockTypes 0,2). False = slow vs natural walking (blockTypes 1,2).")]
    public bool stationaryVsNatural = false;

    //Walking Parameters:
    public float defaultMaxSpeed; // set high, will adjust the distance per ppant after calibration.
    public float slowSpdPcnt; // percentage of normal speed for slow blocks.
    public float distanceBetweenZones; // set by StartZone and endZone positions.
    
    public float walkDuration; // from default max or calibrated speed.
    // note that slowDuration is now the same - all trials the same duration, distance varies instead.
    public float slowSpeed, normSpeed; //  will be set after calibration.

    
    [HideInInspector]
    public int[] maxTargsbySpeed; // [0] = normal speed, [1] = slow speed. // Max targets array - will be calculated after calibration

    //Within trial parameters:

    [HideInInspector]
    public float preTrialsec, responseWindow, targDurationsec, nTrials, minITI, maxITI, jittermax;
    

    //Experiment Design parameers:
    public int nTrialsperBlock, nBlocks, nPracticeBlocks, nWalkSpeeds, nstandingStilltrials;
    private int[] blockTypelist;
    [HideInInspector]
    public int[,] blockTypeArray; //nTrials x 3 (block, trialID, type)
    private float propSlowSpeed, propNaturalSpeed;
    //reference to walkCalibrator to get speeds:
    WalkSpeedCalibrator walkCalibrator;
    runExperiment runExperiment;
    // colors [ contrast is updated within staircase]
    [HideInInspector]
    public Color preTrialColor, probeColor, targetColor; // green, to show ready/idle state
    // ──────────────────────────────────────────────────────────────────
    // StimulusEvent: An immutable snapshot of a beep-train ISI change.
    //
    // Created once by MakeAuditoryStimulus.RunBeepTrain() at the moment
    // the ISI shifts, then passed through the pipeline without mutation:
    //   RunBeepTrain creates it  →  runExperiment scores & staircases it
    //                            →  RecordData logs it to CSV
    //
    // Because it is a readonly struct, its fields cannot be changed after
    // construction.
    // ──────────────────────────────────────────────────────────────────
    public readonly struct StimulusEvent
    {
        public readonly float toneFrequencyHz;      // Tone frequency (Hz)
        public readonly float standardDurationMs;   // Reference tone duration (ms)
        
        public readonly float ratio;                // Weber fraction applied (e.g. 0.25 = 25%)
        public readonly float comparisonDurationMs; // comparison tone after ratio applied
        public readonly bool isShorter;             // Was comparison shorter than standard?
        public readonly float changeOnsetTime;      // Trial-relative time (seconds) when comparison was played
        public readonly int changeIndex;            // Which comparison in this trial (0-based)

        // Derived convenience property — actual ms difference for logging
        public float toneMs => standardDurationMs * ratio;

        public StimulusEvent(
            float toneFrequencyHz, float standardDurationMs,
            float ratio, float comparisonDurationMs,  bool isShorter,
            float changeOnsetTime, int changeIndex )
        {
            this.toneFrequencyHz    = toneFrequencyHz;
            this.standardDurationMs = standardDurationMs;
            this.comparisonDurationMs = comparisonDurationMs;
            this.ratio              = ratio;
            this.isShorter          = isShorter;
            this.changeOnsetTime    = changeOnsetTime;
            this.changeIndex        = changeIndex;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // trialData: Mutable per-trial context and response data.
    //
    // Set in two phases:
    //   1. Trial start  (runExperiment.startTrial):  trialNumber, blockID, trialID, blockType, isStationary
    //   2. Per-event     (runExperiment.RecordChangeEvent / RecordNoResponse / RecordFalseAlarm)
    //
    // Stimulus-specific fields (what was played, when it changed, etc.)
    // live in the immutable StimulusEvent instead.
    // ──────────────────────────────────────────────────────────────────
    [System.Serializable]
    public struct trialData
    {
        // Trial context — set once at trial start
        public int trialNumber, blockID, trialID, trialType, walkSpeed, blockType;
        public bool isStationary;

        // Response data — set per change event (2AFC: faster vs slower)
        public float clickOnsetTime;       // trial-relative click time (-1 = no response)
        public int targCorrect;            // 1 = correct direction, 0 = wrong direction or miss
        public float targResponse;         // 1 = responded "faster", 0 = responded "slower", -1 = no response

        // Reserved for staircase / future use
        public float targDuration, targResponseTime, stairCase;
    }

    public trialData trialD;

    public    GameObject startZone, endZone;

    void Start()
    {
        walkCalibrator = GetComponent<WalkSpeedCalibrator>();
        runExperiment = GetComponent<runExperiment>();
        //set some defaults
        // slowDuration = 15f;
        // normDuration = 10f;
        // now adjusting distance instead, so that the duration is matched for each participant after calibration.
         distanceBetweenZones = Vector3.Distance(startZone.transform.position, endZone.transform.position); // metres
        //use this  as default before walk calibration:
        defaultMaxSpeed =1.2f; // m/s (fast-ish)
        slowSpdPcnt= 0.6f;
        normSpeed = defaultMaxSpeed;
        slowSpeed = normSpeed * slowSpdPcnt; // e.g.  80% of normal speed.
        walkDuration = distanceBetweenZones / normSpeed; // seconds
        // note that these parameters are set here, but used in WalkSpeedCalibrator to determine adjusted start/end zones, as well as duration.



        preTrialsec = .5f; // time before trial starts, to show ready state.
        targDurationsec = 0.3f; // used by CalculateStimTimes for onset scheduling (comparison tone duration + buffer)
        responseWindow = 0.8f; // time to respond after target onset.
        // targDurationsec = 0.4f; // Initial value (start easy) to be updated by staricase.
        nstandingStilltrials = 4; // ensure mod%2 to not mess with gide positioning.
        
        jittermax = 0.25f; // in seconds, will be a uniform distribution from 0  + jittermax.
        // set colour presets
        // preTrialColor = new Color(0f, 1f, 0f, 1); //drk green
        // probeColor = new Color(0.4f, 0.4f, 0.4f, targetAlpha); // dark grey
        // targetColor = new Color(.55f, .55f, .55f, targetAlpha); // light grey (start easy, become difficult).


        nWalkSpeeds = 2; // [0,1,2]; 1 and 2 are slow and natural pace
        
        //
        nTrialsperBlock = 20; // 
        nBlocks = 11; //total. (experiment same duration since now more 'natural pace' blocks)
        nPracticeBlocks = 1; // overrides the first block with some additional controls.
                             //
        propSlowSpeed = 0.5f; // proption slow speed. (blocks) (reduced to account for more targs in slow blocks)
        propNaturalSpeed = 1 - propSlowSpeed; // proportion natural speed

        createTrialTypes();

    }


    void createTrialTypes()
    {
        

        int nTrials = nTrialsperBlock * nBlocks;

        // float[] walkDurs = new float[nWalkSpeeds];

        // walkDurs[0] = 15f; //slowDuration;
        // walkDurs[1] = 9f; //natural;


        // also create wrapper to determine block conditions.
        // first few trials (or block) should be stationary, for burn-in.
        // this is fixed by adding an extra natural speed block at first index.
        // Mode A (stationaryVsNatural=false): slow walk (1) and natural (2)
        // Mode B (stationaryVsNatural=true):  stationary (0) and natural (2)
        int[] walktypeArray = stationaryVsNatural
            ? new int[] { 0, 2 }
            : new int[] { 1, 2 };

        // One non-practice block is always forced to natural (prepended below), so
        // allocate nBlocks-nPracticeBlocks-1 slots here. After prepending the forced
        // natural block the array has exactly nBlocks-nPracticeBlocks entries, one per
        // non-practice block. Previously allocating nBlocks-nPracticeBlocks here meant
        // the prepend created an 11-element list from which only 10 were consumed,
        // silently dropping the last shuffled block and biasing the counts (6 nat / 4 stat).
        int nRemaining = nBlocks - nPracticeBlocks - 1;
        blockTypelist = new int[nRemaining];

        int nSlowBlocks = Mathf.RoundToInt(nRemaining * propSlowSpeed);
        int nFastBlocks = nRemaining - nSlowBlocks;

        // Fill the blockTypelist with proportional amounts
        int icount = 0;

        // Add slow speed blocks (type 1)
        for (int i = 0; i < nSlowBlocks; i++)
        {
            blockTypelist[icount] = walktypeArray[0];
            icount++;
        }

        // Add fast speed blocks (type 2)
        for (int i = 0; i < nFastBlocks; i++)
        {
            blockTypelist[icount] = walktypeArray[1];
            icount++;
        }


        shuffleArray(blockTypelist);
        // now shoehorn in a natural pace block at the start of this array:, final should be [natural, random, random, random ...] with random proportional slow/natural as defined by propSlowSpeed.

        blockTypelist = new[] { walktypeArray[1] }.Concat(blockTypelist).ToArray();

        blockTypeArray = new int[(int)nTrials, 3];
        // 3 columns. blockiD, trialID (within block), walkspeed
        
        int icounter;
        icounter = 0;
        // for practice block: first nstandingStilltrials are stationary burn-in,
        // then an even shuffle of both walk conditions for the remainder.
        for (int iblock = 0; iblock < nPracticeBlocks; iblock++)
        {
            int nPracticeWalk = nTrialsperBlock - nstandingStilltrials;
            int[] practiceWalkTypes = new int[nPracticeWalk];
            int half = nPracticeWalk / 2;
            for (int i = 0; i < half; i++)
                practiceWalkTypes[i] = walktypeArray[0]; // slow or stationary
            for (int i = half; i < nPracticeWalk; i++)
                practiceWalkTypes[i] = walktypeArray[1]; // natural
            shuffleArray(practiceWalkTypes);

            int walkIdx = 0;
            for (int itrial = 0; itrial < nTrialsperBlock; itrial++)
            {
                blockTypeArray[icounter, 0] = iblock;
                blockTypeArray[icounter, 1] = itrial;

                if (icounter < nstandingStilltrials)
                    blockTypeArray[icounter, 2] = 0; // stationary burn-in
                else
                    blockTypeArray[icounter, 2] = practiceWalkTypes[walkIdx++];

                icounter++;
            }
        }

        //now fill remaining blocks 
        //
        for (int iblock = nPracticeBlocks; iblock < nBlocks; iblock++)
        {
            for (int itrial = 0; itrial < nTrialsperBlock; itrial++)
            {
                blockTypeArray[icounter, 0] = iblock;
                blockTypeArray[icounter, 1] = itrial;
                blockTypeArray[icounter, 2] = blockTypelist[iblock - nPracticeBlocks]; //mvmnt (randomized).

                icounter++;
            }

        }

        // Auditory task: no per-block detection task assignment needed
        // (all blocks use the same ISI change detection beep train task)
        Debug.Log("Auditory ISI change detection — block types assigned.");
    }


    ///
    ///
    /// METHODS called:

    
    public float GetStimulusDuration()
    {
        return targDurationsec;
    }

    public float GetTrialDuration()
    {
        return walkDuration;
        
    }
    

    // shuffle array once populated.
    void shuffleArray(int[] a)
    {
        int n = a.Length;

        for (int id = 0; id < n; id++)
        {
            swap(a, id, id + Random.Range(0, n - id));
        }

    }

    void swap(int[] inputArray, int a, int b)
    {
        int temp = inputArray[a];
        inputArray[a] = inputArray[b];
        inputArray[b] = temp;

    }
}
