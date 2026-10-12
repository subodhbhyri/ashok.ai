using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ashok.Core.Contracts;

/// <summary>Mirrors docs/contracts/plan.schema.json.</summary>
public sealed record Plan(
    string Intent,
    IReadOnlyList<PlanStep> Steps,
    double Confidence,
    bool NeedsScreenshot,
    string? Refusal);

/// <summary>
/// One step. These describe what the USER does; the app never performs them.
/// </summary>
public sealed record PlanStep(
    string Id,
    string Say,
    ActionKind Action,
    Target? Target,
    string? Keys,
    DoneWhen DoneWhen);

/// <summary>
/// A place on screen. <see cref="Ref"/> is only valid inside the snapshot it came from; playbooks never carry one.
/// </summary>
public sealed record Target(string? Ref, string Role, string Name, string? AutomationId);

public sealed record DoneWhen(DoneKind Kind, string? Process, string? Title, Target? Target, string? State);

/// <summary>Mirrors docs/contracts/playbook.schema.json.</summary>
public sealed record Playbook(
    string Id,
    string Title,
    IReadOnlyList<string> Os,
    IReadOnlyList<string> Examples,
    IReadOnlyList<PlanStep> Steps,
    PlaybookScope Scope = PlaybookScope.System,
    IReadOnlyList<string>? AvoidWords = null);

/// <summary>System playbooks are about Windows itself; app playbooks about one application.</summary>
public enum PlaybookScope { System, App }

public enum ActionKind { Click, DoubleClick, RightClick, Type, PressKeys, Observe }

public enum DoneKind { WindowOpened, ElementVisible, ToggleState, FocusOn, ElementGone, UserConfirms }

public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
