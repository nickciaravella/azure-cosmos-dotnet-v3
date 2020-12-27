﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.ChangeFeed;
    using Microsoft.Azure.Cosmos.ChangeFeed.Pagination;
    using Microsoft.Azure.Cosmos.Pagination;
    using Microsoft.Azure.Cosmos.Query.Core.QueryClient;
    using Microsoft.Azure.Cosmos.Resource.CosmosExceptions;
    using Microsoft.Azure.Cosmos.Routing;
    using Microsoft.Azure.Cosmos.Scripts;
    using Microsoft.Azure.Cosmos.Tracing;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Routing;

    /// <summary>
    /// Operations for reading, replacing, or deleting a specific, existing container by id.
    /// 
    /// <see cref="Cosmos.Database"/> for creating new containers, and reading/querying all containers;
    /// </summary>
    internal abstract partial class ContainerCore : ContainerInternal
    {
        private readonly Lazy<BatchAsyncContainerExecutor> lazyBatchExecutor;
        private static readonly Range<string> allRanges = new Range<string>(
                            PartitionKeyInternal.MinimumInclusiveEffectivePartitionKey,
                            PartitionKeyInternal.MaximumExclusiveEffectivePartitionKey,
                            isMinInclusive: true,
                            isMaxInclusive: false);

        protected ContainerCore(
            CosmosClientContext clientContext,
            DatabaseInternal database,
            string containerId,
            CosmosQueryClient cosmosQueryClient = null)
        {
            this.Id = containerId;
            this.ClientContext = clientContext;
            this.LinkUri = clientContext.CreateLink(
                parentLink: database.LinkUri,
                uriPathSegment: Paths.CollectionsPathSegment,
                id: containerId);

            this.Database = database;
            this.Conflicts = new ConflictsInlineCore(this.ClientContext, this);
            this.Scripts = new ScriptsInlineCore(this, this.ClientContext);
            this.cachedUriSegmentWithoutId = this.GetResourceSegmentUriWithoutId();
            this.queryClient = cosmosQueryClient ?? new CosmosQueryClientCore(this.ClientContext, this);
            this.lazyBatchExecutor = new Lazy<BatchAsyncContainerExecutor>(() => this.ClientContext.GetExecutorForContainer(this));
        }

        public override string Id { get; }

        public override Database Database { get; }

        public override string LinkUri { get; }

        public override CosmosClientContext ClientContext { get; }

        public override BatchAsyncContainerExecutor BatchExecutor => this.lazyBatchExecutor.Value;

        public override Conflicts Conflicts { get; }

        public override Scripts.Scripts Scripts { get; }

        public async Task<ContainerResponse> ReadContainerAsync(
            CosmosDiagnosticsContext diagnosticsContext,
            ITrace trace,
            ContainerRequestOptions requestOptions = null,
            CancellationToken cancellationToken = default)
        {
            ResponseMessage response = await this.ReadContainerStreamAsync(
                diagnosticsContext: diagnosticsContext,
                requestOptions: requestOptions,
                trace: trace,
                cancellationToken: cancellationToken);

            return this.ClientContext.ResponseFactory.CreateContainerResponse(this, response);
        }

        public async Task<ContainerResponse> ReplaceContainerAsync(
            CosmosDiagnosticsContext diagnosticsContext,
            ContainerProperties containerProperties,
            ITrace trace,
            ContainerRequestOptions requestOptions = null,
            CancellationToken cancellationToken = default)
        {
            if (containerProperties == null)
            {
                throw new ArgumentNullException(nameof(containerProperties));
            }

            this.ClientContext.ValidateResource(containerProperties.Id);
            ResponseMessage response = await this.ReplaceStreamInternalAsync(
                diagnosticsContext: diagnosticsContext,
                streamPayload: this.ClientContext.SerializerCore.ToStream(containerProperties),
                requestOptions: requestOptions,
                trace: trace,
                cancellationToken: cancellationToken);

            return this.ClientContext.ResponseFactory.CreateContainerResponse(this, response);
        }

        public async Task<ContainerResponse> DeleteContainerAsync(
            CosmosDiagnosticsContext diagnosticsContext,
            ITrace trace,
            ContainerRequestOptions requestOptions = null,
            CancellationToken cancellationToken = default)
        {
            ResponseMessage response = await this.DeleteContainerStreamAsync(
                diagnosticsContext: diagnosticsContext,
                requestOptions: requestOptions,
                trace: trace,
                cancellationToken: cancellationToken);

            return this.ClientContext.ResponseFactory.CreateContainerResponse(this, response);
        }

        public async Task<int?> ReadThroughputAsync(
            CosmosDiagnosticsContext diagnosticsContext,
            ITrace trace,
            CancellationToken cancellationToken = default)
        {
            ThroughputResponse response = await this.ReadThroughputIfExistsAsync(null, cancellationToken);
            return response.Resource?.Throughput;
        }

        public async Task<ThroughputResponse> ReadThroughputAsync(
            CosmosDiagnosticsContext diagnosticsContext,
            RequestOptions requestOptions,
            ITrace trace,
            CancellationToken cancellationToken = default)
        {
            ThroughputResponse throughputResponse = await this.ReadThroughputIfExistsAsync(
                diagnosticsContext,
                requestOptions,
                trace,
                cancellationToken);

            if (throughputResponse.StatusCode == HttpStatusCode.NotFound)
            {
                throw CosmosExceptionFactory.CreateNotFoundException(
                    message: $"Throughput is not configured for {this.Id}",
                    headers: throughputResponse.Headers,
                    diagnosticsContext: diagnosticsContext);
            }

            return throughputResponse;
        }

        public Task<ThroughputResponse> ReadThroughputIfExistsAsync(
            CosmosDiagnosticsContext diagnosticsContext,
            RequestOptions requestOptions,
            ITrace trace,
            CancellationToken cancellationToken = default)
        {
            CosmosOffers cosmosOffers = new CosmosOffers(this.ClientContext);
            return this.OfferRetryHelperForStaleRidCacheAsync(
                (rid) => cosmosOffers.ReadThroughputIfExistsAsync(rid, requestOptions, cancellationToken),
                diagnosticsContext,
                trace,
                cancellationToken);
        }

        public Task<ThroughputResponse> ReplaceThroughputAsync(
            CosmosDiagnosticsContext diagnosticsContext,
            int throughput,
            ITrace trace,
            RequestOptions requestOptions = null,
            CancellationToken cancellationToken = default)
        {
            return this.ReplaceThroughputAsync(
                diagnosticsContext: diagnosticsContext,
                throughputProperties: ThroughputProperties.CreateManualThroughput(throughput),
                requestOptions: requestOptions,
                trace: trace,
                cancellationToken: cancellationToken);
        }

        public Task<ThroughputResponse> ReplaceThroughputIfExistsAsync(
            CosmosDiagnosticsContext diagnosticsContext,
            ThroughputProperties throughput,
            ITrace trace,
            RequestOptions requestOptions = null,
            CancellationToken cancellationToken = default)
        {
            CosmosOffers cosmosOffers = new CosmosOffers(this.ClientContext);
            return this.OfferRetryHelperForStaleRidCacheAsync(
                (rid) => cosmosOffers.ReplaceThroughputPropertiesIfExistsAsync(
                    targetRID: rid,
                    throughputProperties: throughput,
                    requestOptions: requestOptions,
                    cancellationToken: cancellationToken),
                diagnosticsContext,
                trace,
                cancellationToken);
        }

        public async Task<ThroughputResponse> ReplaceThroughputAsync(
            CosmosDiagnosticsContext diagnosticsContext,
            ThroughputProperties throughputProperties,
            ITrace trace,
            RequestOptions requestOptions = null,
            CancellationToken cancellationToken = default)
        {
            ThroughputResponse throughputResponse = await this.ReplaceThroughputIfExistsAsync(
                diagnosticsContext,
                throughputProperties,
                trace,
                requestOptions,
                cancellationToken);

            if (throughputResponse.StatusCode == HttpStatusCode.NotFound)
            {
                throw CosmosExceptionFactory.CreateNotFoundException(
                    message: $"Throughput is not configured for {this.Id}",
                    headers: throughputResponse.Headers,
                    diagnosticsContext: diagnosticsContext);
            }

            return throughputResponse;
        }

        public Task<ResponseMessage> DeleteContainerStreamAsync(
            CosmosDiagnosticsContext diagnosticsContext,
            ITrace trace,
            ContainerRequestOptions requestOptions = null,
            CancellationToken cancellationToken = default)
        {
            return this.ProcessStreamAsync(
                diagnosticsContext: diagnosticsContext,
                streamPayload: null,
                operationType: OperationType.Delete,
                requestOptions: requestOptions,
                trace: trace,
                cancellationToken: cancellationToken);
        }

        public Task<ResponseMessage> ReadContainerStreamAsync(
            CosmosDiagnosticsContext diagnosticsContext,
            ITrace trace,
            RequestOptions requestOptions = null,
            CancellationToken cancellationToken = default)
        {
            return this.ProcessStreamAsync(
                diagnosticsContext: diagnosticsContext,
                streamPayload: null,
                operationType: OperationType.Read,
                requestOptions: requestOptions,
                trace: trace,
                cancellationToken: cancellationToken);
        }

        public Task<ResponseMessage> ReplaceContainerStreamAsync(
            CosmosDiagnosticsContext diagnosticsContext,
            ContainerProperties containerProperties,
            ITrace trace,
            ContainerRequestOptions requestOptions = null,
            CancellationToken cancellationToken = default)
        {
            if (containerProperties == null)
            {
                throw new ArgumentNullException(nameof(containerProperties));
            }

            this.ClientContext.ValidateResource(containerProperties.Id);
            return this.ReplaceStreamInternalAsync(
                diagnosticsContext: diagnosticsContext,
                streamPayload: this.ClientContext.SerializerCore.ToStream(containerProperties),
                requestOptions: requestOptions,
                trace: trace,
                cancellationToken: cancellationToken);
        }

        public async Task<IReadOnlyList<FeedRange>> GetFeedRangesAsync(
            CosmosDiagnosticsContext diagnosticsContext,
            ITrace trace,
            CancellationToken cancellationToken = default)
        {
            PartitionKeyRangeCache partitionKeyRangeCache = await this.ClientContext.DocumentClient.GetPartitionKeyRangeCacheAsync();
            
            string containerRId;
            using (diagnosticsContext.CreateScope(nameof(GetCachedRIDAsync)))
            {
                containerRId = await this.GetCachedRIDAsync(
                    forceRefresh: false,
                    cancellationToken);
            }

            IReadOnlyList<PartitionKeyRange> partitionKeyRanges;
            using (diagnosticsContext.CreateScope(nameof(partitionKeyRangeCache.TryGetOverlappingRangesAsync)))
            {
                partitionKeyRanges = await partitionKeyRangeCache.TryGetOverlappingRangesAsync(
                        containerRId,
                        ContainerCore.allRanges,
                        forceRefresh: true);
            }

            if (partitionKeyRanges == null)
            {
                string refreshedContainerRId;
                using (diagnosticsContext.CreateScope("GetRIDAsyncForceRefresh"))
                {
                    refreshedContainerRId = await this.GetCachedRIDAsync(
                        forceRefresh: true,
                        cancellationToken);
                }

                if (string.Equals(containerRId, refreshedContainerRId))
                {
                    throw CosmosExceptionFactory.CreateInternalServerErrorException(
                        $"Container rid {containerRId} did not have a partition key range after refresh",
                        diagnosticsContext: diagnosticsContext);
                }

                using (diagnosticsContext.CreateScope(nameof(partitionKeyRangeCache.TryGetOverlappingRangesAsync)))
                {
                    partitionKeyRanges = await partitionKeyRangeCache.TryGetOverlappingRangesAsync(
                            containerRId,
                            ContainerCore.allRanges,
                            forceRefresh: true);
                }

                if (partitionKeyRanges == null)
                {
                    throw CosmosExceptionFactory.CreateInternalServerErrorException(
                        $"Container rid {containerRId} returned partitionKeyRanges null after Container RID refresh",
                        diagnosticsContext: diagnosticsContext);
                }
            }

            List<FeedRange> feedTokens = new List<FeedRange>(partitionKeyRanges.Count);
            foreach (PartitionKeyRange partitionKeyRange in partitionKeyRanges)
            {
                feedTokens.Add(new FeedRangeEpk(partitionKeyRange.ToRange()));
            }

            return feedTokens;
        }

        public override FeedIterator GetChangeFeedStreamIterator(
            ChangeFeedStartFrom changeFeedStartFrom,
            ChangeFeedRequestOptions changeFeedRequestOptions = null)
        {
            if (changeFeedStartFrom == null)
            {
                throw new ArgumentNullException(nameof(changeFeedStartFrom));
            }

            NetworkAttachedDocumentContainer networkAttachedDocumentContainer = new NetworkAttachedDocumentContainer(
                this,
                this.queryClient,
                new CosmosDiagnosticsContextCore());
            DocumentContainer documentContainer = new DocumentContainer(networkAttachedDocumentContainer);

            return new ChangeFeedIteratorCore(
                documentContainer: documentContainer,
                changeFeedStartFrom: changeFeedStartFrom,
                changeFeedRequestOptions: changeFeedRequestOptions);
        }

        public override FeedIterator<T> GetChangeFeedIterator<T>(
            ChangeFeedStartFrom changeFeedStartFrom,
            ChangeFeedRequestOptions changeFeedRequestOptions = null)
        {
            if (changeFeedStartFrom == null)
            {
                throw new ArgumentNullException(nameof(changeFeedStartFrom));
            }

            NetworkAttachedDocumentContainer networkAttachedDocumentContainer = new NetworkAttachedDocumentContainer(
                this,
                this.queryClient,
                new CosmosDiagnosticsContextCore());
            DocumentContainer documentContainer = new DocumentContainer(networkAttachedDocumentContainer);

            ChangeFeedIteratorCore changeFeedIteratorCore = new ChangeFeedIteratorCore(
                documentContainer: documentContainer,
                changeFeedStartFrom: changeFeedStartFrom,
                changeFeedRequestOptions: changeFeedRequestOptions);

            return new FeedIteratorCore<T>(
                changeFeedIteratorCore,
                responseCreator: this.ClientContext.ResponseFactory.CreateChangeFeedUserTypeResponse<T>);
        }

        public override async Task<IEnumerable<string>> GetPartitionKeyRangesAsync(
            FeedRange feedRange,
            CancellationToken cancellationToken = default)
        {
            IRoutingMapProvider routingMapProvider = await this.ClientContext.DocumentClient.GetPartitionKeyRangeCacheAsync();
            string containerRid = await this.GetCachedRIDAsync(
                forceRefresh: false,
                cancellationToken);
            PartitionKeyDefinition partitionKeyDefinition = await this.GetPartitionKeyDefinitionAsync(cancellationToken);

            if (!(feedRange is FeedRangeInternal feedTokenInternal))
            {
                throw new ArgumentException(nameof(feedRange), ClientResources.FeedToken_UnrecognizedFeedToken);
            }

            return await feedTokenInternal.GetPartitionKeyRangesAsync(routingMapProvider, containerRid, partitionKeyDefinition, cancellationToken);
        }

        /// <summary>
        /// Gets the container's Properties by using the internal cache.
        /// In case the cache does not have information about this container, it may end up making a server call to fetch the data.
        /// </summary>
        /// <param name="forceRefresh">Forces the cache to refresh</param>
        /// <param name="cancellationToken"><see cref="CancellationToken"/> representing request cancellation.</param>
        /// <returns>A <see cref="Task"/> containing the <see cref="ContainerProperties"/> for this container.</returns>
        public override async Task<ContainerProperties> GetCachedContainerPropertiesAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            try
            {
                ClientCollectionCache collectionCache = await this.ClientContext.DocumentClient.GetCollectionCacheAsync();
                return await collectionCache.ResolveByNameAsync(
                    HttpConstants.Versions.CurrentVersion,
                    this.LinkUri,
                    forceRefresh,
                    cancellationToken);
            }
            catch (DocumentClientException ex)
            {
                throw CosmosExceptionFactory.Create(
                    dce: ex,
                    diagnosticsContext: null);
            }
        }

        // Name based look-up, needs re-computation and can't be cached
        public override async Task<string> GetCachedRIDAsync(
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            ContainerProperties containerProperties = await this.GetCachedContainerPropertiesAsync(
                forceRefresh,
                cancellationToken);
            return containerProperties?.ResourceId;
        }

        public override async Task<PartitionKeyDefinition> GetPartitionKeyDefinitionAsync(CancellationToken cancellationToken = default)
        {
            ContainerProperties cachedContainerPropertiesAsync = await this.GetCachedContainerPropertiesAsync(
                forceRefresh: false,
                cancellationToken: cancellationToken);
            return cachedContainerPropertiesAsync?.PartitionKey;
        }

        /// <summary>
        /// Used by typed API only. Exceptions are allowed.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns>Returns the partition key path</returns>
        public override async Task<IReadOnlyList<IReadOnlyList<string>>> GetPartitionKeyPathTokensAsync(CancellationToken cancellationToken = default)
        {
            ContainerProperties containerProperties = await this.GetCachedContainerPropertiesAsync(
                forceRefresh: false,
                cancellationToken: cancellationToken);
            if (containerProperties == null)
            {
                throw new ArgumentOutOfRangeException($"Container {this.LinkUri} not found");
            }

            if (containerProperties.PartitionKey?.Paths == null)
            {
                throw new ArgumentOutOfRangeException($"Partition key not defined for container {this.LinkUri}");
            }

            return containerProperties.PartitionKeyPathTokens;
        }

        /// <summary>
        /// Instantiates a new instance of the <see cref="PartitionKeyInternal"/> object.
        /// </summary>
        /// <remarks>
        /// The function selects the right partition key constant for inserting documents that don't have
        /// a value for partition key. The constant selection is based on whether the collection is migrated
        /// or user partitioned
        /// 
        /// For non-existing container will throw <see cref="DocumentClientException"/> with 404 as status code
        /// </remarks>
        public override async Task<PartitionKeyInternal> GetNonePartitionKeyValueAsync(CancellationToken cancellationToken = default)
        {
            ContainerProperties containerProperties = await this.GetCachedContainerPropertiesAsync(cancellationToken: cancellationToken);
            return containerProperties.GetNoneValue();
        }

        public override async Task<CollectionRoutingMap> GetRoutingMapAsync(CancellationToken cancellationToken, bool forceRefresh = false)
        {
            string collectionRID = await this.GetCachedRIDAsync(
                forceRefresh: forceRefresh,
                cancellationToken: cancellationToken);
            PartitionKeyRangeCache partitionKeyRangeCache = await this.ClientContext.Client.DocumentClient.GetPartitionKeyRangeCacheAsync();
            return await partitionKeyRangeCache.TryLookupAsync(
                collectionRID,
                null,
                null,
                cancellationToken,
                forceRefresh);
        }

        private async Task<ThroughputResponse> OfferRetryHelperForStaleRidCacheAsync(
            Func<string, Task<ThroughputResponse>> executeOfferOperation,
            CosmosDiagnosticsContext diagnosticsContext,
            ITrace trace,
            CancellationToken cancellationToken)
        {
            string rid = await this.GetCachedRIDAsync(
                forceRefresh: false,
                cancellationToken: cancellationToken);
            ThroughputResponse throughputResponse = await executeOfferOperation(rid);
            if (throughputResponse.StatusCode != HttpStatusCode.NotFound)
            {
                return throughputResponse;
            }

            // Check if RID cache is stale
            ResponseMessage responseMessage = await this.ReadContainerStreamAsync(
                diagnosticsContext: diagnosticsContext,
                requestOptions: null,
                trace: trace,
                cancellationToken: cancellationToken);

            // Container does not exist
            if (responseMessage.StatusCode == HttpStatusCode.NotFound)
            {
                return new ThroughputResponse(
                    responseMessage.StatusCode,
                    responseMessage.Headers,
                    null,
                    diagnosticsContext.Diagnostics);
            }

            responseMessage.EnsureSuccessStatusCode();

            ContainerProperties containerProperties = this.ClientContext.SerializerCore.FromStream<ContainerProperties>(responseMessage.Content);

            // The RIDs match so return the original response.
            if (string.Equals(rid, containerProperties.ResourceId))
            {
                return throughputResponse;
            }

            // Get the offer with the new rid value
            return await executeOfferOperation(containerProperties.ResourceId);
        }

        private Task<ResponseMessage> ReplaceStreamInternalAsync(
            CosmosDiagnosticsContext diagnosticsContext,
            Stream streamPayload,
            ITrace trace,
            ContainerRequestOptions requestOptions = null,
            CancellationToken cancellationToken = default)
        {
            return this.ProcessStreamAsync(
                diagnosticsContext: diagnosticsContext,
                streamPayload: streamPayload,
                operationType: OperationType.Replace,
                requestOptions: requestOptions,
                trace: trace,
                cancellationToken: cancellationToken);
        }

        private Task<ResponseMessage> ProcessStreamAsync(
            CosmosDiagnosticsContext diagnosticsContext,
            Stream streamPayload,
            OperationType operationType,
            RequestOptions requestOptions,
            ITrace trace,
            CancellationToken cancellationToken)
        {
            return this.ProcessResourceOperationStreamAsync(
                diagnosticsContext: diagnosticsContext,
                streamPayload: streamPayload,
                operationType: operationType,
                linkUri: this.LinkUri,
                resourceType: ResourceType.Collection,
                requestOptions: requestOptions,
                trace: trace,
                cancellationToken: cancellationToken);
        }

        private Task<ResponseMessage> ProcessResourceOperationStreamAsync(
            CosmosDiagnosticsContext diagnosticsContext,
            Stream streamPayload,
            OperationType operationType,
            string linkUri,
            ResourceType resourceType,
            ITrace trace,
            RequestOptions requestOptions = null,
            CancellationToken cancellationToken = default)
        {
            return this.ClientContext.ProcessResourceOperationStreamAsync(
              resourceUri: linkUri,
              resourceType: resourceType,
              operationType: operationType,
              cosmosContainerCore: null,
              feedRange: null,
              streamPayload: streamPayload,
              requestOptions: requestOptions,
              requestEnricher: null,
              diagnosticsContext: diagnosticsContext,
              trace: trace,
              cancellationToken: cancellationToken);
        }
    }
}