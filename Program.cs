using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using OpenAI.Responses;


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

var agentDefinition = new DeclarativeAgentDefinition(model: modelDeployment)
{
    Instructions = "You are an helpful assistant that reviews the user feedbacks",
    Tools = {  }
};

// Create the agent version in the project. Run it once only
var agentVersion = projectClient.AgentAdministrationClient.CreateAgentVersion(
    agentName: agentName,
    options: new ProjectsAgentVersionCreationOptions(agentDefinition)
);

// Start a new conversation with the agent
ProjectConversation conversation = await projectClient
            .ProjectOpenAIClient
            .GetProjectConversationsClient()
            .CreateProjectConversationAsync();
Console.WriteLine($"Conversation ID: {conversation.Id}");

ProjectResponsesClient responsesClient = projectClient
            .ProjectOpenAIClient
            .GetProjectResponsesClientForAgent(agentName, conversation);

ResponseResult response = await responsesClient.CreateResponseAsync(
    "It was very nice to visit the city last summer. The weather was perfect and the people were friendly.");
Console.WriteLine(response.GetOutputText());

// continue with the same conversation
ResponseResult followUp = await responsesClient.CreateResponseAsync(
    "I liked the food there as well, especially the local seafood dishes.");
Console.WriteLine(followUp.GetOutputText());