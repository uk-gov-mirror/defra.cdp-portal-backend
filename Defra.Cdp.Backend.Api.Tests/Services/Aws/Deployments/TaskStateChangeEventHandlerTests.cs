using Defra.Cdp.Backend.Api.Models;
using Defra.Cdp.Backend.Api.Services.Aws;
using Defra.Cdp.Backend.Api.Services.Aws.Deployments;
using Defra.Cdp.Backend.Api.Services.Deployments;
using Defra.Cdp.Backend.Api.Services.Entities;
using Defra.Cdp.Backend.Api.Services.TestSuites;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace Defra.Cdp.Backend.Api.Tests.Services.Aws.Deployments;

class MockEnvironmentLookup : IEnvironmentLookup
{
    public string? FindEnv(string account)
    {
        return "test";
    }
}

public class TaskStateChangeEventHandlerTests
{
    private readonly EcsTaskStateChangeEvent _testEvent = new(
        "12345",
        "ECS Task State Change",
        "1111111111",
        DateTime.Now,
        "eu-west-2",
        new EcsEventDetail(
            CreatedAt: DateTime.Now,
            Memory: "1024",
            Cpu: "1024",
            TaskArn: "arn:aws:ecs:eu-west-2:120185944470:task/perf-test-ecs-public/b3e14b20479b48608694fe4fee50ba77",
            Group: "family:cdp-example-node-backend",
            DesiredStatus: "RUNNING",
            LastStatus: "RUNNING",
            Containers: [],
            StartedBy: "ecs-svc/6276605373259507742",
            TaskDefinitionArn: "arn:aws:ecs:eu-west-2:506190012364:task-definition/cdp-example-node-backend:47",
            EcsSvcDeploymentId: null,
            StoppedReason: null,
            StopCode: null
        ),
        "ecs-svc/6276605373259507742",
        "ecs-svc/6276605373259507742"
    );


    [Fact]
    public async Task TestUpdatesUsingLinkedRecord()
    {
        var entitiesService = Substitute.For<IEntitiesService>();
        var deploymentsService = Substitute.For<IDeploymentsService>();
        var testRunService = Substitute.For<ITestRunService>();

        var deployment = new Deployment
        {
            CdpDeploymentId = "cdp-123"
        };
        deploymentsService.FindDeploymentByLambdaId("ecs-svc/6276605373259507742", Arg.Any<CancellationToken>())
            .Returns(deployment);
        deploymentsService.FindDeployment(deployment.CdpDeploymentId, Arg.Any<CancellationToken>()).Returns(deployment);

        var handler = new TaskStateChangeEventHandler(
            new MockEnvironmentLookup(),
            deploymentsService,
            testRunService,
            ConsoleLogger.CreateLogger<TaskStateChangeEventHandler>());

        await handler.UpdateDeployment(_testEvent, new CancellationToken());

        await deploymentsService.Received().FindDeploymentByLambdaId("ecs-svc/6276605373259507742", Arg.Any<CancellationToken>());
        await deploymentsService.DidNotReceiveWithAnyArgs().FindDeploymentByTaskArn(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await deploymentsService.Received().UpdateOverallTaskStatus(Arg.Any<Deployment>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestFallbackLinking()
    {
        var entitiesService = Substitute.For<IEntitiesService>();
        var deploymentsService = Substitute.For<IDeploymentsService>();
        var testRunService = Substitute.For<ITestRunService>();

        var deployment = new Deployment
        {
            CdpDeploymentId = "cdp-123"
        };
        deploymentsService.FindDeploymentByLambdaId("ecs-svc/6276605373259507742", Arg.Any<CancellationToken>()).ReturnsNull();
        deploymentsService.FindDeploymentByTaskArn("arn:aws:ecs:eu-west-2:506190012364:task-definition/cdp-example-node-backend:47", Arg.Any<CancellationToken>()).Returns(deployment);
        deploymentsService.FindDeployment(deployment.CdpDeploymentId, Arg.Any<CancellationToken>()).Returns(deployment);

        var handler = new TaskStateChangeEventHandler(
            new MockEnvironmentLookup(),
            deploymentsService,
            testRunService,
            ConsoleLogger.CreateLogger<TaskStateChangeEventHandler>());

        await handler.UpdateDeployment(_testEvent, new CancellationToken());

        await deploymentsService.Received().FindDeploymentByLambdaId("ecs-svc/6276605373259507742", Arg.Any<CancellationToken>());
        await deploymentsService.Received().FindDeploymentByTaskArn("arn:aws:ecs:eu-west-2:506190012364:task-definition/cdp-example-node-backend:47", Arg.Any<CancellationToken>());
        await deploymentsService.Received().UpdateOverallTaskStatus(Arg.Any<Deployment>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestNotUpdatedWhenNoLinkExists()
    {
        var entitiesService = Substitute.For<IEntitiesService>();
        var deploymentsService = Substitute.For<IDeploymentsService>();
        var testRunService = Substitute.For<ITestRunService>();

        deploymentsService.FindDeploymentByLambdaId("ecs-svc/6276605373259507742", Arg.Any<CancellationToken>()).ReturnsNull();
        deploymentsService
            .FindDeploymentByTaskArn("arn:aws:ecs:eu-west-2:506190012364:task-definition/cdp-example-node-backend:47",
                Arg.Any<CancellationToken>()).ReturnsNull();


        var handler = new TaskStateChangeEventHandler(
            new MockEnvironmentLookup(),
            deploymentsService,
            testRunService,
            ConsoleLogger.CreateLogger<TaskStateChangeEventHandler>());

        await handler.UpdateDeployment(_testEvent, new CancellationToken());

        await deploymentsService.Received().FindDeploymentByLambdaId("ecs-svc/6276605373259507742", Arg.Any<CancellationToken>());
        await deploymentsService.Received().FindDeploymentByTaskArn("arn:aws:ecs:eu-west-2:506190012364:task-definition/cdp-example-node-backend:47", Arg.Any<CancellationToken>());
        await deploymentsService.DidNotReceive().UpdateOverallTaskStatus(Arg.Any<Deployment>(), Arg.Any<CancellationToken>());
    }

}