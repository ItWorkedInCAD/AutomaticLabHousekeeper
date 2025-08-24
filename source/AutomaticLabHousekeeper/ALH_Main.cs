using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using KSP.UI.Screens;
using System.Collections;
using KSP.Localization;

namespace ALH
{
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class AutomaticLabHousekeeper : MonoBehaviour
    {
        private static double lastCheckTime = 0; // Manually track last check time

        void Awake()
        {
            lastCheckTime = 0; // Reset lastCheckTime on every scene change or quickload
            GameEvents.onGameStateLoad.Add(OnGameStateLoad); // Detect quickload
            StartCoroutine(WaitForSettings());
        }

        private IEnumerator WaitForSettings()
        {
            while (HighLogic.CurrentGame == null || ALHSettings.Instance == null)
            {
                yield return null; // Wait until settings are available
            }

            // Check if we are in Career or Science mode
            if (HighLogic.CurrentGame.Mode != Game.Modes.CAREER && HighLogic.CurrentGame.Mode != Game.Modes.SCIENCE_SANDBOX)
            {
                Debug.Log("[AutomaticLabHousekeeper] Not in Career or Science mode. Mod will not initialize.");
                Destroy(this);
                yield break;
            }

            Debug.Log($"[AutomaticLabHousekeeper] Settings loaded, proceeding with initialization. Check Interval: {ALHSettings.Instance.checkInterval}, Debug Mode: {ALHSettings.Instance.enableDebug}");

            // Destroy if ALH is disabled in settings
            if (!ALHSettings.Instance.enableALH)
            {
                Debug.Log("[AutomaticLabHousekeeper] Disabled in settings.");
                Destroy(this);
                yield break;
            }

            // Destroy if not in Flight, Tracking Station, or Space Center
            if (HighLogic.LoadedScene != GameScenes.FLIGHT &&
                HighLogic.LoadedScene != GameScenes.TRACKSTATION &&
                HighLogic.LoadedScene != GameScenes.SPACECENTER)
            {
                Destroy(this);
                yield break;
            }

            Debug.Log($"[AutomaticLabHousekeeper] Initialized in {HighLogic.LoadedScene}");

            // Register settings update event
            GameEvents.OnGameSettingsApplied.Add(OnSettingsChanged);
            StartCoroutine(DailyScienceCheck());
        }

        void OnDestroy()
        {
            Debug.Log("[AutomaticLabHousekeeper] Destroying instance, cleaning up...");

            // Remove event listeners to prevent memory leaks
            GameEvents.OnGameSettingsApplied.Remove(OnSettingsChanged);
            GameEvents.onGameStateLoad.Remove(OnGameStateLoad);

            // Stop all coroutines to ensure they do not continue running
            StopAllCoroutines();

            // Reset lastCheckTime so that it properly resets on next initialization
            lastCheckTime = 0;
        }

        void OnSettingsChanged()
        {
            Debug.Log("[AutomaticLabHousekeeper] Settings changed, reloading...");

            // Handle ALH enable/disable in real-time
            if (!ALHSettings.Instance.enableALH)
            {
                Debug.Log("[AutomaticLabHousekeeper] Disabled via settings, stopping ALH.");
                StopAllCoroutines();
                return;
            }

            // Restart science processing with new settings
            StopAllCoroutines();
            StartCoroutine(DailyScienceCheck());
        }

        void OnGameStateLoad(ConfigNode gameNode)
        {
            Debug.Log("[AutomaticLabHousekeeper] Quicksave Loaded, Resetting Mod...");

            // Stop all coroutines to ensure a fresh start
            StopAllCoroutines();

            // Reset lastCheckTime so the science check starts fresh
            lastCheckTime = 0;

            // Restart the science processing coroutine
            StartCoroutine(DailyScienceCheck());
        }

        private IEnumerator DailyScienceCheck()
        {
            while (true)
            {
                yield return new WaitUntil(() => HasInGameTimePassed());
                ProcessScienceForAllVessels();
            }
        }

        bool HasInGameTimePassed()
        {
            double currentTime = Planetarium.GetUniversalTime();
            double dayLength = GameSettings.KERBIN_TIME ? 21600.0 : 86400.0;
            double interval = ALHSettings.Instance.checkInterval * dayLength; // Custom interval in settings

            if (currentTime - lastCheckTime >= interval)
            {
                lastCheckTime = currentTime; // Update last check time
                return true;
            }

            return false;
        }

        void ProcessScienceForAllVessels()
        {
            DebugLog("[AutomaticLabHousekeeper] Checking science for ALL vessels...");

            foreach (Vessel vessel in FlightGlobals.Vessels)
            {
                if (!HasScienceLab(vessel)) continue;

                DebugLog("[AutomaticLabHousekeeper] ============================================================");

                // Check if ALH is on this vessel
                if (!HasALHModule(vessel))
                {
                    DebugLog($"[AutomaticLabHousekeeper] Skipping {vessel.vesselName}, no ALH module found.");
                    continue;
                }
                if (!HasConnectionToKSC(vessel))
                {
                    DebugLog($"[AutomaticLabHousekeeper] Skipping {vessel.vesselName}, no connection to KSC.");
                    continue;
                }

                DebugLog($"[AutomaticLabHousekeeper] Processing vessel: {vessel.vesselName}");

                // Process loaded vessels
                if (vessel.loaded)
                {
                    foreach (Part part in vessel.Parts)
                    {
                        var lab = part.FindModuleImplementing<ModuleScienceLab>();
                        if (lab == null) continue;

                        var alhModule = part.FindModuleImplementing<Module_AutomaticLabHousekeeper>();
                        if (alhModule == null) continue;

                        bool transmissionEnabled = alhModule.transmissionAutomationEnabled;
                        bool dataPullingEnabled = alhModule.dataPullingEnabled;

                        if (transmissionEnabled)
                        {
                            TransferScienceFromLab(vessel, part, lab, alhModule);
                        }

                        if (dataPullingEnabled)
                        {
                            PullDataIntoLab(vessel, part, alhModule);
                        }
                    }
                }
                else // Process unloaded vessels
                {
                    foreach (ProtoPartSnapshot protoPart in vessel.protoVessel.protoPartSnapshots)
                    {
                        ProtoPartModuleSnapshot labModule = protoPart.modules.FirstOrDefault(m => m.moduleName == "ModuleScienceLab");
                        ProtoPartModuleSnapshot alhModule = protoPart.modules.FirstOrDefault(m => m.moduleName == "Module_AutomaticLabHousekeeper");
                        ProtoPartModuleSnapshot converterModule = protoPart.modules.FirstOrDefault(m => m.moduleName == "ModuleScienceConverter" || m.moduleName == "CW_ModuleScienceConverter");

                        if (labModule == null || alhModule == null) continue;

                        bool transmissionEnabled = bool.Parse(alhModule.moduleValues.GetValue("transmissionAutomationEnabled"));
                        bool dataPullingEnabled = bool.Parse(alhModule.moduleValues.GetValue("dataPullingEnabled"));

                        if (transmissionEnabled)
                        {
                            SimulateScienceProcessingForUnloadedLab(vessel, protoPart, labModule, converterModule);
                            TransferScienceFromUnloadedLab(vessel, protoPart, labModule, alhModule);
                        }

                        if (dataPullingEnabled)
                        {
                            PullDataIntoUnloadedLab(vessel, protoPart, alhModule);
                        }
                    }
                }
            }
        }

        bool HasScienceLab(Vessel vessel)
        {
            return vessel.loaded
                ? vessel.Parts.Any(p => p.FindModuleImplementing<ModuleScienceLab>() != null)
                : vessel.protoVessel.protoPartSnapshots.Any(protoPart =>
                    protoPart.modules.Any(protoModule => protoModule.moduleName == "ModuleScienceLab"));
        }

        bool HasALHModule(Vessel vessel)
        {
            return vessel.loaded
                ? vessel.Parts.Any(p => p.FindModuleImplementing<Module_AutomaticLabHousekeeper>() != null)
                : vessel.protoVessel.protoPartSnapshots.Any(protoPart =>
                    protoPart.modules.Any(protoModule => protoModule.moduleName == "Module_AutomaticLabHousekeeper"));
        }

        bool HasConnectionToKSC(Vessel vessel)
        {
            // If CommNet is enabled, require a valid connection object and IsConnected
            if (HighLogic.CurrentGame.Parameters.Difficulty.EnableCommNet)
            {
                return vessel.Connection != null && vessel.Connection.IsConnected;
            }
            // If CommNet is not enabled, always return true (no communication restrictions)
            return true;
        }

        void TransferScienceFromLab(Vessel vessel, Part part, ModuleScienceLab lab, PartModule alhModule)
        {
            Debug.Log($"[AutomaticLabHousekeeper] Processing science transfer for lab {part.partName} in LOADED vessel {vessel.vesselName}");
            float ecPerMit = GetECPerMit(alhModule);
            if (ecPerMit < 0)
            {
                Debug.LogWarning($"[AutomaticLabHousekeeper] Invalid ecPerMit value ({ecPerMit}) in ALH module on {part.partName}. Skipping science transfer.");
            }
            else
            {
                if (lab.storedScience >= 1)
                {
                    float wholeScience = Mathf.Floor(lab.storedScience);
                    float remainingScience = lab.storedScience - wholeScience;
                    float requiredEC = wholeScience * GetMitPerScience() * ecPerMit;
                    double ecAvailable = getAvailableEC(vessel);

                    if (ecAvailable < requiredEC)
                    {
                        Debug.Log($"[AutomaticLabHousekeeper] Not enough EC ({ecAvailable:F1} / {requiredEC:F1}) for science transfer on {vessel.vesselName}");
                        ScreenMessages.PostScreenMessage(Localizer.Format("#autoLOC_ALH_0031", requiredEC, ecAvailable, vessel.vesselName), 10f, ScreenMessageStyle.UPPER_CENTER);
                    }
                    else
                    {
                        DebugLog($"[AutomaticLabHousekeeper] Sufficient EC ({ecAvailable:F1} / {requiredEC:F1}) for science transfer on {vessel.vesselName}");
                        // Consume EC
                        double ecTaken = vessel.rootPart.RequestResource("ElectricCharge", (double)requiredEC);
                        DebugLog($"[AutomaticLabHousekeeper] Consumed {ecTaken:F2} EC for transmitting {wholeScience} science from {vessel.vesselName}");
                        DebugLog($"[AutomaticLabHousekeeper] Transferring {wholeScience} science from {vessel.vesselName} to R&D, keeping {remainingScience} science in the lab");
                        // Add science to R&D and update lab storage
                        ResearchAndDevelopment.Instance.AddScience(wholeScience, TransactionReasons.ScienceTransmission);
                        lab.storedScience = remainingScience;

                        ScreenMessages.PostScreenMessage(Localizer.Format("#autoLOC_ALH_0003", wholeScience, vessel.vesselName), 10f, ScreenMessageStyle.UPPER_CENTER);
                    }


                }
                else
                {
                    Debug.Log("[AutomaticLabHousekeeper] Not enough storedScience");
                }
            }
        }

        void TransferScienceFromUnloadedLab(Vessel vessel, ProtoPartSnapshot protoPart, ProtoPartModuleSnapshot labModule, ProtoPartModuleSnapshot alhModule)
        {
            Debug.Log($"[AutomaticLabHousekeeper] Processing science transfer for lab {protoPart.partName} in UNLOADED vessel {vessel.vesselName}");
            float ecPerMit = GetECPerMit(alhModule);
            if (ecPerMit < 0)
            {
                Debug.LogWarning($"[AutomaticLabHousekeeper] Invalid ecPerMit value ({ecPerMit}) in ALH module on {protoPart.partName}. Skipping science transfer.");
            }
            else
            {
                float storedScience = float.Parse(labModule.moduleValues.GetValue("storedScience"));
                if (storedScience >= 1)
                {
                    float wholeScience = Mathf.Floor(storedScience);
                    float remainingScience = storedScience - wholeScience;
                    float requiredEC = wholeScience * GetMitPerScience() * ecPerMit;
                    double ecAvailable = getAvailableEC(vessel);

                    if (ecAvailable < requiredEC)
                    {
                        Debug.Log($"[AutomaticLabHousekeeper] Not enough EC ({ecAvailable:F1} / {requiredEC:F1}) for science transfer on {vessel.vesselName} (UNLOADED)");
                        ScreenMessages.PostScreenMessage(Localizer.Format("#autoLOC_ALH_0031", requiredEC, ecAvailable, vessel.vesselName), 10f, ScreenMessageStyle.UPPER_CENTER);
                    }
                    else
                    {
                        DebugLog($"[AutomaticLabHousekeeper] Sufficient EC ({ecAvailable:F1} / {requiredEC:F1}) for science transfer on {vessel.vesselName} (UNLOADED)");
                        DebugLog($"[AutomaticLabHousekeeper] Transferring {wholeScience} science from {vessel.vesselName} to R&D, keeping {remainingScience} science in the lab");
                        // For unloaded, we cannot request EC, so we just check
                        // Add science to R&D and update lab storage
                        ResearchAndDevelopment.Instance.AddScience(wholeScience, TransactionReasons.ScienceTransmission);
                        labModule.moduleValues.SetValue("storedScience", remainingScience.ToString("F2"));

                        ScreenMessages.PostScreenMessage(Localizer.Format("#autoLOC_ALH_0003", wholeScience, vessel.vesselName), 10f, ScreenMessageStyle.UPPER_CENTER);
                    }
                }
                else
                {
                    Debug.Log("[AutomaticLabHousekeeper] Not enough storedScience");
                }
            }
        }

        void SimulateScienceProcessingForUnloadedLab(Vessel vessel, ProtoPartSnapshot protoPart, ProtoPartModuleSnapshot labModule, ProtoPartModuleSnapshot converterModule)
        {
            Debug.Log($"[AutomaticLabHousekeeper] Simulating Science for Unloaded Lab in Vessel {vessel.vesselName}");

            float dataStored = float.Parse(labModule.moduleValues.GetValue("dataStored"));
            DebugLog($"[AutomaticLabHousekeeper] dataStored {dataStored}");
            float storedScience = float.Parse(labModule.moduleValues.GetValue("storedScience"));
            DebugLog($"[AutomaticLabHousekeeper] storedScience {storedScience}");
            bool isActivated = bool.Parse(converterModule.moduleValues.GetValue("IsActivated"));
            DebugLog($"[AutomaticLabHousekeeper] isActivated {isActivated}");

            if (!isActivated)
            {
                Debug.Log("[AutomaticLabHousekeeper] ConverterModule not activated");
                return;
            }
            if (dataStored <= 0)
            {
                Debug.Log("[AutomaticLabHousekeeper] No dataStored on-board");
                return;
            }

            ConfigNode partConfig = PartLoader.getPartInfoByName(protoPart.partName).partConfig;
            ConfigNode moduleNode = partConfig.GetNodes("MODULE")
                .FirstOrDefault(n =>
                    n.GetValue("name") == "ModuleScienceConverter" ||
                    n.GetValue("name") == "CW_ModuleScienceConverter");

            float scienceCap = float.Parse(moduleNode.GetValue("scienceCap"));
            DebugLog($"[AutomaticLabHousekeeper] scienceCap {scienceCap}");

            float dataProcessingMultiplier = 0.5f; // Default value
            string value = string.Empty; // Initialize an empty string
            if (moduleNode.TryGetValue("dataProcessingMultiplier", ref value))
            {
                if (!float.TryParse(value, out dataProcessingMultiplier))
                {
                    DebugLog($"[AutomaticLabHousekeeper] Failed to parse dataProcessingMultiplier, using default {dataProcessingMultiplier}");
                }
            }
            else
            {
                DebugLog($"[AutomaticLabHousekeeper] dataProcessingMultiplier not found, using default {dataProcessingMultiplier}");
            }
            DebugLog($"[AutomaticLabHousekeeper] dataProcessingMultiplier {dataProcessingMultiplier}");

            float scientistBonus = float.Parse(moduleNode.GetValue("scientistBonus"));
            DebugLog($"[AutomaticLabHousekeeper] scientistBonus {scientistBonus}");
            float researchTime = float.Parse(moduleNode.GetValue("researchTime"));
            DebugLog($"[AutomaticLabHousekeeper] researchTime {researchTime}");
            float scienceMultiplier = float.Parse(moduleNode.GetValue("scienceMultiplier"));
            DebugLog($"[AutomaticLabHousekeeper] scienceMultiplier {scienceMultiplier}");

            double lastUpdateTime = double.Parse(converterModule.moduleValues.GetValue("lastUpdateTime"));
            DebugLog($"[AutomaticLabHousekeeper] lastUpdateTime {lastUpdateTime}");
            double currentTime = Planetarium.GetUniversalTime();
            double timeElapsed = currentTime - lastUpdateTime;

            float totalScientistLevel;
            if (converterModule.moduleName == "CW_ModuleScienceConverter")
            {
                // Special handling for CW_ModuleScienceConverter
                totalScientistLevel = GetAIScientistsLevel(vessel, protoPart, scientistBonus);
                DebugLog($"[AutomaticLabHousekeeper] CW_ModuleScienceConverter détecté, totalScientistLevel : {totalScientistLevel}");
            }
            else
            {
                // Normal calculation based on crew
                totalScientistLevel = CalculateScientistLevel(protoPart, scientistBonus);
                DebugLog($"[AutomaticLabHousekeeper] ModuleScienceConverter détecté, totalAIScientistLevel calculé : {totalScientistLevel}");
            }
            double secondsPerDay = GameSettings.KERBIN_TIME ? 21600.0 : 86400.0;
            float scienceRatePerDay = (float)(secondsPerDay * totalScientistLevel * dataStored * dataProcessingMultiplier * scienceMultiplier) / Mathf.Pow(10f, researchTime);
            DebugLog($"[AutomaticLabHousekeeper] scienceRatePerDay {scienceRatePerDay}");
            float scienceRate = scienceRatePerDay / (float)secondsPerDay;
            DebugLog($"[AutomaticLabHousekeeper] scienceRate {scienceRate}");

            // Compute total science generated over elapsed time
            float scienceGenerated = scienceRate * (float)timeElapsed;

            // Ensure generated science doesn't exceed science cap
            if (storedScience + scienceGenerated >= scienceCap)
            {
                scienceGenerated = scienceCap - storedScience;
            }
            DebugLog($"[AutomaticLabHousekeeper] scienceGenerated {scienceGenerated}");

            // Convert dataStored into science at the correct rate
            float dataUsed = scienceGenerated / scienceMultiplier;
            DebugLog($"[AutomaticLabHousekeeper] dataUsed {dataUsed}");

            // Ensure science storage does not exceed the cap
            float newStoredScience = storedScience + scienceGenerated;
            DebugLog($"[AutomaticLabHousekeeper] newStoredScience {newStoredScience}");

            // Update remaining dataStored
            float newDataStored = dataStored - dataUsed;

            // Update ProtoModule values
            labModule.moduleValues.SetValue("storedScience", newStoredScience.ToString("F2"));
            labModule.moduleValues.SetValue("dataStored", newDataStored.ToString("F2"));
            converterModule.moduleValues.SetValue("lastUpdateTime", currentTime.ToString());

            Debug.Log($"[AutomaticLabHousekeeper] Simulated {scienceGenerated} science for {vessel.vesselName} (Data remaining: {newDataStored}, Stored Science: {newStoredScience}, Total Scientist Level: {totalScientistLevel})");
        }

        float CalculateScientistLevel(ProtoPartSnapshot protoPart, float scientistBonus)
        {
            float totalScientistLevel = 0;

            foreach (ProtoCrewMember crew in protoPart.protoModuleCrew) // Only count crew inside this part
            {
                if (crew.experienceTrait.Effects.Any(effect => effect.GetType().Name == "ScienceSkill"))
                {
                    DebugLog($"[AutomaticLabHousekeeper] Detected scientist {crew} with stars {crew.experienceLevel}");

                    totalScientistLevel += (float)(1.0 + (double)scientistBonus * (double)crew.experienceLevel);
                }
                else
                {
                    DebugLog($"[AutomaticLabHousekeeper] Detected crew with trait {crew.experienceTrait.Title}");
                }
            }

            return Mathf.Max(totalScientistLevel, 0.0f); // If no scientists, processing stops
        }

        float GetAIScientistsLevel(Vessel vessel, ProtoPartSnapshot converterPart, float scientistBonus)
        {
            float totalLevel = 0f;

            // Find the parent index of the converter part
            int parentIndex = converterPart.parentIdx;
            if (parentIndex < 0 || parentIndex >= vessel.protoVessel.protoPartSnapshots.Count)
                return 0f;

            // Converter part's parent
            ProtoPartSnapshot parentPart = vessel.protoVessel.protoPartSnapshots[parentIndex];

            // All chips with this parent
            var chips = vessel.protoVessel.protoPartSnapshots
                .Where(p => p.parentIdx == parentIndex);

            foreach (var chip in chips)
            {
                foreach (var module in chip.modules)
                {
                    if (module.moduleName == "CW_AIScientists")
                    {
                        bool isActive = false;
                        float level = 0f;

                        if (module.moduleValues.HasValue("isActive"))
                            bool.TryParse(module.moduleValues.GetValue("isActive"), out isActive);

                        if (module.moduleValues.HasValue("level"))
                            float.TryParse(module.moduleValues.GetValue("level"), out level);

                        if (isActive)
                        {
                            DebugLog($"[AutomaticLabHousekeeper] Found active AI Scientist on {chip.partName}, level {level}");
                            totalLevel += (1.0f + scientistBonus * level);
                        }
                    }
                }
            }
            return Mathf.Max(totalLevel, 0.0f);
        }

        void PullDataIntoLab(Vessel vessel, Part part, Module_AutomaticLabHousekeeper alhModule)
        {
            DebugLog($"[AutomaticLabHousekeeper] Attempting data pull for loaded lab {part.partInfo.name} on-board {vessel.vesselName}...");

            Part storagePart = part.vessel.Parts.FirstOrDefault(p => p.persistentId.ToString() == alhModule.selectedExperimentStorageUnit);
            if (storagePart == null)
            {
                DebugLog($"[AutomaticLabHousekeeper] No valid storage unit found for {part.partInfo.name}, skipping.");
                return;
            }

            ModuleScienceContainer storageContainer = storagePart.FindModuleImplementing<ModuleScienceContainer>();
            ModuleScienceLab lab = part.FindModuleImplementing<ModuleScienceLab>();

            if (storageContainer == null || lab == null)
            {
                DebugLog($"[AutomaticLabHousekeeper] Missing storage container or lab on {part.partInfo.name}");
                return;
            }

            List<ScienceData> storedExperiments = storageContainer.GetData().ToList();
            float availableDataSpace = lab.dataStorage - lab.dataStored;

            if (storedExperiments.Count == 0 || availableDataSpace <= 0)
            {
                DebugLog("[AutomaticLabHousekeeper] No experiments available or lab is full.");
                return;
            }

            foreach (ScienceData experiment in storedExperiments)
            {
                float processedData = CalculateProcessedData(experiment, part);

                if (processedData <= availableDataSpace)
                {
                    lab.dataStored += processedData;
                    availableDataSpace -= processedData;
                    storageContainer.DumpData(experiment);

                    DebugLog($"[AutomaticLabHousekeeper] Transferred experiment {experiment.subjectID} with {processedData} data from {storagePart.partInfo.name} to lab.");
                }
                else
                {
                    DebugLog($"[AutomaticLabHousekeeper] Not enough space in lab {part.partName} to store experiment {experiment.subjectID}. Current stored Data: {lab.dataStored}");
                }
            }
        }

        void PullDataIntoUnloadedLab(Vessel vessel, ProtoPartSnapshot protoPart, ProtoPartModuleSnapshot alhModule)
        {
            DebugLog($"[AutomaticLabHousekeeper] Attempting data pull for unloaded lab {protoPart.partInfo.name} on-board {vessel.vesselName}...");

            string selectedStorageUnit = alhModule.moduleValues.GetValue("selectedExperimentStorageUnit");
            if (selectedStorageUnit == "None")
            {
                DebugLog("[AutomaticLabHousekeeper] Not able to pull data, Experiment Storage Unit None");
                return;
            }

            ProtoPartSnapshot storagePart = vessel.protoVessel.protoPartSnapshots.FirstOrDefault(p => p.persistentId.ToString() == selectedStorageUnit);
            if (storagePart == null)
            {
                DebugLog("[AutomaticLabHousekeeper] Not able to pull data, Storage Part faulty");
                return;
            }

            ProtoPartModuleSnapshot storageContainer = storagePart.modules.FirstOrDefault(m => m.moduleName == "ModuleScienceContainer");
            ProtoPartModuleSnapshot labModule = protoPart.modules.FirstOrDefault(m => m.moduleName == "ModuleScienceLab");

            if (storageContainer == null || labModule == null)
            {
                DebugLog("[AutomaticLabHousekeeper] Missing storage container or lab on protoPart");
                return;
            }

            // Get stored Experiments
            ConfigNode[] storedExperiments = storageContainer.moduleValues.GetNodes("ScienceData");

            if (storedExperiments.Length == 0)
            {
                DebugLog($"[AutomaticLabHousekeeper] No stored experiments in {storagePart.partName}");
                return;
            }

            // Retrieve dataStorage from the part config if not found in the module values
            ConfigNode partConfig = PartLoader.getPartInfoByName(protoPart.partName).partConfig;

            // Get the ModuleScienceLab node inside partConfig
            ConfigNode labModuleConfig = partConfig.GetNodes("MODULE").FirstOrDefault(n => n.GetValue("name") == "ModuleScienceLab");

            if (labModuleConfig == null)
            {
                Debug.LogError($"[AutomaticLabHousekeeper] ERROR: Could not find ModuleScienceLab config for {protoPart.partName}");
                return;
            }

            // Retrieve dataStorage from part config
            float dataStorage = float.Parse(labModuleConfig.GetValue("dataStorage"));
            DebugLog($"[AutomaticLabHousekeeper] Found dataStorage = {dataStorage} for {protoPart.partName}");

            // Get the current dataStored from protoModule
            string dataStoredStr = labModule.moduleValues.GetValue("dataStored");
            float dataStored = string.IsNullOrEmpty(dataStoredStr) ? 0f : float.Parse(dataStoredStr);

            float availableDataSpace = dataStorage - dataStored;

            if (availableDataSpace <= 0)
            {
                DebugLog($"[AutomaticLabHousekeeper] Lab in {protoPart.partName} is full.");
                return;
            }

            foreach (ConfigNode experimentNode in storedExperiments)
            {
                float processedData = CalculateProcessedDataUnloaded(experimentNode, vessel.protoVessel);

                if (processedData <= availableDataSpace)
                {
                    // Add the processed data to the lab
                    availableDataSpace -= processedData;
                    float newStoredData = float.Parse(labModule.moduleValues.GetValue("dataStored")) + processedData;
                    labModule.moduleValues.SetValue("dataStored", newStoredData.ToString("F2"));

                    // Remove experiment data from storage
                    storageContainer.moduleValues.RemoveNode(experimentNode);

                    DebugLog($"[AutomaticLabHousekeeper] Transferred experiment {experimentNode.GetValue("subjectID")} with {processedData} data from {storagePart.partName} to {protoPart.partName}.");
                }
                else
                {
                    DebugLog($"[AutomaticLabHousekeeper] Not enough space in lab {protoPart.partName} to store experiment {experimentNode.GetValue("subjectID")}. Current stored Data: {labModule.moduleValues.GetValue("dataStored")}");
                }
            }
        }

        float CalculateProcessedData(ScienceData experiment, Part part)
        {
            // Retrieve the ScienceSubject associated with this experiment
            ScienceSubject subject = ResearchAndDevelopment.GetSubjectByID(experiment.subjectID);

            if (subject == null)
            {
                DebugLog($"[AutomaticLabHousekeeper] WARNING: ScienceSubject for {experiment.subjectID} not found!");
                return 0f;
            }

            // Get the correct science value
            float scienceValue = ResearchAndDevelopment.GetReferenceDataValue(experiment.dataAmount, subject)
                                 * HighLogic.CurrentGame.Parameters.Career.ScienceGainMultiplier;

            float SurfaceBonus = (part.vessel.LandedOrSplashed) ? 0.1f : 0f;
            float homeworldMultiplier = (part.vessel.mainBody.isHomeWorld && part.vessel.LandedOrSplashed) ? 0.1f : 1f;
            bool sameSOI = experiment.subjectID.Contains(part.vessel.mainBody.bodyName);
            float ContextBonus = sameSOI ? 0.25f : 0f;

            DebugLog($"[AutomaticLabHousekeeper] Science Value: {scienceValue}, Bonuses Applied: SurfaceBonus={SurfaceBonus}, ContextBonus={ContextBonus}, HomeworldMultiplier={homeworldMultiplier}");

            return scienceValue * (1 + SurfaceBonus) * (1 + ContextBonus) * homeworldMultiplier;
        }

        float CalculateProcessedDataUnloaded(ConfigNode experimentNode, ProtoVessel protoVessel)
        {
            if (experimentNode == null)
            {
                Debug.LogError("[AutomaticLabHousekeeper] Experiment node is null!");
                return 0f;
            }

            // Extract the experiment subject ID
            string subjectID = experimentNode.GetValue("subjectID");

            // Get the corresponding science subject
            ScienceSubject subject = ResearchAndDevelopment.GetSubjectByID(subjectID);

            if (subject == null)
            {
                DebugLog($"[AutomaticLabHousekeeper] WARNING: ScienceSubject for {subjectID} not found!");
                return 0f;
            }

            // Extract the science data amount
            float dataAmount = float.Parse(experimentNode.GetValue("data"));

            // Get the actual science value
            float scienceValue = ResearchAndDevelopment.GetReferenceDataValue(dataAmount, subject)
                                 * HighLogic.CurrentGame.Parameters.Career.ScienceGainMultiplier;

            // Determine if the vessel is landed or splashed down
            bool landed = protoVessel.landed || protoVessel.splashed;

            // Get the celestial body the vessel is orbiting
            CelestialBody vesselBody = FlightGlobals.Bodies[protoVessel.orbitSnapShot.ReferenceBodyIndex];

            // Apply the surface and homeworld multipliers
            float SurfaceBonus = landed ? 0.1f : 0f;
            float homeworldMultiplier = (vesselBody.isHomeWorld && landed) ? 0.1f : 1f;

            // Determine if the experiment was taken in the same SOI as the lab
            bool sameSOI = experimentNode.GetValue("subjectID").Contains(vesselBody.bodyName);
            float ContextBonus = sameSOI ? 0.25f : 0f;

            DebugLog($"[AutomaticLabHousekeeper] Science Value: {scienceValue}, Bonuses Applied: " +
                      $"SurfaceBonus={SurfaceBonus}, ContextBonus={ContextBonus}, HomeworldMultiplier={homeworldMultiplier}");

            // Calculate final processed data
            return scienceValue * (1 + SurfaceBonus) * (1 + ContextBonus) * homeworldMultiplier;
        }

        void DebugLog(string message)
        {
            if (ALHSettings.Instance.enableDebug)
            {
                Debug.Log($"{message}");
            }
        }

        // Helper method to get mitPerScience (inverse of science per MB)
        private float GetMitPerScience()
        {
            return 1f; // Vanilla KSP uses 1 Mit per Science by default
            //TODO: Add support for mods that change this ratio
        }

        private double getAvailableEC(Vessel vessel)
        {
            if (vessel == null)
            {
                Debug.LogError("[AutomaticLabHousekeeper] getAvailableEC called with null vessel.");
                return -1;
            }

            double ecAvailable = 0;
            if (vessel.loaded)
            {
                foreach (Part p in vessel.Parts)
                {
                    foreach (PartResource r in p.Resources)
                    {
                        if (r.resourceName == "ElectricCharge")
                            ecAvailable += r.amount;
                    }

                }
            }
            else
            {
                    foreach (var PartSnap in vessel.protoVessel.protoPartSnapshots)
                    {
                        foreach (var res in PartSnap.resources)
                        {
                            if (res.resourceName == "ElectricCharge")
                                ecAvailable += double.Parse(res.amount.ToString("F2"));
                        }
                    }
            }

            return ecAvailable;
        }

        private float GetECPerMit(object alhModule)
        {
            float ecPerMit = -1f; // fallback default
            string value = null; 
            switch (alhModule)
            {
                case Module_AutomaticLabHousekeeper loadedModule:
                    value = loadedModule.ecPerMit;
                    break;
                case ProtoPartModuleSnapshot protoModule when protoModule.moduleValues.HasValue("ecPerMit"):
                    value = protoModule.moduleValues.GetValue("ecPerMit");
                    break;
                default:
                    Debug.LogWarning($"[AutomaticLabHousekeeper] GetECPerMit called with unknown alhModule type. Using fallback EC per Mit = {ecPerMit}");
                    return ecPerMit;

            }

            if (!string.IsNullOrEmpty(value) && float.TryParse(value, out float ParsedEcPerMit))
                return ParsedEcPerMit;
            else
            {
                DebugLog($"[AutomaticLabHousekeeper] Failed to parse ecPerMit ('{value}'), using fallback -1");
                return ecPerMit;                
            }
        }
    }
}



