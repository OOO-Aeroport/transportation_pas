using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://26.132.135.106:5555");

var app = builder.Build();

var httpClient = new HttpClient();

const string unoServiceUrl = "http://uno/api/v1/order/successReport";
const string groundControlUrl = "http://26.21.3.228:5555";
const string passengerServiceUrl = "http://26.49.89.37:5555";
const string boardServiceUrl = "http://aircraft-service";

var passengerLoadQueue = new ConcurrentDictionary<string, List<int>>(); // Для погрузки пассажиров
var passengerUnloadQueue = new ConcurrentDictionary<string, List<int>>(); // Для выгрузки пассажиров
var baggageLoadQueue = new ConcurrentDictionary<string, List<BaggageInfo>>(); // Для погрузки багажа
var baggageUnloadQueue = new ConcurrentDictionary<string, List<BaggageInfo>>(); // Для выгрузки багажа

var activeOrders = new ConcurrentDictionary<string, string>(); // orderId -> flightId

var buses = new List<Vehicle> { new Vehicle("bus1"), new Vehicle("bus2"), new Vehicle("bus3") }; // 3 автобуса
var carts = new List<Vehicle> { new Vehicle("cart1"), new Vehicle("cart2"), new Vehicle("cart3") }; // 3 тележки

// Прием заказов от УНО
app.MapGet("/plane-info/{flightId}/{orderId}", async (HttpContext context, string flightId, string orderId) =>
{
    activeOrders[orderId] = flightId;
    Console.WriteLine($"Новый заказ от УНО: рейс {flightId}, orderId {orderId}");

    // Определяем тип заказа (погрузка или выгрузка)
    // Например, можно добавить параметр в запрос или использовать orderId для определения типа
    bool isLoadOrder = true; // Заглушка: предполагаем, что это заказ на погрузку

    if (isLoadOrder)
    {
        // Обработка заказа на погрузку
        var passengerIds = passengerLoadQueue.ContainsKey(flightId) ? passengerLoadQueue[flightId] : new List<int>();
        if (passengerIds.Count >= 2)
        {
            var group = passengerIds.Take(2).ToList();
            await TransportPassengersAsync(flightId, group, isLoad: true);
        }
    }
    else
    {
        // Обработка заказа на выгрузку
        var passengerIds = passengerUnloadQueue.ContainsKey(flightId) ? passengerUnloadQueue[flightId] : new List<int>();
        if (passengerIds.Count >= 2)
        {
            var group = passengerIds.Take(2).ToList();
            await UnloadPassengersAsync(flightId, group);
        }
    }

    await context.Response.WriteAsync("Заказ принят");
});

// Получение данных о пассажирах
app.MapPost("/transportation-pass", async (HttpContext context) =>
{
    var passengers = await context.Request.ReadFromJsonAsync<List<PassengerRequest>>();
    if (passengers == null || passengers.Count == 0)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Некорректные данные");
        return;
    }

    // Распределение пассажиров по рейсам
    foreach (var passenger in passengers)
    {
        if (passengerLoadQueue.ContainsKey(passenger.FlightId))
        {
            passengerLoadQueue[passenger.FlightId].Add(passenger.PassengerId);
        }
        else
        {
            passengerLoadQueue[passenger.FlightId] = new List<int> { passenger.PassengerId };
        }
    }

    Console.WriteLine($"Добавлено {passengers.Count} пассажиров в очередь");

    // Обработка пассажиров для каждого рейса
    foreach (var flight in passengerLoadQueue.Keys.ToList())
    {
        while (passengerLoadQueue[flight].Count >= 2)
        {
            var group = passengerLoadQueue[flight].Take(2).ToList();
            passengerLoadQueue[flight] = passengerLoadQueue[flight].Skip(2).ToList();

            // Отправка группы на погрузку, если есть свободный автобус
            var bus = buses.FirstOrDefault(b => !b.IsBusy);
            if (bus != null)
            {
                await TransportPassengersAsync(flight, group, isLoad: true);
            }
            else
            {
                Console.WriteLine("Нет свободных автобусов. Пассажиры остаются в очереди.");
                // Возвращаем пассажиров в очередь, если автобусов нет
                passengerLoadQueue[flight].InsertRange(0, group);
                break;
            }
        }
    }

    await context.Response.WriteAsync("Пассажиры обработаны");
});

// Получение данных о багаже
app.MapPost("/transportation-bagg", async (HttpContext context) =>
{
    var baggageData = await context.Request.ReadFromJsonAsync<List<BaggageData>>();
    if (baggageData == null || baggageData.Count == 0)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Некорректные данные");
        return;
    }

    // Распределение багажа по рейсам
    foreach (var baggage in baggageData)
    {
        if (baggageLoadQueue.ContainsKey(baggage.FlightID))
        {
            baggageLoadQueue[baggage.FlightID].Add(new BaggageInfo { PassengerID = baggage.PassengerID, Weight = baggage.BaggageWeight });
        }
        else
        {
            baggageLoadQueue[baggage.FlightID] = new List<BaggageInfo> { new BaggageInfo { PassengerID = baggage.PassengerID, Weight = baggage.BaggageWeight } };
        }
    }

    Console.WriteLine($"Добавлено {baggageData.Count} единиц багажа в очередь");

    // Обработка багажа для каждого рейса
    foreach (var flight in baggageLoadQueue.Keys.ToList())
    {
        if (baggageLoadQueue[flight].Count > 0)
        {
            var cart = carts.FirstOrDefault(c => !c.IsBusy);
            if (cart != null)
            {
                await TransportBaggageAsync(flight, baggageLoadQueue[flight], isLoad: true);
                baggageLoadQueue[flight].Clear();
            }
            else
            {
                Console.WriteLine("Нет свободных тележек. Багаж остается в очереди.");
            }
        }
    }

    await context.Response.WriteAsync("Багаж обработан");
});

// Метод перевозки пассажиров
async Task TransportPassengersAsync(string flightId, List<int> passengerIds, bool isLoad)
{
    var bus = buses.FirstOrDefault(b => !b.IsBusy);
    if (bus == null)
    {
        Console.WriteLine("Нет свободных автобусов");
        return;
    }

    bus.IsBusy = true;

    // Запрос на выезд из гаража
    await RequestGarageExit("bus");

    if (isLoad)
    {
        // Запрос маршрута к терминалу
        var terminalRoute = await GetRouteToTerminal();
        if (terminalRoute == null) return;

        // Погрузка пассажиров
        Console.WriteLine($"Погружено 100 пассажиров на рейс {flightId}");

        // Отправка списка пассажиров в службу "Пассажиры"
        await SendPassengerIdsToPassengerService(passengerIds);

        // Запрос маршрута к самолету
        var planeRoute = await GetRouteToPlane(flightId);
        if (planeRoute == null) return;

        // Передача списка пассажиров в "Борт"
        await SendPassengersToBoard(flightId, passengerIds);

        // Отправка отчета в УНО
        var orderEntry = activeOrders.FirstOrDefault(x => x.Value == flightId);
        if (!string.IsNullOrEmpty(orderEntry.Key))
        {
            await ReportSuccessToUNO(orderEntry.Key);
        }
    }
    else
    {
        // Запрос маршрута к самолету
        var planeRoute = await GetRouteToPlane(flightId);
        if (planeRoute == null) return;

        // Погрузка пассажиров
        Console.WriteLine($"Погружено 100 пассажиров с рейса {flightId}");

        // Запрос маршрута к терминалу
        var terminalRoute = await GetRouteToTerminal();
        if (terminalRoute == null) return;

        // Очистка автобуса
        Console.WriteLine($"Автобус очищен от пассажиров");
    }

    // Возврат в гараж
    await ReturnToGarage("bus");

    bus.IsBusy = false;
}

// Метод для выгрузки пассажиров из самолета в терминал 2
async Task UnloadPassengersAsync(string flightId, List<int> passengerIds)
{
    var bus = buses.FirstOrDefault(b => !b.IsBusy);
    if (bus == null)
    {
        Console.WriteLine("Нет свободных автобусов");
        return;
    }

    bus.IsBusy = true;

    // Запрос на выезд из гаража
    await RequestGarageExit("bus");

    // Запрос маршрута до самолета
    var planeRoute = await GetRouteToPlane(flightId);
    if (planeRoute == null) return;

    // Погрузка пассажиров из самолета
    Console.WriteLine($"Погружено 100 пассажиров с рейса {flightId}");

    // Запрос маршрута до терминала 2
    var terminalRoute = await GetRouteToTerminal2();
    if (terminalRoute == null) return;

    // Выгрузка пассажиров
    Console.WriteLine($"Выгружено 100 пассажиров в терминал 2");

    // Отправка отчета в УНО
    var orderEntry = activeOrders.FirstOrDefault(x => x.Value == flightId);
    if (!string.IsNullOrEmpty(orderEntry.Key))
    {
        await ReportSuccessToUNO(orderEntry.Key);
    }

    // Возврат в гараж
    await ReturnToGarage("bus");

    bus.IsBusy = false;
}

// Метод перевозки багажа
async Task TransportBaggageAsync(string flightId, List<BaggageInfo> baggage, bool isLoad)
{
    var cart = carts.FirstOrDefault(c => !c.IsBusy);
    if (cart == null)
    {
        Console.WriteLine("Нет свободных тележек");
        return;
    }

    cart.IsBusy = true;

    // Запрос на выезд из гаража
    await RequestGarageExit("baggageTruck");

    if (isLoad)
    {
        // Запрос маршрута к грузовому терминалу
        var luggageRoute = await GetRouteToLuggage();
        if (luggageRoute == null) return;

        // Погрузка багажа
        Console.WriteLine($"Погружено {baggage.Count} единиц багажа на рейс {flightId}");

        // Запрос маршрута к самолету
        var planeRoute = await GetRouteToPlane(flightId);
        if (planeRoute == null) return;

        // Передача количества багажа в "Борт"
        await SendBaggageToBoard(flightId, baggage.Sum(b => b.Weight));

        // Отправка отчета в УНО
        var orderEntry = activeOrders.FirstOrDefault(x => x.Value == flightId);
        if (!string.IsNullOrEmpty(orderEntry.Key))
        {
            await ReportSuccessToUNO(orderEntry.Key);
        }
    }
    else
    {
        // Запрос маршрута к самолету
        var planeRoute = await GetRouteToPlane(flightId);
        if (planeRoute == null) return;

        // Погрузка багажа
        Console.WriteLine($"Погружено {baggage.Count} единиц багажа с рейса {flightId}");

        // Запрос маршрута к грузовому терминалу
        var luggageRoute = await GetRouteToLuggage();
        if (luggageRoute == null) return;

        // Очистка тележки
        Console.WriteLine($"Тележка очищена от багажа");
    }

    // Возврат в гараж
    await ReturnToGarage("baggageTruck");

    cart.IsBusy = false;
}

// Запрос на выезд из гаража
async Task RequestGarageExit(string vehicleType)
{
    var response = await httpClient.GetAsync($"{groundControlUrl}/garage/{vehicleType}");
    if (!response.IsSuccessStatusCode) Console.WriteLine($"Ошибка выезда из гаража ({vehicleType})");
}

// Получение маршрута к терминалу
async Task<string> GetRouteToTerminal()
{
    var currentPoint = "garage";
    for (int attempt = 0; attempt < 5; attempt++)
    {
        var response = await httpClient.GetAsync($"{groundControlUrl}/{currentPoint}/terminal1");
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsStringAsync();
        }
        await Task.Delay(1000);
    }
    Console.WriteLine($"Не удалось получить маршрут к терминалу за 5 попыток");
    return null;
}

// Получение маршрута до терминала 2
async Task<string> GetRouteToTerminal2()
{
    var currentPoint = "garage";
    for (int attempt = 0; attempt < 5; attempt++)
    {
        var response = await httpClient.GetAsync($"{groundControlUrl}/{currentPoint}/terminal2");
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsStringAsync();
        }
        await Task.Delay(1000);
    }
    Console.WriteLine($"Не удалось получить маршрут к терминалу 2 за 5 попыток");
    return null;
}

// Получение маршрута к грузовому терминалу
async Task<string> GetRouteToLuggage()
{
    var currentPoint = "garage";
    for (int attempt = 0; attempt < 5; attempt++)
    {
        var response = await httpClient.GetAsync($"{groundControlUrl}/{currentPoint}/luggage");
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsStringAsync();
        }
        await Task.Delay(1000);
    }
    Console.WriteLine($"Не удалось получить маршрут к грузовому терминалу за 5 попыток");
    return null;
}

// Получение маршрута к самолету
async Task<string> GetRouteToPlane(string flightId)
{
    var currentPoint = "garage";
    for (int attempt = 0; attempt < 5; attempt++)
    {
        var response = await httpClient.GetAsync($"{groundControlUrl}/{currentPoint}/plane/{flightId}");
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsStringAsync();
        }
        await Task.Delay(1000);
    }
    Console.WriteLine($"Не удалось получить маршрут к самолету {flightId} за 5 попыток");
    return null;
}

// Отправка списка пассажиров в службу "Пассажиры"
async Task SendPassengerIdsToPassengerService(List<int> passengerIds)
{
    var passengerIdsRequest = new PassengerIdsRequest
    {
        PassengerIds = passengerIds.Select(id => new PassengerId { PassengerIds = id }).ToList()
    };

    var content = new StringContent(JsonSerializer.Serialize(passengerIdsRequest), Encoding.UTF8, "application/json");
    var response = await httpClient.PostAsync($"{passengerServiceUrl}/passenger/transporting", content);
    if (!response.IsSuccessStatusCode)
    {
        Console.WriteLine("Ошибка отправки списка пассажиров в службу 'Пассажиры'");
    }
}

// Отправка пассажиров на борт
async Task SendPassengersToBoard(string flightId, List<int> passengerIds)
{
    var passengerIdsRequest = new PassengerIdsRequest
    {
        PassengerIds = passengerIds.Select(id => new PassengerId { PassengerIds = id }).ToList()
    };

    var content = new StringContent(JsonSerializer.Serialize(passengerIdsRequest), Encoding.UTF8, "application/json");
    var response = await httpClient.PostAsync($"{boardServiceUrl}/board_passengers/{flightId}/{passengerIds.Count}", content);
    if (!response.IsSuccessStatusCode)
    {
        Console.WriteLine("Ошибка отправки пассажиров на борт");
    }
}

// Отправка багажа на борт
async Task SendBaggageToBoard(string flightId, int weight)
{
    var response = await httpClient.GetAsync($"{boardServiceUrl}/board_baggage/{flightId}/{weight}");
    if (!response.IsSuccessStatusCode) Console.WriteLine("Ошибка отправки багажа на борт");
}

// Отправка отчета об успешном выполнении заказа в УНО
async Task ReportSuccessToUNO(string orderId)
{
    var response = await httpClient.GetAsync($"{unoServiceUrl}/{orderId}/passenger-and-baggage");
    if (!response.IsSuccessStatusCode) Console.WriteLine("Ошибка отправки отчета в УНО");
}

// Возврат в гараж
async Task ReturnToGarage(string vehicleType)
{
    var response = await httpClient.GetAsync($"{groundControlUrl}/plane/garage/{vehicleType}");
    if (!response.IsSuccessStatusCode) Console.WriteLine($"Ошибка возврата в гараж ({vehicleType})");
}

// Запуск сервиса
app.Run();

// Модели данных
public class PassengerRequest
{
    public int PassengerId { get; set; }
    public string FlightId { get; set; }
}

public class BaggageData
{
    public string FlightID { get; set; }
    public int PassengerID { get; set; }
    public int BaggageWeight { get; set; }
}

public class BaggageInfo
{
    public int PassengerID { get; set; }
    public int Weight { get; set; }
}

public class Vehicle
{
    public string Id { get; set; }
    public bool IsBusy { get; set; }

    public Vehicle(string id)
    {
        Id = id;
        IsBusy = false;
    }
}

// Модель для передачи списка пассажиров
public class PassengerIdsRequest
{
    public List<PassengerId> PassengerIds { get; set; }
}

public class PassengerId
{
    public int PassengerIds { get; set; }
}