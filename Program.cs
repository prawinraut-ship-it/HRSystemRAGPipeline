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



  