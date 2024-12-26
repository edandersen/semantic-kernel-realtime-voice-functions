// Import packages
using System.ClientModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI.RealtimeConversation;

#pragma warning disable SKEXP0070
#pragma warning disable OPENAI002

var modelId = "gpt-4";
var apiKey = "";

var builder = Kernel.CreateBuilder().AddOpenAIChatCompletion(modelId, apiKey);

// Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
var realtimeConversationClient = new RealtimeConversationClient(
            model: "gpt-4o-realtime-preview",
            credential: new ApiKeyCredential(apiKey));

// Build the kernel
Kernel kernel = builder.Build();
var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();

kernel.Plugins.AddFromType<LightsPlugin>("Lights");



ConversationSessionOptions sessionOptions = new()
{
    Voice = ConversationVoice.Echo,
    InputAudioFormat = ConversationAudioFormat.Pcm16,
    OutputAudioFormat = ConversationAudioFormat.Pcm16,

    InputTranscriptionOptions = new()
    {
        Model = "whisper-1",
    },
};

// Add plugins/function from kernel as session tools.
foreach (var tool in ConvertFunctions(kernel))
{
    sessionOptions.Tools.Add(tool);
}

// If any tools are available, set tool choice to "auto".
if (sessionOptions.Tools.Count > 0)

{
    sessionOptions.ToolChoice = ConversationToolChoice.CreateAutoToolChoice();
}

// Start a new conversation session.
RealtimeConversationSession session = await realtimeConversationClient.StartConversationSessionAsync();

// Configure session with defined options.
await session.ConfigureSessionAsync(sessionOptions);

await session.AddItemAsync(ConversationItem.CreateSystemMessage(["You are a helpful digital assistant called ChadGPT. Your persona is that of a college " +
"frat kid. You can use the plugins and functions and tools provided. Respond in character."]));

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
GatherResponses(kernel, session);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

do
{

    Console.WriteLine("Press enter to start recording... Press Enter to stop.");
    Console.ReadLine();

    var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
    var inputAudioPath = $"input_{timestamp}.wav";
    var soxProcess = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "sox",
            Arguments = $"-d {inputAudioPath}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }

    };

    soxProcess.Start();

    Console.WriteLine("Recording... Press Enter to stop.");
    Console.ReadLine();

    soxProcess.Kill();
    soxProcess.WaitForExit();

    soxProcess = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "sox",
            Arguments = $"{inputAudioPath} -b 16 -r 24000 inputaudio.wav channels 1",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }

    };

    soxProcess.Start();
    soxProcess.WaitForExit();

    File.Delete(inputAudioPath);

    Stream inputAudioStream = File.OpenRead("inputaudio.wav");

    await session.SendInputAudioAsync(inputAudioStream);


} while (true);


static KernelArguments? DeserializeArguments(string argumentsString)
{
    var arguments = JsonSerializer.Deserialize<KernelArguments>(argumentsString);

    if (arguments is not null)
    {
        // Iterate over copy of the names to avoid mutating the dictionary while enumerating it
        var names = arguments.Names.ToArray();
        foreach (var name in names)
        {
            arguments[name] = arguments[name]?.ToString();
        }
    }

    return arguments;
}

static string? ProcessFunctionResult(object? functionResult)
{
    if (functionResult is string stringResult)
    {
        return stringResult;
    }

    return JsonSerializer.Serialize(functionResult);
}

static (string FunctionName, string? PluginName) ParseFunctionName(string fullyQualifiedName)
{
    const string FunctionNameSeparator = "-";

    string? pluginName = null;
    string functionName = fullyQualifiedName;

    int separatorPos = fullyQualifiedName.IndexOf(FunctionNameSeparator, StringComparison.Ordinal);
    if (separatorPos >= 0)
    {
        pluginName = fullyQualifiedName.AsSpan(0, separatorPos).Trim().ToString();
        functionName = fullyQualifiedName.AsSpan(separatorPos + FunctionNameSeparator.Length).Trim().ToString();
    }

    return (functionName, pluginName);
}

/// <summary>Helper method to convert Kernel plugins/function to realtime session conversation tools.</summary>
static IEnumerable<ConversationTool> ConvertFunctions(Kernel kernel)
{
    foreach (var plugin in kernel.Plugins)
    {
        var functionsMetadata = plugin.GetFunctionsMetadata();

        foreach (var metadata in functionsMetadata)
        {
            var toolDefinition = metadata.ToOpenAIFunction().ToFunctionDefinition();

            yield return new ConversationFunctionTool()
            {
                Name = toolDefinition.FunctionName,
                Description = toolDefinition.FunctionDescription,
                Parameters = toolDefinition.FunctionParameters
            };
        }
    }
}

static async Task GatherResponses(Kernel kernel, RealtimeConversationSession session)
{
    try {

    // Initialize dictionaries to store streamed audio responses and function arguments.
    Dictionary<string, MemoryStream> outputAudioStreamsById = [];
    Dictionary<string, StringBuilder> functionArgumentBuildersById = [];

    // Define a loop to receive conversation updates in the session.
    await foreach (ConversationUpdate update in session.ReceiveUpdatesAsync())
    {
        // Notification indicating the start of the conversation session.
        if (update is ConversationSessionStartedUpdate sessionStartedUpdate)
        {
            Console.WriteLine($"<<< Session started. ID: {sessionStartedUpdate.SessionId}");
            Console.WriteLine();
        }

        // Notification indicating the start of detected voice activity.
        if (update is ConversationInputSpeechStartedUpdate speechStartedUpdate)
        {
            Console.WriteLine(
                $"  -- Voice activity detection started at {speechStartedUpdate.AudioStartTime}");
        }

        // Notification indicating the end of detected voice activity.
        if (update is ConversationInputSpeechFinishedUpdate speechFinishedUpdate)
        {
            Console.WriteLine(
                $"  -- Voice activity detection ended at {speechFinishedUpdate.AudioEndTime}");
        }

        // Notification indicating the start of item streaming, such as a function call or response message.
        if (update is ConversationItemStreamingStartedUpdate itemStreamingStartedUpdate)
        {
            Console.WriteLine("  -- Begin streaming of new item");
            if (!string.IsNullOrEmpty(itemStreamingStartedUpdate.FunctionName))
            {
                Console.Write($"    {itemStreamingStartedUpdate.FunctionName}: ");
            }
        }

        // Notification about item streaming delta, which may include audio transcript, audio bytes, or function arguments.
        if (update is ConversationItemStreamingPartDeltaUpdate deltaUpdate)
        {
            Console.Write(deltaUpdate.AudioTranscript);
            Console.Write(deltaUpdate.Text);
            Console.Write(deltaUpdate.FunctionArguments);

            // Handle audio bytes.
            if (deltaUpdate.AudioBytes is not null)
            {
                if (!outputAudioStreamsById.TryGetValue(deltaUpdate.ItemId, out MemoryStream? value))
                {
                    value = new MemoryStream();
                    outputAudioStreamsById[deltaUpdate.ItemId] = value;
                }

                value.Write(deltaUpdate.AudioBytes);
            }

            // Handle function arguments.
            if (!functionArgumentBuildersById.TryGetValue(deltaUpdate.ItemId, out StringBuilder? arguments))
            {
                functionArgumentBuildersById[deltaUpdate.ItemId] = arguments = new();
            }

            if (!string.IsNullOrWhiteSpace(deltaUpdate.FunctionArguments))
            {
                arguments.Append(deltaUpdate.FunctionArguments);
            }
        }

        // Notification indicating the end of item streaming, such as a function call or response message.
        // At this point, audio transcript can be displayed on console, or a function can be called with aggregated arguments.
        if (update is ConversationItemStreamingFinishedUpdate itemStreamingFinishedUpdate)
        {
            Console.WriteLine();
            Console.WriteLine($"  -- Item streaming finished, item_id={itemStreamingFinishedUpdate.ItemId}");

            // If an item is a function call, invoke a function with provided arguments.
            if (itemStreamingFinishedUpdate.FunctionCallId is not null)
            {
                Console.WriteLine($"    + Responding to tool invoked by item: {itemStreamingFinishedUpdate.FunctionName}");

                // Parse function name.
                var (functionName, pluginName) = ParseFunctionName(itemStreamingFinishedUpdate.FunctionName);

                // Deserialize arguments.
                var argumentsString = functionArgumentBuildersById[itemStreamingFinishedUpdate.ItemId].ToString();
                var arguments = DeserializeArguments(argumentsString);

                // Create a function call content based on received data. 
                var functionCallContent = new FunctionCallContent(
                    functionName: functionName,
                    pluginName: pluginName,
                    id: itemStreamingFinishedUpdate.FunctionCallId,
                    arguments: arguments);

                // Invoke a function.
                var resultContent = await functionCallContent.InvokeAsync(kernel);

                // Create a function call output conversation item with function call result.
                ConversationItem functionOutputItem = ConversationItem.CreateFunctionCallOutput(
                    callId: itemStreamingFinishedUpdate.FunctionCallId,
                    output: ProcessFunctionResult(resultContent.Result));

                // Send function call output conversation item to the session, so the model can use it for further processing.
                await session.AddItemAsync(functionOutputItem);
            }
            // If an item is a response message, output it to the console.
            else if (itemStreamingFinishedUpdate.MessageContentParts?.Count > 0)
            {
                Console.Write($"    + [{itemStreamingFinishedUpdate.MessageRole}]: ");

                foreach (ConversationContentPart contentPart in itemStreamingFinishedUpdate.MessageContentParts)
                {
                    Console.Write(contentPart.AudioTranscript);
                }

                Console.WriteLine();
            }
        }

        // Notification indicating the completion of transcription from input audio.
        if (update is ConversationInputTranscriptionFinishedUpdate transcriptionCompletedUpdate)
        {
            Console.WriteLine();
            Console.WriteLine($"  -- User audio transcript: {transcriptionCompletedUpdate.Transcript}");
            Console.WriteLine();
        }

        // Notification about completed model response turn.
        if (update is ConversationResponseFinishedUpdate turnFinishedUpdate)
        {
            Console.WriteLine($"  -- Model turn generation finished. Status: {turnFinishedUpdate.Status}");

            // If the created session items contain a function name, it indicates a function call result has been provided,
            // and response updates can begin.
            if (turnFinishedUpdate.CreatedItems.Any(item => item.FunctionName?.Length > 0))
            {
                Console.WriteLine("  -- Ending client turn for pending tool responses");

                await session.StartResponseAsync();
            }
            // Otherwise, the model's response is provided, signaling that updates can be stopped.
            else
            {

                
            // Output the size of received audio data and dispose streams.
            foreach ((string itemId, Stream outputAudioStream) in outputAudioStreamsById)
            {
                Console.WriteLine($"Raw audio output for {itemId}: {outputAudioStream.Length} bytes");

                // Convert raw PCM data to WAV format
                var wavHeader = new byte[44];
                int sampleRate = 22050;
                short bitsPerSample = 16;
                short channels = 1;
                int byteRate = sampleRate * channels * (bitsPerSample / 8);
                int blockAlign = channels * (bitsPerSample / 8);
                int subChunk2Size = (int)outputAudioStream.Length;
                int chunkSize = 36 + subChunk2Size;

                // RIFF header
                Buffer.BlockCopy(Encoding.ASCII.GetBytes("RIFF"), 0, wavHeader, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(chunkSize), 0, wavHeader, 4, 4);
                Buffer.BlockCopy(Encoding.ASCII.GetBytes("WAVE"), 0, wavHeader, 8, 4);

                // fmt subchunk
                Buffer.BlockCopy(Encoding.ASCII.GetBytes("fmt "), 0, wavHeader, 12, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(16), 0, wavHeader, 16, 4); // Subchunk1Size (16 for PCM)
                Buffer.BlockCopy(BitConverter.GetBytes((short)1), 0, wavHeader, 20, 2); // AudioFormat (1 for PCM)
                Buffer.BlockCopy(BitConverter.GetBytes(channels), 0, wavHeader, 22, 2
                ); // NumChannels
                Buffer.BlockCopy(BitConverter.GetBytes(sampleRate), 0, wavHeader, 24, 4); // SampleRate
                Buffer.BlockCopy(BitConverter.GetBytes(byteRate), 0, wavHeader, 28, 4); // ByteRate
                Buffer.BlockCopy(BitConverter.GetBytes(blockAlign), 0, wavHeader, 32, 2); // BlockAlign
                Buffer.BlockCopy(BitConverter.GetBytes(bitsPerSample), 0, wavHeader, 34, 2); // BitsPerSample

                // data subchunk
                Buffer.BlockCopy(Encoding.ASCII.GetBytes("data"), 0, wavHeader, 36, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(subChunk2Size), 0, wavHeader, 40, 4);

                // Write WAV header to output stream
                outputAudioStream.Seek(0, SeekOrigin.Begin);
                outputAudioStream.Write(wavHeader, 0, wavHeader.Length);

                var outputAudioPath = $"outputaudio_{DateTime.Now:yyyyMMddHHmmss}.wav";
                using (var fileStream = new FileStream(outputAudioPath, FileMode.Create, FileAccess.Write))
                {
                    outputAudioStream.Seek(0, SeekOrigin.Begin);
                    await outputAudioStream.CopyToAsync(fileStream);
                    await fileStream.FlushAsync();
                    outputAudioStream.Dispose();
                    outputAudioStreamsById.Remove(itemId);
                }

                Console.WriteLine($"Output audio saved to {outputAudioPath}");

                var playProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "afplay",
                        Arguments = $"\"{outputAudioPath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                playProcess.Start();
                playProcess.WaitForExit();

            }

                //break;
            }


        }

        // Notification about error in conversation session.
        if (update is ConversationErrorUpdate errorUpdate)
        {
            Console.WriteLine();
            Console.WriteLine($"ERROR: {errorUpdate.Message}");
            //break;
        }
    }

    } catch (Exception e) {
        Console.WriteLine(e.Message);
    }
    Console.WriteLine("NOOOOO");
}