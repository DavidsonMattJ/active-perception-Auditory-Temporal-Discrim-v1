using UnityEngine;
using System;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine.UIElements.Experimental;
using UnityEngine.XR.Interaction.Toolkit.Utilities.Tweenables.Primitives;
using Unity.VisualScripting;
using TMPro;
using JetBrains.Annotations;
using System.Collections;
using UnityEditor.Media;

public class runExperiment : MonoBehaviour
{
    // This is the launch script for the experiment, useful for toggling certain input states. 

    //Navon v1  -UTS 


    [Header("User Input")]
    public bool playinVR;
    public string participant;
    public bool skipWalkCalibration;


    [Header("Experiment State")]

    public string responseMapping = "L:Shorter R:Longer"; // show for experimenter (default, randomised at start)
    public int trialCount;
    public float trialTime;
    public float thisTrialDuration;
    public bool trialinProgress;
    [SerializeField] public int responseMap; // +1: L=Faster R=Slower; -1: L=Slower R=Faster


    [HideInInspector]
    public int detectIndex, targState, blockType; //


    [HideInInspector]
    public bool isStationary, collectTrialSummary, collectEventSummary, hasResponded;
    
    bool SetUpSession;

    //todo
    //public bool forceheightCalibration;
    //public bool forceEyecalibration;
    //public bool recordEEG;
    //public bool isEyetracked;


    CollectPlayerInput playerInput;
    experimentParameters expParams;
    controlWalkingGuide controlWalkingGuide;
    WalkSpeedCalibrator walkCalibrator;
    ShowText ShowText;
    FeedbackText FeedbackText;
    targetAppearance targetAppearance;
    RecordData RecordData;
    AdaptiveStaircase adaptiveStaircase;
    CalculateStimTimes calculateStimTimes;
    MakeAuditoryStimulus makeAuditoryStimulus;


    //use  serialize field to require drag-drop in inspector. less expensive than GameObject.Find() .
    [SerializeField] GameObject TextScreen;
    [SerializeField] GameObject TextFeedback;
    [SerializeField] GameObject StimulusScreen;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {


        adaptiveStaircase = GetComponent<AdaptiveStaircase>();
        playerInput = GetComponent<CollectPlayerInput>();
        expParams = GetComponent<experimentParameters>();
        controlWalkingGuide = GetComponent<controlWalkingGuide>();
        walkCalibrator = GetComponent<WalkSpeedCalibrator>();
        RecordData = GetComponent<RecordData>();
        calculateStimTimes = GetComponent<CalculateStimTimes>();

        ShowText = TextScreen.GetComponent<ShowText>();
        FeedbackText = TextFeedback.GetComponent<FeedbackText>();

        targetAppearance = StimulusScreen.GetComponent<targetAppearance>();
        makeAuditoryStimulus = StimulusScreen.GetComponent<MakeAuditoryStimulus>();
        // hide player camera if not in VR (useful for debugging).
        togglePlayers();

        // Randomise response mapping (L/R → faster/slower)
        assignResponses();

        trialCount = 0;
        trialinProgress = false;

        trialTime = 0f;
        collectEventSummary = false; // send info after each target to csv file.

        hasResponded = false;

        SetUpSession = true;

    }

    // Update is called once per frame
    void Update()
    {
        if (SetUpSession && ShowText.isInitialized)
        {
            // Pre-calculate all trial onset times now that expParams is fully initialised
            calculateStimTimes.CalculateStimulusPresentationTimes();

            if (skipWalkCalibration)
            {
                ShowText.UpdateText(ShowText.TextType.CalibrationComplete);
            }
            else
            {
                ShowText.UpdateText(ShowText.TextType.Welcome);
            }
            SetUpSession = false;
        }


        //pseudo code: 
        // listen for trial start (input)/
        // if input. 1) start the walking guide movement
        //           2) start the within trial co-routine
        //           3) start the data recording.

        if (!trialinProgress && playerInput.botharePressed)
        {

            // if we have not yet calibrated walk speed, simply move the wlaking guide to start loc:
            if (playinVR)
            {
                if (walkCalibrator.isCalibrationComplete())
                {
                    //start trial sequence, including:
                    // movement, co-routine, datarecording.

                    Debug.Log("Starting Trial in VR mode");
                    startTrial();
                }
                else
                {
                    Debug.Log("button pressed but walk calibration still in progress");
                    // lets hide the walking guide temporarily. 
                    controlWalkingGuide.setGuidetoHidden();
                }
            }
            else // not in VR, skip calibration:
            {
                // Non-VR mode: skip calibration check and start trial directly
                Debug.Log("Starting Trial (Non-VR mode)");
                startTrial();
            }

        }

        // increment trial time.
        if (trialinProgress)
        {
            trialTime += Time.deltaTime; // increment timer.

            if (trialTime > thisTrialDuration)
            {
                trialPackDown(); // includes trial incrementer
                trialCount++;
            }

            // Note: player input is now checked inside the beep train coroutine
            // (MakeAuditoryStimulus.RunBeepTrain), not here. The beep train calls
            // RecordChangeEvent(), RecordNoResponse(), and RecordFalseAlarm() directly.
        }

    } //end Update()

    
        
        
    

    void togglePlayers()
    {
        if (playinVR)
        {
            GameObject.Find("VR_Player").SetActive(true);
            GameObject.Find("Kb_Player").SetActive(false);
        }
        else
        {
            GameObject.Find("VR_Player").SetActive(false);
            GameObject.Find("Kb_Player").SetActive(true);

        }
    }
    // ──────────────────────────────────────────────────────────────────
    //  Public methods called by MakeAuditoryStimulus.RunBeepTrain()
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Determines whether a left or right button press maps to "responded shorter"
    /// based on the randomised responseMap. Called by the comparison trial coroutine.
    /// </summary>
    public bool DeriveShorterResponse(bool leftPressed)
    {
        // responseMap == 1: L=Shorter, R=Longer → left press = shorter
        // responseMap == -1: L=Longer, R=Shorter → left press = longer (i.e. not shorter)
        return (responseMap == 1) ? leftPressed : !leftPressed;
    }

    /// <summary>
    /// Called by the comparison trial coroutine when the participant responds.
    /// Scores the 2AFC judgement (shorter/longer), records to CSV, and updates
    /// the appropriate staircase.
    /// </summary>
    /// <param name="evt">Immutable snapshot of the comparison tone event</param>
    /// <param name="respondedShorter">Did the participant indicate "shorter"?</param>
    public void RecordChangeEvent(experimentParameters.StimulusEvent evt, bool respondedShorter)
    {
        bool isCorrect = (respondedShorter == evt.isShorter);

        expParams.trialD.targCorrect    = isCorrect ? 1 : 0;
        expParams.trialD.targResponse   = respondedShorter ? 1f : 0f; // 1=shorter, 0=longer
        expParams.trialD.clickOnsetTime = trialTime;

        RecordData.extractEventSummary(evt, isFalseAlarm: false);

        string respondedLabel = respondedShorter ? "Shorter" : "Longer";
        string actualLabel    = evt.isShorter    ? "Shorter" : "Longer";
        Debug.Log($"[CompTrial] {(isCorrect ? "CORRECT" : "INCORRECT")} — responded {respondedLabel}, was {actualLabel}");

        // Update staircase (skip during standing-still practice trials)
        if (trialCount >= expParams.nstandingStilltrials)
        {
            string condition = GetConditionLabel(expParams.trialD.blockType);

            if (condition != null)
            {
                float nextDelta = adaptiveStaircase.ProcessResponse(condition, isCorrect);
                makeAuditoryStimulus.SetRatio(nextDelta);

                Debug.Log($"[Staircase:{condition}] {(isCorrect ? "✓" : "✗")} → Next delta: {nextDelta:F1}ms");
            }
        }
        else
        {
            // Practice trials: provide visual feedback, no staircase update
            FeedbackText.UpdateText(isCorrect ? FeedbackText.TextType.Correct : FeedbackText.TextType.Incorrect);
            Invoke(nameof(HideFeedbackText), 0.2f);
        }
    }

    /// <summary>
    /// Called when no response was made within the detection window (miss / timeout).
    /// Always counts as incorrect for the staircase.
    /// </summary>
    public void RecordNoResponse(experimentParameters.StimulusEvent evt)
    {
        expParams.trialD.targCorrect    = 0;
        expParams.trialD.targResponse   = -1f;  // sentinel: no response
        expParams.trialD.clickOnsetTime = -1f;

        RecordData.extractEventSummary(evt, isFalseAlarm: false);

        Debug.Log($"[CompTrial] MISS — comparison was {(evt.isShorter ? "Shorter" : "Longer")}");

        if (trialCount >= expParams.nstandingStilltrials)
        {
            string condition = GetConditionLabel(expParams.trialD.blockType);

            if (condition != null)
            {
                float nextDelta = adaptiveStaircase.ProcessResponse(condition, false);
                makeAuditoryStimulus.SetRatio(nextDelta);

                Debug.Log($"[Staircase:{condition}] ✗ (miss) → Next delta: {nextDelta:F1}ms");
            }
        }
        else
        {
            FeedbackText.UpdateText(FeedbackText.TextType.Incorrect);
            Invoke(nameof(HideFeedbackText), 0.2f);
        }
    }

    /// <summary>
    /// Called when the participant presses a button before the comparison tone is played.
    /// Logged but does NOT update the staircase.
    /// </summary>
    /// <param name="respondedShorter">Did the participant indicate "shorter"?</param>
    public void RecordFalseAlarm(bool respondedShorter)
    {
        var faEvt = new experimentParameters.StimulusEvent(
            toneFrequencyHz:    makeAuditoryStimulus.trialState.toneFrequencyHz,
            standardDurationMs: makeAuditoryStimulus.trialState.standardDurationMs,
            ratio:            0f,
            comparisonDurationMs: makeAuditoryStimulus.trialState.comparisonDurationMs,
            isShorter:          false,
            changeOnsetTime:    trialTime,
            changeIndex:        -1  // sentinel: false alarm, no comparison played yet
        );

        expParams.trialD.targCorrect    = 0;
        expParams.trialD.targResponse   = respondedShorter ? 1f : 0f;
        expParams.trialD.clickOnsetTime = trialTime;

        RecordData.extractEventSummary(faEvt, isFalseAlarm: true);

        Debug.Log($"[CompTrial] FALSE ALARM at t={trialTime:F3}s — responded {(respondedShorter ? "Shorter" : "Longer")}");
    }

    private void HideFeedbackText()
    {
        FeedbackText.UpdateText(FeedbackText.TextType.Hide);
    }

    /// <summary>
    /// Randomises the L/R → faster/slower response mapping per participant.
    /// </summary>
    void assignResponses()
    {
        bool switchMapping = UnityEngine.Random.Range(0f, 1f) < 0.5f;

        if (switchMapping)
        {
            responseMap = -1;
            responseMapping = "L:Longer R:Shorter";
        }
        else
        {
            responseMap = 1;
            responseMapping = "L:Shorter R:Longer";
        }

        Debug.Log($"Response mapping assigned: {responseMapping} (responseMap={responseMap})");
    }

    void startTrial()
    {
        // This method handles the trial sequence.
        // First play the standard tone sequence (~1s), then start walking + comparisons.

        //recalibrate screen height to participants HMD
        controlWalkingGuide.updateScreenHeight();
        //remove text
        ShowText.UpdateText(ShowText.TextType.Hide);
        FeedbackText.UpdateText(FeedbackText.TextType.Hide);

        //establish trial parameters:
        if (expParams.maxTargsbySpeed == null)
        {
            // expParams.CalculateMaxTargetsBySpeed();
        }

        trialinProgress = true; // for coroutine (handled in targetAppearance.cs).
        ShowText.UpdateText(ShowText.TextType.Hide);
        trialTime = 0;
        targState = 0; //target is hidden.

        //Establish (this trial) specific parameters:
        blockType = expParams.blockTypeArray[trialCount, 2]; //third column [0,1,2].

        thisTrialDuration = expParams.walkDuration; // all trials the same duration now, distance varies instead.

        //query if stationary (restricts movement guide)
        isStationary = blockType == 0;

        //populate public trialD structure for extraction in recordData.cs
        expParams.trialD.trialNumber = trialCount;
        expParams.trialD.blockID = expParams.blockTypeArray[trialCount, 0];
        expParams.trialD.trialID = expParams.blockTypeArray[trialCount, 1]; // count within block
        expParams.trialD.isStationary = isStationary;
        expParams.trialD.blockType = blockType; // 0,1,2

        //updated phases for flow managers:
        RecordData.recordPhase = RecordData.phase.collectResponse;

        //start coroutine to control target onset and target behaviour:
        print("Starting Trial " + (trialCount + 1) + " of " + expParams.nTrialsperBlock);

        // Play standard tone sequence first (~1s), then start walking + comparisons
        StartCoroutine(StandardThenWalkSequence());
    }

    /// <summary>
    /// Coroutine: plays the standard tone sequence, then starts the walking guide
    /// and comparison stimulus sequence.
    /// </summary>
    IEnumerator StandardThenWalkSequence()
    {
        // 1. Play standard tone sequence so participant learns the reference duration
        yield return StartCoroutine(makeAuditoryStimulus.PlayStandardSequence());

        // 2. Start movement guide (if not stationary)
        if (!isStationary)
        {
            controlWalkingGuide.moveGuideatWalkSpeed();
        }

        // 3. Start comparison stimulus coroutine (onset times fetched inside startSequence)
        targetAppearance.startSequence();
    }

    void trialPackDown()
    {
        Debug.Log("End of Trial " + (trialCount + 1));

        RecordData.recordPhase = RecordData.phase.stop;

        trialinProgress = false;
        trialTime = 0f;
        makeAuditoryStimulus.StopAllAudio();

        int totalTrials = expParams.nTrialsperBlock * expParams.nBlocks;
        bool isLastTrial = (trialCount >= totalTrials - 1);

        if (isLastTrial)
        {
            controlWalkingGuide.setGuidetoCentre();
            adaptiveStaircase.PrintSummary();
            ShowText.UpdateText(ShowText.TextType.ExperimentComplete);
        }
        else
        {
            controlWalkingGuide.SetGuideForNextTrial();
            ShowText.UpdateText(ShowText.TextType.TrialStart);
        }
    }

    /// <summary>
    /// Maps blockType and comparison direction to a staircase condition label.
    ///
    /// Mode A (slow vs natural):       slow_shorter, slow_longer, natural_shorter, natural_longer
    /// Mode B (stationary vs natural): stationary_shorter, stationary_longer, natural_shorter, natural_longer
    ///
    /// Returns null for blockType 0 in Mode A (those are practice-only stationary trials).
    /// </summary>
    string GetConditionLabel(int blockType)
    {
        switch (blockType)
        {
            case 0: return expParams.stationaryVsNatural ? "stationary" : null;
            case 1: return "slow";
            case 2: return "natural";
            default: return null;
        }
    }




}
