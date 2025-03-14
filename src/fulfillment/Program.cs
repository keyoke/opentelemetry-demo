using Fulfillment; 
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using CloudNative.CloudEvents.AspNetCore;
using Microsoft.AspNetCore.HttpLogging;
using Dapr;
using Dapr.Client;
using Oteldemo;

Console.WriteLine("Fulfillment service started");

Environment.GetEnvironmentVariables()
    .FilterRelevant()
    .OutputInOrder();

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.AddHttpLogging(httpLoggingOptions =>
{
    httpLoggingOptions.LoggingFields =
        HttpLoggingFields.RequestPath |
        HttpLoggingFields.RequestMethod |
        HttpLoggingFields.ResponseStatusCode |
        HttpLoggingFields.RequestBody;
});

var app = builder.Build();

app.UseHttpLogging();
app.UseRouting();

// Dapr configurations
app.UseCloudEvents();
app.MapSubscribeHandler();

string PUBSUB_NAME = "pubsub";
string TOPIC_NAME = "processed-orders";

app.MapPost("/orders", [Topic("pubsub", "orders")] async (ILogger<Program> logger, [FromBody] Stream stream) =>
{
    if (stream is not null &&
        stream is MemoryStream)
    {
        var bytes = ((MemoryStream)stream).ToArray();
        if (bytes.Length > 0)
        {
            try
            {
                var order = OrderResult.Parser.ParseFrom(bytes);

                Log.OrderReceivedMessage(logger, order);

                CancellationTokenSource source = new CancellationTokenSource();
                CancellationToken cancellationToken = source.Token;

                using var client = new DaprClientBuilder().Build();
                //Using Dapr SDK to publish a topic
                await client.PublishEventAsync(PUBSUB_NAME, TOPIC_NAME, 1, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Order parsing failed:");
            }
            return Results.Ok();
        }
    }

    return Results.BadRequest();
});
app.Run();