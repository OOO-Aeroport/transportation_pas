using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://26.132.135.106:5556"); // Укажите нужный URL

var app = builder.Build();

// Настройка статических файлов
app.UseStaticFiles();

// Перенаправление корневого URL на index.html
app.MapGet("/", (HttpContext context) =>
{
    context.Response.Redirect("/index.html");
    return Task.CompletedTask;
});

var httpClient = new HttpClient();

// URL-адреса серверов (все моки будут на одном порту)
const string groundControlUrl = "http://26.21.3.228:5555/dispatcher"; // УНО и диспетчер
const string boardServiceUrl = "http://26.125.155.211:5555"; // Борт
const string passengerServiceUrl = "http://26.49.89.37:5555"; // Пассажиры
const string unoServiceUrl = "http://26.53.143.176:5555"; // УНО
const string table = "http://26.228.200.110:5555"; // УНО

var passengerQueue = new ConcurrentQueue<PassengerRequest>();
var activeOrders = new ConcurrentDictionary<int, PassengerRequest>();

// Фоновый процесс для обработки заказов
_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            if (passengerQueue.TryDequeue(out var order))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        

                        if (order.passengers == null || order.passengers.Count == 0)
                        {
                            await ProcessDischargeOrderAsync(order);
                            Console.WriteLine($"Started processing order {order.orderId} (Type: discharge)");
                        }
                        else
                        {
                            await ProcessLoadOrderAsync(order);
                            Console.WriteLine($"Started processing order {order.orderId} (Type: load)");
                        }

                        Console.WriteLine($"Finished processing order {order.orderId} ");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error processing order {order.orderId}: {ex.Message}");
                        // Возвращаем заказ в очередь для повторной обработки
                        passengerQueue.Enqueue(order);
                    }
                });
            }
            else
            {
                // Если очередь пуста, делаем небольшую паузу, чтобы не нагружать CPU
                await Task.Delay(500);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in background task: {ex.Message}");
        }
    }
});

// Эндпоинт для выгрузки пассажиров
app.MapPost("/passengers-discharge", async (HttpContext context) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<PassengerRequest>();
        if (request == null)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Invalid request");
            return;
        }

        passengerQueue.Enqueue(request);
        activeOrders.TryAdd(request.orderId, request);

        Console.WriteLine($"[DISCHARGE] Order {request.orderId} accepted for flight {request.planeId}");
        await context.Response.WriteAsync("Order accepted");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in /passengers-discharge: {ex.Message}");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("Internal server error");
    }
});

// Эндпоинт для загрузки пассажиров
app.MapPost("/passengers-loading", async (HttpContext context) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<PassengerRequest>();
        if (request == null)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Invalid request");
            return;
        }

        passengerQueue.Enqueue(request);
        activeOrders.TryAdd(request.orderId, request);

        Console.WriteLine($"[LOAD] Order {request.orderId} accepted for flight {request.planeId}");
        await context.Response.WriteAsync("Order accepted");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in /passengers-loading: {ex.Message}");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("Internal server error");
    }
});

// Эндпоинт для получения всех активных заказов
app.MapGet("/active-order", (HttpContext context) =>
{
    try
    {
        // Получаем все активные заказы
        var activeOrdersList = activeOrders.Values.ToList();
        return Results.Json(activeOrdersList);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in /active-order: {ex.Message}");
        return Results.StatusCode(500);
    }
});

async Task ProcessDischargeOrderAsync(PassengerRequest order)
{
    try
    {
        Console.WriteLine($"[DISCHARGE] Processing order {order.orderId} for flight {order.planeId}");
        await TimeOut(10);

        var state = new MovementState { CurrentPoint = 299 };

        if (!await RequestGarageExitWithRetry("passenger_bus"))
        {
            Console.WriteLine($"[DISCHARGE] Failed to exit garage for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[DISCHARGE] Exited garage for order {order.orderId}");

        var routeToPlane = await GetRouteGarageToPlane(state.CurrentPoint, order.planeId);
        if (routeToPlane == null)
        {
            Console.WriteLine($"[DISCHARGE] Failed to get route to plane for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[DISCHARGE] Route to plane received for order {order.orderId}");

        if (!await MoveAlongRoute(routeToPlane, state, order.planeId, "g_to_plane"))
        {
            Console.WriteLine($"[DISCHARGE] Failed to move to plane for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[DISCHARGE] Arrived at plane for order {order.orderId}");

        if (!await NotifyBoardAboutPassengers(order.planeId, "out"))
        {
            Console.WriteLine($"[DISCHARGE] Failed to notify board about passengers unloading for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[DISCHARGE] Notified board about passengers unloading for order {order.orderId}");

        Console.WriteLine($"[DISCHARGE] Passengers out of the plane for order {order.orderId}");
        await TimeOut(50);
        

        if (!await ReportSuccessToUNO(order.orderId, "discharge"))
        {
            Console.WriteLine($"[DISCHARGE] Failed to report success to UNO for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[DISCHARGE] Success reported to UNO for order {order.orderId}");

        var routeToTerminal = await GetRoutePlaneToTerminal1(state.CurrentPoint);
        if (routeToTerminal == null)
        {
            Console.WriteLine($"[DISCHARGE] Failed to get route to terminal1 for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[DISCHARGE] Route to terminal1 received for order {order.orderId}");

        if (!await MoveAlongRoute(routeToTerminal, state, order.planeId, "terminal1"))
        {
            Console.WriteLine($"[DISCHARGE] Failed to move to terminal1 for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[DISCHARGE] Arrived at terminal1 for order {order.orderId}");

        Console.WriteLine($"[DISCHARGE] Passengers out of the car for order {order.orderId}");
        await TimeOut(50);
        

        var routeToGarage = await GetRouteToGarage(state.CurrentPoint);
        if (routeToGarage == null)
        {
            Console.WriteLine($"[DISCHARGE] Failed to get route to garage for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[DISCHARGE] Route to garage received for order {order.orderId}");

        if (!await MoveAlongRoute(routeToGarage, state, order.planeId, "garage"))
        {
            Console.WriteLine($"[DISCHARGE] Failed to return to garage for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[DISCHARGE] Returned to garage for order {order.orderId}");

        if (!await NotifyGarageFree(state.CurrentPoint))
        {
            Console.WriteLine($"[DISCHARGE] Failed to notify dispatcher about returning to garage for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[DISCHARGE] Dispatcher notified about returning to garage for order {order.orderId}");

        Console.WriteLine($"[DISCHARGE] Order {order.orderId} completed successfully");
        activeOrders.TryRemove(order.orderId, out _);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[DISCHARGE] Error processing order {order.orderId}: {ex.Message}");
    }
}

async Task ProcessLoadOrderAsync(PassengerRequest order)
{
    try
    {
        Console.WriteLine($"[LOAD] Processing order {order.orderId} for flight {order.planeId}");
        await TimeOut(10);

        var state = new MovementState { CurrentPoint = 299 };

        if (!await RequestGarageExitWithRetry("passenger_bus"))
        {
            Console.WriteLine($"[LOAD] Failed to exit garage for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[LOAD] Exited garage for order {order.orderId}");

        var routeToTerminal = await GetRouteGarageToTerminal2(state.CurrentPoint);
        if (routeToTerminal == null)
        {
            Console.WriteLine($"[LOAD] Failed to get route to terminal2 for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[LOAD] Route to terminal2 received for order {order.orderId}");

        if (!await MoveAlongRoute(routeToTerminal, state, order.planeId, "terminal2"))
        {
            Console.WriteLine($"[LOAD] Failed to move to terminal2 for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[LOAD] Arrived at terminal2 for order {order.orderId}");

        Console.WriteLine($"[LOAD] Passengers loaded into the car for order {order.orderId}");
        await TimeOut(50);

        if (!await NotifyPassengersAboutTransport(order.passengers))
        {
            Console.WriteLine($"[LOAD] Failed to notify passengers about transport for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[LOAD] Passengers notified about transport for order {order.orderId}");

        var routeToPlane = await GetRouteT2ToPlane(state.CurrentPoint, order.planeId);
        if (routeToPlane == null)
        {
            Console.WriteLine($"[LOAD] Failed to get route to plane for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[LOAD] Route to plane received for order {order.orderId}");

        if (!await MoveAlongRoute(routeToPlane, state, order.planeId, "t2_to_plane"))
        {
            Console.WriteLine($"[LOAD] Failed to move to plane for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[LOAD] Arrived at plane for order {order.orderId}");

        Console.WriteLine($"[LOAD] Passengers loaded into the plane for order {order.orderId}");
        await TimeOut(50);

        if (!await NotifyBoardAboutPassengersWithList(order.planeId, order.passengers))
        {
            Console.WriteLine($"[LOAD] Failed to notify board about passengers loading for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[LOAD] Notified board about passengers loading for order {order.orderId}");

        if (!await ReportSuccessToUNO(order.orderId, "loading"))
        {
            Console.WriteLine($"[LOAD] Failed to report success to UNO for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[LOAD] Success reported to UNO for order {order.orderId}");

        var routeToGarage = await GetRouteToGarage(state.CurrentPoint);
        if (routeToGarage == null)
        {
            Console.WriteLine($"[LOAD] Failed to get route to garage for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[LOAD] Route to garage received for order {order.orderId}");

        if (!await MoveAlongRoute(routeToGarage, state, order.planeId, "garage"))
        {
            Console.WriteLine($"[LOAD] Failed to return to garage for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[LOAD] Returned to garage for order {order.orderId}");

        if (!await NotifyGarageFree(state.CurrentPoint))
        {
            Console.WriteLine($"[LOAD] Failed to notify dispatcher about returning to garage for order {order.orderId}");
            return;
        }
        Console.WriteLine($"[LOAD] Dispatcher notified about returning to garage for order {order.orderId}");

        Console.WriteLine($"[LOAD] Order {order.orderId} completed successfully");
        activeOrders.TryRemove(order.orderId, out _);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[LOAD] Error processing order {order.orderId}: {ex.Message}");
    }
}

// Вспомогательные методы
async Task<bool> RequestGarageExitWithRetry(string vehicleType)
{
    while (true)
    {
        try
        {
            if (await RequestGarageExit(vehicleType))
            {
                Console.WriteLine($"Successfully exited garage for vehicle type {vehicleType}");
                return true;
            }
            Console.WriteLine($"Failed to exit garage. Retrying in 2 seconds...");
            await Task.Delay(2000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in RequestGarageExitWithRetry: {ex.Message}");
            await Task.Delay(2000);
        }
    }
}

async Task<bool> RequestGarageExit(string vehicleType)
{
    try
    {
        // Отправляем GET-запрос
        var response = await httpClient.GetAsync($"{groundControlUrl}/garage/{vehicleType}");

        // Проверяем, что ответ успешный
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Failed to request garage exit for {vehicleType}. Status code: {response.StatusCode}");
            return false;
        }

        // Читаем содержимое ответа как строку
        var content = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"Response content: {content}");

        // Десериализуем JSON в объект
        try
        {
            var result = JsonSerializer.Deserialize<bool>(content);
            return result;
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Failed to deserialize response: {ex.Message}");
            return false;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in RequestGarageExit: {ex.Message}");
        return false;
    }
}

async Task<List<int>> GetRouteGarageToPlane(int currentPoint, int planeId)
{
    try
    {
        var response = await httpClient.GetAsync($"{groundControlUrl}/plane/{currentPoint}/{planeId}");
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<List<int>>();
        }
        Console.WriteLine($"Failed to get route to plane from {currentPoint} for plane {planeId}");
        return null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in GetRouteToPlane: {ex.Message}");
        return null;
    }
}

async Task<List<int>> GetRouteT2ToPlane(int currentPoint, int planeId)
{
    try
    {
        var response = await httpClient.GetAsync($"{groundControlUrl}/plane/terminal2/{currentPoint}/{planeId}");
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<List<int>>();
        }
        Console.WriteLine($"Failed to get route to plane from {currentPoint} for plane {planeId}");
        return null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in GetRouteToPlane: {ex.Message}");
        return null;
    }
}

async Task<List<int>> GetRoutePlaneToTerminal1(int currentPoint)
{
    try
    {
        var response = await httpClient.GetAsync($"{groundControlUrl}/plane/{currentPoint}/terminal1");
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<List<int>>();
        }
        Console.WriteLine($"Failed to get route to terminal1 from {currentPoint}");
        return null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in GetRouteToTerminal1: {ex.Message}");
        return null;
    }
}

async Task<List<int>> GetRouteGarageToTerminal2(int currentPoint)
{
    try
    {
        var response = await httpClient.GetAsync($"{groundControlUrl}/{currentPoint}/terminal2");
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<List<int>>();
        }
        Console.WriteLine($"Failed to get route to terminal2 from {currentPoint}");
        return null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in GetRouteToTerminal2: {ex.Message}");
        return null;
    }
}

async Task<List<int>> GetRouteToGarage(int currentPoint)
{
    try
    {
        var response = await httpClient.GetAsync($"{groundControlUrl}/{currentPoint}/garage");
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<List<int>>();
        }
        Console.WriteLine($"Failed to get route to garage from {currentPoint}");
        return null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in GetRouteToGarage: {ex.Message}");
        return null;
    }
}

async Task<bool> MoveAlongRoute(List<int> route, MovementState state, int flightId, string routeType)
{
    state.CurrentRoute = route; // Сохраняем текущий маршрут в состоянии

    while (state.CurrentRoute.Count > 0)
    {
        int targetPoint = state.CurrentRoute[0]; // Берем первую точку маршрута

        // Запрос разрешения на передвижение
        if (await RequestMovement(state.CurrentPoint, targetPoint))
        {
            // Обновляем текущую точку
            state.CurrentPoint = targetPoint;
            Console.WriteLine($"Moved to point {state.CurrentPoint}");

            // Удаляем пройденную точку из маршрута
            state.CurrentRoute.RemoveAt(0);

            // Имитация времени движения
            await TimeOut(40);

            // Сбрасываем счетчик при успешном перемещении
            state.AttemptsWithoutMovement = 0;
        }
        else
        {
            Console.WriteLine($"Failed to move from {state.CurrentPoint} to {targetPoint}.");
            state.AttemptsWithoutMovement++;

            // Если попыток больше 5, запрашиваем новый маршрут
            if (state.AttemptsWithoutMovement >= 5)
            {
                Console.WriteLine($"Car stuck in traffic. Rebuilding route.");
                var newRoute = await GetNewRoute(state.CurrentPoint, flightId, routeType);
                if (newRoute == null)
                {
                    Console.WriteLine($"Failed to get a new route. Aborting movement.");
                    return false;
                }

                // Обновляем маршрут
                state.CurrentRoute = newRoute;
                state.AttemptsWithoutMovement = 0;
            }

            // Задержка перед повторной попыткой
            await Task.Delay(200);
        }

        // Проверяем, достигли ли конечной точки маршрута
        if (state.CurrentRoute.Count == 0 || state.CurrentPoint == state.CurrentRoute[state.CurrentRoute.Count - 1])
        {
            return true; // Маршрут завершен
        }
    }

    return true; // Маршрут завершен
}

async Task<bool> RequestMovementWithRetry(int from, int to, MovementState state, int flightId, string routeType)
{
    for (int i = 0; i < 5; i++) // Пытаемся 5 раз
    {
        try
        {
            if (await RequestMovement(from, to))
            {
                return true; // Если разрешение получено, возвращаем true
            }
            else
            {
                Console.WriteLine($"Failed to get permission to move from {from} to {to}. Attempt {i + 1}/5. Retrying...");
                await Task.Delay(1000); // Ждем 1 секунду перед повторной попыткой
            }
            
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in RequestMovementWithRetry: {ex.Message}");
            await Task.Delay(1000); // Ждем 1 секунду в случае ошибки
        }
    }

    // Если после 5 попыток разрешение не получено, запрашиваем новый маршрут
    Console.WriteLine($"Failed to get permission after 5 attempts. Requesting a new route...");

    var newRoute = await GetNewRoute(state.CurrentPoint, flightId, routeType);
    if (newRoute == null)
    {
        Console.WriteLine($"Failed to get a new route. Aborting movement.");
        return false; // Если новый маршрут не получен, возвращаем false
    }

    // Обновляем маршрут и продолжаем движение
    Console.WriteLine($"New route received. Continuing movement.");
    state.CurrentRoute = newRoute; // Обновляем маршрут в состоянии
    return await MoveAlongRoute(newRoute, state, flightId, routeType); // Продолжаем движение по новому маршруту
}

async Task<bool> RequestMovement(int from, int to)
{
    try
    {
        // Отправляем GET-запрос
        var response = await httpClient.GetAsync($"{groundControlUrl}/point/{from}/{to}");

        // Проверяем, что ответ успешный
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Failed to get permission to move from {from} to {to}. Status code: {response.StatusCode}");
            return false;
        }

        // Читаем содержимое ответа как строку
        var content = await response.Content.ReadAsStringAsync();

        // Десериализуем JSON в объект
        var result = JsonSerializer.Deserialize<bool>(content);

        // Возвращаем значение
        return result;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in RequestMovement: {ex.Message}");
        return false; // Возвращает false в случае ошибки
    }
}

async Task<List<int>> GetNewRoute(int currentPoint, int flightId, string routeType)
{
    try
    {
        Console.WriteLine("Getting new route");
        if (routeType == "g_to_plane")
        {
            return await GetRouteGarageToPlane(currentPoint, flightId);
        }
        else if (routeType == "terminal1")
        {
            return await GetRoutePlaneToTerminal1(currentPoint);
        }
        else if (routeType == "terminal2")
        {
            return await GetRouteGarageToTerminal2(currentPoint);
        }
        else if (routeType == "t2_to_plane")
        {
            return await GetRouteT2ToPlane(currentPoint, flightId);
        }
        else if (routeType == "garage")
        {
            return await GetRouteToGarage(currentPoint);
        }
        return null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in GetNewRoute: {ex.Message}");
        return null;
    }
}

async Task<bool> NotifyBoardAboutPassengers(int aircraftId, string action)
{
    try
    {
        var response = await httpClient.GetAsync($"{boardServiceUrl}/passengers_{action}/{aircraftId}");
        return response.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in NotifyBoardAboutPassengers: {ex.Message}");
        return false;
    }
}

async Task<bool> NotifyBoardAboutPassengersWithList(int aircraftId, List<Passenger> passengers)
{
    try
    {
        var json = JsonSerializer.Serialize(passengers);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync($"{boardServiceUrl}/board_passengers/{aircraftId}", content);
        return response.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in NotifyBoardAboutPassengersWithList: {ex.Message}");
        return false;
    }
}

async Task<bool> NotifyPassengersAboutTransport(List<Passenger> passengers)
{
    try
    {
        var json = JsonSerializer.Serialize(passengers);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync($"{passengerServiceUrl}/passenger/transporting", content);
        return response.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in NotifyPassengersAboutTransport: {ex.Message}");
        return false;
    }
}

async Task<bool> ReportSuccessToUNO(int orderId, string serviceName)
{
    try
    {
        var url = $"{unoServiceUrl}/uno/api/v1/order/successReport/{orderId}/passengers-{serviceName}";
        var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync(url, content);
        return response.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in ReportSuccessToUNO: {ex.Message}");
        return false;
    }
}

async Task<bool> NotifyGarageFree(int endPoint)
{
    try
    {
        var response = await httpClient.DeleteAsync($"{groundControlUrl}/garage/free/{endPoint}");
        return response.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in NotifyGarageFree: {ex.Message}");
        return false;
    }
}

async Task<bool> TimeOut(int time)
{
    try
    {
        var response = await httpClient.GetAsync($"{table}/dep-board/api/v1/time/timeout?timeout={time}");
        return response.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in TimeOut: {ex.Message}");
        return false;
    }
}

app.Run();

// Модели данных
public class PassengerRequest
{
    public int planeId { get; set; }
    public int orderId { get; set; }
    public List<Passenger> passengers { get; set; }
}

public class Passenger
{
    public int passenger_id { get; set; }
}

public class MovementState
{
    public int CurrentPoint { get; set; }
    public int AttemptsWithoutMovement { get; set; } = 0;
    public List<int> CurrentRoute { get; set; } // Текущий маршрут
}