using LosSantosRED.lsr;
using LosSantosRED.lsr.Interface;
using LSR.Vehicles;
using Rage;
using Rage.Native;
using RAGENativeUI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LosSantosRED.lsr
{
    public class CriminalHistory
    {
        private BOLO CurrentHistory;
        private string LawEnforcementBOLOID;
        private IPoliceRespondable Player;
        private ISettingsProvideable Settings;
        private ITimeReportable Time;
        private Blip CriminalHistoryBlip;
        private float PlayerDistanceToLastSeen = 9999f;
        private Color blipColor => IsNearLastSeenLocation ? Color.Orange : Color.Yellow;
        public bool IsNearLastSeenLocation { get; set; }
        public bool IsWithinMarshalDistance => HasHistory && PlayerDistanceToLastSeen <= SearchRadius + Settings.SettingsManager.PoliceSettings.MarshalsAPBResponseExtraRadiusDistance;
        public CriminalHistory(IPoliceRespondable currentPlayer, ISettingsProvideable settings, ITimeReportable time)
        {
            Player = currentPlayer;
            Settings = settings;
            Time = time;
        }
        private int LastWantedMaxLevel => CurrentHistory == null ? 0 : CurrentHistory.WantedLevel;
        private float SearchRadius => LastWantedMaxLevel > 0 ? LastWantedMaxLevel * Settings.SettingsManager.CriminalHistorySettings.SearchRadiusIncrement : Settings.SettingsManager.CriminalHistorySettings.MinimumSearchRadius;// 400f;
        public bool HasHistory => CurrentHistory != null;
        public bool HasDeadlyHistory => CurrentHistory != null && CurrentHistory.Crimes.Any(x => x.AssociatedCrime.ResultsInLethalForce);
        public int MaxWantedLevel => LastWantedMaxLevel;
        public List<Crime> WantedCrimes => CurrentHistory?.Crimes.Select(x => x.AssociatedCrime).ToList();
        public void Dispose()
        {
            if (CriminalHistoryBlip.Exists())
            {
                CriminalHistoryBlip.Delete();
            }
        }
        public void OnSuspectEluded(List<CrimeEvent> CrimesAssociated, Vector3 PlaceLastSeen)
        {
            if (CrimesAssociated == null || PlaceLastSeen == Vector3.Zero ||
                !Player.PoliceResponse.HasPlayerBeenIdentified)
                return;

            CurrentHistory = new BOLO(PlaceLastSeen, CrimesAssociated, Player.WantedLevel);
            ClearLawEnforcementBOLO();

            Mod.Player modPlayer = Player as Mod.Player;
            if (modPlayer == null || modPlayer.CurrentDateTime == DateTime.MinValue)
                return;

            List<string> crimeNames = CrimesAssociated
                .Where(x => x != null && x.AssociatedCrime != null)
                .Select(x => x.AssociatedCrime.Name)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();
            string description = crimeNames.Count == 0
                ? "Identified suspect who previously eluded police."
                : "Escaped suspect associated with: " + string.Join(", ", crimeNames);
            string zoneID = modPlayer.CurrentLocation?.CurrentZone?.InternalGameName;

            BOLORecord issuedBOLO = modPlayer.IssueLawEnforcementBOLO(
                BOLOTargetType.Person,
                description,
                null,
                null,
                zoneID,
                zoneID,
                modPlayer.CurrentDateTime.AddDays(7),
                "Person BOLO issued after an identified suspect eluded police. Foot identification only.");

            LawEnforcementBOLOID = issuedBOLO?.ID;
            IssueOrUpdateArrestWarrant(modPlayer, CrimesAssociated, zoneID);
        }

        private void IssueOrUpdateArrestWarrant(Mod.Player modPlayer,
            IEnumerable<CrimeEvent> crimesAssociated, string jurisdictionID)
        {
            if (modPlayer == null || crimesAssociated == null ||
                modPlayer.CurrentDateTime == DateTime.MinValue ||
                modPlayer.Settings?.SettingsManager?.LawEnforcementIntelligenceSettings?.EnableWarrants != true)
                return;

            List<CrimeEvent> identifiedCrimes = crimesAssociated
                .Where(x => x?.AssociatedCrime != null && !string.IsNullOrWhiteSpace(x.AssociatedCrime.ID))
                .ToList();
            List<string> crimeIDs = identifiedCrimes.Select(x => x.AssociatedCrime.ID)
                .Distinct().ToList();
            if (crimeIDs.Count == 0)
                return;

            bool lethalCrime = identifiedCrimes.Any(x => x.AssociatedCrime.ResultsInLethalForce);
            WarrantPriority priority = lethalCrime || CurrentHistory.WantedLevel >= 5
                ? WarrantPriority.Critical
                : CurrentHistory.WantedLevel >= 4
                    ? WarrantPriority.High
                    : CurrentHistory.WantedLevel >= 3
                        ? WarrantPriority.Moderate
                        : WarrantPriority.Low;

            Residence suspectedResidence = (modPlayer.CurrentLocation?.CurrentInterior as ResidenceInterior)?.Residence;
            if (suspectedResidence == null || (!suspectedResidence.IsOwned && !suspectedResidence.IsRented))
            {
                suspectedResidence = modPlayer.Properties?.PropertyList?.OfType<Residence>()
                    .FirstOrDefault(x => x != null && (x.IsOwned || x.IsRented));
            }

            string residenceID = suspectedResidence == null
                ? null
                : suspectedResidence.ResidenceID.ToString(System.Globalization.CultureInfo.InvariantCulture);
            float residenceConfidence = suspectedResidence == null
                ? 0f
                : suspectedResidence.IsOwned ? 0.9f : 0.8f;
            if (suspectedResidence != null && modPlayer.CurrentLocation?.IsInside == true &&
                (modPlayer.CurrentLocation.CurrentInterior as ResidenceInterior)?.Residence?.ResidenceID ==
                suspectedResidence.ResidenceID)
                residenceConfidence = 0.98f;

            LawEnforcementIntelligence intelligence = modPlayer.LawEnforcementIntelligence;
            if (intelligence == null)
                return;
            intelligence.Normalize();

            WarrantRecord warrant = modPlayer.LawEnforcementIntelligenceManager?.WarrantManager?.FindActive(
                intelligence, modPlayer.AssignedAgency?.ID, jurisdictionID);
            if (warrant == null)
            {
                warrant = modPlayer.IssueLawEnforcementWarrant(
                    WarrantType.Arrest,
                    priority,
                    modPlayer.AssignedAgency?.ID ?? "LSPD",
                    jurisdictionID,
                    crimeIDs,
                    residenceID,
                    residenceConfidence);
            }
            else
            {
                if (warrant.LinkedCrimeIDs == null)
                    warrant.LinkedCrimeIDs = new List<string>();
                foreach (string crimeID in crimeIDs)
                    if (!warrant.LinkedCrimeIDs.Contains(crimeID))
                        warrant.LinkedCrimeIDs.Add(crimeID);
                if (priority > warrant.Priority)
                    warrant.Priority = priority;
                if (!string.IsNullOrWhiteSpace(residenceID) &&
                    (string.IsNullOrWhiteSpace(warrant.SuspectedResidenceID) ||
                     residenceConfidence > warrant.ResidenceConfidence))
                {
                    warrant.SuspectedResidenceID = residenceID;
                    warrant.ResidenceConfidence = residenceConfidence;
                }
                intelligence.LastUpdatedAt = modPlayer.CurrentDateTime;
            }

            if (warrant == null)
                return;
            if (warrant.EvidenceObservationIDs == null)
                warrant.EvidenceObservationIDs = new List<string>();
            foreach (CrimeEvent crimeEvent in identifiedCrimes)
            {
                string crimeName = crimeEvent.AssociatedCrime.Name;
                foreach (IntelligenceObservation observation in intelligence.Observations.Where(x =>
                    x != null && x.IsKnownToPolice && x.ObservedAt <= modPlayer.CurrentDateTime &&
                    (string.Equals(x.TargetDescription, "Player suspected of " + crimeName,
                        StringComparison.OrdinalIgnoreCase) ||
                     (!string.IsNullOrWhiteSpace(x.Notes) &&
                      x.Notes.IndexOf(crimeName, StringComparison.OrdinalIgnoreCase) >= 0))))
                {
                    if (!string.IsNullOrWhiteSpace(observation.ID) &&
                        !warrant.EvidenceObservationIDs.Contains(observation.ID))
                        warrant.EvidenceObservationIDs.Add(observation.ID);
                }
            }
        }

        private void ClearLawEnforcementBOLO()
        {
            Mod.Player modPlayer = Player as Mod.Player;
            if (modPlayer != null && !string.IsNullOrWhiteSpace(LawEnforcementBOLOID))
            {
                modPlayer.LawEnforcementIntelligenceManager?.BOLOManager?.Cancel(
                    modPlayer.LawEnforcementIntelligence,
                    LawEnforcementBOLOID,
                    Time?.CurrentDateTime ?? DateTime.MinValue);
            }
            LawEnforcementBOLOID = null;
        }

        public void ResolveLawEnforcementBOLO()
        {
            Mod.Player modPlayer = Player as Mod.Player;
            if (modPlayer != null && !string.IsNullOrWhiteSpace(LawEnforcementBOLOID))
            {
                modPlayer.LawEnforcementIntelligenceManager?.BOLOManager?.Resolve(
                    modPlayer.LawEnforcementIntelligence,
                    LawEnforcementBOLOID,
                    Time?.CurrentDateTime ?? DateTime.MinValue);
            }
            LawEnforcementBOLOID = null;
        }
        public void OnLostWanted()
        {
            //clear criminal history?
        }
        public void Update()
        {
            UpdateData();
            //GameFiber.Yield();//TR 05
            UpdateBlip();
        }
        public void Reset()
        {
            Clear();
        }
        public void Clear()
        {
            ClearLawEnforcementBOLO();
            CurrentHistory = null;
            //EntryPoint.WriteToConsole($" PLAYER EVENT: Criminal History Clear");
        }
        public void AddCrime(Crime crime)
        {
            Player.PoliceResponse.OnLostWanted();
            if (CurrentHistory == null)
            {
                CurrentHistory = new BOLO(Vector3.Zero,new List<CrimeEvent>() { new CrimeEvent(crime,null) }, crime.ResultingWantedLevel);
            }
            else
            {
                if (!CurrentHistory.Crimes.Any(x => x.AssociatedCrime != null && x.AssociatedCrime.Name == crime.Name))
                {
                    CurrentHistory.Crimes.Add(new CrimeEvent(crime, null));
                }
            }
        }
        public string PrintCriminalHistory()
        {
            if(CurrentHistory != null)
            {
                string CrimeString = "";
                foreach (CrimeEvent MyCrime in CurrentHistory.Crimes.Where(x=> x.AssociatedCrime != null).OrderBy(x => x.AssociatedCrime.Priority).Take(3))
                {
                    CrimeString += string.Format("~n~{0}~s~", MyCrime.AssociatedCrime.Name);
                }
                return CrimeString;
            }
            return "";
        }
        private void UpdateData()
        {
            if (!Player.IsAliveAndFree || !HasHistory)
            {
                PlayerDistanceToLastSeen = 9999f;
                IsNearLastSeenLocation = false;
                return;
            }
            IsNearLastSeenLocation = UpdateLastSeenDistance();
            if (Player.AnyPoliceCanRecognizePlayer)
            {
                if (Player.IsWanted)
                {
                    ApplyLastWantedStats();
                    GameFiber.Yield();//TR 05
                    //EntryPoint.WriteToConsole("CRIMINAL HISTORY EVENT: Became Wanted");
                }
                else if (IsNearLastSeenLocation && Player.PoliceResponse.HasBeenNotWantedFor >= 5000)//move the second one OUT
                {
                    ApplyLastWantedStats();
                    GameFiber.Yield();//TR 05
                    //EntryPoint.WriteToConsole("CRIMINAL HISTORY EVENT: Near Last Location");
                }
                else if (Player.IsInVehicle && Player.CurrentVehicle != null && Player.CurrentVehicle.IsWanted)//.CopsRecognizeAsStolen)
                {
                    ApplyLastWantedStats();
                    GameFiber.Yield();//TR 05
                    //EntryPoint.WriteToConsole("CRIMINAL HISTORY EVENT: Recognized Vehicle");
                }
            }
            if (Player.PoliceResponse.HasBeenNotWantedFor >= (Settings.SettingsManager.CriminalHistorySettings.RealTimeExpireWantedMultiplier * LastWantedMaxLevel))// 120000)
            {
                Clear();
                //EntryPoint.WriteToConsole("CRIMINAL HISTORY EVENT: History Expired (Real Time)");
            }
            if (DateTime.Compare(Player.PoliceResponse.DateTimeLastWantedEnded.AddHours(LastWantedMaxLevel * Settings.SettingsManager.CriminalHistorySettings.CalendarTimeExpireWantedMultiplier), Time.CurrentDateTime) < 0)
            {
                //EntryPoint.WriteToConsole($"POLICE RESPONSE: Lost Wanted ToExpire: {Player.PoliceResponse.DateTimeLastWantedEnded.AddHours(LastWantedMaxLevel * Settings.SettingsManager.CriminalHistorySettings.CalendarTimeExpireWantedMultiplier)} Current: {Time.CurrentDateTime}");
                Clear();
                //EntryPoint.WriteToConsole("CRIMINAL HISTORY EVENT: History Expired (Calendar Time)");
            }    

            if(Player.IsWanted && Player.PoliceResponse.WantedLevelHasBeenRadioedIn && HasHistory)
            {
                CurrentHistory = null;
            }
        }
        private bool UpdateLastSeenDistance()
        {
            if(CurrentHistory == null)
            {
                PlayerDistanceToLastSeen = 9999f;
                return false;
            }
            PlayerDistanceToLastSeen = Player.Position.DistanceTo2D(CurrentHistory.LastSeenLocation);
            if (PlayerDistanceToLastSeen <= SearchRadius)
            {
                return true;
            }
            return false;
        }
        private void ApplyLastWantedStats()
        {
            if(CurrentHistory == null)
            {
                return;
            }
            foreach(CrimeEvent crime in CurrentHistory.Crimes)
            {
                //EntryPoint.WriteToConsole($"PLAYER EVENT: APPLYING WANTED STATS: ADDING CRIME: {crime.Name}");
                Player.AddCrime(crime.AssociatedCrime, true, Player.Position, Player.CurrentSeenVehicle, Player.WeaponEquipment.CurrentSeenWeapon, true,false, true, false);
                CrimeEvent addedCrime = Player.PoliceResponse.CrimesObserved.Where(x => x.AssociatedCrime?.ID == crime.AssociatedCrime.ID).FirstOrDefault();
                if(addedCrime != null)
                {
                    addedCrime.Instances = crime.Instances;
                }
            }
            int highestWantedLevel = CurrentHistory.WantedLevel;
            //CurrentHistory = null;
            Player.OnAppliedWantedStats(highestWantedLevel);        
        }
        private void UpdateBlip()
        {
            if (HasHistory && Player.IsNotWanted && Settings.SettingsManager.CriminalHistorySettings.CreateBlip)
            {
                CreateBlip();
            }
            else
            {
                RemoveBlip();
            }
        }
        private void CreateBlip()
        {
            if (!CriminalHistoryBlip.Exists())
            {
                CriminalHistoryBlip = new Blip(CurrentHistory.LastSeenLocation, SearchRadius)
                {
                    Name = "APB Center",
                    Color = blipColor,//Color.Yellow,
                    Alpha = 0.25f
                };
                NativeFunction.Natives.BEGIN_TEXT_COMMAND_SET_BLIP_NAME("STRING");
                NativeFunction.Natives.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME("APB Center");
                NativeFunction.Natives.END_TEXT_COMMAND_SET_BLIP_NAME(CriminalHistoryBlip);
                NativeFunction.Natives.SET_BLIP_AS_SHORT_RANGE((uint)CriminalHistoryBlip.Handle, true);

                EntryPoint.WriteToConsole($"CRIMINAL HISORY BLIP CREATED");
                //GameFiber.Yield();//TR Yield RemovedTest 1
            }
            else
            {
                CriminalHistoryBlip.Position = CurrentHistory.LastSeenLocation;
                CriminalHistoryBlip.Color = blipColor;
            }
        }
        private void RemoveBlip()
        {
            if (CriminalHistoryBlip.Exists())
            {
                CriminalHistoryBlip.Delete();
            }
        }
       

    }


}