using System;
using System.Collections.Generic;
using System.Linq;

namespace LosSantosRED.lsr
{
    // BOLO lifecycle and matching are separate from warrant authority.
    public class BOLOManager
    {
        public BOLORecord Issue(LawEnforcementIntelligence intelligence, DateTime issuedAt,
            BOLOTargetType targetType, string description, string vehicleModel, string plateNumber,
            string zoneID, string lastKnownLocationID, DateTime? expiresAt,
            LawEnforcementIntelligenceSettings settings, string notes = null, string issuingAgencyID = null)
        {
            if (intelligence == null || settings == null || !settings.EnableBOLOSystem) return null;
            settings.Normalize();
            BOLORecord bolo = intelligence.IssueBOLO(issuedAt, targetType, description, vehicleModel, plateNumber,
                zoneID, lastKnownLocationID, expiresAt, settings.BOLOInZoneStopChance / 100f,
                settings.BOLOOutOfZoneStopChance / 100f, notes);
            if (bolo != null && !string.IsNullOrWhiteSpace(issuingAgencyID))
                bolo.KnownToAgencyIDs.Add(issuingAgencyID);
            return bolo;
        }

        public void Update(LawEnforcementIntelligence intelligence, DateTime currentTime)
        {
            intelligence?.UpdateExpirations(currentTime);
        }

        public IEnumerable<BOLORecord> GetActive(LawEnforcementIntelligence intelligence, DateTime currentTime)
        {
            if (intelligence == null || intelligence.BOLOs == null) return Enumerable.Empty<BOLORecord>();
            intelligence.UpdateExpirations(currentTime);
            return intelligence.BOLOs.Where(x => x != null && x.Status == BOLOStatus.Active)
                .OrderByDescending(x => x.StopChanceInZone).ToList();
        }

        public float GetVehicleStopChance(LawEnforcementIntelligence intelligence, DateTime currentTime,
            string zoneID, string plateNumber, string vehicleModel, string agencyID = null)
        {
            if (intelligence == null || intelligence.BOLOs == null || string.IsNullOrWhiteSpace(agencyID)) return 0f;
            intelligence.UpdateExpirations(currentTime);
            float chance = 0f;
            foreach (BOLORecord bolo in intelligence.BOLOs.Where(x => x != null && x.Status == BOLOStatus.Active &&
                x.SubjectID == intelligence.SubjectID && x.TargetType != BOLOTargetType.Person &&
                x.KnownToAgencyIDs != null && x.KnownToAgencyIDs.Contains(agencyID, StringComparer.OrdinalIgnoreCase)))
            {
                if (bolo.TargetType == BOLOTargetType.LicensePlate &&
                    (string.IsNullOrWhiteSpace(plateNumber) || !string.Equals(bolo.PlateNumber, plateNumber.Trim(), StringComparison.OrdinalIgnoreCase))) continue;
                if (bolo.TargetType == BOLOTargetType.Vehicle && !string.IsNullOrWhiteSpace(bolo.VehicleModel) &&
                    !string.Equals(bolo.VehicleModel, vehicleModel, StringComparison.OrdinalIgnoreCase)) continue;
                float candidate = !string.IsNullOrWhiteSpace(bolo.ZoneID) && string.Equals(bolo.ZoneID, zoneID, StringComparison.OrdinalIgnoreCase)
                    ? bolo.StopChanceInZone : bolo.StopChanceOutsideZone;
                chance = Math.Max(chance, candidate);
            }
            return Math.Max(0f, Math.Min(1f, chance));
        }

        public float GetPersonRecognitionChance(LawEnforcementIntelligence intelligence, DateTime currentTime,
            string personSubjectID, string zoneID, string agencyID = null)
        {
            if (intelligence == null || intelligence.BOLOs == null || string.IsNullOrWhiteSpace(personSubjectID) || string.IsNullOrWhiteSpace(agencyID))
                return 0f;
            intelligence.UpdateExpirations(currentTime);
            float chance = 0f;
            foreach (BOLORecord bolo in intelligence.BOLOs.Where(x => x != null &&
                x.Status == BOLOStatus.Active && x.TargetType == BOLOTargetType.Person &&
                string.Equals(x.SubjectID, personSubjectID, StringComparison.OrdinalIgnoreCase) &&
                x.KnownToAgencyIDs != null && x.KnownToAgencyIDs.Contains(agencyID, StringComparer.OrdinalIgnoreCase)))
            {
                float candidate = !string.IsNullOrWhiteSpace(bolo.ZoneID) &&
                    string.Equals(bolo.ZoneID, zoneID, StringComparison.OrdinalIgnoreCase)
                    ? bolo.StopChanceInZone : bolo.StopChanceOutsideZone;
                chance = Math.Max(chance, candidate);
            }
            return Math.Max(0f, Math.Min(1f, chance));
        }

        public bool Cancel(LawEnforcementIntelligence intelligence, string boloID, DateTime at)
        {
            return intelligence != null && intelligence.SetBOLOStatus(boloID, BOLOStatus.Cancelled, at);
        }

        public bool Resolve(LawEnforcementIntelligence intelligence, string boloID, DateTime at)
        {
            return intelligence != null && intelligence.SetBOLOStatus(boloID, BOLOStatus.Resolved, at);
        }
    }
}
