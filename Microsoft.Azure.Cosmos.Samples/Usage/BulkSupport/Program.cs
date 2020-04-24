﻿namespace Cosmos.Samples.Shared
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos;
    using Microsoft.Extensions.Configuration;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    // ----------------------------------------------------------------------------------------------------------
    // Prerequisites - 
    // 
    // 1. An Azure Cosmos account - 
    //    https://azure.microsoft.com/en-us/itemation/articles/itemdb-create-account/
    //
    // 2. Microsoft.Azure.Cosmos NuGet package - 
    //    http://www.nuget.org/packages/Microsoft.Azure.Cosmos/ 
    // ----------------------------------------------------------------------------------------------------------
    // Sample - demonstrates the basic usage of the CosmosClient bulk mode by performing a high volume of operations
    // ----------------------------------------------------------------------------------------------------------

    public class Program
    {
        private static readonly JsonSerializer Serializer = new JsonSerializer();
        private static CosmosClient client;
        private static Database database = null;
        private static int itemsToCreate;
        private static int itemSize;
        private static int runtimeInSeconds;
        private static bool shouldCleanupOnFinish;
        private static int numWorkers;

        private static bool waitAfterDelete = false;

        // Async main requires c# 7.1 which is set in the csproj with the LangVersion attribute
        // <Main>
        public static async Task Main(string[] args)
        {
            try
            {
                // Intialize container or create a new container.
                Container container = await Program.Initialize();
                List<Task> workerTasks = new List<Task>();

                workerTasks.Add(Task.Run(() =>  Program.BatchTestingAsync(container))); // CRD workload
                workerTasks.Add(Task.Run(() => Program.CreateItemsConcurrentlyAsync(container))); // Pure ingestion workload

                await Task.WhenAll(workerTasks);
            }
            catch (CosmosException cre)
            {
                Console.WriteLine(cre.ToString());
            }
            catch (Exception e)
            {
                Exception baseException = e.GetBaseException();
                Console.WriteLine("Error: {0}, Message: {1}", e.Message, baseException.Message);
            }
            finally
            {
                if (Program.shouldCleanupOnFinish)
                {
                    await Program.CleanupAsync();
                }
                client.Dispose();

                Console.WriteLine("End of demo, press any key to exit.");
                Console.ReadKey();
            }
        }
        // </Main>

        private static async Task BatchTestingAsync(Container container)
        {
            Console.WriteLine("Running a test job BatchTestingAsync");

            for (int loop = 0; loop <= Program.itemsToCreate/100; loop++)
            {
                string pk = Guid.NewGuid().ToString();
                TransactionalBatch cosmosBatch = container.CreateTransactionalBatch(new PartitionKey(pk));
                DataSource dataSource = new DataSource(100, itemSize, numWorkers);
                int numberOfOperationsPerBatch = 100;

                for (int i = 0; i < numberOfOperationsPerBatch; i++)
                {
                    Stream documentGeneratorOutput = dataSource.CreateTempDocItemStream(pk);
                    cosmosBatch.CreateItemStream(documentGeneratorOutput);
                }

                TransactionalBatchResponse batchResponse = await cosmosBatch.ExecuteAsync();

                if (batchResponse.StatusCode != HttpStatusCode.OK || batchResponse.Count != numberOfOperationsPerBatch)
                {
                    Console.WriteLine($"Batch Create parent failed status code [{batchResponse.StatusCode}] and operation count [{batchResponse.Count}]");
                }

                // Validate all response status codes
                List<JObject> listOfObjects = new List<JObject>();
                for (int index = 0; index < numberOfOperationsPerBatch; index++)
                {
                    if (batchResponse[index].StatusCode != HttpStatusCode.Created)
                    {
                        Console.WriteLine($"Batch Create  item failed status code [{batchResponse[index].StatusCode}]");
                    }

                    JObject operation = batchResponse.GetOperationResultAtIndex<JObject>(index).Resource;
                    listOfObjects.Add(operation);
                }

                TransactionalBatch cosmosReplaceBatch = container.CreateTransactionalBatch(new PartitionKey(pk));

                for (int i = 0; i < listOfObjects.Count; i++)
                {
                    listOfObjects[i].Add("tag1", "replaced");
                    listOfObjects[i]["N"] = 100;
                    cosmosReplaceBatch.ReplaceItem(listOfObjects[i]["id"].ToString(), listOfObjects[i]);
                }

                // Execute batch
                TransactionalBatchResponse batchReplaceResponse = await cosmosReplaceBatch.ExecuteAsync();
                if (batchReplaceResponse.StatusCode != HttpStatusCode.OK || batchReplaceResponse.Count != listOfObjects.Count)
                {
                    Console.WriteLine($"Batch Replace parent failed status code [{batchReplaceResponse.StatusCode}] and operation count [{batchReplaceResponse.Count}]");
                }

                List<JObject> listOfObjects2 = new List<JObject>();
                // Validate all response status codes
                for (int index = 0; index < batchReplaceResponse.Count; index++)
                {
                    if (batchReplaceResponse[index].StatusCode != HttpStatusCode.OK)
                    {
                        Console.WriteLine($"Batch Replace  item failed status code [{batchReplaceResponse[index].StatusCode}]");
                    }

                    JObject operation = batchReplaceResponse.GetOperationResultAtIndex<JObject>(index).Resource;
                    listOfObjects2.Add(operation);
                }

                if(loop % 2 == 0)
                {
                    // Delete half the time
                    TransactionalBatch cosmosDeleteBatch = container.CreateTransactionalBatch(new PartitionKey(pk));
                    for (int i = 0; i < listOfObjects2.Count; i++)
                    {
                        cosmosDeleteBatch.DeleteItem(listOfObjects2[i]["id"].ToString());
                    }

                    TransactionalBatchResponse deleteBatchResponse = await cosmosDeleteBatch.ExecuteAsync();

                    // Check batch response
                    if (deleteBatchResponse.StatusCode != HttpStatusCode.OK || deleteBatchResponse.Count != listOfObjects2.Count)
                    {
                        Console.WriteLine($"Batch Delete parent failed status code [{deleteBatchResponse.StatusCode}] and operation count [{batchResponse.Count}]");
                    }

                    // Validate all response status codes
                    for (int index = 0; index < deleteBatchResponse.Count; index++)
                    {
                        if (deleteBatchResponse[index].StatusCode != HttpStatusCode.NoContent)
                        {
                            Console.WriteLine($"Batch Delete  item failed status code [{deleteBatchResponse[index].StatusCode}]");
                        }
                    }

                    if (loop % 100 == 0)
                    {
                        Console.WriteLine($"loop {loop} done");
                    }
                }
            }

            Console.WriteLine("Successfull");
        }

        private static async Task CreateItemsConcurrentlyAsync(Container container)
        {
            Console.WriteLine($"Starting creation of {itemsToCreate} items of about {itemSize} bytes each in a limit of {runtimeInSeconds} seconds using {numWorkers} workers.");

            ConcurrentDictionary<HttpStatusCode, int> countsByStatus = new ConcurrentDictionary<HttpStatusCode, int>();
            int taskCompleteCounter = 0;
            int globalDocCounter = 0;

            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.CancelAfter(runtimeInSeconds * 1000);
            CancellationToken cancellationToken = cancellationTokenSource.Token;

            Stopwatch stopwatch = Stopwatch.StartNew();
            long startMilliseconds = stopwatch.ElapsedMilliseconds;

            try
            {
                List<Task> workerTasks = new List<Task>();
                for (int i = 0; i < numWorkers; i++)
                {
                    workerTasks.Add(Task.Run(() =>
                    {
                        DataSource dataSource = new DataSource(itemsToCreate, itemSize, numWorkers);
                        int docCounter = 0;

                        while (!cancellationToken.IsCancellationRequested && docCounter < itemsToCreate)
                        {
                            docCounter++;

                            MemoryStream stream = dataSource.GetNextDocItem(out PartitionKey partitionKeyValue);
                            _ = container.CreateItemStreamAsync(stream, partitionKeyValue, null, cancellationToken)
                                .ContinueWith((Task<ResponseMessage> task) =>
                                {
                                    Interlocked.Increment(ref taskCompleteCounter);

                                    if (task.IsCompletedSuccessfully)
                                    {
                                        if (stream != null) { stream.Dispose(); }
                                        HttpStatusCode resultCode = task.Result.StatusCode;
                                        countsByStatus.AddOrUpdate(resultCode, 1, (_, old) => old + 1);
                                        if (task.Result != null) { task.Result.Dispose(); }
                                    }
                                    task.Dispose();
                                });
                        }

                        Interlocked.Add(ref globalDocCounter, docCounter);
                    }));
                }

                await Task.WhenAll(workerTasks);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not insert {itemsToCreate * numWorkers} items in {runtimeInSeconds} seconds.");
                Console.WriteLine(ex);
            }
            finally
            {
                while (globalDocCounter > taskCompleteCounter)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Console.WriteLine($"Could not insert {itemsToCreate * numWorkers} items in {runtimeInSeconds} seconds.");
                        break;
                    }
                    Console.WriteLine($"In progress. Processed: {taskCompleteCounter}, Pending: {globalDocCounter - taskCompleteCounter}");
                    Thread.Sleep(2000);
                }

                foreach (var countForStatus in countsByStatus)
                {
                    Console.WriteLine(countForStatus.Key + " " + countForStatus.Value);
                }
            }

            int created = countsByStatus.SingleOrDefault(x => x.Key == HttpStatusCode.Created).Value;
            Console.WriteLine($"Inserted {created} items in {(stopwatch.ElapsedMilliseconds - startMilliseconds) /1000} seconds");
        }

        // <Model>
        private class MyDocument
        {
            public string id { get; set; }

            public string pk { get; set; }

            public string other { get; set; }
        }
        // </Model>

        private static async Task<Container> Initialize()
        {
            // Read the Cosmos endpointUrl and authorization keys from configuration
            // These values are available from the Azure Management Portal on the Cosmos Account Blade under "Keys"
            // Keep these values in a safe & secure location. Together they provide Administrative access to your Cosmos account
            IConfigurationRoot configuration = new ConfigurationBuilder()
                    .AddJsonFile("appSettings.json")
                    .Build();

            string endpointUrl = configuration["EndPointUrl"];
            if (string.IsNullOrEmpty(endpointUrl))
            {
                throw new ArgumentNullException("Please specify a valid EndPointUrl in the appSettings.json");
            }

            string authKey = configuration["AuthorizationKey"];
            if (string.IsNullOrEmpty(authKey) || string.Equals(authKey, "Super secret key"))
            {
                throw new ArgumentException("Please specify a valid AuthorizationKey in the appSettings.json");
            }

            string databaseName = configuration["DatabaseName"];
            if (string.IsNullOrEmpty(databaseName))
            {
                throw new ArgumentException("Please specify a valid DatabaseName in the appSettings.json");
            }

            string containerName = configuration["ContainerName"];
            if (string.IsNullOrEmpty(containerName))
            {
                throw new ArgumentException("Please specify a valid ContainerName in the appSettings.json");
            }

            // Important: Needed to regulate the main execution/ingestion job.
            Program.itemsToCreate = int.Parse(string.IsNullOrEmpty(configuration["ItemsToCreate"]) ? "1000" : configuration["ItemsToCreate"]);
            Program.itemSize = int.Parse(string.IsNullOrEmpty(configuration["ItemSize"]) ? "1024" : configuration["ItemSize"]);
            Program.runtimeInSeconds = int.Parse(string.IsNullOrEmpty(configuration["RuntimeInSeconds"]) ? "30" : configuration["RuntimeInSeconds"]);
            Program.numWorkers = int.Parse(string.IsNullOrEmpty(configuration["numWorkers"]) ? "1" : configuration["numWorkers"]);
            Program.waitAfterDelete = bool.Parse(string.IsNullOrEmpty(configuration["waitAfterDelete"]) ? "false" : configuration["waitAfterDelete"]);

            Program.shouldCleanupOnFinish = bool.Parse(string.IsNullOrEmpty(configuration["ShouldCleanupOnFinish"]) ? "false" : configuration["ShouldCleanupOnFinish"]);
            bool shouldCleanupOnStart = bool.Parse(string.IsNullOrEmpty(configuration["ShouldCleanupOnStart"]) ? "false" : configuration["ShouldCleanupOnStart"]);
            int collectionThroughput = int.Parse(string.IsNullOrEmpty(configuration["CollectionThroughput"]) ? "30000" : configuration["CollectionThroughput"]);

            Program.client = GetBulkClientInstance(endpointUrl, authKey);
            Program.database = client.GetDatabase(databaseName);
            Container container = Program.database.GetContainer(containerName); ;
            if (shouldCleanupOnStart)
            {
                container = await CreateFreshContainerAsync(client, databaseName, containerName, collectionThroughput);
            }

            try
            {
                await container.ReadContainerAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error in reading collection: {0}", ex.Message);
                throw ex;
            }

            Console.WriteLine("Running demo for container {0} with a Bulk enabled CosmosClient.", containerName);

            return container;
        }

        private static CosmosClient GetBulkClientInstance(
            string endpoint,
            string authKey) =>
        // </Initialization>
            new CosmosClient(endpoint, authKey, new CosmosClientOptions()
            {
                AllowBulkExecution = true,
                ConnectionMode = ConnectionMode.Direct,
                MaxRetryAttemptsOnRateLimitedRequests = 100,
                RequestTimeout = new TimeSpan(0, 5, 0),
                MaxRetryWaitTimeOnRateLimitedRequests = new TimeSpan(0, 5, 0)
            });
        // </Initialization>

        private static async Task CleanupAsync()
        {
            if (Program.database != null)
            {
                await Program.database.DeleteAsync();
            }
        }

        private static async Task<Container> CreateFreshContainerAsync(CosmosClient client, string databaseName, string containerName, int throughput)
        {
            Program.database = await client.CreateDatabaseIfNotExistsAsync(databaseName);

            try
            {
                Console.WriteLine("Deleting old container if it exists.");
                await database.GetContainer(containerName).DeleteContainerStreamAsync();
            }
            catch(Exception) {
                // Do nothing
            }

            // We create a partitioned collection here which needs a partition key. Partitioned collections
            // can be created with very high values of provisioned throughput and used to store 100's of GBs of data. 
            Console.WriteLine($"The demo will create a {throughput} RU/s container, press any key to continue.");
            //Console.ReadKey();

            // Indexing Policy to exclude all attributes to maximize RU/s usage
            Container container = await database.DefineContainer(containerName, "/partitionKey")
                    .WithIndexingPolicy()
                        .WithIndexingMode(IndexingMode.Consistent)
                        .WithIncludedPaths()
                            .Attach()
                        .WithExcludedPaths()
                            .Path("/*")
                            .Attach()
                    .Attach()
                .CreateAsync(throughput);

            return container;
        }

        private class DataSource
        {
            private readonly int itemSize;
            private const long maxStoredSizeInBytes = 100 * 1024 * 1024;
            private Queue<KeyValuePair<PartitionKey, MemoryStream>> documentsToImportInBatch;
            string padding = string.Empty;
            private PayloadGenerator payloadGenerator;

            public DataSource(int itemCount, int itemSize, int numWorkers)
            {
                this.itemSize = itemSize;
                string[] documentKeys = GetDocumentKeys(300);
                this.payloadGenerator = new PayloadGenerator(documentKeys, 256);
            }

            private static string[] GetDocumentKeys(long numKeys)
            {
                // generate numKeys - 1 GUIDs that will be used as key values in addition to partitionKey
                string[] keys = new string[numKeys];
                keys[0] = "partitionKey";

                string keynameprefix = "longkeyname";

                for (int count = 1; count < numKeys; ++count)
                {
                    keys[count] = keynameprefix + count;
                }

                return keys;
            }

            private MemoryStream CreateNextDocItem(out PartitionKey partitionKeyValue)
            {
                string partitionKey = Guid.NewGuid().ToString();
                string payloadToUse = payloadGenerator.GeneratePayload(partitionKey);
                partitionKeyValue = new PartitionKey(partitionKey);

                return new MemoryStream(Encoding.UTF8.GetBytes(payloadToUse));
            }

            public JObject CreateTempDocItem(string partitionKey)
            {
                string id = Guid.NewGuid().ToString();
                MyDocument myDocument = new MyDocument() { id = id, pk = partitionKey, other = padding };

                return JObject.FromObject(myDocument);
            }

            public MemoryStream CreateTempDocItemStream(string partitionKey)
            {
                string payloadToUse = payloadGenerator.GeneratePayload(partitionKey);
                return new MemoryStream(Encoding.UTF8.GetBytes(payloadToUse));
            }

            public MemoryStream GetNextDocItem(out PartitionKey partitionKeyValue)
            {
                if (documentsToImportInBatch != null && documentsToImportInBatch.Count > 0)
                {
                    KeyValuePair<PartitionKey, MemoryStream> pair = documentsToImportInBatch.Dequeue();
                    partitionKeyValue = pair.Key;
                    return pair.Value;
                }
                else
                {
                    MemoryStream value = CreateNextDocItem(out PartitionKey pkValue);
                    partitionKeyValue = pkValue;
                    return value;
                }
            }
        }
    }
}

