using KSP.IO;
using KSP.UI.Screens;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ForScience
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    class ForScience : MonoBehaviour
    {
        // Singleton
        private static ForScience instance;
        public static ForScience Instance { get { return instance; } }
        private ApplicationLauncherButton FSAppButton;
        private PluginConfiguration config;
        private long delay;
        // available experiments
        private List<ModuleScienceExperiment> experiments;
        //states
        CelestialBody stateBody;
        string stateBiome;
        ExperimentSituations stateSituation = 0;
        // timer
        System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();

        private void Start()
        {
            if (!(HighLogic.CurrentGame.Mode == Game.Modes.CAREER || HighLogic.CurrentGame.Mode == Game.Modes.SCIENCE_SANDBOX))
            {
                Log("Game mode not supported, unloading...");
                Destroy(this);
                return;
            }
            config = PluginConfiguration.CreateForType<ForScience>();
            config.load();
            delay = config.GetValue<long>("delay");
            GameEvents.onGUIApplicationLauncherReady.Add(SetupAppButton);
            GameEvents.onLevelWasLoaded.Add(OnLevelWasLoaded);
            GameEvents.onVesselChange.Add(OnVesselChange);
            GameEvents.onVesselWasModified.Add(OnVesselChange);
            GameEvents.onGamePause.Add(OnGamePause);
            GameEvents.onGameUnpause.Add(OnGameUnpause);
            Log("Loaded");
        }

        void OnFlightReady()
        {
            stopwatch.Restart();
        }

        void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(SetupAppButton);
            GameEvents.onLevelWasLoaded.Remove(OnLevelWasLoaded);
            GameEvents.onVesselChange.Remove(OnVesselChange);
            GameEvents.onVesselWasModified.Remove(OnVesselChange);
            GameEvents.onGamePause.Remove(OnGamePause);
            GameEvents.onGameUnpause.Remove(OnGameUnpause);
            if (FSAppButton != null) ApplicationLauncher.Instance.RemoveModApplication(FSAppButton);
            if (instance == this)
                instance = null;
        }

        void SetupAppButton()
        {
            if (FSAppButton == null)
            {
                FSAppButton = ApplicationLauncher.Instance.AddModApplication(
                       onTrue: ToggleCollection,
                       onFalse: ToggleCollection,
                       onHover: null,
                       onHoverOut: null,
                       onEnable: null,
                       onDisable: null,
                       visibleInScenes: ApplicationLauncher.AppScenes.FLIGHT,
                       texture: GetIconTexture(config.GetValue<bool>("autoTransfer"))
                   );
            }
        }

        private void FixedUpdate() // running in update
        {
            // stopwatch active?
            if (!stopwatch.IsRunning)
                return;
            // check if we already need to run a science check
            if (stopwatch.ElapsedMilliseconds < delay)
                return;
            // check if everything is ready
            if (!IsReady())
                return;
            TransferScience();// always move experiment data to science container, mostly for manual experiments
            if (StatesHaveChanged()) // if we are in a new state, we will check and run experiments
            {
                RunScience();
            }
            // add delay to next update time
            stopwatch.Restart();
        }

        public void OnLevelWasLoaded(GameScenes scene)
        {
            OnVesselChange(FlightGlobals.ActiveVessel);
        }

        public void OnGamePause()
        {
            stopwatch.Stop();
            stopwatch.Reset();
        }

        public void OnGameUnpause()
        {
            stopwatch.Start();
        }

        private void OnVesselChange(Vessel data)
        {
            Log("OnVesselChange");
            if (FlightGlobals.ActiveVessel == null)
            {
                Log("ActiveVessel is null! this shouldn't happen!");
                return;
            }
            experiments = GetExperimentList();
            stopwatch.Reset();
            stopwatch.Start();
        }

        private bool IsReady()
        {
            if (!FlightGlobals.ready)
            {
                Log("FlightGlobals aren't ready");
                return false;
            }
            if (!FlightGlobals.ActiveVessel.IsControllable)
            {
                Log("Vessel isn't controllable");
                return false;
            }
            return true;
        }

        void TransferScience() // automaticlly find, transer and consolidate science data on the vessel
        {
            if (ActiveContainer().GetActiveVesselDataCount() != ActiveContainer().GetScienceCount()) // only actually transfer if there is data to move
            {
                Log("Transfering science to container.");
                ActiveContainer().StoreData(GetExperimentList().Cast<IScienceDataContainer>().ToList(), true); // this is what actually moves the data to the active container
                var containerstotransfer = GetContainerList(); // a temporary list of our containers
                containerstotransfer.Remove(ActiveContainer()); // we need to remove the container we storing the data in because that would be wierd and buggy
                ActiveContainer().StoreData(containerstotransfer.Cast<IScienceDataContainer>().ToList(), true); // now we store all data from other containers
            }
        }

        void RunScience() // this is primary business logic for finding and running valid experiments
        {
            if (experiments.Count > 0) // any experiments?
            {
                foreach (ModuleScienceExperiment currentExperiment in experiments) // loop through all the experiments onboard
                {
                    Log("Checking experiment: '" + currentExperiment.experiment.experimentTitle + "'");
                    if (ActiveContainer().HasData(NewScienceData(currentExperiment))) // skip data we already have onboard
                        continue;
                    if (currentExperiment.experiment.id == "surfaceSample" && !SurfaceSamplesUnlocked()) // check to see is surface samples are unlocked
                        continue;
                    if (!currentExperiment.rerunnable && !IsScientistOnBoard) // no cheating goo and materials here
                        continue;
                    if (!currentExperiment.experiment.IsAvailableWhile(CurrentSituation(), CurrentBody())) // this experiement isn't available here so we skip it
                        continue;
                    if (CurrentScienceValue(currentExperiment) < 0.1) // this experiment has no more value so we skip it
                        continue;
                    Log("Running experiment: '" + CurrentScienceSubject(currentExperiment.experiment).id + "'");
                    ActiveContainer().AddData(NewScienceData(currentExperiment)); //manually add data to avoid deployexperiment state issues
                }
            }
        }

        private bool SurfaceSamplesUnlocked() // checking that the appropriate career unlocks are flagged
        {
            return GameVariables.Instance.UnlockedEVA(ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.AstronautComplex))
                && GameVariables.Instance.UnlockedFuelTransfer(ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.ResearchAndDevelopment));
        }

        float CurrentScienceValue(ModuleScienceExperiment currentExperiment) // the ammount of science an experiment should return
        {
            return ResearchAndDevelopment.GetScienceValue(
                currentExperiment.experiment.baseValue * currentExperiment.experiment.dataScale,
                CurrentScienceSubject(currentExperiment.experiment)
            );
        }

        ScienceData NewScienceData(ModuleScienceExperiment currentExperiment) // construct our own science data for an experiment
        {
            return new ScienceData(
                amount: currentExperiment.experiment.baseValue * CurrentScienceSubject(currentExperiment.experiment).dataScale,
                xmitValue: currentExperiment.xmitDataScalar,
                xmitBonus: 0f,
                id: CurrentScienceSubject(currentExperiment.experiment).id,
                dataName: CurrentScienceSubject(currentExperiment.experiment).title
            );
        }

        CelestialBody CurrentBody()
        {
            return FlightGlobals.ActiveVessel.mainBody;
        }

        ExperimentSituations CurrentSituation()
        {
            return ScienceUtil.GetExperimentSituation(FlightGlobals.ActiveVessel);
        }

        string CurrentBiome() // some crazy nonsense to get the actual biome string
        {
            if (FlightGlobals.ActiveVessel != null)
                if (FlightGlobals.ActiveVessel.mainBody.BiomeMap != null)
                    return !string.IsNullOrEmpty(FlightGlobals.ActiveVessel.landedAt)
                                    ? Vessel.GetLandedAtString(FlightGlobals.ActiveVessel.landedAt)
                                    : ScienceUtil.GetExperimentBiome(FlightGlobals.ActiveVessel.mainBody,
                                                FlightGlobals.ActiveVessel.latitude, FlightGlobals.ActiveVessel.longitude);

            return string.Empty;
        }

        ScienceSubject CurrentScienceSubject(ScienceExperiment experiment)
        {
            string fixBiome = string.Empty; // some biomes don't have 4th string, so we just put an empty in to compare strings later
            if (experiment.BiomeIsRelevantWhile(CurrentSituation())) fixBiome = CurrentBiome();// for those that do, we add it to the string
            return ResearchAndDevelopment.GetExperimentSubject(experiment, CurrentSituation(), CurrentBody(), fixBiome, null);//ikr!, we pretty much did all the work already, jeez
        }

        ModuleScienceContainer ActiveContainer() // set the container to gather all science data inside, usualy this is the root command pod of the oldest vessel
        {
            return FlightGlobals.ActiveVessel.FindPartModulesImplementing<ModuleScienceContainer>().FirstOrDefault();
        }

        List<ModuleScienceExperiment> GetExperimentList() // a list of all experiments
        {
            return FlightGlobals.ActiveVessel.FindPartModulesImplementing<ModuleScienceExperiment>();
        }

        List<ModuleScienceContainer> GetContainerList() // a list of all science containers
        {
            return FlightGlobals.ActiveVessel.FindPartModulesImplementing<ModuleScienceContainer>(); // list of all experiments onboard
        }

        bool StatesHaveChanged() // Track our vessel state, it is used for thread control to know when to fire off new experiments since there is no event for this
        {
            if (CurrentBiome() != string.Empty)
            {
                if (CurrentSituation() != stateSituation | CurrentBody() != stateBody | CurrentBiome() != stateBiome)
                {
                    stateBody = CurrentBody();
                    stateSituation = CurrentSituation();
                    stateBiome = CurrentBiome();
                    Log("New States: Body = '" + stateBody + "', Situation = '" + stateSituation + "', Biome = '" + stateBiome + "'");
                    return true;
                }
            }
            return false;
        }

        void ToggleCollection() // This is our main toggle for the logic and changes the icon between green and red versions on the bar when it does so.
        {
            bool autoTransfer = config.GetValue<bool>("autoTransfer");
            autoTransfer = !autoTransfer;
            FSAppButton.SetTexture(GetIconTexture(autoTransfer));
            config.SetValue("autoTransfer", autoTransfer);
        }

        // check if there is a scientist onboard so we can rerun things like goo or scijrs
        bool IsScientistOnBoard => FlightGlobals.ActiveVessel.GetVesselCrew().Any(k => k.trait == KerbalRoster.scientistTrait);

        Texture2D GetIconTexture(bool b) // just returns the correct icon for the given state
        {
            if (b) return GameDatabase.Instance.GetTexture("ForScience/Icons/FS_active", false);
            else return GameDatabase.Instance.GetTexture("ForScience/Icons/FS_inactive", false);
        }

        void Log(string s)
        {
            Debug.Log("[ForScience!] " + s);
        }
    }
}