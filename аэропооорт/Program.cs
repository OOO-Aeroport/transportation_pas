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

// Указываем порт для текущего сервиса
builder.WebHost.UseUrls("http://*:5000");

var app = builder.Build();

// Конфигурация
var passengerServiceUrl = "http://passenger-service:5001"; // URL сервиса пассажиров
var groundServiceUrl = "http://ground-service:5002"; // URL сервиса управления наземным обслуживанием
var groundControlUrl = "http://ground-control:5003"; // URL сервиса наземного диспетчера
var httpClient = new HttpClient();

// Накопитель пассажиров
var passengerQueue = new Dictionary<string, List<int>>(); // Ключ: рейс, Значение: список ID пассажиров

// Накопитель багажа
var baggageQueue = new Dictionary<string, List<int>>(); // Ключ: рейс, Значение: список ID багажа

// Эндпоинт для получения данных о пассажирах
app.MapPost("/transportation-pass", async context =>
{
    Console.WriteLine("Получен запрос на перевозку пассажира");

    // Чтение данных из тела запроса
    var requestBody = await context.Request.ReadFromJsonAsync<PassengerRequest>();
    if (requestBody == null)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Неверный формат запроса");
        return;
    }

    // Добавление пассажира в накопитель
    if (!passengerQueue.ContainsKey(requestBody.FlightId))
    {
        passengerQueue[requestBody.FlightId] = new List<int>();
    }
    passengerQueue[requestBody.FlightId].Add(requestBody.PassengerId);

    Console.WriteLine($"Пассажир {requestBody.PassengerId} добавлен в накопитель для рейса {requestBody.FlightId}");

    // Проверка, достигнуто ли 50 пассажиров
    if (passengerQueue[requestBody.FlightId].Count >= 50)
    {
        Console.WriteLine($"Набрано 50 пассажиров для рейса {requestBody.FlightId}. Отправка автобуса.");
        await TransportPassengersAsync(requestBody.FlightId);
    }

    await context.Response.WriteAsync("Пассажир успешно добавлен в накопитель");
});

// Эндпоинт для получения данных о багаже
app.MapPost("/transportation-bagg", async context =>
{
    Console.WriteLine("Получен запрос на перевозку багажа");

    // Чтение данных из тела запроса
    var requestBody = await context.Request.ReadFromJsonAsync<BaggageRequest>();
    if (requestBody == null)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Неверный формат запроса");
        return;
    }

    // Добавление багажа в накопитель
    if (!baggageQueue.ContainsKey(requestBody.FlightId))
    {
        baggageQueue[requestBody.FlightId] = new List<int>();
    }
    baggageQueue[requestBody.FlightId].Add(requestBody.BaggageId);

    Console.WriteLine($"Багаж {requestBody.BaggageId} добавлен в накопитель для рейса {requestBody.FlightId}");

    await context.Response.WriteAsync("Багаж успешно добавлен в накопитель");
});

// Запуск приложения
app.Run();

// Метод для перевозки пассажиров
async Task TransportPassengersAsync(string flightId)
{
    // Получение данных о самолете от управления наземным обслуживанием
    var (planeId, gateNumber) = await GetPlaneInfoAsync(flightId);
    if (planeId == null || gateNumber == null)
    {
        Console.WriteLine($"Не удалось получить данные о самолете для рейса {flightId}");
        return;
    }

    // Запрос разрешения на выезд из гаража
    var vehicleType = "bus";
    var garageResponse = await httpClient.PostAsync($"{groundControlUrl}/garage/{vehicleType}", null);
    if (!garageResponse.IsSuccessStatusCode)
    {
        Console.WriteLine("Не удалось получить разрешение на выезд из гаража");
        return;
    }

    // Получение маршрута к самолету
    var currentPoint = "garage";
    var route = await GetRouteToPlaneAsync(currentPoint, planeId);
    if (route == null)
    {
        Console.WriteLine("Не удалось получить маршрут к самолету");
        return;
    }

    Console.WriteLine($"Автобус отправляется по маршруту: {route}");

    // Перевозка пассажиров
    var passengers = passengerQueue[flightId];
    Console.WriteLine($"Перевозка {passengers.Count} пассажиров на рейс {flightId}");
    passengerQueue.Remove(flightId); // Очистка накопителя

    // Отправка отчета в управление наземным обслуживанием
    await SendReportToGroundServiceAsync($"Пассажиры рейса {flightId} успешно перевезены");
}

// Метод для получения данных о самолете
async Task<(string PlaneId, string GateNumber)> GetPlaneInfoAsync(string flightId)
{
    try
    {
        var response = await httpClient.GetAsync($"{groundServiceUrl}/api/plane-info/{flightId}");
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var planeInfo = JsonSerializer.Deserialize<PlaneInfoResponse>(content);
        return (planeInfo.PlaneId, planeInfo.GateNumber);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка при получении данных о самолете: {ex.Message}");
        return (null, null);
    }
}

// Метод для получения маршрута к самолету
async Task<string> GetRouteToPlaneAsync(string currentPoint, string planeId)
{
    for (int attempt = 0; attempt < 5; attempt++)
    {
        var response = await httpClient.GetAsync($"{groundControlUrl}/plane/{currentPoint}/{planeId}");
        if (response.IsSuccessStatusCode)
        {
            var route = await response.Content.ReadAsStringAsync();
            return route;
        }
        await Task.Delay(1000); // Задержка перед повторным запросом
    }
    Console.WriteLine("Не удалось получить маршрут после 5 попыток");
    return null;
}

// Метод для отправки отчета в управление наземным обслуживанием
async Task SendReportToGroundServiceAsync(string report)
{
    try
    {
        var content = new StringContent(report, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync($"{groundServiceUrl}/api/report", content);
        response.EnsureSuccessStatusCode();
        Console.WriteLine("Отчет успешно отправлен в управление наземным обслуживанием");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка при отправке отчета: {ex.Message}");
    }
}

// Модели данных
public class PassengerRequest
{
    public int PassengerId { get; set; }
    public string FlightId { get; set; }
}

public class BaggageRequest
{
    public int BaggageId { get; set; }
    public string FlightId { get; set; }
}

public class PlaneInfoResponse
{
    public string PlaneId { get; set; }
    public string GateNumber { get; set; }
}