using Orca.Core.Domain;

namespace Orca.Core.Vault;

/// <summary>The YAML spellings of every schema enum. Parsing is exact-match and fail-loud;
/// the ToYaml overloads are the single source of truth for how values are written back
/// (the two status overloads are public so front-end messages spell statuses the way the
/// plan file does; parsing stays internal to the vault layer).</summary>
public static class SchemaEnums
{
    internal static PlanStatus ParsePlanStatus(string value, string filePath) => value switch
    {
        "draft" => PlanStatus.Draft,
        "approved" => PlanStatus.Approved,
        "in_progress" => PlanStatus.InProgress,
        "blocked" => PlanStatus.Blocked,
        "complete" => PlanStatus.Complete,
        "abandoned" => PlanStatus.Abandoned,
        _ => throw new VaultSchemaException(filePath,
            $"invalid plan status '{value}' (expected draft|approved|in_progress|blocked|complete|abandoned)"),
    };

    internal static CheckpointStatus ParseCheckpointStatus(string value, string filePath) => value switch
    {
        "pending" => CheckpointStatus.Pending,
        "in_progress" => CheckpointStatus.InProgress,
        "awaiting_review" => CheckpointStatus.AwaitingReview,
        "complete" => CheckpointStatus.Complete,
        "rejected" => CheckpointStatus.Rejected,
        "blocked" => CheckpointStatus.Blocked,
        _ => throw new VaultSchemaException(filePath,
            $"invalid checkpoint status '{value}' (expected pending|in_progress|awaiting_review|complete|rejected|blocked)"),
    };

    internal static PlanningStrategyKind ParseStrategy(string value, string filePath) => value switch
    {
        "single_candidate" => PlanningStrategyKind.SingleCandidate,
        "multi_candidate" => PlanningStrategyKind.MultiCandidate,
        _ => throw new VaultSchemaException(filePath,
            $"invalid planning strategy '{value}' (expected single_candidate|multi_candidate)"),
    };

    internal static GateOutcome ParseGateOutcome(string value, string filePath) => value switch
    {
        "pass" => GateOutcome.Pass,
        "fail" => GateOutcome.Fail,
        _ => throw new VaultSchemaException(filePath,
            $"invalid gate outcome '{value}' (expected pass|fail)"),
    };

    internal static SemanticVerdict ParseSemanticVerdict(string value, string filePath) => value switch
    {
        "pass" => SemanticVerdict.Pass,
        "fail" => SemanticVerdict.Fail,
        "unclear" => SemanticVerdict.Unclear,
        _ => throw new VaultSchemaException(filePath,
            $"invalid semantic verdict '{value}' (expected pass|fail|unclear)"),
    };

    internal static VerificationVerdict ParseVerificationVerdict(string value, string filePath) => value switch
    {
        "pass" => VerificationVerdict.Pass,
        "fail" => VerificationVerdict.Fail,
        _ => throw new VaultSchemaException(filePath,
            $"invalid verdict '{value}' (expected pass|fail)"),
    };

    internal static ReconciliationVerdict ParseReconciliationVerdict(string value, string filePath) => value switch
    {
        "full" => ReconciliationVerdict.Full,
        "partial" => ReconciliationVerdict.Partial,
        "none" => ReconciliationVerdict.None,
        _ => throw new VaultSchemaException(filePath,
            $"invalid implements_plan '{value}' (expected full|partial|none)"),
    };

    public static string ToYaml(PlanStatus value) => value switch
    {
        PlanStatus.Draft => "draft",
        PlanStatus.Approved => "approved",
        PlanStatus.InProgress => "in_progress",
        PlanStatus.Blocked => "blocked",
        PlanStatus.Complete => "complete",
        _ => "abandoned",
    };

    public static string ToYaml(CheckpointStatus value) => value switch
    {
        CheckpointStatus.Pending => "pending",
        CheckpointStatus.InProgress => "in_progress",
        CheckpointStatus.AwaitingReview => "awaiting_review",
        CheckpointStatus.Complete => "complete",
        CheckpointStatus.Rejected => "rejected",
        _ => "blocked",
    };

    internal static string ToYaml(PlanningStrategyKind value) =>
        value == PlanningStrategyKind.SingleCandidate ? "single_candidate" : "multi_candidate";

    internal static string ToYaml(GateOutcome value) =>
        value == GateOutcome.Pass ? "pass" : "fail";

    internal static string ToYaml(SemanticVerdict value) => value switch
    {
        SemanticVerdict.Pass => "pass",
        SemanticVerdict.Fail => "fail",
        _ => "unclear",
    };

    internal static string ToYaml(VerificationVerdict value) =>
        value == VerificationVerdict.Pass ? "pass" : "fail";

    internal static string ToYaml(ReconciliationVerdict value) => value switch
    {
        ReconciliationVerdict.Full => "full",
        ReconciliationVerdict.Partial => "partial",
        _ => "none",
    };
}
