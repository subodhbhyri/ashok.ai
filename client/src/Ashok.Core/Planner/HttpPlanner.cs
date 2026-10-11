using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ashok.Core.Capture;
using Ashok.Core.Contracts;
using Ashok.Core.Session;
using Json.Schema;

namespace Ashok.Core.Planner;

/// <summary>
/// Asks the Worker for a plan. Every failure (network, timeout, 4xx/5xx, a body that does not match plan.schema.json,
/// a ref that is not in the snapshot we sent) becomes <see cref="PlannerResult.Unavailable"/>; the session shows a calm message.
/// </summary>
public sealed class HttpPlanner(
    HttpClient http,
    IInstallTokenStore tokens,
    IElementRanker ranker,
    Func<OsInfo> os,
    string appVersion,
    TimeSpan? timeout = null) : IPlanner
{
    public const int MaxElements = 150;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _registering = new(1, 1);

    public async Task<PlannerResult> PlanAsync(PlanRequest request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            return await PlanCoreAsync(request, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PlannerResult.Unavailable("the planner timed out");
        }
        catch (HttpRequestException e)
        {
            return PlannerResult.Unavailable($"the planner could not be reached: {e.Message}");
        }
    }

    private async Task<PlannerResult> PlanCoreAsync(PlanRequest request, CancellationToken ct)
    {
        var token = await EnsureTokenAsync(ct);
        if (token is null) return PlannerResult.Unavailable("could not register this install");

        var snapshot = BuildSnapshot(request);
        var body = new
        {
            question = request.Question,
            snapshot,
            history = request.History.Select(h => new { stepId = h.StepId, say = h.Say, completed = h.Completed }),
            reason = JsonNamingPolicy.CamelCase.ConvertName(request.Reason.ToString()),
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/plan")
        {
            Content = JsonContent.Create(body, options: SnapshotJson.Options),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(message, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return PlannerResult.Unavailable("the daily planner limit was reached");
        if (!response.IsSuccessStatusCode)
            return PlannerResult.Unavailable($"the planner returned {(int)response.StatusCode}");

        var json = await response.Content.ReadAsStringAsync(ct);
        return Parse(json, snapshot.Elements);
    }

    /// <summary>Validates the plan against the contract, then swaps every ref for the element's identity.</summary>
    public static PlannerResult Parse(string json, IReadOnlyList<UiElement> sent)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return PlannerResult.Unavailable("the planner returned something that is not JSON");
        }

        using var _ = document;
        if (!ContractSchemas.Plan.Evaluate(document.RootElement).IsValid)
            return PlannerResult.Unavailable("the planner returned a plan that does not match the contract");

        Plan? plan;
        try
        {
            plan = document.RootElement.Deserialize<Plan>(ContractJson.Options);
        }
        catch (JsonException)
        {
            return PlannerResult.Unavailable("the planner returned a plan that could not be read");
        }
        if (plan is null) return PlannerResult.Unavailable("the planner returned an empty plan");

        var byRef = sent.ToDictionary(e => e.Ref);
        var steps = new List<PlanStep>();
        foreach (var step in plan.Steps)
        {
            if (!TryResolve(step.Target, byRef, out var target) ||
                !TryResolve(step.DoneWhen.Target, byRef, out var doneTarget))
                return PlannerResult.Unavailable("the plan points at an element that was not on the screen we sent");
            steps.Add(step with { Target = target, DoneWhen = step.DoneWhen with { Target = doneTarget } });
        }
        return new PlannerResult(plan with { Steps = steps }, null);
    }

    // A ref is only valid for the snapshot it came from, so later captures must find the element by identity.
    private static bool TryResolve(Target? target, Dictionary<string, UiElement> byRef, out Target? resolved)
    {
        resolved = target;
        if (target?.Ref is not { } r) return true;
        if (!byRef.TryGetValue(r, out var element)) return false;
        resolved = new Target(null, element.Role, element.Name, element.AutomationId);
        return true;
    }

    private UiSnapshot BuildSnapshot(PlanRequest request)
    {
        var ranked = ranker.Rank(request.Candidates, MaxElements);
        var foreground = request.Foreground is { } f
            ? new ForegroundInfo(string.IsNullOrEmpty(f.Process) ? "unknown" : f.Process, f.Title)
            : new ForegroundInfo("unknown", "");
        return new UiSnapshot(os(), foreground, ranked.Elements, request.Redacted, ranked.Truncated);
    }

    private async Task<string?> EnsureTokenAsync(CancellationToken ct)
    {
        if (tokens.Load() is { } existing) return existing;

        await _registering.WaitAsync(ct);
        try
        {
            if (tokens.Load() is { } raced) return raced;

            using var response = await http.PostAsJsonAsync("v1/register", new { appVersion }, SnapshotJson.Options, ct);
            if (!response.IsSuccessStatusCode) return null;

            var registered = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
            if (registered?["installToken"]?.GetValue<string>() is not { Length: > 0 } token) return null;
            tokens.Save(token);
            return token;
        }
        finally
        {
            _registering.Release();
        }
    }
}
