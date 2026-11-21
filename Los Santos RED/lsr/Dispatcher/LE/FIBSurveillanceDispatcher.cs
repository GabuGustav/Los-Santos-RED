using ExtensionsMethods;
using LosSantosRED.lsr;
using LosSantosRED.lsr.Interface;
using LSR.Vehicles;
using Rage;
using Rage.Native;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

 [XmlRoot("FIBSurveillanceVehicles")]
public class FIBSurveillanceVehicleConfiguration
{
    [XmlArray("Vehicles")]
    [XmlArrayItem("Vehicle")]
    public List<FIBSurveillanceVehicleEntry> Vehicles { get; set; } = new List<FIBSurveillanceVehicleEntry>();
}

public class FIBSurveillanceVehicleEntry
{
    [XmlAttribute("model")]
    public string Model { get; set; }
}

public class FIBSurveillanceDispatcher
{
    private readonly IEntityProvideable World;
    private readonly IDispatchable Player;
    private readonly IAgencies Agencies;
    private readonly ISettingsProvideable Settings;
    private readonly IWeapons Weapons;
    private readonly INameProvideable Names;
    private readonly IModItems ModItems;
    private readonly IShopMenus ShopMenus;
    private readonly ICrimes Crimes;
    private readonly Dictionary<string, RuntimeTeam> RuntimeTeams = new Dictionary<string, RuntimeTeam>();
    private uint LastUpdateGameTime;
    private bool HasInitializedRuntime;
    private LawEnforcementIntelligence CurrentRuntimeIntelligence;
    private readonly HashSet<string> ConfiguredSurveillanceVehicleModels;
    private bool HasLoggedUnavailableFleet;
    private const string SurveillanceVehicleConfigPath = "Plugins\\LosSantosRED\\FIBSurveillanceVehicles.xml";

    private class RuntimeTeam
    {
        public string AssignmentID;
        public PedExt Driver;
        public VehicleExt Vehicle;
        public uint LastFollowCommandAt;
        public uint LastDetectionCheckAt;
        public bool DisengageCommandIssued;
    }

    public FIBSurveillanceDispatcher(IEntityProvideable world, IDispatchable player, IAgencies agencies,
        ISettingsProvideable settings, IWeapons weapons, INameProvideable names, IModItems modItems,
        IShopMenus shopMenus, ICrimes crimes)
    {
        World = world;
        Player = player;
        Agencies = agencies;
        Settings = settings;
        Weapons = weapons;
        Names = names;
        ModItems = modItems;
        ShopMenus = shopMenus;
        Crimes = crimes;
        ConfiguredSurveillanceVehicleModels = LoadSurveillanceVehicleConfiguration();
    }

    public void Update()
    {
        Mod.Player modPlayer = Player as Mod.Player;
        if (modPlayer == null || modPlayer.Character == null || !modPlayer.Character.Exists() ||
            modPlayer.LawEnforcementIntelligence == null ||
            Settings?.SettingsManager?.LawEnforcementIntelligenceSettings == null)
            return;
        LawEnforcementIntelligenceSettings settings = Settings.SettingsManager.LawEnforcementIntelligenceSettings;
        settings.Normalize();

        if (Game.GameTime - LastUpdateGameTime < 1000)
            return;
        LastUpdateGameTime = Game.GameTime;

        LawEnforcementIntelligence intelligence = modPlayer.LawEnforcementIntelligence;
        intelligence.Normalize();
        if (modPlayer.IsDead)
        {
            DisengageAll(intelligence, modPlayer.CurrentDateTime);
            return;
        }
        if (!ReferenceEquals(CurrentRuntimeIntelligence, intelligence))
        {
            foreach (RuntimeTeam oldTeam in RuntimeTeams.Values.ToList())
                CleanupTeam(oldTeam);
            RuntimeTeams.Clear();
            CurrentRuntimeIntelligence = intelligence;
            HasInitializedRuntime = false;
        }
        if (!HasInitializedRuntime)
        {
            HasInitializedRuntime = true;
            foreach (SurveillanceAssignment savedAssignment in intelligence.SurveillanceAssignments.Where(x =>
                x != null && x.State != SurveillanceAssignmentState.Available &&
                x.State != SurveillanceAssignmentState.Completed))
            {
                // Runtime entities are deliberately not restored from saves. Complete the stale assignment,
                // then allow a replacement team after a short reacquisition cooldown.
                SurveillanceAssignmentManager assignmentManager =
                    modPlayer.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager;
                savedAssignment.LastStateChangedAt = modPlayer.CurrentDateTime;
                if (savedAssignment.State != SurveillanceAssignmentState.Disengaging &&
                    savedAssignment.State != SurveillanceAssignmentState.Compromised)
                    assignmentManager?.Transition(savedAssignment, SurveillanceAssignmentState.Disengaging,
                        modPlayer.CurrentDateTime);
                assignmentManager?.Transition(savedAssignment, SurveillanceAssignmentState.Completed,
                    modPlayer.CurrentDateTime);
                savedAssignment.ReplacementRequired = true;
                savedAssignment.NextAssignmentAllowedAt = modPlayer.CurrentDateTime.AddMinutes(2);
                savedAssignment.Notes = "Runtime surveillance entities are not restored from saves; a replacement team may be assigned after the reacquisition cooldown.";
            }
        }
        if (!settings.EnableFIBSurveillance || settings.MaxActiveFIBTeams <= 0)
        {
            DisengageAll(intelligence, modPlayer.CurrentDateTime);
            return;
        }

        UpdateRuntimeTeams(intelligence, modPlayer.CurrentDateTime, settings);
        AssignEligibleInvestigations(intelligence, modPlayer.CurrentDateTime, settings);
    }

    public void Dispose()
    {
        Mod.Player modPlayer = Player as Mod.Player;
        if (modPlayer != null && modPlayer.LawEnforcementIntelligence != null)
            DisengageAll(modPlayer.LawEnforcementIntelligence, modPlayer.CurrentDateTime);
        else
        {
            foreach (RuntimeTeam team in RuntimeTeams.Values.ToList())
                CleanupTeam(team);
            RuntimeTeams.Clear();
        }
        CurrentRuntimeIntelligence = null;
        HasInitializedRuntime = false;
    }

    private void AssignEligibleInvestigations(LawEnforcementIntelligence intelligence, DateTime now,
        LawEnforcementIntelligenceSettings settings)
    {
        int activeCount = intelligence.SurveillanceAssignments.Count(x => x != null &&
            x.State != SurveillanceAssignmentState.Available &&
            x.State != SurveillanceAssignmentState.Completed);
        if (activeCount >= settings.MaxActiveFIBTeams)
            return;

        List<InvestigationRecord> candidates = intelligence.Investigations
            .Where(x => x != null && x.Stage >= InvestigationStage.Focused &&
                x.Stage != InvestigationStage.Suspended && x.Stage != InvestigationStage.Closed)
            .OrderByDescending(x => x.Stage)
            .ThenByDescending(x => x.LastActivityAt)
            .ToList();

        foreach (InvestigationRecord investigation in candidates)
        {
            if (activeCount >= settings.MaxActiveFIBTeams)
                break;
            bool alreadyAssigned = intelligence.SurveillanceAssignments.Any(x => x != null &&
                x.InvestigationID == investigation.ID &&
                x.State != SurveillanceAssignmentState.Available &&
                x.State != SurveillanceAssignmentState.Completed);
            bool inCooldown = intelligence.SurveillanceAssignments.Any(x => x != null &&
                x.InvestigationID == investigation.ID && x.NextAssignmentAllowedAt.HasValue &&
                now < x.NextAssignmentAllowedAt.Value);
            if (alreadyAssigned || inCooldown)
                continue;

            string teamID = Guid.NewGuid().ToString("N");
            RuntimeTeam team = SpawnTeam(teamID, settings);
            if (team == null)
                continue;

            if (!((Player as Mod.Player)?.LawEnforcementIntelligenceManager?.TryAssignSurveillance(
                intelligence, investigation.ID, teamID, now, settings) ?? false))
            {
                CleanupTeam(team);
                continue;
            }

            SurveillanceAssignment assignment = intelligence.SurveillanceAssignments
                .LastOrDefault(x => x != null && x.TeamID == teamID);
            if (assignment == null)
            {
                CleanupTeam(team);
                continue;
            }
            team.AssignmentID = assignment.ID;
            RuntimeTeams[assignment.ID] = team;
            (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.Transition(assignment, SurveillanceAssignmentState.Approaching, now);
            activeCount++;
        }
    }

    private RuntimeTeam SpawnTeam(string teamID, LawEnforcementIntelligenceSettings settings)
    {
        Agency fib = Agencies?.GetAgency("FIB");
        if (fib == null || fib.Vehicles == null || fib.Personnel == null)
            return null;

        // The external XML is the sole authority for which FIB fleet vehicles may be
        // used for covert surveillance. No name-based or marked-vehicle fallback is allowed.
        List<DispatchableVehicle> candidates = fib.Vehicles.Where(x => x != null && x.IsCar &&
            !x.IsMotorcycle && !string.IsNullOrWhiteSpace(x.ModelName) &&
            ConfiguredSurveillanceVehicleModels.Contains(x.ModelName.Trim())).ToList();
        DispatchablePerson agent = fib.Personnel.FirstOrDefault(x => x != null && !x.IsAnimal);
        if (candidates.Count == 0 || agent == null)
        {
            if (!HasLoggedUnavailableFleet)
            {
                string reason = candidates.Count == 0
                    ? "none of the XML-listed models are available as FIB dispatchable cars"
                    : "no eligible FIB personnel are configured";
                EntryPoint.WriteToConsole("FIB surveillance unavailable: " + reason + ".", 1);
                HasLoggedUnavailableFleet = true;
            }
            return null;
        }

        DispatchableVehicle vehicleType = candidates[RandomItems.MyRand.Next(0, candidates.Count)];
        for (int attempt = 0; attempt < 8; attempt++)
        {
            float angle = RandomItems.MyRand.Next(0, 360) * (float)Math.PI / 180f;
            int maxSpawnOffset = Math.Max(1, (int)Math.Min(100f,
                settings.SurveillanceMaxDistance - settings.SurveillanceStartDistance - 25f));
            float distance = settings.SurveillanceStartDistance +
                RandomItems.MyRand.Next(0, maxSpawnOffset);
            Vector3 playerPosition = Player.Position;
            Vector3 candidatePosition = new Vector3(
                playerPosition.X + (float)Math.Cos(angle) * distance,
                playerPosition.Y + (float)Math.Sin(angle) * distance,
                playerPosition.Z);

            SpawnLocation spawnLocation = new SpawnLocation(candidatePosition);
            spawnLocation.GetClosestStreet(false);
            if (!spawnLocation.HasSpawns)
                continue;

            float spawnDistance = Distance2D(spawnLocation.StreetPosition, playerPosition);
            if (spawnDistance < settings.SurveillanceStartDistance * 0.8f)
                continue;

            LESpawnTask spawnTask = new LESpawnTask(fib, spawnLocation, vehicleType, agent, false,
                Settings, Weapons, Names, false, World, ModItems, false, ShopMenus, Crimes);
            spawnTask.AllowBuddySpawn = false;
            spawnTask.ClearVehicleArea = false;
            spawnTask.SpawnWithAllWeapons = false;
            // Off-duty classification prevents the normal CopBrain wanted-level/pursuit flow.
            // This team is controlled exclusively by the surveillance dispatcher.
            spawnTask.IsOffDutySpawn = true;
            spawnTask.AttemptSpawn();

            VehicleExt spawnedVehicle = spawnTask.CreatedVehicles.FirstOrDefault(x =>
                x != null && x.Vehicle != null && x.Vehicle.Exists());
            PedExt spawnedDriver = spawnTask.CreatedPeople.FirstOrDefault(x =>
                x != null && x.Pedestrian != null && x.Pedestrian.Exists());
            if (spawnedVehicle == null || spawnedDriver == null)
            {
                foreach (PedExt ped in spawnTask.CreatedPeople.Where(x => x != null))
                {
                    try { if (ped.Pedestrian != null && ped.Pedestrian.Exists()) ped.Pedestrian.Delete(); }
                    catch (Exception cleanupException) { EntryPoint.WriteToConsole("FIB partial ped cleanup failed: " + cleanupException); }
                }
                foreach (VehicleExt vehicle in spawnTask.CreatedVehicles.Where(x => x != null))
                {
                    try { if (vehicle.Vehicle != null && vehicle.Vehicle.Exists()) vehicle.FullyDelete(); }
                    catch (Exception cleanupException) { EntryPoint.WriteToConsole("FIB partial vehicle cleanup failed: " + cleanupException); }
                }
                continue;
            }

            if (NativeFunction.Natives.HAS_ENTITY_CLEAR_LOS_TO_ENTITY<bool>(
                Player.Character, spawnedVehicle.Vehicle, 17))
            {
                try { spawnedDriver.Pedestrian.Delete(); }
                catch (Exception ex) { EntryPoint.WriteToConsole("FIB visible-driver cleanup failed: " + ex); }
                try { spawnedVehicle.FullyDelete(); }
                catch (Exception ex) { EntryPoint.WriteToConsole("FIB visible-vehicle cleanup failed: " + ex); }
                continue;
            }

            spawnTask.CreatedPeople.ForEach(x =>
            {
                if (x != null && x.Pedestrian != null && x.Pedestrian.Exists())
                    World.Pedestrians.AddEntity(x);
            });
            spawnTask.CreatedVehicles.ForEach(x =>
            {
                if (x != null && x.Vehicle.Exists())
                    x.AddVehicleToList(World);
            });

            RuntimeTeam team = new RuntimeTeam
            {
                AssignmentID = teamID,
                Driver = spawnedDriver,
                Vehicle = spawnedVehicle
            };
            IssueFollowCommand(team, settings);
            return team;
        }
        return null;
    }

    private static HashSet<string> LoadSurveillanceVehicleConfiguration()
    {
        HashSet<string> models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string directory = Path.GetDirectoryName(SurveillanceVehicleConfigPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (!File.Exists(SurveillanceVehicleConfigPath))
            {
                string template =
                    "<?xml version=\\"1.0\\" encoding=\\"utf-8\\"?>\\r\\n" +
                    "<FIBSurveillanceVehicles>\\r\\n" +
                    "  <Vehicles>\\r\\n" +
                    "    <!-- Add one entry for each vehicle model already present in the FIB dispatchable fleet. -->\\r\\n" +
                    "    <!-- Example: <Vehicle model=\\"your_vehicle_model\\" /> -->\\r\\n" +
                    "  </Vehicles>\\r\\n" +
                    "</FIBSurveillanceVehicles>\\r\\n";
                File.WriteAllText(SurveillanceVehicleConfigPath, template);
                EntryPoint.WriteToConsole("Created FIB surveillance vehicle configuration: " +
                    SurveillanceVehicleConfigPath, 1);
                return models;
            }

            XmlSerializer serializer = new XmlSerializer(typeof(FIBSurveillanceVehicleConfiguration));
            XmlReaderSettings readerSettings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using (XmlReader reader = XmlReader.Create(SurveillanceVehicleConfigPath, readerSettings))
            {
                FIBSurveillanceVehicleConfiguration configuration =
                    serializer.Deserialize(reader) as FIBSurveillanceVehicleConfiguration;
                if (configuration?.Vehicles != null)
                {
                    foreach (FIBSurveillanceVehicleEntry entry in configuration.Vehicles)
                    {
                        string model = entry?.Model?.Trim();
                        if (!string.IsNullOrWhiteSpace(model))
                            models.Add(model);
                    }
                }
            }

            EntryPoint.WriteToConsole("Loaded " + models.Count +
                " FIB surveillance vehicle model(s) from external XML.", 1);
        }
        catch (Exception ex)
        {
            models.Clear();
            EntryPoint.WriteToConsole("FIB surveillance vehicle XML could not be loaded; " +
                "surveillance vehicle spawning is disabled. " + ex.Message, 1);
        }
        return models;
    }

    private void UpdateRuntimeTeams(LawEnforcementIntelligence intelligence, DateTime now,
        LawEnforcementIntelligenceSettings settings)
    {
        int excessTeams = Math.Max(0, RuntimeTeams.Count - settings.MaxActiveFIBTeams);
        if (excessTeams > 0)
        {
            foreach (KeyValuePair<string, RuntimeTeam> excess in RuntimeTeams
                .Select(pair => new
                {
                    Pair = pair,
                    Assignment = intelligence.SurveillanceAssignments.FirstOrDefault(x =>
                        x != null && x.ID == pair.Key),
                    Investigation = intelligence.SurveillanceAssignments
                        .Where(x => x != null && x.ID == pair.Key)
                        .Select(x => intelligence.Investigations.FirstOrDefault(i =>
                            i != null && i.ID == x.InvestigationID)).FirstOrDefault()
                })
                .OrderBy(x => x.Investigation == null ? InvestigationStage.Preliminary : x.Investigation.Stage)
                .ThenBy(x => x.Assignment == null ? DateTime.MinValue : x.Assignment.LastSuccessfulObservationAt)
                .Take(excessTeams).Select(x => x.Pair).ToList())
            {
                SurveillanceAssignment assignment = intelligence.SurveillanceAssignments
                    .FirstOrDefault(x => x != null && x.ID == excess.Key);
                if (assignment != null && assignment.State != SurveillanceAssignmentState.Disengaging &&
                    assignment.State != SurveillanceAssignmentState.Compromised)
                    (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.Transition(assignment, SurveillanceAssignmentState.Disengaging, now);
            }
        }

        foreach (KeyValuePair<string, RuntimeTeam> pair in RuntimeTeams.ToList())
        {
            RuntimeTeam team = pair.Value;
            SurveillanceAssignment assignment = intelligence.SurveillanceAssignments
                .FirstOrDefault(x => x != null && x.ID == pair.Key);
            if (assignment == null)
            {
                CleanupTeam(team);
                RuntimeTeams.Remove(pair.Key);
                continue;
            }

            InvestigationRecord investigation = intelligence.Investigations.FirstOrDefault(x =>
                x != null && x.ID == assignment.InvestigationID);
            if (investigation == null || investigation.Stage == InvestigationStage.Closed ||
                investigation.Stage == InvestigationStage.Suspended)
            {
                if (assignment.State != SurveillanceAssignmentState.Disengaging &&
                    assignment.State != SurveillanceAssignmentState.Compromised)
                    (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.Transition(assignment, SurveillanceAssignmentState.Disengaging, now);
            }

            bool entitiesExist = team.Driver != null && team.Driver.Pedestrian != null && team.Driver.Pedestrian.Exists() &&
                team.Vehicle != null && team.Vehicle.Vehicle != null && team.Vehicle.Vehicle.Exists() &&
                team.Driver.Pedestrian.IsAlive;
            if (!entitiesExist)
            {
                if (assignment.State != SurveillanceAssignmentState.Disengaging &&
                    assignment.State != SurveillanceAssignmentState.Compromised)
                    (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.Transition(
                        assignment, SurveillanceAssignmentState.Disengaging, now);
                (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.Transition(
                    assignment, SurveillanceAssignmentState.Completed, now);
                assignment.ReplacementRequired = true;
                assignment.NextAssignmentAllowedAt = now.AddMinutes(5);
                assignment.Notes = "Surveillance entities were lost; replacement is delayed to prevent repeated spawn attempts.";
                CleanupTeam(team);
                RuntimeTeams.Remove(pair.Key);
                continue;
            }

            float distance = Distance2D(team.Vehicle.Vehicle.Position, Player.Position);
            bool canObserve = NativeFunction.Natives.HAS_ENTITY_CLEAR_LOS_TO_ENTITY<bool>(
                team.Driver.Pedestrian, Player.Character, 17);
            bool playerCanSeeTeam = NativeFunction.Natives.HAS_ENTITY_CLEAR_LOS_TO_ENTITY<bool>(
                Player.Character, team.Vehicle.Vehicle, 17);

            if (assignment.State == SurveillanceAssignmentState.Compromised ||
                assignment.State == SurveillanceAssignmentState.Disengaging)
            {
                if (!team.DisengageCommandIssued)
                {
                    NativeFunction.Natives.TASK_VEHICLE_DRIVE_WANDER(team.Driver.Pedestrian,
                        team.Vehicle.Vehicle, 20f, 3);
                    team.DisengageCommandIssued = true;
                }
                if (distance >= settings.SurveillanceMaxDistance + 100f ||
                    Game.GameTime - team.LastFollowCommandAt >= 30000)
                {
                    (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.Transition(assignment, SurveillanceAssignmentState.Completed, now);
                    CleanupTeam(team);
                    RuntimeTeams.Remove(pair.Key);
                }
                continue;
            }

            if (distance > settings.SurveillanceMaxDistance || !canObserve)
            {
                if (assignment.State == SurveillanceAssignmentState.Following ||
                    assignment.State == SurveillanceAssignmentState.Approaching ||
                    assignment.State == SurveillanceAssignmentState.StationaryWatch)
                    (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.Transition(assignment, SurveillanceAssignmentState.LostContact, now);

                DateTime lastContact = assignment.LastSuccessfulObservationAt == DateTime.MinValue
                    ? assignment.AssignedAt : assignment.LastSuccessfulObservationAt;
                if (now - lastContact >= TimeSpan.FromMinutes(2))
                {
                    assignment.ReplacementRequired = true;
                    assignment.NextAssignmentAllowedAt = now.AddMinutes(5);
                    (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.Transition(assignment, SurveillanceAssignmentState.Disengaging, now);
                }
                continue;
            }

            if (playerCanSeeTeam && Game.GameTime - team.LastDetectionCheckAt >= 15000)
            {
                team.LastDetectionCheckAt = Game.GameTime;
                if (RandomItems.RandomPercent(settings.SurveillanceDetectionChance))
                {
                    (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.Transition(assignment, SurveillanceAssignmentState.Compromised, now);
                    assignment.ReplacementRequired = true;
                    assignment.NextAssignmentAllowedAt = now.AddMinutes(2);
                    NativeFunction.Natives.TASK_VEHICLE_DRIVE_WANDER(team.Driver.Pedestrian,
                        team.Vehicle.Vehicle, 22f, 3);
                    team.DisengageCommandIssued = true;
                    continue;
                }
            }

            if (assignment.State == SurveillanceAssignmentState.LostContact)
            {
                (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.Transition(assignment, SurveillanceAssignmentState.Approaching, now);
            }
            if (assignment.State == SurveillanceAssignmentState.Approaching)
            {
                (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.Transition(assignment, SurveillanceAssignmentState.Following, now);
            }

            bool playerStationary = Player.IsInVehicle
                ? Player.CurrentVehicle != null && Player.CurrentVehicle.Vehicle.Exists() &&
                    Player.CurrentVehicle.Vehicle.Speed <= 0.5f
                : Player.Character != null && Player.Character.Exists() && Player.Character.Speed <= 0.5f;

            if (assignment.State == SurveillanceAssignmentState.Following &&
                playerStationary && distance <= settings.SurveillanceMinDistance + 40f)
            {
                (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.Transition(
                    assignment, SurveillanceAssignmentState.StationaryWatch, now);
            }
            else if (assignment.State == SurveillanceAssignmentState.StationaryWatch && !playerStationary)
            {
                (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.Transition(
                    assignment, SurveillanceAssignmentState.Following, now);
            }

            if (assignment.State != SurveillanceAssignmentState.StationaryWatch &&
                Game.GameTime - team.LastFollowCommandAt >= 5000)
                IssueFollowCommand(team, settings);

            RecordActualObservation(intelligence, assignment, now, team);
        }

        // A team whose entities disappeared has no runtime object left to drive its timeout.
        // Complete its assignment after the reacquisition window so another team can replace it.
        foreach (SurveillanceAssignment assignment in intelligence.SurveillanceAssignments.Where(x =>
            x != null && x.State == SurveillanceAssignmentState.LostContact &&
            !RuntimeTeams.ContainsKey(x.ID)).ToList())
        {
            DateTime lastContact = assignment.LastSuccessfulObservationAt == DateTime.MinValue
                ? assignment.AssignedAt : assignment.LastSuccessfulObservationAt;
            if (now - lastContact >= TimeSpan.FromMinutes(2))
            {
                assignment.ReplacementRequired = true;
                assignment.NextAssignmentAllowedAt = now.AddMinutes(5);
                (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.Transition(assignment, SurveillanceAssignmentState.Disengaging, now);
                (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.Transition(assignment, SurveillanceAssignmentState.Completed, now);
            }
        }
    }

    private void RecordActualObservation(LawEnforcementIntelligence intelligence,
        SurveillanceAssignment assignment, DateTime now, RuntimeTeam team)
    {
        bool inVehicle = Player.IsInVehicle && Player.CurrentVehicle != null &&
            Player.CurrentVehicle.Vehicle != null && Player.CurrentVehicle.Vehicle.Exists() &&
            Player.CurrentVehicle.Vehicle.Driver != null && Player.CurrentVehicle.Vehicle.Driver.Exists() &&
            Player.CurrentVehicle.Vehicle.Driver.Handle == Player.Character.Handle;
        string vehicleID = null;
        string vehicleDescription = null;
        if (inVehicle)
        {
            string plate = Player.CurrentVehicle.CarPlate?.PlateNumber ??
                Player.CurrentVehicle.Vehicle.LicensePlate;
            string registrationID = Player.CurrentVehicle.CarPlate?.RegistrationId;
            vehicleID = LawEnforcementIntelligence.GetVehicleIdentity(registrationID, plate);
            vehicleDescription = Player.CurrentVehicle.FullName(false);
        }

        string zoneID = Player.CurrentLocation?.CurrentZone?.InternalGameName;
        intelligence.RecordSurveillanceSighting(now, assignment.TeamID, inVehicle,
            vehicleID, vehicleDescription, Player.Position.X, Player.Position.Y, Player.Position.Z,
            zoneID, zoneID);
        // Refresh last-contact time on every real line-of-sight observation, even when the
        // intelligence database deduplicates the stored observation record.
        (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.RecordObservation(
            assignment, now, Player.Position.X, Player.Position.Y, Player.Position.Z);

        Mod.Player modPlayer = Player as Mod.Player;
        Residence observedResidence = (modPlayer?.CurrentLocation?.CurrentInterior as ResidenceInterior)?.Residence;
        if (observedResidence != null && observedResidence.ResidenceID >= 0)
        {
            intelligence.RecordResidenceObservation(now, IntelligenceSource.Surveillance,
                "FIB_SURVEILLANCE:" + assignment.TeamID, observedResidence.ResidenceID.ToString(),
                observedResidence.Name, Player.Position.X, Player.Position.Y, Player.Position.Z,
                modPlayer.CurrentLocation?.CurrentZone?.InternalGameName,
                modPlayer.CurrentLocation?.CurrentZone?.InternalGameName, 0.95f);
        }
    }

    private void IssueFollowCommand(RuntimeTeam team, LawEnforcementIntelligenceSettings settings)
    {
        if (team == null || team.Driver == null || team.Driver.Pedestrian == null ||
            !team.Driver.Pedestrian.Exists() || team.Vehicle == null ||
            team.Vehicle.Vehicle == null || !team.Vehicle.Vehicle.Exists())
            return;
        Entity target = Player.IsInVehicle && Player.CurrentVehicle != null &&
            Player.CurrentVehicle.Vehicle.Exists()
            ? (Entity)Player.CurrentVehicle.Vehicle
            : Player.Character;
        int followDistance = Math.Max(50, (int)settings.SurveillanceMinDistance);
        NativeFunction.Natives.xFC545A9F0626E3B6(team.Driver.Pedestrian,
            team.Vehicle.Vehicle, target, 22f, 3, followDistance);
        team.LastFollowCommandAt = Game.GameTime;
    }

    private void DisengageAll(LawEnforcementIntelligence intelligence, DateTime now)
    {
        foreach (KeyValuePair<string, RuntimeTeam> pair in RuntimeTeams.ToList())
        {
            SurveillanceAssignment assignment = intelligence.SurveillanceAssignments
                .FirstOrDefault(x => x != null && x.ID == pair.Key);
            if (assignment != null && assignment.State != SurveillanceAssignmentState.Completed)
            {
                if (assignment.State != SurveillanceAssignmentState.Disengaging &&
                    assignment.State != SurveillanceAssignmentState.Compromised)
                    (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.Transition(
                        assignment, SurveillanceAssignmentState.Disengaging, now);
                (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.Transition(
                    assignment, SurveillanceAssignmentState.Completed, now);
            }
            CleanupTeam(pair.Value);
            RuntimeTeams.Remove(pair.Key);
        }

        // Also close persisted assignments that have no live runtime entities to clean up.
        foreach (SurveillanceAssignment assignment in intelligence.SurveillanceAssignments.Where(x => x != null &&
            x.State != SurveillanceAssignmentState.Available &&
            x.State != SurveillanceAssignmentState.Completed).ToList())
        {
            if (assignment.State != SurveillanceAssignmentState.Disengaging &&
                assignment.State != SurveillanceAssignmentState.Compromised)
                (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.Transition(
                    assignment, SurveillanceAssignmentState.Disengaging, now);
            (Player as Mod.Player)?.LawEnforcementIntelligenceManager?.SurveillanceAssignmentManager?.Transition(
                assignment, SurveillanceAssignmentState.Completed, now);
            assignment.ReplacementRequired = false;
        }
    }

    private static void CleanupTeam(RuntimeTeam team)
    {
        if (team == null) return;
        try
        {
            if (team.Driver != null && team.Driver.Pedestrian != null && team.Driver.Pedestrian.Exists())
                team.Driver.Pedestrian.Delete();
        }
        catch (Exception ex)
        {
            EntryPoint.WriteToConsole("FIB driver cleanup failed: " + ex);
        }
        try
        {
            if (team.Vehicle != null && team.Vehicle.Vehicle != null && team.Vehicle.Vehicle.Exists())
                team.Vehicle.FullyDelete();
        }
        catch (Exception ex)
        {
            EntryPoint.WriteToConsole("FIB vehicle cleanup failed: " + ex);
        }
    }

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }
}
