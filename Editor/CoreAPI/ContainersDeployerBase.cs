// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading.Tasks;
using AmazonGameLiftPlugin.Core.DeploymentManagement.Models;
using AmazonGameLiftPlugin.Core.Shared;

namespace AmazonGameLift.Editor
{
    /// <summary>
    /// A base type for the scenario scripts.
    /// </summary>
    public abstract class ContainersDeployerBase
    {
        private const int ChangeSetPollPeriodMs = 1000;
        private readonly Delay _delay;

        public abstract string DisplayName { get; }

        public abstract string Description { get; }

        public abstract string HelpUrl { get; }

        public abstract string ScenarioFolder { get; }

        public virtual int PreferredUiOrder { get; }

        internal DeploymentRequestFactory RequestFactory { get; set; }

        protected CoreApi GameLiftCoreApi { get; }
        
        protected ContainersDeployerBase(Delay delay, CoreApi coreApi)
        {
            _delay = delay ?? throw new ArgumentNullException(nameof(delay));
            GameLiftCoreApi = coreApi ?? throw new ArgumentNullException(nameof(coreApi));
            RequestFactory = new DeploymentRequestFactory(coreApi, true);
        }

        protected ContainersDeployerBase()
        {
            _delay = new Delay();
            GameLiftCoreApi = CoreApi.SharedInstance;
            RequestFactory = new DeploymentRequestFactory(GameLiftCoreApi, true);
        }

        /// <exception cref="ArgumentNullException">For all parameters.</exception>
        public virtual async Task<DeploymentResponse> StartDeployment(string scenarioFolderPath, 
            string gameName, bool isDevelopmentBuild)
        {
            (DeploymentRequest request, bool success, Response failedResponse) = RequestFactory
                .CreateRequest(scenarioFolderPath, gameName, isDevelopmentBuild);

            if (!success)
            {
                return Response.Fail(new DeploymentResponse(failedResponse));
            }

            ValidateCfnTemplateResponse validateResponse = GameLiftCoreApi.ValidateCfnTemplate(
                request.Profile, request.Region, request.CfnTemplatePath);

            if (!validateResponse.Success)
            {
                return Response.Fail(new DeploymentResponse(validateResponse));
            }

            (DeploymentResponse createResponse, DescribeChangeSetResponse describeResponse) = await CreateChangeSet(request);

            if (!createResponse.Success)
            {
                return createResponse;
            }

            (bool checkSuccess, bool checkConfirmed, Response checkFailedResponse) = await
                CheckChangeConfirmation(request, describeResponse);

            if (!checkSuccess)
            {
                return Response.Fail(new DeploymentResponse(checkFailedResponse));
            }

            if (!checkConfirmed)
            {
                GameLiftCoreApi.DeleteChangeSet(request.Profile, request.Region, request.StackName, request.ChangeSetName);
                return Response.Fail(new DeploymentResponse(ErrorCode.OperationCancelled));
            }

            DeploymentResponse deploymentResponse = await Task.Run(() => Deploy(request));

            if (!deploymentResponse.Success)
            {
                return Response.Fail(deploymentResponse);
            }

            return Response.Ok(new DeploymentResponse(new DeploymentId(request, DisplayName)));
        }

        internal virtual async Task<(bool success, bool confirmed, Response failedResponse)> CheckChangeConfirmation(
            DeploymentRequest request, DescribeChangeSetResponse changeSetResponse)
        {
            DescribeStackResponse existsResponse = GameLiftCoreApi.DescribeStack(request.Profile, request.Region, request.StackName);

            if (!existsResponse.Success)
            {
                return (false, false, existsResponse);
            }

            return (true, true, null);
        }

        internal virtual async Task<(DeploymentResponse, DescribeChangeSetResponse)> CreateChangeSet(DeploymentRequest request)
        {
            CreateChangeSetResponse createResponse = GameLiftCoreApi.CreateChangeSet(
                request.Profile, request.Region, request.BucketName, request.StackName,
                request.CfnTemplatePath, request.ParametersPath, request.GameName, request.LambdaFolderPath, request.BuildS3Key);

            if (!createResponse.Success)
            {
                return (Response.Fail(new DeploymentResponse(createResponse)), null);
            }

            var poller = new UntilResponseFailurePoller(_delay);

            DescribeChangeSetResponse describeResponse = await poller.Poll(ChangeSetPollPeriodMs,
                () => GameLiftCoreApi.DescribeChangeSet(request.Profile, request.Region, request.StackName, createResponse.CreatedChangeSetName),
                stopCondition: target => target.ExecutionStatus != ChangeSetExecutionStatus.Unavailable);

            if (!describeResponse.Success)
            {
                return (Response.Fail(new DeploymentResponse(describeResponse)), null);
            }

            if (describeResponse.ExecutionStatus != ChangeSetExecutionStatus.Available)
            {
                return (Response.Fail(new DeploymentResponse(ErrorCode.ChangeSetStatusInvalid)), null);
            }

            request.ChangeSetName = createResponse.CreatedChangeSetName;

            return (Response.Ok(new DeploymentResponse()), describeResponse);
        }

        protected abstract Task<DeploymentResponse> Deploy(DeploymentRequest request);
    }
}
