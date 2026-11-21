using System;
using System.Collections.Generic;
using System.Linq;

namespace LosSantosRED.lsr
{
    // Investigation files are private to their owning agency until a durable grant is approved.
    // FIB and other agencies use the same authorization path; agency identity alone is not a bypass.
    public class AgencyIntelligenceAccessManager
    {
        public InvestigationAccessRequest RequestAccess(LawEnforcementIntelligence intelligence,
            string investigationID, string requestingAgencyID, InvestigationAccessScope requestedScope,
            string purpose, DateTime requestedAt, DateTime? requestExpiresAt = null)
        {
            if (intelligence == null || string.IsNullOrWhiteSpace(investigationID) ||
                string.IsNullOrWhiteSpace(requestingAgencyID) || string.IsNullOrWhiteSpace(purpose) ||
                !Enum.IsDefined(typeof(InvestigationAccessScope), requestedScope) ||
                requestedAt == DateTime.MinValue ||
                (requestExpiresAt.HasValue && requestExpiresAt.Value <= requestedAt)) return null;
            DateTime requestExpiry = requestExpiresAt ?? requestedAt.AddDays(7);
            intelligence.Normalize();
            InvestigationRecord investigation = FindInvestigation(intelligence, investigationID);
            string owner = ResolveOwningAgency(intelligence, investigation);
            if (investigation == null || investigation.Stage == InvestigationStage.Closed ||
                investigation.Stage == InvestigationStage.Suspended || string.IsNullOrWhiteSpace(owner) ||
                string.Equals(owner, requestingAgencyID, StringComparison.OrdinalIgnoreCase) ||
                CanAccess(intelligence, investigationID, requestingAgencyID, requestedScope, requestedAt)) return null;
            InvestigationAccessRequest pending = intelligence.AccessRequests.FirstOrDefault(x =>
                x != null && x.Status == InvestigationAccessRequestStatus.Pending &&
                string.Equals(x.InvestigationID, investigationID, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.RequestingAgencyID, requestingAgencyID, StringComparison.OrdinalIgnoreCase));
            if (pending != null) return pending.Copy();
            InvestigationAccessRequest request = new InvestigationAccessRequest
            {
                ID = Guid.NewGuid().ToString("N"), InvestigationID = investigationID,
                RequestingAgencyID = requestingAgencyID, OwningAgencyID = owner,
                RequestedScope = requestedScope, Purpose = purpose.Trim(), RequestedAt = requestedAt,
                ExpiresAt = requestExpiry, Status = InvestigationAccessRequestStatus.Pending
            };
            intelligence.AccessRequests.Add(request);
            AddAudit(intelligence, investigationID, request.ID, requestingAgencyID,
                "AccessRequested", requestedScope, requestedAt, purpose.Trim());
            intelligence.LastUpdatedAt = requestedAt;
            return request.Copy();
        }

        public bool DecideRequest(LawEnforcementIntelligence intelligence, string requestID,
            string decidingAgencyID, bool approve, InvestigationAccessScope approvedScope,
            DateTime decidedAt, DateTime? grantExpiresAt = null, string decisionNotes = null)
        {
            if (intelligence == null || string.IsNullOrWhiteSpace(requestID) ||
                string.IsNullOrWhiteSpace(decidingAgencyID) || decidedAt == DateTime.MinValue ||
                (approve && !Enum.IsDefined(typeof(InvestigationAccessScope), approvedScope)) ||
                (grantExpiresAt.HasValue && grantExpiresAt.Value <= decidedAt)) return false;
            DateTime? effectiveGrantExpiry = approve ? (DateTime?)(grantExpiresAt ?? decidedAt.AddDays(30)) : null;
            intelligence.Normalize();
            InvestigationAccessRequest request = intelligence.AccessRequests.FirstOrDefault(x =>
                x != null && string.Equals(x.ID, requestID, StringComparison.OrdinalIgnoreCase));
            if (request == null || request.Status != InvestigationAccessRequestStatus.Pending ||
                decidedAt < request.RequestedAt ||
                (request.ExpiresAt.HasValue && decidedAt >= request.ExpiresAt.Value) ||
                !string.Equals(request.OwningAgencyID, decidingAgencyID, StringComparison.OrdinalIgnoreCase))
                return false;
            InvestigationRecord investigation = FindInvestigation(intelligence, request.InvestigationID);
            if (investigation == null || investigation.Stage == InvestigationStage.Closed ||
                investigation.Stage == InvestigationStage.Suspended) return false;
            if (approve && (int)approvedScope < (int)request.RequestedScope)
                return false;

            request.DecidedByAgencyID = decidingAgencyID;
            request.DecidedAt = decidedAt;
            request.DecisionNotes = decisionNotes;
            if (!approve)
            {
                request.Status = InvestigationAccessRequestStatus.Denied;
                AddAudit(intelligence, request.InvestigationID, request.ID, decidingAgencyID,
                    "AccessDenied", request.RequestedScope, decidedAt, decisionNotes);
                intelligence.LastUpdatedAt = decidedAt;
                return true;
            }
            InvestigationAccessGrant grant = new InvestigationAccessGrant
            {
                ID = Guid.NewGuid().ToString("N"), RequestID = request.ID,
                InvestigationID = request.InvestigationID, GrantedToAgencyID = request.RequestingAgencyID,
                GrantedByAgencyID = decidingAgencyID, Scope = approvedScope,
                GrantedAt = decidedAt, ExpiresAt = effectiveGrantExpiry
            };
            intelligence.AccessGrants.Add(grant);
            request.Status = InvestigationAccessRequestStatus.Approved;
            request.ApprovedScope = approvedScope;
            request.GrantID = grant.ID;
            AddAudit(intelligence, request.InvestigationID, request.ID, decidingAgencyID,
                "AccessApproved", approvedScope, decidedAt, decisionNotes);
            intelligence.LastUpdatedAt = decidedAt;
            return true;
        }

        public bool CancelRequest(LawEnforcementIntelligence intelligence, string requestID,
            string requestingAgencyID, DateTime cancelledAt, string reason = null)
        {
            if (intelligence == null || string.IsNullOrWhiteSpace(requestID) ||
                string.IsNullOrWhiteSpace(requestingAgencyID) || cancelledAt == DateTime.MinValue)
                return false;
            intelligence.Normalize();
            InvestigationAccessRequest request = intelligence.AccessRequests.FirstOrDefault(x =>
                x != null && string.Equals(x.ID, requestID, StringComparison.OrdinalIgnoreCase));
            if (request == null || request.Status != InvestigationAccessRequestStatus.Pending ||
                cancelledAt < request.RequestedAt ||
                (request.ExpiresAt.HasValue && cancelledAt >= request.ExpiresAt.Value) ||
                !string.Equals(request.RequestingAgencyID, requestingAgencyID, StringComparison.OrdinalIgnoreCase))
                return false;
            request.Status = InvestigationAccessRequestStatus.Cancelled;
            request.DecidedAt = cancelledAt;
            request.DecisionNotes = reason;
            AddAudit(intelligence, request.InvestigationID, request.ID, requestingAgencyID,
                "AccessRequestCancelled", request.RequestedScope, cancelledAt, reason);
            intelligence.LastUpdatedAt = cancelledAt;
            return true;
        }

        public bool RevokeGrant(LawEnforcementIntelligence intelligence, string grantID,
            string revokingAgencyID, DateTime revokedAt, string reason = null)
        {
            if (intelligence == null || string.IsNullOrWhiteSpace(grantID) ||
                string.IsNullOrWhiteSpace(revokingAgencyID) || revokedAt == DateTime.MinValue) return false;
            intelligence.Normalize();
            InvestigationAccessGrant grant = intelligence.AccessGrants.FirstOrDefault(x =>
                x != null && string.Equals(x.ID, grantID, StringComparison.OrdinalIgnoreCase));
            if (grant == null || grant.IsRevoked || revokedAt < grant.GrantedAt ||
                !string.Equals(grant.GrantedByAgencyID, revokingAgencyID, StringComparison.OrdinalIgnoreCase))
                return false;
            grant.IsRevoked = true;
            grant.RevokedAt = revokedAt;
            grant.RevocationReason = reason;
            AddAudit(intelligence, grant.InvestigationID, grant.RequestID, revokingAgencyID,
                "AccessRevoked", grant.Scope, revokedAt, reason);
            intelligence.LastUpdatedAt = revokedAt;
            return true;
        }

        public bool CanAccess(LawEnforcementIntelligence intelligence, string investigationID,
            string agencyID, InvestigationAccessScope requiredScope, DateTime at)
        {
            if (intelligence == null || string.IsNullOrWhiteSpace(investigationID) ||
                string.IsNullOrWhiteSpace(agencyID) || at == DateTime.MinValue) return false;
            intelligence.Normalize();
            InvestigationRecord investigation = FindInvestigation(intelligence, investigationID);
            if (investigation == null) return false;
            string owner = ResolveOwningAgency(intelligence, investigation);
            if (string.Equals(owner, agencyID, StringComparison.OrdinalIgnoreCase)) return true;
            return intelligence.AccessGrants.Any(grant => grant != null && !grant.IsRevoked &&
                string.Equals(grant.InvestigationID, investigationID, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(grant.GrantedToAgencyID, agencyID, StringComparison.OrdinalIgnoreCase) &&
                (int)grant.Scope >= (int)requiredScope && grant.GrantedAt <= at &&
                (!grant.ExpiresAt.HasValue || at < grant.ExpiresAt.Value));
        }

        // Returns a detached copy so callers cannot mutate the persistent case through a read API.
        // Successful and denied read attempts are both written to the audit trail.
        public InvestigationRecord GetInvestigation(LawEnforcementIntelligence intelligence,
            string investigationID, string agencyID, InvestigationAccessScope requiredScope, DateTime at)
        {
            if (intelligence == null || string.IsNullOrWhiteSpace(investigationID) ||
                string.IsNullOrWhiteSpace(agencyID) || at == DateTime.MinValue) return null;
            bool allowed = CanAccess(intelligence, investigationID, agencyID, requiredScope, at);
            AddAudit(intelligence, investigationID, null, agencyID,
                allowed ? "InvestigationRead" : "InvestigationAccessDenied", requiredScope, at,
                allowed ? null : "Requested scope is not authorised.");
            if (!allowed) return null;
            InvestigationRecord record = FindInvestigation(intelligence, investigationID);
            if (record == null) return null;
            InvestigationRecord detached = record.Copy();
            if (requiredScope == InvestigationAccessScope.Summary)
            {
                detached.ObservationIDs.Clear();
                detached.WarrantIDs.Clear();
                detached.BOLOIDs.Clear();
                detached.KnownVehicleIDs.Clear();
                detached.KnownLocationIDs.Clear();
                detached.AssociateIDs.Clear();
                detached.Notes = null;
            }
            return detached;
        }

        public IEnumerable<InvestigationAccessRequest> GetPendingRequestsForAgency(
            LawEnforcementIntelligence intelligence, string owningAgencyID, DateTime at)
        {
            if (intelligence == null || string.IsNullOrWhiteSpace(owningAgencyID))
                return Enumerable.Empty<InvestigationAccessRequest>();
            intelligence.Normalize();
            ProcessExpirations(intelligence, at);
            return intelligence.AccessRequests.Where(request => request != null &&
                request.Status == InvestigationAccessRequestStatus.Pending &&
                string.Equals(request.OwningAgencyID, owningAgencyID, StringComparison.OrdinalIgnoreCase))
                .Select(request => request.Copy()).ToList();
        }

        public IEnumerable<IntelligenceAccessAuditEntry> GetAuditTrailForAgency(
            LawEnforcementIntelligence intelligence, string investigationID, string owningAgencyID)
        {
            if (intelligence == null || string.IsNullOrWhiteSpace(investigationID) ||
                string.IsNullOrWhiteSpace(owningAgencyID))
                return Enumerable.Empty<IntelligenceAccessAuditEntry>();
            intelligence.Normalize();
            InvestigationRecord investigation = FindInvestigation(intelligence, investigationID);
            string owner = ResolveOwningAgency(intelligence, investigation);
            if (!string.Equals(owner, owningAgencyID, StringComparison.OrdinalIgnoreCase))
                return Enumerable.Empty<IntelligenceAccessAuditEntry>();
            return intelligence.AccessAuditEntries.Where(entry => entry != null &&
                    string.Equals(entry.InvestigationID, investigationID, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.OccurredAt).Select(entry => entry.Copy()).ToList();
        }

        public void ProcessExpirations(LawEnforcementIntelligence intelligence, DateTime at)
        {
            if (intelligence == null || at == DateTime.MinValue) return;
            intelligence.Normalize();
            foreach (InvestigationAccessRequest request in intelligence.AccessRequests.Where(x =>
                x != null && x.Status == InvestigationAccessRequestStatus.Pending &&
                x.ExpiresAt.HasValue && at >= x.ExpiresAt.Value).ToList())
            {
                request.Status = InvestigationAccessRequestStatus.Expired;
                request.DecidedAt = at;
                AddAudit(intelligence, request.InvestigationID, request.ID, request.OwningAgencyID,
                    "AccessRequestExpired", request.RequestedScope, at, "The pending request expired without a decision.");
            }
            foreach (InvestigationAccessGrant grant in intelligence.AccessGrants.Where(x =>
                x != null && !x.IsRevoked && x.ExpiresAt.HasValue && at >= x.ExpiresAt.Value).ToList())
            {
                grant.IsRevoked = true;
                grant.RevokedAt = grant.ExpiresAt;
                grant.RevocationReason = "Grant expired.";
                AddAudit(intelligence, grant.InvestigationID, grant.RequestID, grant.GrantedByAgencyID,
                    "AccessGrantExpired", grant.Scope, grant.ExpiresAt.Value, grant.RevocationReason);
            }
        }

        private static InvestigationRecord FindInvestigation(LawEnforcementIntelligence intelligence, string id)
        {
            return intelligence?.Investigations?.FirstOrDefault(x => x != null &&
                string.Equals(x.ID, id, StringComparison.OrdinalIgnoreCase));
        }

        private static string ResolveOwningAgency(LawEnforcementIntelligence intelligence,
            InvestigationRecord investigation)
        {
            if (investigation == null) return null;
            if (!string.IsNullOrWhiteSpace(investigation.AssignedAgencyID)) return investigation.AssignedAgencyID;
            return intelligence?.Warrants?.Where(warrant => warrant != null &&
                investigation.WarrantIDs != null && investigation.WarrantIDs.Contains(warrant.ID) &&
                !string.IsNullOrWhiteSpace(warrant.IssuingAgencyID))
                .OrderBy(warrant => warrant.IssuedAt)
                .Select(warrant => warrant.IssuingAgencyID).FirstOrDefault();
        }

        private static void AddAudit(LawEnforcementIntelligence intelligence, string investigationID,
            string requestID, string actorAgencyID, string action, InvestigationAccessScope? scope,
            DateTime at, string notes)
        {
            if (intelligence == null || string.IsNullOrWhiteSpace(actorAgencyID) ||
                string.IsNullOrWhiteSpace(action) || at == DateTime.MinValue) return;
            if (intelligence.AccessAuditEntries == null)
                intelligence.AccessAuditEntries = new List<IntelligenceAccessAuditEntry>();
            intelligence.AccessAuditEntries.Add(new IntelligenceAccessAuditEntry
            {
                ID = Guid.NewGuid().ToString("N"), InvestigationID = investigationID,
                RequestID = requestID, ActorAgencyID = actorAgencyID, Action = action,
                Scope = scope, OccurredAt = at, Notes = notes
            });
        }
    }
}
