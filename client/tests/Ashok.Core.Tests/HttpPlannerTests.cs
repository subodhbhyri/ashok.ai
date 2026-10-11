using System.Net;
using System.Text;
using System.Text.Json;
using Ashok.Core.Capture;
using Ashok.Core.Contracts;
using Ashok.Core.Evaluation;
using Ashok.Core.Planner;
using Ashok.Core.Session;

namespace Ashok.Core.Tests;

public class HttpPlannerTests
{
    private static string Fixture(string path) =>
        File.ReadAllText(Path.Combine(ContractSchemas.ContractsDir, "fixtures", path));

    private static readonly UiElement Item = new("e12", "ListItem", "Bluetooth & devices", "SettingsPageGroupDevices", [24, 310, 260, 40], true, null);
    private static readonly UiElement Switch = new("e13", "Button", "Bluetooth", null, [400, 200, 60, 30], true, "off");

    private static PlanRequest Request(PlanReason reason = PlanReason.New, int extraElements = 0)
    {
        var elements = new List<UiElement> { Item, Switch };
        for (var i = 0; i < extraElements; i++)
            elements.Add(new UiElement($"e{100 + i}", "Text", $"Label {i}", null, [0, i, 10, 10], true, null));
        return new PlanRequest("turn on bluetooth", reason, [new StepRecord("s1", "Click it.", true)],
            new ForegroundState("SystemSettings", "Settings", 1), elements);
    }

    private sealed class MemoryTokens : IInstallTokenStore
    {
        public string? Token { get; set; }

        public string? Load() => Token;

        public void Save(string token) => Token = token;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return await reply(request, ct);
        }
    }

    private static bool IsRegister(HttpRequestMessage r) => r.RequestUri!.AbsolutePath.EndsWith("register");

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Handler Worker(string planBody, HttpStatusCode planStatus = HttpStatusCode.OK) =>
        new((req, _) => Task.FromResult(IsRegister(req) ? Json("{\"installToken\":\"tok\"}") : Json(planBody, planStatus)));

    private static HttpPlanner Planner(Handler handler, MemoryTokens? tokens = null, TimeSpan? timeout = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://worker.test/") }, tokens ?? new MemoryTokens(),
            new DocumentOrderRanker(), () => new OsInfo("Windows 11", "26300"), "0.1.0", timeout);

    [Fact]
    public async Task Success_ReplacesRefsWithIdentity()
    {
        var result = await Planner(Worker(Fixture("plan/valid/bluetooth.json"))).PlanAsync(Request(), default);

        var plan = Assert.IsType<Plan>(result.Plan);
        var target = plan.Steps[0].Target!;
        Assert.Null(target.Ref);
        Assert.Equal(("ListItem", "Bluetooth & devices", "SettingsPageGroupDevices"), (target.Role, target.Name, target.AutomationId));
        Assert.All(plan.Steps, s => Assert.Null(s.Target?.Ref));
    }

    [Fact]
    public async Task RefNotInSnapshot_IsUnavailable()
    {
        var json = Fixture("plan/valid/bluetooth.json").Replace("\"e12\"", "\"e99\"");
        var result = await Planner(Worker(json)).PlanAsync(Request(), default);

        Assert.Null(result.Plan);
        Assert.Contains("not on the screen", result.Failure);
    }

    [Fact]
    public async Task RefInDoneWhenTarget_IsAlsoResolvedAndChecked()
    {
        var json = Fixture("plan/valid/bluetooth.json").Replace(
            "\"doneWhen\": { \"kind\": \"elementVisible\", \"target\": { \"role\"",
            "\"doneWhen\": { \"kind\": \"elementVisible\", \"target\": { \"ref\": \"e13\", \"role\"");
        Assert.Contains("\"e13\"", json);

        var ok = await Planner(Worker(json)).PlanAsync(Request(), default);
        Assert.Null(ok.Plan!.Steps[0].DoneWhen.Target!.Ref);

        var bad = await Planner(Worker(json.Replace("\"e13\"", "\"e77\""))).PlanAsync(Request(), default);
        Assert.Null(bad.Plan);
    }

    [Fact]
    public async Task Refusal_PassesThrough()
    {
        var result = await Planner(Worker(Fixture("plan/valid/refusal.json"))).PlanAsync(Request(), default);

        Assert.NotNull(result.Plan?.Refusal);
        Assert.Empty(result.Plan!.Steps);
    }

    [Theory]
    [InlineData("{\"intent\":\"x\"}")]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task ContractViolation_IsUnavailable(string body)
    {
        var result = await Planner(Worker(body)).PlanAsync(Request(), default);

        Assert.Null(result.Plan);
        Assert.NotNull(result.Failure);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task HttpErrors_AreUnavailable(HttpStatusCode status)
    {
        var result = await Planner(Worker("{}", status)).PlanAsync(Request(), default);

        Assert.Null(result.Plan);
    }

    [Fact]
    public async Task NetworkFailure_IsUnavailable()
    {
        var handler = new Handler((_, _) => throw new HttpRequestException("offline"));
        var result = await Planner(handler).PlanAsync(Request(), default);

        Assert.Null(result.Plan);
    }

    [Fact]
    public async Task Timeout_IsUnavailable()
    {
        var handler = new Handler(async (req, ct) =>
        {
            if (IsRegister(req)) return Json("{\"installToken\":\"tok\"}");
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        });

        var result = await Planner(handler, timeout: TimeSpan.FromMilliseconds(100)).PlanAsync(Request(), default);

        Assert.Null(result.Plan);
        Assert.Contains("timed out", result.Failure);
    }

    [Fact]
    public void DefaultTimeout_IsTwentySeconds() => Assert.Equal(TimeSpan.FromSeconds(20), HttpPlanner.DefaultTimeout);

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        var handler = new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var tokens = new MemoryTokens { Token = "tok" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Planner(handler, tokens).PlanAsync(Request(), cts.Token));
    }

    [Fact]
    public async Task Request_HasBearerCamelCaseReasonAndOnlyContractFields()
    {
        var handler = Worker(Fixture("plan/valid/bluetooth.json"));
        await Planner(handler).PlanAsync(Request(PlanReason.OffPlan), default);

        var (message, body) = handler.Seen.Single(s => !IsRegister(s.Request));
        Assert.Equal("Bearer tok", message.Headers.Authorization!.ToString());

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(["question", "snapshot", "history", "reason"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("offPlan", root.GetProperty("reason").GetString());
        Assert.Equal("turn on bluetooth", root.GetProperty("question").GetString());
        Assert.Equal(["os", "foreground", "elements", "redacted", "truncated"], root.GetProperty("snapshot").EnumerateObject().Select(p => p.Name));
        Assert.Equal(2, root.GetProperty("snapshot").GetProperty("elements").GetArrayLength());
    }

    [Fact]
    public async Task Snapshot_IsValidAndCappedAt150()
    {
        var handler = Worker(Fixture("plan/valid/bluetooth.json"));
        await Planner(handler).PlanAsync(Request(extraElements: 300), default);

        using var doc = JsonDocument.Parse(handler.Seen.Single(s => !IsRegister(s.Request)).Body);
        var snapshot = doc.RootElement.GetProperty("snapshot");
        Assert.Equal(150, snapshot.GetProperty("elements").GetArrayLength());
        Assert.True(snapshot.GetProperty("truncated").GetBoolean());
        Assert.True(ContractSchemas.Get("ui-snapshot").Evaluate(snapshot).IsValid);
    }

    [Fact]
    public async Task Snapshot_CarriesTheRealRedactedCount()
    {
        var handler = Worker(Fixture("plan/valid/bluetooth.json"));
        await Planner(handler).PlanAsync(Request() with { Redacted = 4 }, default);

        using var doc = JsonDocument.Parse(handler.Seen.Single(s => !IsRegister(s.Request)).Body);
        Assert.Equal(4, doc.RootElement.GetProperty("snapshot").GetProperty("redacted").GetInt32());
    }

    [Fact]
    public async Task MatchedPlaybook_IsSentAsAHint_AndOtherwiseTheFieldIsAbsent()
    {
        var handler = Worker(Fixture("plan/valid/bluetooth.json"));
        await Planner(handler).PlanAsync(Request() with { MatchedPlaybookId = "change-display-scaling" }, default);
        await Planner(handler).PlanAsync(Request(), default);

        var bodies = handler.Seen.Where(s => !IsRegister(s.Request)).Select(s => JsonDocument.Parse(s.Body).RootElement).ToList();
        Assert.Equal("change-display-scaling", bodies[0].GetProperty("playbookHint").GetString());
        Assert.False(bodies[1].TryGetProperty("playbookHint", out _));
    }

    [Fact]
    public async Task NoScreenYet_SendsAnEmptyValidSnapshot()
    {
        var handler = Worker(Fixture("plan/valid/bluetooth.json"));
        await Planner(handler).PlanAsync(new PlanRequest("q", PlanReason.New, [], null, []), default);

        using var doc = JsonDocument.Parse(handler.Seen.Single(s => !IsRegister(s.Request)).Body);
        Assert.True(ContractSchemas.Get("ui-snapshot").Evaluate(doc.RootElement.GetProperty("snapshot")).IsValid);
    }

    [Fact]
    public async Task Registers_OnceAndReusesTheToken()
    {
        var handler = Worker(Fixture("plan/valid/bluetooth.json"));
        var tokens = new MemoryTokens();
        var planner = Planner(handler, tokens);

        await planner.PlanAsync(Request(), default);
        await planner.PlanAsync(Request(), default);

        Assert.Equal("tok", tokens.Token);
        Assert.Single(handler.Seen, s => IsRegister(s.Request));
    }

    [Fact]
    public async Task RegistrationFailure_IsUnavailable()
    {
        var handler = new Handler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.TooManyRequests)));
        var result = await Planner(handler).PlanAsync(Request(), default);

        Assert.Null(result.Plan);
    }
}
