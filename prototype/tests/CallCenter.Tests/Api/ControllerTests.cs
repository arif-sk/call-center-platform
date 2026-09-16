using CallCenter.Api.Controllers;
using CallCenter.Api.Filters;
using CallCenter.Api.Models;
using CallCenter.Application.Contracts;
using CallCenter.Application.Services;
using CallCenter.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CallCenter.Tests.Api;

/// <summary>
/// The HTTP layer on its own, over a stand-in application service. These run with no database and
/// no web server because the controllers depend on <see cref="ICallCenterService"/> rather than on
/// the class that implements it — which is the whole argument for the dependency pointing that way.
/// </summary>
public class ControllerTests
{
    [Fact]
    public async Task Getting_the_snapshot_returns_200_with_the_snapshot()
    {
        var service = new StubCallCenterService();
        var controller = NewAgentsController(service);

        var result = await controller.GetSnapshot(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(service.Returns, ok.Value);
    }

    [Fact]
    public async Task Signing_in_passes_the_agent_from_the_route_to_the_service()
    {
        var service = new StubCallCenterService();
        var controller = NewAgentsController(service);
        var agentId = Guid.NewGuid();

        await controller.SignIn(agentId, CancellationToken.None);

        Assert.Equal($"SignIn:{agentId}", Assert.Single(service.Received));
    }

    [Fact]
    public async Task Going_ready_passes_the_flag_from_the_request_body()
    {
        var service = new StubCallCenterService();
        var controller = NewAgentsController(service);
        var agentId = Guid.NewGuid();

        await controller.SetReady(agentId, new ReadyRequest { Ready = true }, CancellationToken.None);

        Assert.Equal($"SetReady:{agentId}:True", Assert.Single(service.Received));
    }

    [Fact]
    public async Task Wrapping_up_passes_the_disposition_and_notes_through()
    {
        var service = new StubCallCenterService();
        var controller = new CallsController(service, NullLogger<CallsController>.Instance);
        var agentId = Guid.NewGuid();
        var callId = Guid.NewGuid();

        await controller.CompleteWrapUp(
            callId,
            agentId,
            new WrapUpRequest { Disposition = "Resolved", Notes = "Reset it." },
            CancellationToken.None);

        Assert.Equal($"WrapUp:{agentId}:{callId}:Resolved:Reset it.", Assert.Single(service.Received));
    }

    /// <summary>
    /// The seam between the domain's language and HTTP's, tested where it lives. Because this is a
    /// filter rather than a try/catch in each action, one test covers every endpoint.
    /// </summary>
    [Fact]
    public void A_broken_rule_becomes_a_400_problem_details()
    {
        var filter = NewFilter();
        var context = NewExceptionContext(new DomainException("That call belongs to another agent."));

        filter.OnException(context);

        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("The command was refused", problem.Title);
        Assert.Equal("That call belongs to another agent.", problem.Detail);
    }

    /// <summary>
    /// A genuine fault must not be dressed up as a polite refusal — it belongs in the error log as
    /// a 500. Filters that swallow everything are how outages become invisible.
    /// </summary>
    [Fact]
    public void An_unexpected_failure_is_left_for_the_framework()
    {
        var filter = NewFilter();
        var context = NewExceptionContext(new InvalidOperationException("the database fell over"));

        filter.OnException(context);

        Assert.False(context.ExceptionHandled);
        Assert.Null(context.Result);
    }

    // ------------------------------------------------------------------ helpers

    private static AgentsController NewAgentsController(ICallCenterService service) =>
        new(service, NullLogger<AgentsController>.Instance);

    private static DomainExceptionFilter NewFilter() =>
        new(new StubProblemDetailsFactory(), NullLogger<DomainExceptionFilter>.Instance);

    private static ExceptionContext NewExceptionContext(Exception exception)
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());

        return new ExceptionContext(actionContext, []) { Exception = exception };
    }

    /// <summary>Records what it was asked to do and hands back a fixed snapshot.</summary>
    private sealed class StubCallCenterService : ICallCenterService
    {
        public List<string> Received { get; } = [];

        public Snapshot Returns { get; } = new([], [], [], new Stats(0, 0, 0, 0, 0), DateTimeOffset.UtcNow);

        private Task<Snapshot> Record(string entry)
        {
            Received.Add(entry);
            return Task.FromResult(Returns);
        }

        public Task<Snapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Returns);

        public Task<Snapshot> SignInAsync(Guid agentId, CancellationToken cancellationToken = default) =>
            Record($"SignIn:{agentId}");

        public Task<Snapshot> SignOutAsync(Guid agentId, CancellationToken cancellationToken = default) =>
            Record($"SignOut:{agentId}");

        public Task<Snapshot> SetReadyAsync(Guid agentId, bool ready, CancellationToken cancellationToken = default) =>
            Record($"SetReady:{agentId}:{ready}");

        public Task<Snapshot> ReceiveInboundCallAsync(
            string from, string to, CancellationToken cancellationToken = default) =>
            Record($"Inbound:{from}:{to}");

        public Task<Snapshot> AnswerAsync(Guid agentId, Guid callId, CancellationToken cancellationToken = default) =>
            Record($"Answer:{agentId}:{callId}");

        public Task<Snapshot> DeclineAsync(Guid agentId, Guid callId, CancellationToken cancellationToken = default) =>
            Record($"Decline:{agentId}:{callId}");

        public Task<Snapshot> HangUpAsync(Guid agentId, Guid callId, CancellationToken cancellationToken = default) =>
            Record($"HangUp:{agentId}:{callId}");

        public Task<Snapshot> CompleteWrapUpAsync(
            Guid agentId, Guid callId, string disposition, string? notes,
            CancellationToken cancellationToken = default) =>
            Record($"WrapUp:{agentId}:{callId}:{disposition}:{notes}");

        public Task<Snapshot> AbandonAsync(Guid callId, CancellationToken cancellationToken = default) =>
            Record($"Abandon:{callId}");
    }

    private sealed class StubProblemDetailsFactory : ProblemDetailsFactory
    {
        public override ProblemDetails CreateProblemDetails(
            HttpContext httpContext, int? statusCode = null, string? title = null,
            string? type = null, string? detail = null, string? instance = null) =>
            new() { Status = statusCode, Title = title, Type = type, Detail = detail, Instance = instance };

        public override ValidationProblemDetails CreateValidationProblemDetails(
            HttpContext httpContext, ModelStateDictionary modelStateDictionary, int? statusCode = null,
            string? title = null, string? type = null, string? detail = null, string? instance = null) =>
            new(modelStateDictionary) { Status = statusCode, Title = title, Detail = detail };
    }
}
