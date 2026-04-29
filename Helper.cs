using System.ClientModel;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;
using OpenAI.Responses;
 
#pragma warning disable OPENAI001
 
public static class Helper
{
// Create a helper method ParseResponse to format streaming response output.
        // If the stream ends up in error state, it will throw an error.
        public static void ParseResponse(StreamingResponseUpdate streamResponse)
        {
            if (streamResponse is StreamingResponseOutputTextDeltaUpdate textDelta)
            {
                Console.Write($"{textDelta.Delta}");
            }
            else if (streamResponse is StreamingResponseErrorUpdate errorUpdate)
            {
                throw new InvalidOperationException($"The stream has failed with the error: {errorUpdate.Message}");
            }
        }
 
        public static Dictionary<string, string> ParseClientResult(ClientResult result, string[] expectedProperties)
        {
            Dictionary<string, string> results = [];
            Utf8JsonReader reader = new(result.GetRawResponse().Content.ToMemory().ToArray());
            JsonDocument document = JsonDocument.ParseValue(ref reader);
            foreach (JsonProperty prop in document.RootElement.EnumerateObject())
            {
                foreach (string key in expectedProperties)
                {
                    if (prop.NameEquals(Encoding.UTF8.GetBytes(key)) && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        results[key] = prop.Value.GetString();
                    }
                }
            }
            List<string> notFoundItems = expectedProperties.Where((key) => !results.ContainsKey(key)).ToList();
            if (notFoundItems.Count > 0)
            {
                StringBuilder sbNotFound = new();
                foreach (string value in notFoundItems)
                {
                    sbNotFound.Append($"{value}, ");
                }
                if (sbNotFound.Length > 2)
                {
                    sbNotFound.Remove(sbNotFound.Length - 2, 2);
                }
                throw new InvalidOperationException($"The next keys were not found in returned result: {sbNotFound}.");
            }
            return results;
        }
        public static string GetErrorMessageOrEmpty(ClientResult result)
        {
            string error = "";
            Utf8JsonReader reader = new(result.GetRawResponse().Content.ToMemory().ToArray());
            JsonDocument document = JsonDocument.ParseValue(ref reader);
            string code = default;
            string message = default;
            foreach (JsonProperty prop in document.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("error"u8) && prop.Value is JsonElement countsElement)
                {
                    foreach (JsonProperty errorNode in countsElement.EnumerateObject())
                    {
                        if (errorNode.Value.ValueKind == JsonValueKind.String)
                        {
                            if (errorNode.NameEquals("code"u8))
                            {
                                code = errorNode.Value.GetString();
                            }
                            else if (errorNode.NameEquals("message"u8))
                            {
                                message = errorNode.Value.GetString();
                            }
                        }
                    }
                }
            }
            if (!string.IsNullOrEmpty(message))
            {
                error = $"Message: {message}, Code: {code ?? "<None>"}";
            }
            return error;
        }
 
        public static string GetEvaluationResultsCounts(ClientResult result)
        {
            Utf8JsonReader reader = new(result.GetRawResponse().Content.ToMemory().ToArray());
            JsonDocument document = JsonDocument.ParseValue(ref reader);
            StringBuilder sbFormattedCounts = new("{\n");
            foreach (JsonProperty prop in document.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("result_counts"u8) && prop.Value is JsonElement countsElement)
                {
                    foreach (JsonProperty count in countsElement.EnumerateObject())
                    {
                        if (count.Value.ValueKind == JsonValueKind.Number)
                        {
                            sbFormattedCounts.Append($"    {count.Name}: {count.Value.GetInt32()}\n");
                        }
                    }
                }
            }
            sbFormattedCounts.Append('}');
            if (sbFormattedCounts.Length == 3)
            {
                throw new InvalidOperationException("The result does not contain the \"result_counts\" field.");
            }
            return sbFormattedCounts.ToString();
        }
 
        public static async Task<List<string>> GetEvaluationResultsListAsync(OpenAI.Evals.EvaluationClient client, string evaluationId, string evaluationRunId)
        {
            List<string> resultJsons = [];
            bool hasMore = false;
            do
            {
                ClientResult resultList = await client.GetEvaluationRunOutputItemsAsync(evaluationId: evaluationId, evaluationRunId: evaluationRunId, limit: null, order: "asc", after: default, outputItemStatus: default, options: new());
                Utf8JsonReader reader = new(resultList.GetRawResponse().Content.ToMemory().ToArray());
                JsonDocument document = JsonDocument.ParseValue(ref reader);
 
                foreach (JsonProperty topProperty in document.RootElement.EnumerateObject())
                {
                    if (topProperty.NameEquals("has_more"u8))
                    {
                        hasMore = topProperty.Value.GetBoolean();
                    }
                    else if (topProperty.NameEquals("data"u8))
                    {
                        if (topProperty.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement dataElement in topProperty.Value.EnumerateArray())
                            {
                                resultJsons.Add(dataElement.ToString());
                            }
                        }
                    }
                }
            } while (hasMore);
 
            return resultJsons;
        }
 
        public static void SaveResultsToFile(string fullPath, IEnumerable<string> evaluationResults)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                throw new ArgumentException("Path must not be empty.", nameof(fullPath));
 
            string directory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
            Directory.CreateDirectory(directory);
 
            int written = 0;
            int total = 0;
 
            // If evaluationResults is a collection that supports Count, get it; otherwise count first.
            if (evaluationResults is ICollection<string> col)
            {
                total = col.Count;
            }
            else
            {
                foreach (var _ in evaluationResults) total++;
                // Re-enumeration would be needed below; to keep it simple, convert to list
                evaluationResults = new List<string>(evaluationResults);
            }
 
            Console.Write($"Writing {total} items to file: {fullPath} ");
 
            try
            {
                using (var writer = new StreamWriter(fullPath, false)) // overwrite existing file
                {
                    writer.WriteLine($"OUTPUT ITEMS (Total: {total})");
                    writer.WriteLine("------------------------------------------------------------");
 
                    foreach (var result in evaluationResults)
                    {
                        writer.WriteLine(result);
                        written++;
                        Console.Write($"\rWriting {total} items to file: {fullPath} ({written}/{total})");
                    }
 
                    writer.WriteLine("------------------------------------------------------------");
                }
 
                // Final console message (keeps console clean)
                Console.WriteLine();
                Console.WriteLine($"Done. Wrote {written} items to: {fullPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"Error writing file: {ex.Message}");
            }
        }
 
}
 