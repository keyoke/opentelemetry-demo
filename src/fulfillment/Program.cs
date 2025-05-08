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
builder.Services.AddHttpClient();
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

var daprEndpointApiMethod = Environment.GetEnvironmentVariable("FULFILLMENT_DAPR_ENDPOINT_API_METHOD");
if (string.IsNullOrEmpty(daprEndpointApiMethod))
{
    Console.WriteLine("FULFILLMENT_DAPR_ENDPOINT_API_METHOD environment variable is required.");
    Environment.Exit(1);
}
var cloudEventSource = Environment.GetEnvironmentVariable("FULFILLMENT_CLOUD_EVENT_SOURCE");
if (string.IsNullOrEmpty(cloudEventSource))
{
    Console.WriteLine("FULFILLMENT_CLOUD_EVENT_SOURCE environment variable is required.");
    Environment.Exit(1);
}
var cloudEventType = Environment.GetEnvironmentVariable("FULFILLMENT_CLOUD_EVENT_TYPE");
if (string.IsNullOrEmpty(cloudEventType))
{
    Console.WriteLine("FULFILLMENT_CLOUD_EVENT_TYPE environment variable is required.");
    Environment.Exit(1);
}

var client = new DaprClientBuilder().Build();

app.MapPost("/orders", [Topic("orders-pubsub", "orders")] async (ILogger<Program> logger, IHttpClientFactory httpClientFactory, [FromBody] Stream stream) =>
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
                            // We want to simulate a XX% of orders returned by customers
                            if (Random.Shared.Next(101) <= 10) // 10% chance
                                orderStatus = OrderStatus.Returned;
                            else
                            {
                                // Set the completed status
                                orderStatus = OrderStatus.Completed;
                                try
                                {
                                    // Finally trigger webhook callback
                                    logger.LogInformation("Initiate webhook callback.");
                                    using HttpClient httpClient = httpClientFactory.CreateClient();
                                    var cloudEvent = new
                                    {
                                        specversion = "1.0",
                                        id = Guid.NewGuid().ToString(),
                                        source = cloudEventSource,
                                        type = $"{cloudEventType}.Completed",
                                        time = DateTime.UtcNow.ToString("o"),
                                        data = new
                                        {
                                            orderId = order.OrderId,
                                            shippingTrackingId = order.ShippingTrackingId,
                                            streetAddress = order.ShippingAddress.StreetAddress,
                                            city = order.ShippingAddress.City,
                                            state = order.ShippingAddress.State,
                                            country = order.ShippingAddress.Country,
                                            zipCode = order.ShippingAddress.ZipCode,
                                            status = orderStatus.ToString()
                                        }
                                    };
                                    var response = await httpClient.PostAsJsonAsync($"http://localhost:3500/v1.0/invoke/fulfillment-endpoint/method/{daprEndpointApiMethod}", cloudEvent);
                                    if (!response.IsSuccessStatusCode)
                                    {
                                        logger.LogError($"Webhook callback failed with status code {response.StatusCode}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger.LogError("Webhook callback failed:", ex);
                                }
                            }
                            break;
                        case OrderStatus.Returned:
                            logger.LogInformation($"Order {order.OrderId} returned at '{DateTime.UtcNow}'");
                            orderStatus = OrderStatus.Completed;
                            break;
                    }
                    var eventData = new ActivityEvent(
                         "Fulfillment",
                         DateTimeOffset.Now,
                         new ActivityTagsCollection
                     {
                         { "app.order.id", order.OrderId },
                         { "app.order.items.count", order.Items.Count},
                         { "app.order.shipping.tracking.id", order.ShippingTrackingId},
                         { "app.order.shipping.address", order.ShippingAddress},
                         { "app.order.status", orderStatus }
                     });

                    // emit an otel span event after each order status transition
                    activity?.AddEvent(eventData);

                    // also log the event
                    logger.LogInformation("Order fulfillment step executed.", eventData);
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