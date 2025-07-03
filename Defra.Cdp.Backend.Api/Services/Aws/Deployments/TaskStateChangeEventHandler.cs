using System.Collections.Immutable;
using Defra.Cdp.Backend.Api.Models;
using Defra.Cdp.Backend.Api.Services.Deployments;
using Defra.Cdp.Backend.Api.Services.Entities;
using Defra.Cdp.Backend.Api.Services.Entities.Model;
using Defra.Cdp.Backend.Api.Services.TestSuites;
using Type = Defra.Cdp.Backend.Api.Services.Entities.Model.Type;

namespace Defra.Cdp.Backend.Api.Services.Aws.Deployments;

public class TaskStateChangeEventHandler(
    IEnvironmentLookup environmentLookup,
    IDeploymentsService deploymentsService,
    IEntitiesService entitiesService,
    ITestRunService testRunService,
    ILogger<TaskStateChangeEventHandler> logger)
{
    private static readonly ISet<string> s_failureReasonsToIgnore = ImmutableHashSet.Create("UserInitiated", "EssentialContainerExited", "ServiceSchedulerInitiated");

    public async Task Handle(string id, EcsTaskStateChangeEvent ecsTaskStateChangeEvent,
        CancellationToken cancellationToken)
    {
        var name = ecsTaskStateChangeEvent.Detail.Group.Split(":").Last();
        var env = environmentLookup.FindEnv(ecsTaskStateChangeEvent.Account);
        if (env == null)
        {
            logger.LogError(
                "Unable to convert {DeploymentId} to a deployment event, unknown environment/account: {Account} check the mappings!",
                ecsTaskStateChangeEvent.DeploymentId, ecsTaskStateChangeEvent.Account);
            return;
        }

        var entity = await entitiesService.GetEntity(name, cancellationToken);

        if (entity == null)
        {
            logger.LogWarning("No known entity found for task {Id}, {Name}", id, name);
            return;
        }

        switch (entity.Type)
        {
            case Type.Microservice:
                await UpdateDeployment(ecsTaskStateChangeEvent, cancellationToken);
                return;
            case Type.TestSuite:
                await UpdateTestSuite(ecsTaskStateChangeEvent, entity, cancellationToken);
                return;
            default:
                logger.LogWarning("Skipping entity {Name}, unsupported type {Type}", name, entity.Type.ToString());
                break;
        }
    }


    /**
     * Handle events related to a deployed microservice
     */
    public async Task UpdateDeployment(EcsTaskStateChangeEvent ecsTaskStateChangeEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            var lambdaId = ecsTaskStateChangeEvent.Detail.StartedBy.Trim();
            var instanceTaskId = ecsTaskStateChangeEvent.Detail.TaskArn;
            logger.LogInformation("Starting UpdateDeployment for {LambdaId}, instance {InstanceId}", lambdaId,
                instanceTaskId);

            // find the original requested deployment by the lambda id
            var deployment = await deploymentsService.FindDeploymentByLambdaId(lambdaId, cancellationToken);

            if (deployment == null)
            {
                // Fallback to matching on the most recent for that container/version
                var taskDefArn = ecsTaskStateChangeEvent.Detail.TaskDefinitionArn;
                deployment = await deploymentsService.FindDeploymentByTaskArn(taskDefArn, cancellationToken);

                if (deployment == null)
                {
                    logger.LogWarning(
                        "Failed to find a matching deployment for ecs deployment id {LambdaId} or {TaskDefArn}, it may have been triggered by a different instance of portal",
                        lambdaId,
                        taskDefArn);
                    return;
                }

                logger.LogWarning("Falling back to matching on Task-Definition Arn {CdpId} -> {TaskDefArn}",
                    deployment.CdpDeploymentId, taskDefArn);
            }

            var instanceStatus = DeploymentStatus.CalculateStatus(ecsTaskStateChangeEvent.Detail.DesiredStatus,
                ecsTaskStateChangeEvent.Detail.LastStatus);
            if (instanceStatus == null)
            {
                logger.LogWarning("Skipping unknown status for desired:{Desired}, last:{Last}",
                    ecsTaskStateChangeEvent.Detail.DesiredStatus, ecsTaskStateChangeEvent.Detail.LastStatus);
                return;
            }

            // Update the specific instance status
            logger.LogInformation(
                "Updating instance status for cdpID: {CdpId}, lambdaId: {LambdaId} instance {InstanceId}, deployment {DeploymentId} {Status}",
                deployment.CdpDeploymentId, 
                lambdaId, 
                instanceTaskId, 
                ecsTaskStateChangeEvent.DeploymentId, 
                instanceStatus
            );

            await deploymentsService.UpdateInstance(deployment.CdpDeploymentId, instanceTaskId,
                new DeploymentInstanceStatus(instanceStatus, ecsTaskStateChangeEvent.Timestamp), cancellationToken);

            await UpdateStatus(deployment.CdpDeploymentId, ecsTaskStateChangeEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError("Failed to update deployment: {Message}", ex.Message);
        }
    }

    async Task UpdateStatus(string cdpDeploymentId, EcsTaskStateChangeEvent ecsTaskStateChangeEvent, CancellationToken cancellationToken)
    {
        var deployment = await deploymentsService.FindDeployment(cdpDeploymentId, cancellationToken);
        if (deployment == null)
        {
            throw new Exception("Failed to get updated deployment");
        }
            
        // Limit the number of stopped service in the event of a crash-loop
        deployment.TrimInstance(50);

        // update the overall status
        deployment.Status = DeploymentStatus.CalculateOverallStatus(deployment);
        deployment.Unstable = DeploymentStatus.IsUnstable(deployment);
        deployment.Updated = ecsTaskStateChangeEvent.Timestamp;

        deployment.TaskDefinitionArn = ecsTaskStateChangeEvent.Detail.TaskArn;

        if (deployment.FailureReasons.Count == 0)
        {
            deployment.FailureReasons = ExtractFailureReasons(ecsTaskStateChangeEvent);
        }

        await deploymentsService.UpdateOverallTaskStatus(deployment, cancellationToken);
        logger.LogInformation("Updated deployment {Id}, {Status}", deployment.LambdaId, deployment.Status);
    }

    /**
     * Handle events related to a test suite. Unlike a service these are expected to run then exit.
     */
    public async Task UpdateTestSuite(EcsTaskStateChangeEvent ecsTaskStateChangeEvent, Entity entity,
        CancellationToken cancellationToken)
    {
        try
        {
            var env = environmentLookup.FindEnv(ecsTaskStateChangeEvent.Account);
            var taskArn = ecsTaskStateChangeEvent.Detail.TaskArn;

            // see if we've already linked a test run to the arn
            var testRun = await testRunService.FindByTaskArn(taskArn, cancellationToken);

            // if it's not there, find a candidate to link it to
            if (testRun == null)
            {
                logger.LogInformation("trying to link {Id} in environment:{Env}", entity.Name, env);
                testRun = await testRunService.Link(
                    new TestRunMatchIds(entity.Name, env!, ecsTaskStateChangeEvent.Timestamp),
                    taskArn,
                    cancellationToken);
            }

            // if the linking fails, we have nothing to write the data to so bail
            if (testRun == null)
            {
                logger.LogWarning("Failed to find any test job for event {TaskArn}", taskArn);
                return;
            }

            // use the container exit code to figure out if the tests passed. non-zero exit code == failure. 
            var container = ecsTaskStateChangeEvent.Detail.Containers.FirstOrDefault(c => c.Name == entity.Name);
            var testResults = GenerateTestSuiteStatus(container);
            var failureReasons = ExtractFailureReasons(ecsTaskStateChangeEvent);
            var taskStatus = GenerateTestSuiteTaskStatus(ecsTaskStateChangeEvent.Detail.DesiredStatus,
                ecsTaskStateChangeEvent.Detail.LastStatus, failureReasons.Count > 0);

            logger.LogInformation("Updating {Name} test-suite {RunId} status to {Status}:{Result}", testRun.TestSuite,
                testRun.RunId, taskStatus, testResults);
            await testRunService.UpdateStatus(taskArn, taskStatus, testResults, ecsTaskStateChangeEvent.Timestamp,
                failureReasons, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError("Failed to update test suite: {Ex}", ex);
        }
    }

    public static List<FailureReason> ExtractFailureReasons(EcsTaskStateChangeEvent ecsEvent)
    {
        var failureReasons = ecsEvent.Detail.Containers
            .Where(c => c.Reason != null)
            .Select(c => new FailureReason(c.Name, c.Reason!))
            .ToList();

        // Check if it was the timeout container that killed the test run.
        // In most cases it the exit code will be 143 (force killed by ECS), but when it fires the exit code would be <= 1. (TBC)
        var timeoutContainer = ecsEvent.Detail.Containers.FirstOrDefault(c => c.Name.EndsWith("-timeout"));
        if (timeoutContainer is { LastStatus: "STOPPED", ExitCode: <= 1 })
        {
            failureReasons.Add(new FailureReason(timeoutContainer.Name, "Test suite exceeded maximum run time"));
        }

        // Find any non-standard task level exit codes filtering out expected codes
        // EssentialContainerExited - i.e. tests have finished
        // UserInitiated - killed via the kill button
        if (ecsEvent.Detail is { StopCode: not null, StoppedReason: not null, })
        {
            if (!s_failureReasonsToIgnore.Contains(ecsEvent.Detail.StopCode))
            {
                failureReasons.Add(new FailureReason("ECS Task", ecsEvent.Detail.StoppedReason));
            }
        }

        return failureReasons;
    }

    /**
     * Interpret the status of the test suit based on the exit code of the test container
     */
    public static string? GenerateTestSuiteStatus(EcsContainer? container)
    {
        return container?.ExitCode switch
        {
            null => null,
            0 => "passed",
            _ => "failed"
        };
    }

    /**
  * Interpret the overall status of the test run's ECS task
  */
    public static string GenerateTestSuiteTaskStatus(string desired, string last, bool hasFailures)
    {
        if (hasFailures) return "failed";
        return desired switch
        {
            "RUNNING" => last switch
            {
                "PROVISIONING" => "starting",
                "PENDING" => "starting",
                "STOPPED" => "failed",
                _ => "in-progress"
            },
            "STOPPED" => last switch
            {
                "DEPROVISIONING" => "finished",
                "STOPPED" => "finished",
                _ => "stopping"
            },
            _ => "unknown"
        };
    }
}