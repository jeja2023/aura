using System.Security.Claims;
using System.Text.Json;
using Aura.Api.Internal;
using Aura.Api.Product;
using Aura.Api.Extensions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aura.Api.Tests;

public sealed class CommercialProductDomainTests
{
    [Theory]
    [InlineData("rules", "published", true)]
    [InlineData("rules", "pending_approval", false)]
    [InlineData("ai-models", "production", true)]
    [InlineData("legal-holds", "released", true)]
    [InlineData("retention-policies", "active", true)]
    [InlineData("notification-templates", "active", false)]
    public void GovernanceTransitionsDeclareStepUpBoundary(string resource, string status, bool expected)
    {
        Assert.Equal(expected, AuraEndpointsProduct.RequiresGovernanceStepUp(resource, status));
    }

    [Theory]
    [InlineData("new", "acknowledged", true)]
    [InlineData("acknowledged", "in_progress", true)]
    [InlineData("in_progress", "paused", true)]
    [InlineData("paused", "in_progress", true)]
    [InlineData("resolved", "closed", true)]
    [InlineData("closed", "in_progress", false)]
    [InlineData("new", "closed", false)]
    public void CaseStateMachineOnlyAllowsDeclaredTransitions(string current, string target, bool expected)
    {
        var result = CaseStateMachine.TryValidate(current, target, "test", canReview: false, out var normalized, out _);

        Assert.Equal(expected, result);
        Assert.Equal(target, normalized);
    }

    [Fact]
    public void ReopeningAClosedCaseRequiresReviewPermissionAndReason()
    {
        Assert.False(CaseStateMachine.TryValidate("closed", "reopened", "new evidence", canReview: false, out _, out _));
        Assert.False(CaseStateMachine.TryValidate("closed", "reopened", null, canReview: true, out _, out _));
        Assert.True(CaseStateMachine.TryValidate("closed", "reopened", "new evidence", canReview: true, out _, out _));
    }

    [Fact]
    public void StepUpAcceptsMfaClaimsButDoesNotTrustRequestFlags()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:StepUp:AllowLocalSuperAdmin"] = "false"
        }).Build();
        var service = new StepUpAuthorizationService(configuration);
        var mfaUser = Principal("building_admin", new Claim("amr", "mfa"));
        var passwordOnly = Principal("super_admin", new Claim("amr", "pwd"));

        Assert.True(service.HasRecentStepUp(mfaUser));
        Assert.False(service.HasRecentStepUp(passwordOnly));
    }

    [Fact]
    public void CommercialPermissionsRemainActionScoped()
    {
        var user = Principal("building_admin", new Claim(AuraPermissions.ClaimType, AuraPermissions.EventView));

        Assert.True(AuraPermissions.HasPermission(user, AuraPermissions.EventView));
        Assert.False(AuraPermissions.HasPermission(user, AuraPermissions.EventManage));
        Assert.False(AuraPermissions.HasPermission(user, AuraPermissions.EvidenceViewOriginal));
    }

    [Fact]
    public void ControlledQueryPlanNormalizesOnlyPermittedReadOnlyFields()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "queryType": "candidate_people",
              "query": {
                "caseReference": "A-2026-1",
                "building": "3号楼",
                "floor": "2层",
                "occurrenceMin": 3,
                "strangerOnly": true,
                "requireCoOccurrence": true,
                "limit": 50
              },
              "interpretation": ["untrusted client text"]
            }
            """);

        var plan = ControlledQueryService.NormalizeEditablePlan(document.RootElement);

        Assert.Equal("candidate_people", plan.QueryType);
        Assert.Equal("A-2026-1", plan.Query["caseReference"]);
        Assert.Equal(50, plan.Query["limit"]);
        Assert.DoesNotContain(plan.Interpretation, item => item.Contains("untrusted", StringComparison.Ordinal));
        Assert.Contains(plan.ConfirmedFacts, item => item.Contains("read-only", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{\"queryType\":\"timeline\",\"query\":{\"limit\":501}}")]
    [InlineData("{\"queryType\":\"timeline\",\"query\":{\"url\":\"https://example.invalid\"}}")]
    [InlineData("{\"queryType\":\"candidate_people\",\"query\":{\"caseReference\":\"A;DROP\"}}")]
    [InlineData("{\"queryType\":\"camera_paths\",\"query\":{\"fromCameraId\":1}}")]
    public void ControlledQueryPlanRejectsOutOfContractEdits(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Throws<ArgumentException>(() => ControlledQueryService.NormalizeEditablePlan(document.RootElement));
    }

    private static ClaimsPrincipal Principal(string role, params Claim[] additional)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role) };
        claims.AddRange(additional);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
