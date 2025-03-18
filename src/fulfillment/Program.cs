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

var client = new DaprClientBuilder().Build();
var PUBSUB_NAME = "pubsub";
var TOPIC_NAME = "processed-orders";
var tasks = new List<Task>();

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

                // Very simple workflow to simulate an orders lifcycle
                var orderTask = new Task(() =>
                {
                    logger.LogInformation("Processing order {OrderId}", order.OrderId);
                    var orderId = order.OrderId;
                    var orderStatus =  OrderStatus.InProgress;
                    var taskCompleted = false;

                    while (!taskCompleted)
                    {
                        logger.LogInformation("Order parsing failed:");
                        switch (orderStatus)
                        {
                            case OrderStatus.InProgress:
                                logger.LogInformation("Order in progress ");
                                await Task.Delay(5000);
                                orderStatus = OrderStatus.Picked;
                                break;
                            case OrderStatus.Picked:
                                logger.LogInformation("Order picked");
                                await Task.Delay(5000);
                                orderStatus = OrderStatus.Dispatched;
                                break;
                            case OrderStatus.Dispatched:
                                logger.LogInformation("Order dispatched");
                                await Task.Delay(5000);
                                orderStatus = OrderStatus.Returned;
                                break;
                        }
                    }

                    // CancellationTokenSource source = new CancellationTokenSource();
                    // CancellationToken cancellationToken = source.Token;
                    // await client.PublishEventAsync(PUBSUB_NAME, TOPIC_NAME, 1, cancellationToken);
                });
                orderTask.Start();
                tasks.Add(orderTask);
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