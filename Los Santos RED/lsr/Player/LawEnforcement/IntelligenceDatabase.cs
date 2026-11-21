using System;
using System.Collections.Generic;
using System.Linq;

namespace LosSantosRED.lsr
{
    // Query/ingestion facade over the single persistent intelligence store.
    public class IntelligenceDatabase
    {
        public bool Record(LawEnforcementIntelligence intelligence, IntelligenceObservation observation)
        {
            return intelligence != null && observation != null && intelligence.RecordObservation(observation);
        }

        public IEnumerable<IntelligenceObservation> GetPoliceKnown(LawEnforcementIntelligence intelligence,
            DateTime throughTime, string targetID = null)
        {
            if (intelligence == null || intelligence.Observations == null)
                return Enumerable.Empty<IntelligenceObservation>();
            return intelligence.Observations.Where(x => x != null && x.IsKnownToPolice &&
                x.ObservedAt <= throughTime && (string.IsNullOrWhiteSpace(targetID) ||
                string.Equals(x.TargetID, targetID, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(x => x.ObservedAt).ToList();
        }

        public IEnumerable<IntelligenceObservation> GetEvidenceSince(LawEnforcementIntelligence intelligence,
            DateTime since, bool policeKnownOnly = true)
        {
            if (intelligence == null || intelligence.Observations == null)
                return Enumerable.Empty<IntelligenceObservation>();
            return intelligence.Observations.Where(x => x != null && x.ObservedAt >= since &&
                (!policeKnownOnly || x.IsKnownToPolice)).OrderBy(x => x.ObservedAt).ToList();
        }

        public IEnumerable<VehicleAssociationRecord> GetVehicleAssociations(
            LawEnforcementIntelligence intelligence, string vehicleID, bool policeKnownOnly = true)
        {
            if (intelligence == null || intelligence.VehicleAssociations == null ||
                string.IsNullOrWhiteSpace(vehicleID))
                return Enumerable.Empty<VehicleAssociationRecord>();
            return intelligence.VehicleAssociations.Where(x => x != null &&
                string.Equals(x.VehicleID, vehicleID, StringComparison.OrdinalIgnoreCase) &&
                (!policeKnownOnly || x.IsKnownToPolice))
                .OrderByDescending(x => x.ObservedAt).ToList();
        }


        // Operational investigation reads must use an explicit agency grant. The legacy
        // unscoped query methods above remain for internal administration and diagnostics.
        public InvestigationRecord GetInvestigationSummaryForAgency(LawEnforcementIntelligence intelligence,
            string investigationID, string agencyID, DateTime at, AgencyIntelligenceAccessManager accessManager)
        {
            if (accessManager == null) return null;
            return accessManager.GetInvestigation(intelligence, investigationID, agencyID,
                InvestigationAccessScope.Summary, at);
        }

        public IEnumerable<IntelligenceObservation> GetInvestigationEvidenceForAgency(
            LawEnforcementIntelligence intelligence, string investigationID, string agencyID,
            DateTime at, AgencyIntelligenceAccessManager accessManager)
        {
            if (intelligence == null || accessManager == null || at == DateTime.MinValue ||
                string.IsNullOrWhiteSpace(investigationID) || string.IsNullOrWhiteSpace(agencyID) ||
                intelligence.Observations == null)
                return Enumerable.Empty<IntelligenceObservation>();

            InvestigationRecord investigation = accessManager.GetInvestigation(intelligence,
                investigationID, agencyID, InvestigationAccessScope.Evidence, at);
            if (investigation == null || investigation.ObservationIDs == null ||
                investigation.ObservationIDs.Count == 0)
                return Enumerable.Empty<IntelligenceObservation>();

            HashSet<string> authorisedObservationIDs = new HashSet<string>(
                investigation.ObservationIDs.Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase);
            return intelligence.Observations.Where(observation => observation != null &&
                    observation.IsKnownToPolice && observation.ObservedAt <= at &&
                    authorisedObservationIDs.Contains(observation.ID))
                .OrderByDescending(observation => observation.ObservedAt)
                .Select(observation => observation.Copy()).ToList();
        }

        public bool MarkKnownToPolice(LawEnforcementIntelligence intelligence, string observationID,
            IntelligenceSource source, string sourceID, float confidence, DateTime recordedAt, string notes = null)
        {
            if (intelligence == null || intelligence.Observations == null ||
                string.IsNullOrWhiteSpace(observationID) || recordedAt == DateTime.MinValue ||
                source == IntelligenceSource.Unknown || string.IsNullOrWhiteSpace(sourceID)) return false;
            IntelligenceObservation observation = intelligence.Observations.FirstOrDefault(x =>
                x != null && string.Equals(x.ID, observationID, StringComparison.OrdinalIgnoreCase));
            if (observation == null || recordedAt < observation.ObservedAt) return false;
            observation.IsKnownToPolice = true;
            observation.Source = source;
            observation.SourceID = string.IsNullOrWhiteSpace(sourceID) ? "UNKNOWN" : sourceID;
            observation.Confidence = float.IsNaN(confidence) || float.IsInfinity(confidence)
                ? 0f : Math.Max(0f, Math.Min(1f, confidence));
            observation.Notes = string.IsNullOrWhiteSpace(notes) ? observation.Notes : notes;
            observation.IsVerified = true;
            foreach (WarrantRecord warrant in intelligence.Warrants.Where(x => x != null &&
                x.Status == WarrantStatus.Active && observation.ObservedAt >= x.IssuedAt))
            {
                if (warrant.EvidenceObservationIDs == null)
                    warrant.EvidenceObservationIDs = new List<string>();
                if (!warrant.EvidenceObservationIDs.Contains(observation.ID))
                    warrant.EvidenceObservationIDs.Add(observation.ID);
            }
            intelligence.LastUpdatedAt = recordedAt;
            return true;
        }
    }
}
