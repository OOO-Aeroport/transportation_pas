using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://26.132.135.106:5556"); // Укажите нужный URL

var app = builder.Build();

var httpClient = new HttpClient();

// URL-адреса серверов (все моки будут на одном порту)
const string groundControlUrl = "http://26.21.3.228:5555/dispatcher"; // УНО и диспетчер
const string boardServiceUrl = "http://26.125.155.211:5555"; // Борт
const string passengerServiceUrl = "http://26.49.89.37:5555"; // Пассажиры
const string unoServiceUrl = "http://26.132.135.106:5555"; // УНО

var passengerQueue = new ConcurrentQueue<PassengerRequest>();
var activeOrders = new ConcurrentDictionary<int, PassengerRequest>();

// Фоновый процесс для обработки заказов
_ = Task.Run(async () =>
{
    while (true)
    {
        if (passengerQueue.TryDequeue(out var order))
        {
            if (order.Passengers == null || order.Passengers.Count == 0)
            {
                _ = Task.Run(() => ProcessDischargeOrderAsync(order));
            }
            else
            {
                _ = Task.Run(() => ProcessLoadOrderAsync(order));
            }
        }
        await Task.Delay(1000); // Проверяем очередь каждую секунду
    }
});

// Эндпоинт для выгрузки пассажиров
app.MapPost("/passengers-discharge", async (HttpContext context) =>
{
    var request = await context.Request.ReadFromJsonAsync<PassengerRequest>();
    if (request == null)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Invalid request");
        return;
    }

    passengerQueue.Enqueue(request);
    activeOrders.TryAdd(request.OrderId, request);

    await context.Response.WriteAsync("Order accepted");
});

// Эндпоинт для загрузки пассажиров
app.MapPost("/passengers-loading", async (HttpContext context) =>
{
    var request = await context.Request.ReadFromJsonAsync<PassengerRequest>();
    if (request == null)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Invalid request");
        return;
    }

    passengerQueue.Enqueue(request);
    activeOrders.TryAdd(request.OrderId, request);

    await context.Response.WriteAsync("Order accepted");
});

async Task ProcessDischargeOrderAsync(PassengerRequest order)
{
    Console.WriteLine($"[DISCHARGE] Processing order {order.OrderId} for flight {order.PlaneId}");
    await Task.Delay(5000);

    // Текущая точка (начальная точка - 299, гараж)
    var state = new MovementState { CurrentPoint = 299 };

    // 1. Запрос на выезд из гаража с повторными попытками
    if (!await RequestGarageExitWithRetry("passenger_bus"))
    {
        Console.WriteLine($"[DISCHARGE] Failed to exit garage for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[DISCHARGE] Exited garage for order {order.OrderId}");

    // 2. Получение маршрута до самолета
    var routeToPlane = await GetRouteToPlane(state.CurrentPoint, order.PlaneId);
    if (routeToPlane == null)
    {
        Console.WriteLine($"[DISCHARGE] Failed to get route to plane for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[DISCHARGE] Route to plane received for order {order.OrderId}");

    // 3-5. Движение к самолету
    if (!await MoveAlongRoute(routeToPlane, state, order.PlaneId, "plane"))
    {
        Console.WriteLine($"[DISCHARGE] Failed to move to plane for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[DISCHARGE] Arrived at plane for order {order.OrderId}");

    // 6. Уведомление борта о выгрузке пассажиров
    if (!await NotifyBoardAboutPassengers(order.PlaneId, "out"))
    {
        Console.WriteLine($"[DISCHARGE] Failed to notify board about passengers unloading for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[DISCHARGE] Notified board about passengers unloading for order {order.OrderId}");

    // Имитация задержки для выгрузки пассажиров
    Console.WriteLine($"[DISCHARGE] Passengers out of the plane for order {order.OrderId}");
    await Task.Delay(5000); // 5 секунд задержки

    // 7. Отправка отчета в УНО
    if (!await ReportSuccessToUNO(order.OrderId, "passenger-service"))
    {
        Console.WriteLine($"[DISCHARGE] Failed to report success to UNO for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[DISCHARGE] Success reported to UNO for order {order.OrderId}");

    // 8. Получение маршрута до терминала
    var routeToTerminal = await GetRouteToTerminal1(state.CurrentPoint);
    if (routeToTerminal == null)
    {
        Console.WriteLine($"[DISCHARGE] Failed to get route to terminal1 for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[DISCHARGE] Route to terminal1 received for order {order.OrderId}");

    // 9-10. Движение к терминалу
    if (!await MoveAlongRoute(routeToTerminal, state, order.PlaneId, "terminal1"))
    {
        Console.WriteLine($"[DISCHARGE] Failed to move to terminal1 for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[DISCHARGE] Arrived at terminal1 for order {order.OrderId}");

    // Имитация задержки для выгрузки пассажиров
    Console.WriteLine($"[DISCHARGE] Passengers out of the car for order {order.OrderId}");
    await Task.Delay(5000); // 5 секунд задержки

    // 11. Получение маршрута до гаража
    var routeToGarage = await GetRouteToGarage(state.CurrentPoint);
    if (routeToGarage == null)
    {
        Console.WriteLine($"[DISCHARGE] Failed to get route to garage for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[DISCHARGE] Route to garage received for order {order.OrderId}");

    // 12-14. Движение в гараж
    if (!await MoveAlongRoute(routeToGarage, state, order.PlaneId, "garage"))
    {
        Console.WriteLine($"[DISCHARGE] Failed to return to garage for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[DISCHARGE] Returned to garage for order {order.OrderId}");

    // 15. Уведомление диспетчера о возвращении в гараж
    if (!await NotifyGarageFree(state.CurrentPoint))
    {
        Console.WriteLine($"[DISCHARGE] Failed to notify dispatcher about returning to garage for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[DISCHARGE] Dispatcher notified about returning to garage for order {order.OrderId}");

    Console.WriteLine($"[DISCHARGE] Order {order.OrderId} completed successfully");
    activeOrders.TryRemove(order.OrderId, out _);
}

async Task ProcessLoadOrderAsync(PassengerRequest order)
{
    Console.WriteLine($"[LOAD] Processing order {order.OrderId} for flight {order.PlaneId}");
    await Task.Delay(5000);

    // Текущая точка (начальная точка - 299, гараж)
    var state = new MovementState { CurrentPoint = 299 };

    // 1. Запрос на выезд из гаража с повторными попытками
    if (!await RequestGarageExitWithRetry("passenger_bus"))
    {
        Console.WriteLine($"[LOAD] Failed to exit garage for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[LOAD] Exited garage for order {order.OrderId}");

    // 2. Получение маршрута до терминала
    var routeToTerminal = await GetRouteToTerminal2(state.CurrentPoint);
    if (routeToTerminal == null)
    {
        Console.WriteLine($"[LOAD] Failed to get route to terminal2 for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[LOAD] Route to terminal2 received for order {order.OrderId}");

    // 3-5. Движение к терминалу
    if (!await MoveAlongRoute(routeToTerminal, state, order.PlaneId, "terminal2"))
    {
        Console.WriteLine($"[LOAD] Failed to move to terminal2 for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[LOAD] Arrived at terminal2 for order {order.OrderId}");

    // Имитация задержки для загрузки пассажиров
    Console.WriteLine($"[LOAD] Passengers loaded into the car for order {order.OrderId}");
    await Task.Delay(5000); // 5 секунд задержки

    // 6. Уведомление блока пассажиров о транспортировке
    if (!await NotifyPassengersAboutTransport(order.Passengers))
    {
        Console.WriteLine($"[LOAD] Failed to notify passengers about transport for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[LOAD] Passengers notified about transport for order {order.OrderId}");

    // 7. Получение маршрута до самолета
    var routeToPlane = await GetRouteToPlane(state.CurrentPoint, order.PlaneId);
    if (routeToPlane == null)
    {
        Console.WriteLine($"[LOAD] Failed to get route to plane for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[LOAD] Route to plane received for order {order.OrderId}");

    // 8-9. Движение к самолету
    if (!await MoveAlongRoute(routeToPlane, state, order.PlaneId, "plane"))
    {
        Console.WriteLine($"[LOAD] Failed to move to plane for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[LOAD] Arrived at plane for order {order.OrderId}");

    // Имитация задержки для загрузки пассажиров
    Console.WriteLine($"[LOAD] Passengers loaded into the plane for order {order.OrderId}");
    await Task.Delay(5000); // 5 секунд задержки

    // 10. Уведомление борта о загрузке пассажиров
    if (!await NotifyBoardAboutPassengersWithList(order.PlaneId, order.Passengers))
    {
        Console.WriteLine($"[LOAD] Failed to notify board about passengers loading for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[LOAD] Notified board about passengers loading for order {order.OrderId}");

    // 11. Отправка отчета в УНО
    if (!await ReportSuccessToUNO(order.OrderId, "passenger-service"))
    {
        Console.WriteLine($"[LOAD] Failed to report success to UNO for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[LOAD] Success reported to UNO for order {order.OrderId}");

    // 12. Получение маршрута до гаража
    var routeToGarage = await GetRouteToGarage(state.CurrentPoint);
    if (routeToGarage == null)
    {
        Console.WriteLine($"[LOAD] Failed to get route to garage for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[LOAD] Route to garage received for order {order.OrderId}");

    // 13-15. Движение в гараж
    if (!await MoveAlongRoute(routeToGarage, state, order.PlaneId, "garage"))
    {
        Console.WriteLine($"[LOAD] Failed to return to garage for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[LOAD] Returned to garage for order {order.OrderId}");

    // 16. Уведомление диспетчера о возвращении в гараж
    if (!await NotifyGarageFree(state.CurrentPoint))
    {
        Console.WriteLine($"[LOAD] Failed to notify dispatcher about returning to garage for order {order.OrderId}");
        return;
    }
    Console.WriteLine($"[LOAD] Dispatcher notified about returning to garage for order {order.OrderId}");

    Console.WriteLine($"[LOAD] Order {order.OrderId} completed successfully");
    activeOrders.TryRemove(order.OrderId, out _);
}

// Вспомогательные методы
async Task<bool> RequestGarageExitWithRetry(string vehicleType)
{
    for (int i = 0; i < 5; i++)
    {
        if (await RequestGarageExit(vehicleType))
        {
            return true;
        }
        await Task.Delay(2000); // Повторная попытка через 2 секунды
    }
    return false;
}

async Task<bool> RequestGarageExit(string vehicleType)
{
    var response = await httpClient.GetAsync($"{groundControlUrl}/garage/{vehicleType}");
    return response.IsSuccessStatusCode;
}

async Task<List<int>> GetRouteToPlane(int currentPoint, int planeId)
{
    var response = await httpClient.GetAsync($"{groundControlUrl}/plane/{currentPoint}/{planeId}");
    if (response.IsSuccessStatusCode)
    {
        return await response.Content.ReadFromJsonAsync<List<int>>();
    }
    return null;
}

async Task<List<int>> GetRouteToTerminal1(int currentPoint)
{
    var response = await httpClient.GetAsync($"{groundControlUrl}/{currentPoint}/terminal1");
    if (response.IsSuccessStatusCode)
    {
        return await response.Content.ReadFromJsonAsync<List<int>>();
    }
    return null;
}

async Task<List<int>> GetRouteToTerminal2(int currentPoint)
{
    var response = await httpClient.GetAsync($"{groundControlUrl}/{currentPoint}/terminal2");
    if (response.IsSuccessStatusCode)
    {
        return await response.Content.ReadFromJsonAsync<List<int>>();
    }
    return null;
}

async Task<List<int>> GetRouteToGarage(int currentPoint)
{
    var response = await httpClient.GetAsync($"{groundControlUrl}/{currentPoint}/garage");
    if (response.IsSuccessStatusCode)
    {
        return await response.Content.ReadFromJsonAsync<List<int>>();
    }
    return null;
}

async Task<bool> MoveAlongRoute(List<int> route, MovementState state, int flightId, string routeType)
{
    int lastPoint = state.CurrentPoint;
    int newRouteAttempts = 0; // Счетчик запросов нового маршрута

    while (true)
    {
        foreach (var targetPoint in route)
        {
            // Запрос разрешения на передвижение
            if (await RequestMovementWithRetry(state.CurrentPoint, targetPoint, state))
            {
                // Обновляем текущую точку
                state.CurrentPoint = targetPoint;
                Console.WriteLine($"Moved to point {state.CurrentPoint}");
                // Имитация времени движения
                await Task.Delay(500);

                // Сбрасываем счетчик при успешном перемещении
                state.AttemptsWithoutMovement = 0;
            }
            else
            {
                Console.WriteLine($"Failed to get permission to move from {state.CurrentPoint} to {targetPoint}");

                // Если не двигаемся, увеличиваем счетчик
                if (state.CurrentPoint == lastPoint)
                {
                    state.AttemptsWithoutMovement++;
                    if (state.AttemptsWithoutMovement >= 5)
                    {
                        Console.WriteLine($"Stuck at point {state.CurrentPoint}. Requesting new route...");
                        var newRoute = await GetNewRoute(state.CurrentPoint, flightId, routeType);
                        if (newRoute == null)
                        {
                            Console.WriteLine($"Failed to get new route. Aborting.");
                            return false;
                        }

                        // Увеличиваем счетчик запросов нового маршрута
                        newRouteAttempts++;
                        if (newRouteAttempts >= 3) // Лимит запросов нового маршрута
                        {
                            Console.WriteLine($"Too many attempts to get new route. Aborting.");
                            return false;
                        }

                        // Продолжаем движение по новому маршруту
                        route = newRoute;
                        break; // Выходим из цикла foreach и начинаем заново с новым маршрутом
                    }
                }
                else
                {
                    // Если текущая точка изменилась, сбрасываем счетчик
                    state.AttemptsWithoutMovement = 0;
                }

                lastPoint = state.CurrentPoint;
            }
        }

        // Если все точки маршрута пройдены, возвращаем true
        if (state.CurrentPoint == route[route.Count - 1])
        {
            return true;
        }
    }
}

async Task<bool> RequestMovementWithRetry(int from, int to, MovementState state)
{
    for (int i = 0; i < 5; i++)
    {
        if (await RequestMovement(from, to))
        {
            return true;
        }
        await Task.Delay(1000); // Повторная попытка через 1 секунду
    }
    return false;
}

async Task<bool> RequestMovement(int from, int to)
{
    var response = await httpClient.GetAsync($"{groundControlUrl}/point/{from}/{to}");
    return response.IsSuccessStatusCode;
}

async Task<List<int>> GetNewRoute(int currentPoint, int flightId, string routeType)
{
    Console.WriteLine("Getting new route");
    if (routeType == "plane")
    {
        return await GetRouteToPlane(currentPoint, flightId);
    }
    else if (routeType == "terminal1")
    {
        return await GetRouteToTerminal1(currentPoint);
    }
    else if (routeType == "terminal2")
    {
        return await GetRouteToTerminal2(currentPoint);
    }
    else if (routeType == "garage")
    {
        return await GetRouteToGarage(currentPoint);
    }
    return null;
}

async Task<bool> NotifyBoardAboutPassengers(int aircraftId, string action)
{
    var response = await httpClient.GetAsync($"{boardServiceUrl}/passengers_{action}/{aircraftId}");
    return response.IsSuccessStatusCode;
}

async Task<bool> NotifyBoardAboutPassengersWithList(int aircraftId, List<Passenger> passengers)
{
    var response = await httpClient.PostAsJsonAsync($"{boardServiceUrl}/board_passengers/{aircraftId}", passengers);
    return response.IsSuccessStatusCode;
}

async Task<bool> NotifyPassengersAboutTransport(List<Passenger> passengers)
{
    var response = await httpClient.PostAsJsonAsync($"{passengerServiceUrl}/passenger/transporting", passengers);
    return response.IsSuccessStatusCode;
}

async Task<bool> ReportSuccessToUNO(int orderId, string serviceName)
{
    var response = await httpClient.PostAsync($"{unoServiceUrl}/uno/api/v1/order/successReport/{orderId}/{serviceName}", null);
    return response.IsSuccessStatusCode;
}

async Task<bool> NotifyGarageFree(int endPoint)
{
    var response = await httpClient.DeleteAsync($"{groundControlUrl}/garage/free/{endPoint}");
    return response.IsSuccessStatusCode;
}

app.Run();

// Модели данных
public class PassengerRequest
{
    public int PlaneId { get; set; }
    public int OrderId { get; set; }
    public List<Passenger> Passengers { get; set; }
}

public class Passenger
{
    public int PassengerId { get; set; }
}

public class MovementState
{
    public int CurrentPoint { get; set; }
    public int AttemptsWithoutMovement { get; set; } = 0;
}