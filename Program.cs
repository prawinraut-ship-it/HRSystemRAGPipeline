﻿using Azure.AI.Projects;
using Microsoft.Extensions.Configuration;
using Azure.Identity;
using System.ClientModel;
using Azure.AI.Projects.Agents;
using OpenAI.Responses;
using Azure.AI.Extensions.OpenAI;
using OpenAI.VectorStores;
using OpenAI.Files;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;



#pragma warning disable OPENAI001

var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

foreach (var setting in configuration.AsEnumerable())
{
    if (!string.IsNullOrEmpty(setting.Value))
        Environment.SetEnvironmentVariable(setting.Key, setting.Value);
}

var projectEndpoint = Environment.GetEnvironmentVariable("PROJECT_ENDPOINT");
var modelDeployment = Environment.GetEnvironmentVariable("MODEL_DEPLOYMENT_NAME");
var agentName = Environment.GetEnvironmentVariable("AGENT_NAME") ?? "reviewer";
bool skipKnowledgeConfiguration = bool.Parse(Environment.GetEnvironmentVariable("SKIPKNOWLDGECONFIGURATION") ?? "false");
bool skipEvaluationDatasetSetup = bool.Parse(Environment.GetEnvironmentVariable("SKIPEVALUATIONDATASETSETUP") ?? "false");
var proxyEvaluationDatasetFile = Environment.GetEnvironmentVariable("PROXYEVALUATIONDATASETFILE");

if (string.IsNullOrEmpty(projectEndpoint))
{
    Console.WriteLine("Error: PROJECT_ENDPOINT environment variable not set.");
    Console.WriteLine("Please set it in appsettings.json or as an environment variable.");
    return;
}

if (string.IsNullOrEmpty(modelDeployment))
{
    Console.WriteLine("Error: MODEL_DEPLOYMENT_NAME environment variable not set.");
    Console.WriteLine("Please set it in appsettings.json or as an environment variable.");
    return;
}

AIProjectClient projectClient = new(
    endpoint: new Uri(projectEndpoint),
    tokenProvider: new DefaultAzureCredential()
);



string pdfFolder = Path.Combine(Directory.GetCurrentDirectory(), "PolicyDocuments");
VectorStoreClient vctStoreClient = projectClient.ProjectOpenAIClient.GetVectorStoreClient();
var store = vctStoreClient.GetVectorStores().FirstOrDefault(s => s.Name == "policy-documents-vectorstore");
VectorStore vectorStore = store;

if (!skipKnowledgeConfiguration)
{

    List<string> fileIds = new List<string>() { };

    Console.WriteLine("Uploading files to the Foundry...\n");

    var files = await projectClient.ProjectOpenAIClient.GetProjectFilesClient().GetFilesAsync(FilePurpose.Assistants);
    foreach (string filePath in Directory.EnumerateFiles(pdfFolder, "*", SearchOption.AllDirectories))
    {
        string fileName = Path.GetFileName(filePath);
        byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
        BinaryData fileData = new BinaryData(fileBytes);
        var fileId = files.Value.FirstOrDefault(f => f.Filename == fileName)?.Id;
        if (!string.IsNullOrEmpty(fileId))
        {
            _ = await projectClient.ProjectOpenAIClient.GetProjectFilesClient().DeleteFileAsync(fileId);
        }
        OpenAIFile uploadedFile = await projectClient.ProjectOpenAIClient.GetProjectFilesClient().UploadFileAsync(fileData, fileName, FileUploadPurpose.Assistants);
        fileIds.Add(uploadedFile.Id);
        Console.WriteLine($"File uploaded: {fileName}");


        Console.WriteLine("Files uploaded successfully...\n");
    }

    // Create the VectorStore and provide it with uploaded file ID.

    if (store != null)
    {
        Console.WriteLine("Vector store already exists. Deleting existing vector store...\n");
        await vctStoreClient.DeleteVectorStoreAsync(store.Id);
    }
    VectorStoreCreationOptions options = new()
    {
        Name = "policy-documents-vectorstore",
    };
    vectorStore = await vctStoreClient.CreateVectorStoreAsync(options: options);
    await vctStoreClient.AddFileBatchToVectorStoreAsync(vectorStore.Id, fileIds);

    Console.WriteLine("Vector store created and files added to vector store...\n");

    var agentDefinition = new DeclarativeAgentDefinition(model: modelDeployment)
    {
        Instructions = @"
You are a helpful Human Resources agent for Cobalt Ridge Systems.

- Answer ONLY using retrieved HR policy documents
- Do NOT use general knowledge
- Provide detailed, clear explanations

CRITICAL:
- Every answer MUST include citations
- Use formatCitation tool for ALL citations
- NEVER modify retrieved text when sending to formatCitation

If no data found:
'Sorry, I lack the information to assist you with this query.'

If outside HR scope:
Politely refuse
",
        Tools = {
                    ResponseTool.CreateFileSearchTool(new List<string> { vectorStore.Id }, 12),
                    CitationBuilder.FormatCitationTool
                }
    };

    // Create the agent version in the project. Run it once only
    var agentVersion = projectClient.AgentAdministrationClient.CreateAgentVersion(
        agentName: agentName,
        options: new ProjectsAgentVersionCreationOptions(agentDefinition)
    );

    Console.WriteLine("Agent version created/updated...\n");
 
    
}

// Run evaluation 
    Console.WriteLine("Run Evalaution...\n");
    Console.WriteLine("Type 'Yes' to start the evaluation or 'No' to skip.");
    string? startEvaluationInput = Console.ReadLine() ?? "No";
    if (startEvaluationInput.Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase))
    {
        string fileName = $"evaluation_dataset_{DateTime.Now:yyyyMMddHHmmss}";
        string outputPath = Path.Combine(Directory.GetCurrentDirectory(), "EvaluationDataset", $"{fileName}.jsonl");

        if (!skipEvaluationDatasetSetup || string.IsNullOrEmpty(proxyEvaluationDatasetFile))
        {
            // Input and output file paths
            string templatePath = Path.Combine(Directory.GetCurrentDirectory(), "EvaluationDataset", "EvaluationTemplate.json");

            // 1. Read the entire file
            if (!File.Exists(templatePath))
            {
                Console.WriteLine($"Input file not found: {templatePath}");
                return;
            }

            string jsonText = await File.ReadAllTextAsync(templatePath);

            // 2. Parse into a JsonNode (DOM)
            JsonNode? root = JsonNode.Parse(jsonText);
            if (root is null || root is not JsonArray jsonArray)
            {
                Console.WriteLine("The JSON root is not an array. Expected a top-level JSON array.");
                return;
            }

            var evalArray = new JsonArray();
            int testCaseNumber = 1;
            foreach (JsonNode? itemNode in jsonArray)
            {
                Console.WriteLine($"\n\nProcessing test case #{testCaseNumber++}...\n");
                if (itemNode is JsonObject obj)
                {
                    // Create the conversation to store responses.
                    ClientResult<ProjectConversation> evalConversationResult = projectClient.ProjectOpenAIClient.GetProjectConversationsClient().CreateProjectConversation();
                    CreateResponseOptions evalResponseOptions = new CreateResponseOptions()
                    {
                        Agent = new AgentReference("policyassistant", "1"),
                        AgentConversationId = evalConversationResult.Value.Id,
                        StreamingEnabled = true,
                    };
                    string? query = obj["query"]?.GetValue<string>();
                    string? ground_truth = obj["ground_truth"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(query))
                    {
                        evalResponseOptions.InputItems.Clear();
                        evalResponseOptions.InputItems.Add(ResponseItem.CreateUserMessageItem(query));

                        Console.WriteLine($"\nQuery: {query}\n");
                        Console.Write("Agent: ");
                        while (true)
                        {
                            List<ResponseItem> inputItems = new List<ResponseItem>();
                            bool functionCalled = false;

                            foreach (StreamingResponseUpdate streamResponse in projectClient.ProjectOpenAIClient.GetResponsesClient().CreateResponseStreaming(evalResponseOptions))
                            {
                                if (streamResponse is StreamingResponseOutputItemDoneUpdate itemDoneUpdate)
                                {
                                    if (itemDoneUpdate.Item is FunctionCallResponseItem functionToolCall)
                                    {
                                        var functionOutputItem = CitationBuilder.GetResolvedToolOutput(functionToolCall);

                                        if (functionOutputItem != null)
                                        {
                                            inputItems.Add(functionOutputItem);
                                            functionCalled = true;
                                        }
                                    }
                                }

                                if (streamResponse is StreamingResponseOutputTextDoneUpdate text)
                                {
                                    Console.WriteLine($"{text.Text}");

                                    var newObj = new JsonObject
                                    {
                                        ["query"] = query,
                                        ["response"] = text.Text,
                                        ["ground_truth"] = ground_truth
                                    };
                                    evalArray.Add(newObj);
                                }
                                else if (streamResponse is StreamingResponseErrorUpdate errorUpdate)
                                {
                                    throw new InvalidOperationException($"The stream has failed with the error: {errorUpdate.Message}");
                                }
                            }

                            // If function was called, submit the output and loop again
                            if (functionCalled)
                            {
                                evalResponseOptions.InputItems.Clear();
                                foreach (var inputItem in inputItems)
                                {
                                    evalResponseOptions.InputItems.Add(inputItem);
                                }
                            }
                            else
                            {
                                // No more function calls, break the loop
                                break;
                            }
                        }
                    }
                }
            }

            Console.WriteLine("Preparing evaluation dataset file...\n");
            var eval_options = new JsonSerializerOptions { WriteIndented = false };
            // evalArray is JsonArray
            var lines = evalArray.Select(node => node.ToJsonString(eval_options));
            await File.WriteAllLinesAsync(outputPath, lines);
            Console.WriteLine($"Evaluation dataset file created.\n");
        }
        else if (skipEvaluationDatasetSetup && !string.IsNullOrEmpty(proxyEvaluationDatasetFile))
        {
            fileName = proxyEvaluationDatasetFile;
            outputPath = Path.Combine(Directory.GetCurrentDirectory(), "EvaluationDataset", $"{fileName}.jsonl");
        }

        ClientResult<FileDataset> uploadedDatasetFile = await projectClient.Datasets.UploadFileAsync(fileName, "1", outputPath);
        Console.WriteLine($"Evaluation dataset file uploaded: {fileName}\n");

        object[] testingCriteria = [
            new {
                        type = "azure_ai_evaluator",
                        name = "groundedness",
                        evaluator_name = "builtin.groundedness",
                        initialization_parameters = new { deployment_name = modelDeployment },
                        data_mapping = new{context = "{{item.ground_truth}}", response = "{{item.response}}"},
                    },
                    new {
                        type = "azure_ai_evaluator",
                        name = "relevance",
                        evaluator_name = "builtin.relevance",
                        initialization_parameters = new { deployment_name = modelDeployment },
                        data_mapping = new { query = "{{item.query}}", response = "{{item.response}}"},
                    },
                    new {
                        type = "azure_ai_evaluator",
                        name = "fluency",
                        evaluator_name = "builtin.fluency",
                        initialization_parameters = new { deployment_name = modelDeployment },
                        data_mapping = new { response = "{{item.response}}"},
                    },
                    new {
                        type = "azure_ai_evaluator",
                        name = "coherence",
                        evaluator_name = "builtin.coherence",
                        data_mapping = new { query = "{{item.query}}", response = "{{item.response}}"},
                        initialization_parameters = new { deployment_name = modelDeployment},
                    },
                ];

        object dataSourceConfig = new
        {
            type = "custom",
            item_schema = new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string" },
                    response = new { type = "string" },
                    ground_truth = new { type = "string" },
                },
                required = new string[] { "query", "response", "ground_truth" }
            },
            include_sample_schema = true
        };

        BinaryData evaluationData = BinaryData.FromObjectAsJson(
            new
            {
                name = "Label model test with dataset ID",
                data_source_config = dataSourceConfig,
                testing_criteria = testingCriteria
            }
        );

        using BinaryContent evaluationDataContent = BinaryContent.Create(evaluationData);
        ClientResult evaluation = await projectClient.ProjectOpenAIClient.GetEvaluationClient().CreateEvaluationAsync(evaluationDataContent);
        Dictionary<string, string> fields = Helper.ParseClientResult(evaluation, ["name", "id"]);
        string evaluationName = fields["name"];
        string evaluationId = fields["id"];
        Console.WriteLine($"Evaluation created (id: {evaluationId}, name: {evaluationName})");

        object dataSource = new
        {
            type = "jsonl",
            source = new
            {
                type = "file_id",
                id = uploadedDatasetFile.Value.Id
            },
        };
        object runMetadata = new
        {
            team = "evaluator-experimentation",
            scenario = "dataset-with-id",
        };
        BinaryData runData = BinaryData.FromObjectAsJson(
            new
            {
                eval_id = evaluationId,
                name = $"Evaluation Run for dataset {uploadedDatasetFile.Value.Name}",
                metadata = runMetadata,
                data_source = dataSource
            }
        );

        using BinaryContent runDataContent = BinaryContent.Create(runData);
        ClientResult run = await projectClient.ProjectOpenAIClient.GetEvaluationClient().CreateEvaluationRunAsync(evaluationId: evaluationId, content: runDataContent);
        fields = Helper.ParseClientResult(run, ["id", "status"]);
        string runId = fields["id"];
        string runStatus = fields["status"];
        Console.WriteLine($"Evaluation run created (id: {runId})");

        while (runStatus != "failed" && runStatus != "completed")
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            run = await projectClient.ProjectOpenAIClient.GetEvaluationClient().GetEvaluationRunAsync(evaluationId: evaluationId, evaluationRunId: runId, options: new());
            runStatus = Helper.ParseClientResult(run, ["status"])["status"];
            Console.WriteLine($"Waiting for eval run to complete... current status: {runStatus}");
        }
        if (runStatus == "failed")
        {
            throw new InvalidOperationException($"Evaluation run failed with error: {Helper.GetErrorMessageOrEmpty(run)}");
        }

        Console.WriteLine("Evaluation run completed successfully!");
        Console.WriteLine($"Result Counts: {Helper.GetEvaluationResultsCounts(run)}");
        List<string> evaluationResults = await Helper.GetEvaluationResultsListAsync(client: projectClient.ProjectOpenAIClient.GetEvaluationClient(), evaluationId: evaluationId, evaluationRunId: runId);

        var path = Path.Combine(Directory.GetCurrentDirectory(), "EvaluationResults", $"evaluation_results_{fileName}.txt");
        Helper.SaveResultsToFile(path, evaluationResults);


    }

Console.WriteLine("Do you want to interact with the agent?\n");
Console.WriteLine("Type 'Yes' to start the conversation or 'No' to exit.");
string? startConversationInput = Console.ReadLine();
if (string.IsNullOrWhiteSpace(startConversationInput) ||
    !startConversationInput.Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Exiting the application. Goodbye!");
    return;
}

Console.WriteLine("Agent is being prepared, please wait...\n");
// Create the conversation to store responses.
ClientResult<ProjectConversation> conversationResult = projectClient.ProjectOpenAIClient.GetProjectConversationsClient().CreateProjectConversation();
CreateResponseOptions responseOptions = new CreateResponseOptions()
{
    Agent = new AgentReference("policyassistant", "1"),
    AgentConversationId = conversationResult.Value.Id,
    StreamingEnabled = true,
};

Console.WriteLine("Virtual HR Agent is ready! Type 'Quit' or 'Exit' to end the conversation.\n");

// Main conversation loop
while (true)
{
    Console.Write("You: ");
    string? userInput = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(userInput))
        continue;

    // Check for exit conditions
    if (userInput.Trim().Equals("Quit", StringComparison.OrdinalIgnoreCase) ||
        userInput.Trim().Equals("Exit", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Goodbye!");
        break;
    }

    // Send the user message and get streaming response
    responseOptions.InputItems.Clear();
    responseOptions.InputItems.Add(ResponseItem.CreateUserMessageItem(userInput));

    Console.Write("\nAgent: ");
    List<string> citations = new List<string>();
    // Loop to handle multiple function calls (e.g., multiple citations)
    while (true)
    {
        List<ResponseItem> inputItems = new List<ResponseItem>();
        bool functionCalled = false;

        foreach (StreamingResponseUpdate streamResponse in projectClient.ProjectOpenAIClient.GetResponsesClient().CreateResponseStreaming(responseOptions))
        {
            if (streamResponse is StreamingResponseOutputItemDoneUpdate itemDoneUpdate)
            {
                if (itemDoneUpdate.Item is FunctionCallResponseItem functionToolCall)
                {
                    var functionOutputItem = CitationBuilder.GetResolvedToolOutput(functionToolCall);

                    if (functionOutputItem != null)
                    {
                        inputItems.Add(functionOutputItem);
                        citations.Add(functionOutputItem.FunctionOutput);
                        functionCalled = true;
                    }
                }
            }

           // ParseResponse(streamResponse);

           if (streamResponse is StreamingResponseOutputTextDeltaUpdate textDelta)
            {
                Console.Write($"{textDelta.Delta}");
            }
            else if (streamResponse is StreamingResponseErrorUpdate errorUpdate)
            {
                throw new InvalidOperationException($"The stream has failed with the error: {errorUpdate.Message}");
            }
        }

        // If function was called, submit the output and loop again
        if (functionCalled)
        {
            responseOptions.InputItems.Clear();
            foreach (var inputItem in inputItems)
            {
                responseOptions.InputItems.Add(inputItem);
            }
        }
        else
        {
            // No more function calls, break the loop
            break;
        }
    }
    foreach (var citation in citations)
    {
        Console.WriteLine($"\n{citation}");
    }
}


 





