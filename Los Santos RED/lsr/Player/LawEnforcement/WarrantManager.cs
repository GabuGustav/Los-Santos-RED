using System;
using System.Collections.Generic;
using System.Linq;

namespace LosSantosRED.lsr
{
    // Warrant operations are centralized here; the intelligence object remains the sole record owner.
    public class WarrantManager
    {
        public WarrantRecord Issue(LawEnforcementIntelligence intelligence, DateTime issuedAt, WarrantType type,
            WarrantPriority priority, string agencyID, string jurisdictionID, IEnumerable<string> crimeIDs,
            IEnumerable<string> ownedBusinessIDs, string residenceID = null, float residenceConfidence = 0f,
            DateTime? expiresAt = null)
        {
            if (intelligence == null || issuedAt == DateTime.MinValue ||
                (expiresAt.HasValue && expiresAt.Value <= issuedAt)) return null;
            intelligence.Normalize();
            WarrantRecord warrant = intelligence.IssueWarrant(issuedAt, type, priority, agencyID, jurisdictionID,
                crimeIDs, ownedBusinessIDs, residenceID, residenceConfidence);
            if (warrant != null) warrant.ExpiresAt = expiresAt;
            return warrant;
        }

        // Snapshot only businesses that are owned at the exact moment the warrant is issued.
        // Later purchases/sales are represented by subsequent ownership observations instead.
        public WarrantRecord IssueWithPropertySnapshot(LawEnforcementIntelligence intelligence, DateTime issuedAt,
            WarrantType type, WarrantPriority priority, string agencyID, string jurisdictionID,
            IEnumerable<string> crimeIDs, IEnumerable<GameLocation> currentProperties,
            string residenceID = null, float residenceConfidence = 0f, DateTime? expiresAt = null)
        {
            IEnumerable<string> ownedBusinessIDs = currentProperties == null
                ? Enumerable.Empty<string>()
                : currentProperties.OfType<Business>()
                    .Where(business => business != null && business.IsOwned)
                    .Select(business => LawEnforcementIntelligence.GetBusinessIdentity(
                        business.Name, business.EntrancePosition.X,
                        business.EntrancePosition.Y, business.EntrancePosition.Z))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            WarrantRecord warrant = Issue(intelligence, issuedAt, type, priority, agencyID, jurisdictionID,
                crimeIDs, ownedBusinessIDs, residenceID, residenceConfidence, expiresAt);
            if (warrant == null || intelligence.Observations == null) return warrant;

            DateTime evidenceCutoff = issuedAt.AddDays(-30);
            IEnumerable<IntelligenceObservation> supportingEvidence = intelligence.Observations
                .Where(observation => observation != null && observation.IsKnownToPolice &&
                    observation.IsVerified && observation.ObservedAt >= evidenceCutoff &&
                    observation.ObservedAt <= issuedAt &&
                    (observation.EvidenceType != IntelligenceEvidenceType.BusinessVisit ||
                     intelligence.Warrants.Any(existingWarrant => existingWarrant != null &&
                        existingWarrant.ID != warrant.ID &&
                        existingWarrant.IssuedAt <= observation.ObservedAt)));

            foreach (IntelligenceObservation observation in supportingEvidence)
            {
                if (!string.IsNullOrWhiteSpace(observation.ID) &&
                    !warrant.EvidenceObservationIDs.Contains(observation.ID))
                    warrant.EvidenceObservationIDs.Add(observation.ID);
            }
            return warrant;
        }

        public WarrantRecord FindActive(LawEnforcementIntelligence intelligence, string agencyID, string jurisdictionID)
        {
            if (intelligence == null || intelligence.Warrants == null) return null;
            // Warrants are discoverable across jurisdiction boundaries. Agency/jurisdiction are provenance,
            // not a legal barrier to recognizing an active warrant.
            return intelligence.Warrants.Where(x => x != null && x.Status == WarrantStatus.Active &&
                    IsArrestAuthority(x.Type))
                .OrderByDescending(x => x.Priority).ThenBy(x => x.IssuedAt).FirstOrDefault();
        }

        public IEnumerable<WarrantRecord> GetActive(LawEnforcementIntelligence intelligence)
        {
            if (intelligence == null || intelligence.Warrants == null) return Enumerable.Empty<WarrantRecord>();
            return intelligence.Warrants.Where(x => x != null && x.Status == WarrantStatus.Active)
                .OrderByDescending(x => x.Priority).ThenBy(x => x.IssuedAt).ToList();
        }

        public void UpdateExpirations(LawEnforcementIntelligence intelligence, DateTime currentTime,
            LawEnforcementIntelligenceSettings settings = null)
        {
            if (intelligence == null || intelligence.Warrants == null || currentTime == DateTime.MinValue)
                return;
            foreach (WarrantRecord warrant in intelligence.Warrants.Where(x => x != null &&
                x.Status == WarrantStatus.Active && x.ExpiresAt.HasValue &&
                currentTime >= x.ExpiresAt.Value).ToList())
            {
                intelligence.ResolveWarrant(warrant.ID, WarrantStatus.Expired, currentTime,
                    "Warrant expired at its configured expiration time.");
            }

            if (intelligence.BOLOs == null) return;
            foreach (WarrantRecord warrant in intelligence.Warrants.Where(x => x != null &&
                IsArrestAuthority(x.Type)).ToList())
            {
                List<BOLORecord> linkedBOLOs = intelligence.BOLOs.Where(bolo => bolo != null &&
                    string.Equals(bolo.LinkedWarrantID, warrant.ID, StringComparison.OrdinalIgnoreCase)).ToList();

                if (warrant.Status != WarrantStatus.Active)
                {
                    foreach (BOLORecord bolo in linkedBOLOs.Where(x => x.Status == BOLOStatus.Active))
                    {
                        bolo.Status = warrant.Status == WarrantStatus.Expired
                            ? BOLOStatus.Expired
                            : (warrant.Status == WarrantStatus.Served || warrant.Status == WarrantStatus.Cleared
                                ? BOLOStatus.Resolved : BOLOStatus.Cancelled);
                        bolo.Notes = (bolo.Notes ?? string.Empty) + " Linked warrant status: " +
                            warrant.Status + ".";
                    }
                    continue;
                }

                // One person BOLO per warrant. Existing resolved/cancelled records are retained
                // as history and are not silently re-issued.
                if (linkedBOLOs.Count > 0 || settings == null || !settings.EnableBOLOSystem)
                    continue;

                settings.Normalize();
                IntelligenceObservation lastKnownObservation = intelligence.Observations?
                    .Where(observation => observation != null && observation.IsKnownToPolice &&
                        observation.IsVerified && observation.ObservedAt <= currentTime)
                    .OrderByDescending(observation => observation.ObservedAt).FirstOrDefault();

                BOLORecord boloRecord = intelligence.IssueBOLO(currentTime, BOLOTargetType.Person,
                    "Wanted person associated with an active " + warrant.Type + " warrant.",
                    null, null, null, lastKnownObservation?.LocationID, warrant.ExpiresAt,
                    settings.BOLOInZoneStopChance / 100f,
                    settings.BOLOOutOfZoneStopChance / 100f,
                    "AUTO_WARRANT_BOLO:" + warrant.ID);
                if (boloRecord != null)
                    boloRecord.LinkedWarrantID = warrant.ID;
            }
        }

        public bool CanAuthorizeArrest(WarrantRecord warrant)
        {
            return warrant != null && warrant.Status == WarrantStatus.Active && IsArrestAuthority(warrant.Type);
        }

        public bool CanAuthorizeSearch(WarrantRecord warrant)
        {
            // A search warrant is the only warrant type that authorizes a planned premises search.
            return warrant != null && warrant.Status == WarrantStatus.Active &&
                warrant.Type == WarrantType.Search;
        }

        public bool Resolve(LawEnforcementIntelligence intelligence, string warrantID, WarrantStatus status,
            DateTime resolvedAt, string notes)
        {
            return intelligence != null && intelligence.ResolveWarrant(warrantID, status, resolvedAt, notes);
        }

        private static bool IsArrestAuthority(WarrantType type)
        {
            return type == WarrantType.Arrest || type == WarrantType.Bench ||
                type == WarrantType.ProbationParole || type == WarrantType.Extradition;
        }
    }
}
