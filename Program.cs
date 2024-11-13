using System;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace TooLongToRead
{
    class Program
    {
        #pragma warning disable SKEXP0001
        #pragma warning disable SKEXP0010

        static async Task Main(string[] args)
        {
            EnvReader.Load(".env");

            using ILoggerFactory loggerFactory =
                LoggerFactory.Create(builder =>
                    builder.AddSimpleConsole(options =>
                    {
                        options.IncludeScopes = true;
                        options.SingleLine = true;
                        options.TimestampFormat = "HH:mm:ss ";
                    })
                    .AddDebug()
                    .AddFilter("Microsoft", LogLevel.Warning)
                    .AddFilter("Microsoft.SemanticKernel", LogLevel.Error)
                    .SetMinimumLevel(LogLevel.Error));

            var logger = loggerFactory.CreateLogger<Program>();

            logger.LogTrace("Getting environment variables");

            // Get Environment Variables
            var azure_openai_modelId = Environment.GetEnvironmentVariable("MODEL_ID") ?? throw new ArgumentNullException("MODEL_ID");
            var azure_openai_endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? throw new ArgumentNullException("AZURE_OPENAI_ENDPOINT");
            var azure_openai_apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_KEY") ?? throw new ArgumentNullException("AZURE_OPENAI_KEY");
            var speech_region = Environment.GetEnvironmentVariable("SPEECH_REGION") ?? throw new ArgumentNullException("SPEECH_REGION");
            var speech_key = Environment.GetEnvironmentVariable("SPEECH_KEY") ?? throw new ArgumentNullException("SPEECH_KEY");

            logger.LogTrace("Create semantic kernel");

            // Create a kernel with Azure OpenAI chat completion
            var builder = Kernel.CreateBuilder()
                .AddAzureOpenAIChatCompletion(azure_openai_modelId, azure_openai_endpoint, azure_openai_apiKey);

            builder.Services.AddSingleton(loggerFactory);
            
            // Build the kernel
            var kernel = builder.Build();

            var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();

            logger.LogTrace("Importing plugins");
            kernel.Plugins.AddFromType<ArticleFetcher>("ArticleFetcher");

            // Enable planning
            OpenAIPromptExecutionSettings openAIPromptExecutionSettings = new() 
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
            };

            var title_prompt = """
                {{$input}}
        
                Create a title for this article and make sure to be brief.
            """;
            var titleGeneratorFunction = kernel.CreateFunctionFromPrompt(title_prompt, openAIPromptExecutionSettings);

            logger.LogTrace("Setting up speech synthesis");

            var speechConfig = SpeechConfig.FromSubscription(speech_key, speech_region);   
            speechConfig.SpeechSynthesisLanguage = "en-US";
            speechConfig.SpeechSynthesisVoiceName = "en-US-BrandonMultilingualNeural";
            //speechConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Riff24Khz16BitMonoPcm);
            speechConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Audio24Khz48KBitRateMonoMp3);

            var fileName = Path.Combine(Directory.GetCurrentDirectory(), $"{DateTime.Now.ToString("yyyy-MM-dd")} - daily notes.md");

            if (!File.Exists(fileName))
            {
                await File.WriteAllTextAsync(fileName, $"# {DateTime.Now.ToString("yyyy-MM-dd")} - daily notes\n\n");
            }
        
            // Create a history store the conversation
            var history = new ChatHistory();

            // Initiate a back-and-forth chat
            string? userInput;
            do {
                // Collect user input
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write("User > ");
                userInput = Console.ReadLine();
                Console.ResetColor();

                // Add user input
                history.AddUserMessage(userInput ?? string.Empty);

                string fullMessage = string.Empty;

                // Get the response from the AI
                logger.LogTrace("Getting streaming chat message contents");

                var response = chatCompletionService.GetStreamingChatMessageContentsAsync(
                    chatHistory: history,
                    executionSettings: openAIPromptExecutionSettings,
                    kernel: kernel
                );

                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("Assistant > ");
                Console.ResetColor();
                await foreach (var chunk in response)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write(chunk);
                    Console.ResetColor();

                    fullMessage += chunk;
                }
                Console.ResetColor();

                Console.WriteLine();

                var title = await kernel.InvokeAsync(titleGeneratorFunction, new KernelArguments { ["input"] = fullMessage });

                logger.LogTrace("Writing to daily notes file.");
                await File.AppendAllTextAsync(fileName, $"## {title}\n\n");
                await File.AppendAllTextAsync(fileName, $"{userInput}\n\n");
                await File.AppendAllTextAsync(fileName, $"{fullMessage}\n\n");

                logger.LogTrace("Converting text to speech");
                using (var speechSynthesizer = new SpeechSynthesizer(speechConfig, null))
                {
                    speechSynthesizer.SynthesisStarted += (s, e) => logger.LogInformation("Synthesis started.");
                    speechSynthesizer.SynthesisCompleted += (s, e) => logger.LogInformation("Synthesis completed.");
                    speechSynthesizer.SynthesisCanceled += (s, e) => logger.LogTrace("Synthesis canceled.");
                    speechSynthesizer.Synthesizing += (s, e) => logger.LogTrace("Synthesizing.");

                    var speechSynthesisResult = await speechSynthesizer.SpeakTextAsync(fullMessage);
                    
                    using var stream = AudioDataStream.FromResult(speechSynthesisResult);
                    //await stream.SaveToWaveFileAsync("output.wav");
                    await stream.SaveToWaveFileAsync("output.mp3");
                    
                    using (var waveOut = new NAudio.Wave.WaveOutEvent())
                    using (var audioFileReader = new NAudio.Wave.AudioFileReader("output.mp3"))
                    {
                        waveOut.Init(audioFileReader);
                        waveOut.Play();

                        while (waveOut.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                        {
                            if (Console.KeyAvailable)
                            {
                                var key = Console.ReadKey(intercept: true);
                                if (key.Key == ConsoleKey.C)
                                {
                                    waveOut.Stop();
                                    break;
                                }
                            }
                            await Task.Delay(100);
                        }
                    }

                    var finalFileName = Path.Combine(Directory.GetCurrentDirectory(), $"{DateTime.Now.ToString("yyyy-MM-dd")} - daily notes.mp3");
                    if (File.Exists(finalFileName))
                    {
                        using (var reader1 = new NAudio.Wave.AudioFileReader(finalFileName))
                        using (var reader2 = new NAudio.Wave.AudioFileReader("output.mp3"))
                        using (var waveFileWriter = new NAudio.Wave.WaveFileWriter("temp_final.mp3", reader1.WaveFormat))
                        {
                            reader1.CopyTo(waveFileWriter);
                            reader2.CopyTo(waveFileWriter);
                        }
                        File.Delete(finalFileName);
                        File.Move("temp_final.mp3", finalFileName);
                    }
                    else
                    {
                        File.Move("output.mp3", finalFileName);
                    }
                }

                // Add the message from the agent to the chat history
                history.AddMessage(AuthorRole.Assistant, fullMessage ?? string.Empty);
            } while (userInput is not null);
        }
    }
}