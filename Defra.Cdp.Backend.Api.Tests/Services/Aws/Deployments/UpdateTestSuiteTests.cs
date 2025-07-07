using System.Text.Json;
using Defra.Cdp.Backend.Api.Config;
using Defra.Cdp.Backend.Api.Models;
using Defra.Cdp.Backend.Api.Services.Aws.Deployments;
using Defra.Cdp.Backend.Api.Services.Deployments;
using Defra.Cdp.Backend.Api.Services.Entities;
using Defra.Cdp.Backend.Api.Services.Entities.Model;
using Defra.Cdp.Backend.Api.Services.TestSuites;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using Type = Defra.Cdp.Backend.Api.Services.Entities.Model.Type;

namespace Defra.Cdp.Backend.Api.Tests.Services.Aws.Deployments;

public class UpdateTestSuiteTests
{
    private readonly OptionsWrapper<EcsEventListenerOptions> _config = new(new EcsEventListenerOptions());
    private readonly IEntitiesService _entitiesService = Substitute.For<IEntitiesService>();
    private readonly IDeploymentsService _deploymentsService = Substitute.For<IDeploymentsService>();
    private readonly ITestRunService _testRunService = Substitute.For<ITestRunService>();

    [Fact]
    public async Task TestEventNotLinkable()
    {
        var json = await File.ReadAllTextAsync("Resources/ecs/tests/task-start-starting.json");
        var ecsEvent = JsonSerializer.Deserialize<EcsTaskStateChangeEvent>(json);
        Assert.NotNull(ecsEvent);
        var entity = new Entity
        {
            Name = "forms-perf-test",
            Type = Type.TestSuite
        };

        var handler = new TaskStateChangeEventHandler(
            new MockEnvironmentLookup(),
            _deploymentsService,
            _testRunService,
            new NullLogger<TaskStateChangeEventHandler>());

        _testRunService.FindByTaskArn(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await handler.UpdateTestSuite(ecsEvent, entity.Name, CancellationToken.None);

        await _testRunService.DidNotReceive().UpdateStatus(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<DateTime>(),
            Arg.Any<List<FailureReason>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestEventLinkedByService()
    {
        var json = await File.ReadAllTextAsync("Resources/ecs/tests/task-start-starting.json");
        var ecsEvent = JsonSerializer.Deserialize<EcsTaskStateChangeEvent>(json);
        Assert.NotNull(ecsEvent);
        var entity = new Entity
        {
            Name = "forms-perf-test",
            Type = Type.TestSuite
        };

        var handler = new TaskStateChangeEventHandler(
            new MockEnvironmentLookup(),
            _deploymentsService,
            _testRunService,
            new NullLogger<TaskStateChangeEventHandler>());

        _testRunService.FindByTaskArn(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        _testRunService.Link(
            Arg.Any<TestRunMatchIds>(),
            ecsEvent.Detail.TaskArn,
            Arg.Any<CancellationToken>()
        ).Returns(new TestRun
        {
            RunId = "1234",
            TestSuite = "forms-perf-test"
        });

        await handler.UpdateTestSuite(ecsEvent, entity.Name, CancellationToken.None);

        await _testRunService.Received().UpdateStatus(
            ecsEvent.Detail.TaskArn,
            "starting",
            null,
            Arg.Any<DateTime>(),
            Arg.Is<List<FailureReason>>(l => l.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestEvenStarting()
    {
        var json = await File.ReadAllTextAsync("Resources/ecs/tests/task-start-starting.json");
        var ecsEvent = JsonSerializer.Deserialize<EcsTaskStateChangeEvent>(json);
        Assert.NotNull(ecsEvent);
        var entity = new Entity
        {
            Name = "forms-perf-test",
            Type = Type.TestSuite
        };
        var handler = new TaskStateChangeEventHandler(
            new MockEnvironmentLookup(),
            _deploymentsService,
            _testRunService,
            new NullLogger<TaskStateChangeEventHandler>());

        _testRunService.FindByTaskArn(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TestRun
            {
                RunId = "1234",
                TestSuite = "forms-perf-test"
            });

        await handler.UpdateTestSuite(ecsEvent, entity.Name, CancellationToken.None);

        await _testRunService.Received().UpdateStatus(
            ecsEvent.Detail.TaskArn,
            "starting",
            null,
            Arg.Any<DateTime>(),
            Arg.Is<List<FailureReason>>(l => l.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestEventRunning()
    {
        var json = await File.ReadAllTextAsync("Resources/ecs/tests/task-start-running.json");
        var ecsEvent = JsonSerializer.Deserialize<EcsTaskStateChangeEvent>(json);
        Assert.NotNull(ecsEvent);
        var entity = new Entity
        {
            Name = "forms-perf-test",
            Type = Type.TestSuite
        };
        var handler = new TaskStateChangeEventHandler(

            new MockEnvironmentLookup(),
            _deploymentsService,
            _testRunService,
            new NullLogger<TaskStateChangeEventHandler>());

        _testRunService.FindByTaskArn(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TestRun
            {
                RunId = "1234",
                TestSuite = "forms-perf-test"
            });

        await handler.UpdateTestSuite(ecsEvent, entity.Name, CancellationToken.None);

        await _testRunService.Received().UpdateStatus(
            ecsEvent.Detail.TaskArn,
            "in-progress",
            null,
            Arg.Any<DateTime>(),
            Arg.Is<List<FailureReason>>(l => l.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestEventUpdateTestSuitePassed()
    {
        var json = await File.ReadAllTextAsync("Resources/ecs/tests/task-stop-test-suite-pass.json");
        var ecsEvent = JsonSerializer.Deserialize<EcsTaskStateChangeEvent>(json);
        Assert.NotNull(ecsEvent);
        var entity = new Entity
        {
            Name = "forms-perf-test",
            Type = Type.TestSuite
        };
        
        var handler = new TaskStateChangeEventHandler(
            new MockEnvironmentLookup(),
            _deploymentsService,
            _testRunService,
            new NullLogger<TaskStateChangeEventHandler>());

        _testRunService.FindByTaskArn(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TestRun
            {
                RunId = "1234",
                TestSuite = "forms-perf-test"
            });

        await handler.UpdateTestSuite(ecsEvent, entity.Name, CancellationToken.None);

        await _testRunService.Received().UpdateStatus(
            ecsEvent.Detail.TaskArn,
            "finished",
            "passed",
            Arg.Any<DateTime>(),
            Arg.Is<List<FailureReason>>(l => l.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestEventUpdateTestSuiteFailed()
    {
        var json = await File.ReadAllTextAsync("Resources/ecs/tests/task-stop-test-suite-fail.json");
        var ecsEvent = JsonSerializer.Deserialize<EcsTaskStateChangeEvent>(json);
        Assert.NotNull(ecsEvent);
        var entity = new Entity
        {
            Name = "forms-perf-test",
            Type = Type.TestSuite
        };

        var handler = new TaskStateChangeEventHandler(
            new MockEnvironmentLookup(),
            _deploymentsService,
            _testRunService,
            new NullLogger<TaskStateChangeEventHandler>());

        _testRunService.FindByTaskArn(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TestRun
            {
                RunId = "1234",
                TestSuite = "forms-perf-test"
            });

        await handler.UpdateTestSuite(ecsEvent, entity.Name, CancellationToken.None);

        await _testRunService.Received().UpdateStatus(
            ecsEvent.Detail.TaskArn,
            "finished",
            "failed",
            Arg.Any<DateTime>(),
            Arg.Is<List<FailureReason>>(l => l.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestEventUpdateFailsWithOomError()
    {
        var json = await File.ReadAllTextAsync("Resources/ecs/tests/task-stop-out-of-memory.json");
        var ecsEvent = JsonSerializer.Deserialize<EcsTaskStateChangeEvent>(json);
        Assert.NotNull(ecsEvent);
        var entity = new Entity
        {
            Name = "forms-perf-test",
            Type = Type.TestSuite
        };
        var handler = new TaskStateChangeEventHandler(
            new MockEnvironmentLookup(),
            _deploymentsService,
            _testRunService,
            new NullLogger<TaskStateChangeEventHandler>());

        _testRunService.FindByTaskArn(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TestRun
            {
                RunId = "1234",
                TestSuite = "forms-perf-test"
            });

        await handler.UpdateTestSuite(ecsEvent, entity.Name, CancellationToken.None);

        await _testRunService.Received().UpdateStatus(
            ecsEvent.Detail.TaskArn,
            "failed",
            "failed",
            Arg.Any<DateTime>(),
            Arg.Is<List<FailureReason>>(l =>
                l.Count == 1 &&
                l[0].ContainerName == "forms-perf-test" &&
                l[0].Reason == "OutOfMemoryError: Container killed due to memory usage"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestEventUpdateFailsWithTimeout()
    {
        var json = await File.ReadAllTextAsync("Resources/ecs/tests/task-stop-timeout-sidecar.json");
        var ecsEvent = JsonSerializer.Deserialize<EcsTaskStateChangeEvent>(json);
        Assert.NotNull(ecsEvent);
        var entity = new Entity
        {
            Name = "forms-perf-test",
            Type = Type.TestSuite
        };

        var handler = new TaskStateChangeEventHandler(
            new MockEnvironmentLookup(),
            _deploymentsService,
            _testRunService,
            new NullLogger<TaskStateChangeEventHandler>());

        _testRunService.FindByTaskArn(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TestRun
            {
                RunId = "1234",
                TestSuite = "forms-perf-test"
            });

        await handler.UpdateTestSuite(ecsEvent, entity.Name, CancellationToken.None);

        await _testRunService.Received().UpdateStatus(
            ecsEvent.Detail.TaskArn,
            "failed",
            "failed",
            Arg.Any<DateTime>(),
            Arg.Is<List<FailureReason>>(l => l.Count == 1 && l.Contains(new FailureReason("forms-perf-test-timeout", "Test suite exceeded maximum run time"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestEventUpdateFailsWithEcsTaskError()
    {
        var json = await File.ReadAllTextAsync("Resources/ecs/tests/task-stop-missing-secret.json");
        var ecsEvent = JsonSerializer.Deserialize<EcsTaskStateChangeEvent>(json);
        Assert.NotNull(ecsEvent);
        var entity = new Entity
        {
            Name = "forms-perf-test",
            Type = Type.TestSuite
        };
        
        var handler = new TaskStateChangeEventHandler(
            new MockEnvironmentLookup(),
            _deploymentsService,
            _testRunService,
            new NullLogger<TaskStateChangeEventHandler>());

        _testRunService.FindByTaskArn(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TestRun
            {
                RunId = "1234",
                TestSuite = "forms-perf-test"
            });

        await handler.UpdateTestSuite(ecsEvent, entity.Name, CancellationToken.None);

        await _testRunService.Received().UpdateStatus(
            ecsEvent.Detail.TaskArn,
            "failed",
            null,
            Arg.Any<DateTime>(),
            Arg.Is<List<FailureReason>>(l => l.Count == 1 && l.Contains(new FailureReason("ECS Task", "ResourceInitializationError: unable to pull secrets or registry auth: execution resource retrieval failed: unable to retrieve secret from asm: service call has been retried 1 time(s): retrieved secret from Secrets Manager did not contain json key MY_SECRET"))),
            Arg.Any<CancellationToken>());
    }

}