﻿using Fulfillment;
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
var PUBSUB_NAME = "pubsub-kafka";
var TOPIC_NAME = "processed-orders";

app.MapPost("/orders", [Topic("pubsub-kafka", "orders")] async (ILogger<Program> logger, [FromBody] Stream stream) =>
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

                logger.LogInformation($"Order {order.OrderId} fufillment started at '{DateTime.UtcNow}'");
                while (orderStatus != OrderStatus.Completed)
                {
                    // add a random delay between status transitions
                    await Task.Delay(Random.Shared.Next(10) * 1000);
                    // use very simple status transition rules
                    switch (orderStatus)
                    {
                        case OrderStatus.InProgress:
                            logger.LogInformation($"Order {order.OrderId} in progress at '{DateTime.UtcNow}'");
                            // We want to simulate a XX% of orders picking failed
                            if (Random.Shared.Next(101) <= 10) // 10% chance
                            {
                                orderStatus = OrderStatus.PickingFailed;
                            }
                            else
                                orderStatus = OrderStatus.Picked;
                            break;
                        case OrderStatus.Picked:
                            logger.LogInformation($"Order {order.OrderId} picked at '{DateTime.UtcNow}'");
                            orderStatus = OrderStatus.Dispatched;
                            break;
                        case OrderStatus.PickingFailed:
                            logger.LogInformation($"Order {order.OrderId} picking failed at '{DateTime.UtcNow}'");
                            orderStatus = OrderStatus.Completed;
                            break;
                        case OrderStatus.Dispatched:
                            logger.LogInformation($"Order {order.OrderId} dispatched at '{DateTime.UtcNow}'");
                            await client.PublishEventAsync(PUBSUB_NAME, TOPIC_NAME, 1);
                            // We want to simulate a XX% of orders returned by customers
                            if (Random.Shared.Next(101) <= 10) // 10% chance
                                orderStatus = OrderStatus.Returned;
                            else
                                orderStatus = OrderStatus.Completed;
                            break;
                        case OrderStatus.Returned:
                            logger.LogInformation($"Order {order.OrderId} returned at '{DateTime.UtcNow}'");
                            orderStatus = OrderStatus.Completed;
                            break;
                    }
                    // emit an otel span event after each order status transition
                    activity?.AddEvent(new("Fulfillment", DateTimeOffset.Now, new ActivityTagsCollection
                    {
                        { "app.order.id", order.OrderId },
                        { "app.order.items.count", order.Items.Count},
                        { "app.order.shipping.tracking.id", order.ShippingTrackingId},
                        { "app.order.shipping.address", order.ShippingAddress},
                        { "app.order.status", orderStatus }
                    }));
                }
                logger.LogInformation($"Order {order.OrderId} fufillment completed at '{DateTime.UtcNow}'");
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