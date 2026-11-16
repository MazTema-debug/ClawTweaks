// SPDX-License-Identifier: AGPL-3.0-only
using System;

namespace LegionTweaks
{
    // A strict comparison of two inspector observations, not a maintenance
    // authorization. The caller still needs identity, exclusion, process and
    // recovery proofs which this comparison cannot provide.
    internal static class FpsY3MaintenanceSnapshot
    {
        internal static bool SameCandidate(FpsY3InspectionResult before, FpsY3InspectionResult after)
        {
            if (!Candidate(before) || !Candidate(after) || before.Classification != after.Classification) return false;
            if (before.Classification == "absent") return true;
            return before.ContentSha256 == after.ContentSha256 && before.FileIdentitySha256 == after.FileIdentitySha256;
        }

        private static bool Candidate(FpsY3InspectionResult value)
        {
            if (value == null || value.Version != 1 || value.Scope != "current_process_identity"
                || value.AuthorizesMaintenance || value.Reason != null) return false;
            if (value.Classification == "absent")
                return value.Phase == null && value.OriginalPresent == null && value.ErrorPresent == null
                    && value.ContentSha256 == null && value.FileIdentitySha256 == null;
            return value.Classification == "restored_with_original" && value.Phase == "Restored"
                && value.OriginalPresent == true && value.ErrorPresent == false
                && Sha256(value.ContentSha256) && Sha256(value.FileIdentitySha256);
        }

        internal static bool Sha256(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'A' && c <= 'F')) return false;
            return true;
        }
    }
}
