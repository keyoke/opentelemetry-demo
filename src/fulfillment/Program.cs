using Fulfillment;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
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
                var orderStatus = OrderStatus.InProgress;
                var activity = Activity.Current;

                while (orderStatus != OrderStatus.Completed)
                {
                    // add a random delay between status transitions
                    await Task.Delay(Random.Shared.Next(10) * 1000);
                    // use very simple status transition rules
                    switch (orderStatus)
                    {
                        case OrderStatus.InProgress:
                            logger.LogInformation($"Order {order.OrderId} in progress");
                            // We want to simulate a XX% of orders picking failed
                            if (Random.Shared.Next(101) <= 10) // 10% chance
                            {
                                orderStatus = OrderStatus.PickingFailed;
                            }
                            else
                                orderStatus = OrderStatus.Picked;
                            break;
                        case OrderStatus.Picked:
                            logger.LogInformation($"Order {order.OrderId} picked");
                            orderStatus = OrderStatus.Dispatched;
                            break;
                        case OrderStatus.PickingFailed:
                            logger.LogInformation($"Order {order.OrderId} picking failed");
                            orderStatus = OrderStatus.Completed;
                            break;
                        case OrderStatus.Dispatched:
                            logger.LogInformation($"Order {order.OrderId} dispatched");
                            await client.PublishEventAsync(PUBSUB_NAME, TOPIC_NAME, 1);
                            // We want to simulate a XX% of orders returned by customers
                            if (Random.Shared.Next(101) <= 10) // 10% chance
                                orderStatus = OrderStatus.Returned;
                            else
                                orderStatus = OrderStatus.Completed;
                            break;
                        case OrderStatus.Returned:
                            logger.LogInformation($"Order {order.OrderId} returned");
                            orderStatus = OrderStatus.Completed;
                            break;
                    }
                    logger.LogInformation($"Order {order.OrderId} completed");
                    // emit an otel span event after each order status transition
                    activity?.AddEvent(new("Fulfillment", DateTimeOffset.Now, new ActivityTagsCollection
                    {
                        { "app.order.id", order.OrderId },
                        { "app.order.status", orderStatus }
                    }));
                }
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


async ValueTask BuildWorkItem(ILogger<Program> logger, CancellationToken token, OrderResult order)
{
    var guid = Guid.NewGuid().ToString();

    logger.LogInformation("Queued Background Task {Guid} is starting.", guid);
    logger.LogInformation($"Order {order.OrderId} processing");

    var taskCompleted = false;
    var orderStatus = OrderStatus.InProgress;
    var activity = Activity.Current;

    while (!token.IsCancellationRequested &&
            !taskCompleted)
    {
        try
        {
            switch (orderStatus)
            {
                case OrderStatus.InProgress:
                    logger.LogInformation($"Order {order.OrderId} in progress");
                    await Task.Delay(Random.Shared.Next(10) * 1000, token);
                    orderStatus = OrderStatus.Picked;
                    // Create span event 
                    activity?.AddEvent(new("Fulfillment", DateTimeOffset.Now, new ActivityTagsCollection
                    {
                        { "OrderStatus", orderStatus }
                    }));
                    break;
                case OrderStatus.Picked:
                    logger.LogInformation($"Order {order.OrderId} picked");
                    await Task.Delay(Random.Shared.Next(10) * 1000, token);
                    orderStatus = OrderStatus.Dispatched;
                    // Create span event 
                    activity?.AddEvent(new("Fulfillment", DateTimeOffset.Now, new ActivityTagsCollection
                    {
                        { "OrderStatus", orderStatus }
                    }));
                    break;
                case OrderStatus.Dispatched:
                    logger.LogInformation($"Order {order.OrderId} dispatched");
                    await Task.Delay(Random.Shared.Next(10) * 1000, token);
                    orderStatus = OrderStatus.Returned;
                    // Create span event 
                    activity?.AddEvent(new("Fulfillment", DateTimeOffset.Now, new ActivityTagsCollection
                    {
                        { "OrderStatus", orderStatus }
                    }));
                    break;
                default:
                    logger.LogInformation($"Order {order.OrderId} completed");
                    // Create span event 
                    activity?.AddEvent(new("Fulfillment", DateTimeOffset.Now, new ActivityTagsCollection
                    {
                        { "OrderStatus", orderStatus }
                    }));
                    taskCompleted = true;
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Prevent throwing if the Delay is cancelled
        }
        logger.LogInformation("Queued Background Task {Guid} is running. ", guid);
    }

    if (taskCompleted)
    {
        logger.LogInformation("Queued Background Task {Guid} is complete.", guid);
    }
    else
    {
        logger.LogInformation("Queued Background Task {Guid} was cancelled.", guid);
    }
}

app.Run();