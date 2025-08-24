using KSP;
using UnityEngine;
using KSP.Localization;
using System.Collections.Generic;
using System.Collections;
using static PartModule;

namespace ALH
{
    [KSPModule("#autoLOC_ALH_0001")]
    public class Module_AutomaticLabHousekeeper : PartModule
    {
        [KSPField(isPersistant = true)]
        public bool transmissionAutomationEnabled = true;

        [KSPField(isPersistant = true)]
        public bool dataPullingEnabled = false;

        [KSPField(isPersistant = true)]
        public string selectedExperimentStorageUnit = "None";

        [KSPField(isPersistant = true)]
        public string ecPerMit = "-1.0"; //default to -1.0 to indicate uninitialized

        private bool selectingStorage = false;
        private List<Part> validStorageParts = new List<Part>();

        public override string GetInfo()
        {
            return base.GetInfo() + "#autoLOC_ALH_0004";
        }

        [KSPEvent(guiActive = true, guiActiveEditor = false, guiName = "#autoLOC_ALH_0304",
                  groupName = "ALH", groupDisplayName = "#autoLOC_ALH_0001")]
        public void ToggleLabAutomation()
        {
            transmissionAutomationEnabled = !transmissionAutomationEnabled;
            Debug.Log($"[AutomaticLabHousekeeper] Lab Automation set to {transmissionAutomationEnabled} for {part.partInfo.title}");
            UpdatePAW();
        }

        [KSPEvent(guiActive = true, guiActiveEditor = false, guiName = "#autoLOC_ALH_0305",
                  groupName = "ALH", groupDisplayName = "#autoLOC_ALH_0001")]
        public void ToggleDataPulling()
        {
            dataPullingEnabled = !dataPullingEnabled;
            Debug.Log($"[AutomaticLabHousekeeper] Data Pulling set to {dataPullingEnabled} for {part.partInfo.title}");

            if (!dataPullingEnabled)
            {
                selectedExperimentStorageUnit = "None"; // Reset to None when disabled
            }

            UpdatePAW();
        }

        [KSPEvent(guiActive = true, guiActiveEditor = false, guiName = "#autoLOC_ALH_0306",
                  groupName = "ALH", groupDisplayName = "#autoLOC_ALH_0001", active = false)]
        public void SelectExperimentStorageUnit()
        {
            if (selectingStorage) // Prevent multiple selections at once
            {
                CancelSelection();
                return;
            }

            if (vessel == null) // Ensure vessel exists
            {
                Debug.LogError("[AutomaticLabHousekeeper] No valid vessel found!");
                return;
            }

            validStorageParts = FindAvailableStorageUnits();

            if (validStorageParts.Count == 0)
            {
                ScreenMessages.PostScreenMessage(Localizer.Format("#autoLOC_ALH_0312"), 5f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }

            selectingStorage = true;
            ScreenMessages.PostScreenMessage(Localizer.Format("#autoLOC_ALH_0320"), 4f, ScreenMessageStyle.UPPER_CENTER);

            // Highlight valid storage units
            foreach (Part p in validStorageParts)
            {
                p.SetHighlight(true, false);
            }

            StartCoroutine(WaitForStorageSelection());
        }


        private IEnumerator WaitForStorageSelection()
        {
            while (selectingStorage)
            {
                if (Input.GetMouseButtonDown(0)) // Left-click to select a part
                {
                    Part selectedPart = GetPartUnderMouse();
                    if (selectedPart != null && validStorageParts.Contains(selectedPart))
                    {
                        selectedExperimentStorageUnit = selectedPart.persistentId.ToString();
                        Debug.Log($"[AutomaticLabHousekeeper] Selected {selectedPart.partInfo.title} as experiment storage.");
                        ScreenMessages.PostScreenMessage(Localizer.Format("#autoLOC_ALH_0313", selectedPart.partInfo.title), 5f, ScreenMessageStyle.UPPER_CENTER);
                        selectingStorage = false;
                    }
                }
                else if (Input.GetKeyDown(KeyCode.Escape)) // ESC cancels selection
                {
                    Debug.Log("[AutomaticLabHousekeeper] Experiment storage selection canceled.");
                    selectedExperimentStorageUnit = "None";
                    selectingStorage = false;

                    ScreenMessages.PostScreenMessage(Localizer.Format("#autoLOC_ALH_0321"), 3f, ScreenMessageStyle.UPPER_CENTER);
                }

                yield return null; // Wait for next frame
            }

            // Remove highlights
            foreach (Part p in validStorageParts)
            {
                p.SetHighlight(false, false);
            }

            validStorageParts.Clear();
            UpdatePAW();
        }

        private Part GetPartUnderMouse()
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            if (UnityEngine.Physics.Raycast(ray, out RaycastHit hit, 100f))
            {
                GameObject hitObject = hit.collider?.gameObject;
                Part part = hitObject?.GetComponentInParent<Part>();

                if (part != null && vessel.parts.Contains(part)) // Ensure it's part of this vessel
                {
                    return part;
                }
            }
            return null;
        }


        public List<Part> FindAvailableStorageUnits()
        {
            List<Part> containers = new List<Part>();
            if (vessel != null)
            {
                foreach (Part p in vessel.parts)
                {
                    if (p.FindModuleImplementing<ModuleScienceContainer>() != null)
                    {
                        containers.Add(p);
                    }
                }
            }
            return containers;
        }

        private void CancelSelection()
        {
            if (!selectingStorage) return; // Avoid redundant operations

            selectingStorage = false;

            if (selectedExperimentStorageUnit != "None")
            {
                selectedExperimentStorageUnit = "None";
                UpdatePAW(); // Only update UI if a real change happened
            }

            // Remove highlights
            foreach (Part p in validStorageParts)
            {
                p.SetHighlight(false, false);
            }

            validStorageParts.Clear();
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            if (HighLogic.LoadedSceneIsFlight)
            {
                CalculateAndStoreECPerMit();
                Debug.Log($"[AutomaticLabHousekeeper] Lab settings loaded for {part.partInfo.title}");
                UpdatePAW();
            }
        }

        public override void OnSave(ConfigNode node)
        {
            base.OnSave(node);
            CalculateAndStoreECPerMit();
        }

        private void CalculateAndStoreECPerMit()
        {
            if (part?.vessel == null || !part.vessel.loaded)
                return;

            float totalCombinableBandwidth = 0f;
            float totalCombinableEC = 0f;
            float lowestNonCombinableECPerMit = float.MaxValue;
            bool foundAny = false;

            foreach (Part p in part.vessel.Parts)
            {
                foreach (PartModule module in p.Modules)
                {
                    if (module.moduleName != "ModuleDataTransmitter")
                        continue;

                    float packetSize = -1f, packetResourceCost = 1f; // Default values, should be overridden by actual values
                    bool antennaCombinable = false;

                    if (float.TryParse(module.Fields.GetValue("packetSize").ToString() ?? "-1f", out packetSize))
                    if (float.TryParse(module.Fields.GetValue("packetResourceCost").ToString() ?? "1f", out packetResourceCost))
                    if (bool.TryParse(module.Fields.GetValue("antennaCombinable").ToString() ?? "false", out antennaCombinable))

                    if (packetSize <= 0f) continue;

                    float ecPerMitValue = packetResourceCost / packetSize;
                    foundAny = true;

                    if (antennaCombinable)
                    {
                        totalCombinableBandwidth += packetSize;
                        totalCombinableEC += packetResourceCost;
                    }
                    else
                    {
                        if (ecPerMitValue < lowestNonCombinableECPerMit)
                            lowestNonCombinableECPerMit = ecPerMitValue;
                    }
                }
            }

            float combinableECPerMit = totalCombinableBandwidth > 0f ? totalCombinableEC / totalCombinableBandwidth : float.MaxValue;
            float best = Mathf.Min(combinableECPerMit, lowestNonCombinableECPerMit);

            // Store in the module's moduleValues safely
            ecPerMit = (foundAny && best < float.MaxValue ? best : 1f).ToString("F4");

            if (!foundAny)
            {
                Debug.Log($"[AutomaticLabHousekeeper] No ModuleDataTransmitter found on vessel {part.vessel.vesselName}");
            }
        }

        public void UpdatePAW()
        {
            Events["ToggleLabAutomation"].guiName = transmissionAutomationEnabled
                ? Localizer.Format("#autoLOC_ALH_0307")  // "Disable Lab Automation"
                : Localizer.Format("#autoLOC_ALH_0308"); // "Enable Lab Automation"

            Events["ToggleDataPulling"].active = transmissionAutomationEnabled;
            Events["ToggleDataPulling"].guiName = dataPullingEnabled
                ? Localizer.Format("#autoLOC_ALH_0309")  // "Disable Automatic Data Pulling"
                : Localizer.Format("#autoLOC_ALH_0310"); // "Enable Automatic Data Pulling"

            Events["SelectExperimentStorageUnit"].active = transmissionAutomationEnabled && dataPullingEnabled;
            Events["SelectExperimentStorageUnit"].guiName = Localizer.Format("#autoLOC_ALH_0311", selectedExperimentStorageUnit);

            if (selectedExperimentStorageUnit == "None")
            {
                Events["SelectExperimentStorageUnit"].guiName = Localizer.Format("#autoLOC_ALH_0314"); // "Select Experiment Storage Unit"
            }

            // Refresh PAW
            if (HighLogic.LoadedSceneIsEditor)
            {
                GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
            }
            else if (HighLogic.LoadedSceneIsFlight)
            {
                UIPartActionWindow window = UIPartActionController.Instance?.GetItem(part);
                if (window != null) window.displayDirty = true;
            }
        }
    }
}
