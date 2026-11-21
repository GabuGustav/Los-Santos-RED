using System;
using System.Collections.Generic;
using System.Linq;

namespace LosSantosRED.lsr
{
    // Coordinates lifecycle processing for the persistent intelligence data. This class
    // deliberately contains no GTA entity handles and performs no world scans.
    public class LawEnforcementIntelligenceManager
    {
        private DateTime LastUpdateAt { get; set; }

        public WarrantManager WarrantManager { get; private set; } = new WarrantManager();
        public BOLOManager BOLOManager { get; private set; } = new BOLOManager();
        public IntelligenceDatabase IntelligenceDatabase { get; private set; } = new IntelligenceDatabase();
        public InvestigationManager InvestigationManager { get; private set; } = new InvestigationManager();
        public SurveillanceAssignmentManager SurveillanceAssignmentManager { get; private set; } = new SurveillanceAssignmentManager();
        public PoliceOperationsManager PoliceOperationsManager { get; private set; } = new PoliceOperationsManager();
        public AgencyIntelligenceAccessManager AgencyIntelligenceAccessManager { get; private set; } = new AgencyIntelligenceAccessManager();

        public string GetDebugSummary(LawEnforcementIntelligence intelligence)
        {
            if (intelligence == null) return "Law enforcement intelligence: unavailable";
            intelligence.Normalize();
            int activeWarrants = intelligence.Warrants.Count(x => x != null && x.Status == WarrantStatus.Active);
            int activeBOLOs = intelligence.BOLOs.Count(x => x != null && x.Status == BOLOStatus.Active);
            int policeObservations = intelligence.Observations.Count(x => x != null && x.IsKnownToPolice);
            int activeSurveillance = intelligence.SurveillanceAssignments.Count(x => x != null &&
                x.State != SurveillanceAssignmentState.Available &&
                x.State != SurveillanceAssignmentState.Completed);
            int activeOperations = intelligence.PoliceOperations.Count(x => x != null &&
                x.Stage != PoliceOperationStage.Completed && x.Stage != PoliceOperationStage.Aborted);
            int pendingRelays = intelligence.AlertDisseminations.Count(x => x != null &&
                x.Status == AlertDisseminationStatus.Pending);
            int pendingAccessRequests = intelligence.AccessRequests.Count(x => x != null &&
                x.Status == InvestigationAccessRequestStatus.Pending);
            int activeAccessGrants = intelligence.AccessGrants.Count(x => x != null && !x.IsRevoked);
            InvestigationRecord current = intelligence.Investigations
                .Where(x => x != null && x.Stage != InvestigationStage.Closed &&
                    x.Stage != InvestigationStage.Suspended)
                .OrderByDescending(x => x.HeatLevel).FirstOrDefault();
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "LE Intelligence | Observations: {0} (Police-known: {1}) | Investigations: {2} | Stage: {3} | Warrants: {4} | BOLOs: {5} | FIB teams: {6} | Operations: {7} | Pending alert relays: {8} | Access requests: {9} | Active access grants: {10}",
                intelligence.Observations.Count, policeObservations, intelligence.Investigations.Count,
                current == null ? "None" : current.Stage.ToString(), activeWarrants, activeBOLOs,
                activeSurveillance, activeOperations, pendingRelays, pendingAccessRequests, activeAccessGrants);
        }

        public bool TryAssignSurveillance(LawEnforcementIntelligence intelligence, string investigationID,
            string teamID, DateTime assignedAt, LawEnforcementIntelligenceSettings settings)
        {
            if (intelligence == null || settings == null || !settings.EnableFIBSurveillance ||
                string.IsNullOrWhiteSpace(teamID) || assignedAt == DateTime.MinValue) return false;
            settings.Normalize();
            intelligence.Normalize();
            int activeCount = intelligence.SurveillanceAssignments.Count(x => x != null &&
                x.State != SurveillanceAssignmentState.Available &&
                x.State != SurveillanceAssignmentState.Completed);
            if (activeCount >= settings.MaxActiveFIBTeams ||
                intelligence.SurveillanceAssignments.Any(x => x != null && x.TeamID == teamID &&
                    x.State != SurveillanceAssignmentState.Completed &&
                    x.State != SurveillanceAssignmentState.Available)) return false;
            InvestigationRecord investigation = intelligence.Investigations.FirstOrDefault(x =>
                x != null && x.ID == investigationID && x.Stage != InvestigationStage.Closed &&
                x.Stage != InvestigationStage.Suspended);
            if (investigation == null) return false;
            intelligence.SurveillanceAssignments.Add(new SurveillanceAssignment
            {
                ID = Guid.NewGuid().ToString("N"),
                InvestigationID = investigation.ID,
                TeamID = teamID,
                TargetSubjectID = intelligence.SubjectID,
                State = SurveillanceAssignmentState.Assigned,
                AssignedAt = assignedAt,
                LastStateChangedAt = assignedAt
            });
            intelligence.LastUpdatedAt = assignedAt;
            return true;
        }

        // Queue a warrant or BOLO for another agency. The recipient gains no alert
        // awareness until the configured relay delay has elapsed.
        public bool ScheduleAlertDissemination(LawEnforcementIntelligence intelligence,
            IntelligenceAlertType alertType, string alertID, string sendingAgencyID,
            string receivingAgencyID, DateTime scheduledAt,
            LawEnforcementIntelligenceSettings settings, bool isMutualAid = false, string notes = null)
        {
            if (intelligence == null || settings == null || !settings.EnableInterAgencyAlertSharing ||
                string.IsNullOrWhiteSpace(alertID) || string.IsNullOrWhiteSpace(sendingAgencyID) ||
                string.IsNullOrWhiteSpace(receivingAgencyID) ||
                string.Equals(sendingAgencyID, receivingAgencyID, StringComparison.OrdinalIgnoreCase) ||
                scheduledAt == DateTime.MinValue)
                return false;

            settings.Normalize();
            intelligence.Normalize();
            bool alertIsActive = alertType == IntelligenceAlertType.Warrant
                ? intelligence.Warrants.Any(x => x != null && x.ID == alertID && x.Status == WarrantStatus.Active)
                : intelligence.BOLOs.Any(x => x != null && x.ID == alertID && x.Status == BOLOStatus.Active);
            if (!alertIsActive) return false;

            bool alreadyQueued = intelligence.AlertDisseminations.Any(x => x != null &&
                x.AlertType == alertType && string.Equals(x.AlertID, alertID, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.ReceivingAgencyID, receivingAgencyID, StringComparison.OrdinalIgnoreCase) &&
                (x.Status == AlertDisseminationStatus.Pending || x.Status == AlertDisseminationStatus.Delivered));
            if (alreadyQueued) return false;

            int delayMinutes = isMutualAid
                ? settings.MutualAidAlertDelayMinutes : settings.InterAgencyAlertDelayMinutes;
            intelligence.AlertDisseminations.Add(new AlertDisseminationRecord
            {
                ID = Guid.NewGuid().ToString("N"),
                AlertType = alertType,
                AlertID = alertID,
                SendingAgencyID = sendingAgencyID,
                ReceivingAgencyID = receivingAgencyID,
                ScheduledAt = scheduledAt,
                AvailableAt = scheduledAt.AddMinutes(delayMinutes),
                Status = AlertDisseminationStatus.Pending,
                IsMutualAid = isMutualAid,
                Notes = notes
            });
            intelligence.LastUpdatedAt = scheduledAt;
            return true;
        }

        // Called when a unit from another agency is actually dispatched to the
        // player's active incident. This is the point at which mutual-aid sharing begins.
        public int ScheduleActiveAlertsForAgency(LawEnforcementIntelligence intelligence,
            string receivingAgencyID, DateTime scheduledAt,
            LawEnforcementIntelligenceSettings settings, bool isMutualAid = true)
        {
            if (intelligence == null || settings == null || !settings.EnableInterAgencyAlertSharing ||
                string.IsNullOrWhiteSpace(receivingAgencyID) || scheduledAt == DateTime.MinValue)
                return 0;

            intelligence.Normalize();
            int scheduled = 0;
            foreach (WarrantRecord warrant in intelligence.Warrants.Where(x => x != null &&
                x.Status == WarrantStatus.Active &&
                (x.KnownToAgencyIDs == null || !x.KnownToAgencyIDs.Contains(receivingAgencyID, StringComparer.OrdinalIgnoreCase))))
            {
                if (ScheduleAlertDissemination(intelligence, IntelligenceAlertType.Warrant,
                    warrant.ID, warrant.IssuingAgencyID, receivingAgencyID, scheduledAt,
                    settings, isMutualAid, "Shared because this agency was dispatched to the active incident."))
                    scheduled++;
            }

            foreach (BOLORecord bolo in intelligence.BOLOs.Where(x => x != null &&
                x.Status == BOLOStatus.Active && string.IsNullOrWhiteSpace(x.LinkedWarrantID) &&
                (x.KnownToAgencyIDs == null || !x.KnownToAgencyIDs.Contains(receivingAgencyID, StringComparer.OrdinalIgnoreCase))))
            {
                string sendingAgencyID = bolo.KnownToAgencyIDs?.FirstOrDefault();
                if (ScheduleAlertDissemination(intelligence, IntelligenceAlertType.BOLO,
                    bolo.ID, sendingAgencyID, receivingAgencyID, scheduledAt,
                    settings, isMutualAid, "Shared because this agency was dispatched to the active incident."))
                    scheduled++;
            }
            return scheduled;
        }

        private void ProcessAlertDisseminations(LawEnforcementIntelligence intelligence, DateTime currentTime)
        {
            if (intelligence?.AlertDisseminations == null) return;
            foreach (AlertDisseminationRecord delivery in intelligence.AlertDisseminations
                .Where(x => x != null && x.Status == AlertDisseminationStatus.Pending &&
                    x.AvailableAt <= currentTime).ToList())
            {
                if (delivery.AlertType == IntelligenceAlertType.Warrant)
                {
                    WarrantRecord warrant = intelligence.Warrants.FirstOrDefault(x => x != null &&
                        x.ID == delivery.AlertID && x.Status == WarrantStatus.Active);
                    if (warrant == null)
                    {
                        delivery.Status = AlertDisseminationStatus.Cancelled;
                        delivery.Notes = "Alert was no longer active when the relay became available.";
                        continue;
                    }
                    WarrantManager.DisseminateToAgency(intelligence, warrant.ID, delivery.ReceivingAgencyID);
                }
                else
                {
                    BOLORecord bolo = intelligence.BOLOs.FirstOrDefault(x => x != null &&
                        x.ID == delivery.AlertID && x.Status == BOLOStatus.Active);
                    if (bolo == null)
                    {
                        delivery.Status = AlertDisseminationStatus.Cancelled;
                        delivery.Notes = "Alert was no longer active when the relay became available.";
                        continue;
                    }
                    if (bolo.KnownToAgencyIDs == null) bolo.KnownToAgencyIDs = new List<string>();
                    if (!bolo.KnownToAgencyIDs.Contains(delivery.ReceivingAgencyID, StringComparer.OrdinalIgnoreCase))
                        bolo.KnownToAgencyIDs.Add(delivery.ReceivingAgencyID);
                }
                delivery.Status = AlertDisseminationStatus.Delivered;
                delivery.DeliveredAt = currentTime;
            }
        }

        public void Update(LawEnforcementIntelligence intelligence, DateTime currentTime, LawEnforcementIntelligenceSettings settings)
        {
            if (intelligence == null || currentTime == DateTime.MinValue || settings == null) return;
            settings.Normalize();

            // Do not run against a clock that has moved backwards (for example, a save load).
            if (LastUpdateAt != DateTime.MinValue && currentTime < LastUpdateAt)
                LastUpdateAt = DateTime.MinValue;

            // BOLO expiry is a record-lifecycle concern, not investigation processing.
            // Run it on every intelligence tick, even when background investigation is
            // throttled or BOLO generation/response has been disabled in settings.
            intelligence.Normalize();
            BOLOManager.Update(intelligence, currentTime);
            if (settings.EnableWarrants)
                WarrantManager.UpdateExpirations(intelligence, currentTime, settings);

            if (LastUpdateAt != DateTime.MinValue &&
                currentTime - LastUpdateAt < TimeSpan.FromMilliseconds(settings.InvestigationUpdateInterval))
                return;

            ProcessAlertDisseminations(intelligence, currentTime);
            AgencyIntelligenceAccessManager.ProcessExpirations(intelligence, currentTime);

            if (settings.EnableBackgroundInvestigation)
            {
                bool hasPoliceEvidence = intelligence.Observations.Any(x => x != null &&
                    x.IsKnownToPolice && x.ObservedAt <= currentTime);
                bool hasActiveRecords = intelligence.Warrants.Any(x => x != null &&
                    x.Status == WarrantStatus.Active) ||
                    intelligence.BOLOs.Any(x => x != null && x.Status == BOLOStatus.Active);
                InvestigationRecord openInvestigation = intelligence.Investigations.FirstOrDefault(x =>
                    x != null && x.Stage != InvestigationStage.Closed &&
                    x.Stage != InvestigationStage.Suspended);
                InvestigationRecord latestClosedOrSuspended = intelligence.Investigations
                    .Where(x => x != null && (x.Stage == InvestigationStage.Closed ||
                        x.Stage == InvestigationStage.Suspended))
                    .OrderByDescending(x => x.LastActivityAt).FirstOrDefault();

                bool hasNewEvidence = latestClosedOrSuspended == null
                    ? hasPoliceEvidence
                    : intelligence.Observations.Any(x => x != null && x.IsKnownToPolice &&
                        x.ObservedAt > latestClosedOrSuspended.LastActivityAt &&
                        x.ObservedAt <= currentTime);
                bool hasNewWarrant = latestClosedOrSuspended == null
                    ? hasActiveRecords
                    : intelligence.Warrants.Any(x => x != null && x.Status == WarrantStatus.Active &&
                        x.IssuedAt > latestClosedOrSuspended.LastActivityAt &&
                        x.IssuedAt <= currentTime);
                bool hasNewBOLO = latestClosedOrSuspended == null
                    ? hasActiveRecords
                    : intelligence.BOLOs.Any(x => x != null && x.Status == BOLOStatus.Active &&
                        x.IssuedAt > latestClosedOrSuspended.LastActivityAt &&
                        x.IssuedAt <= currentTime);

                // A closed or suspended case must not silently reopen just because old records
                // remain in the database. New police evidence or a newly issued alert is required.
                if (openInvestigation != null || hasNewEvidence || hasNewWarrant || hasNewBOLO)
                    InvestigationManager.Refresh(intelligence, currentTime);
            }

            PruneOldObservations(intelligence, currentTime, settings.IntelligenceRetentionDays);
            intelligence.LastUpdatedAt = currentTime;
            LastUpdateAt = currentTime;
        }

        private static void PruneOldObservations(LawEnforcementIntelligence intelligence, DateTime currentTime, int retentionDays)
        {
            DateTime cutoff = currentTime.AddDays(-Math.Max(1, retentionDays));
            List<string> expiredIDs = new List<string>();

            if (intelligence.Observations != null && intelligence.Observations.Count > 0)
            {
                expiredIDs = intelligence.Observations
                    .Where(x => x != null && x.ObservedAt < cutoff)
                    .Select(x => x.ID)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();

                if (expiredIDs.Count > 0)
                {
                    intelligence.Observations.RemoveAll(x => x == null || x.ObservedAt < cutoff);
                    foreach (InvestigationRecord investigation in intelligence.Investigations.Where(x => x != null))
                        if (investigation.ObservationIDs != null)
                            investigation.ObservationIDs.RemoveAll(x => expiredIDs.Contains(x));
                    foreach (WarrantRecord warrant in intelligence.Warrants.Where(x => x != null))
                        if (warrant.EvidenceObservationIDs != null)
                            warrant.EvidenceObservationIDs.RemoveAll(x => expiredIDs.Contains(x));
                }
            }

            if (intelligence.VehicleAssociations != null)
                intelligence.VehicleAssociations.RemoveAll(x => x == null || x.ObservedAt < cutoff);
            if (intelligence.SurveillanceAssignments != null)
                intelligence.SurveillanceAssignments.RemoveAll(x => x == null ||
                    (x.State == SurveillanceAssignmentState.Completed &&
                     x.LastStateChangedAt != DateTime.MinValue && x.LastStateChangedAt < cutoff));
            if (intelligence.PoliceOperations != null)
                intelligence.PoliceOperations.RemoveAll(x => x == null ||
                    ((x.Stage == PoliceOperationStage.Completed || x.Stage == PoliceOperationStage.Aborted) &&
                     x.LastUpdatedAt != DateTime.MinValue && x.LastUpdatedAt < cutoff));
            if (intelligence.AlertDisseminations != null)
                intelligence.AlertDisseminations.RemoveAll(x => x == null ||
                    (x.Status != AlertDisseminationStatus.Pending &&
                     x.ScheduledAt != DateTime.MinValue && x.ScheduledAt < cutoff));
            if (intelligence.AccessRequests != null)
                intelligence.AccessRequests.RemoveAll(x => x == null ||
                    (x.Status != InvestigationAccessRequestStatus.Pending &&
                     x.RequestedAt != DateTime.MinValue && x.RequestedAt < cutoff));
            if (intelligence.AccessGrants != null)
                intelligence.AccessGrants.RemoveAll(x => x == null ||
                    ((x.IsRevoked || (x.ExpiresAt.HasValue && x.ExpiresAt.Value <= currentTime)) &&
                     x.GrantedAt != DateTime.MinValue && x.GrantedAt < cutoff));
            if (intelligence.AccessAuditEntries != null)
                intelligence.AccessAuditEntries.RemoveAll(x => x == null ||
                    (x.OccurredAt != DateTime.MinValue && x.OccurredAt < cutoff));
        }

        public void ResetClock()
        {
            LastUpdateAt = DateTime.MinValue;
        }
    }
}
